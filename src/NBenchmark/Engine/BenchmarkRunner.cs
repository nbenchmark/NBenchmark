using System.Diagnostics;
using System.Runtime.CompilerServices;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

/// <summary>
///     The single owner of the per-benchmark measurement lifecycle: warmup loop,
///     force-GC, per-iteration timing, allocation measurement, outlier trimming,
///     stats computation, <see cref="BenchmarkResult" /> construction, error
///     translation, JIT-elision, and warmup progress emission. Tier 1/2/3 entry
///     points are thin adapters on top of this module.
/// </summary>
public sealed class BenchmarkRunner
{
    public static BenchmarkRunner Instance { get; } = new();

    public MeasurementOutcome Run(string name, Action body, RunSpec spec, CancellationToken ct = default)
        => RunSyncVoid(name, body, spec, ct);

    public MeasurementOutcome Run<T>(string name, Func<T> body, RunSpec spec, CancellationToken ct = default)
        => RunSyncReturning(name, body, spec, ct);

    public Task<MeasurementOutcome> RunAsync(string name, Func<Task> body, RunSpec spec, CancellationToken ct = default)
        => RunAsyncVoid(name, body, spec, ct);

    public Task<MeasurementOutcome> RunAsync<T>(string name, Func<Task<T>> body, RunSpec spec, CancellationToken ct = default)
        => RunAsyncReturning(name, body, spec, ct);

    // ---------- Sync cores ----------

    private static MeasurementOutcome RunSyncVoid(string name, Action body, RunSpec spec, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var options = spec.Options;
        var progress = spec.Progress;

        try
        {
            progress.OnWarmupStarting(name, options.WarmupIterations).GetAwaiter().GetResult();

            for (var i = 0; i < options.WarmupIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                spec.IterationSetup?.Invoke();
                body();
                spec.IterationTeardown?.Invoke();
            }

            if (options.Iterations == 0)
            {
                var dryRun = DryRunOutcome(name, spec, totalTimer);
                progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
                return dryRun;
            }

            ForceFullGc();

            var outcome = MeasureSyncVoid(name, body, spec, ct);

            progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErroredOutcome(name, spec, ex, totalTimer);
        }
    }

    private static MeasurementOutcome RunSyncReturning<T>(string name, Func<T> body, RunSpec spec, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var options = spec.Options;
        var progress = spec.Progress;

        try
        {
            progress.OnWarmupStarting(name, options.WarmupIterations).GetAwaiter().GetResult();

            for (var i = 0; i < options.WarmupIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                spec.IterationSetup?.Invoke();
                Consume(body());
                spec.IterationTeardown?.Invoke();
            }

            if (options.Iterations == 0)
            {
                var dryRun = DryRunOutcome(name, spec, totalTimer);
                progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
                return dryRun;
            }

            ForceFullGc();

            var outcome = MeasureSyncReturning(name, body, spec, ct);

            progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErroredOutcome(name, spec, ex, totalTimer);
        }
    }

    // ---------- Async cores ----------

    private static async Task<MeasurementOutcome> RunAsyncVoid(string name, Func<Task> body, RunSpec spec, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var options = spec.Options;
        var progress = spec.Progress;

        try
        {
            await progress.OnWarmupStarting(name, options.WarmupIterations).ConfigureAwait(false);

            for (var i = 0; i < options.WarmupIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                spec.IterationSetup?.Invoke();
                await body().ConfigureAwait(false);
                spec.IterationTeardown?.Invoke();
            }

            if (options.Iterations == 0)
            {
                var dryRun = DryRunOutcome(name, spec, totalTimer);
                await progress.OnWarmupCompleted(name).ConfigureAwait(false);
                return dryRun;
            }

            ForceFullGc();

            var outcome = await MeasureAsyncVoid(name, body, spec, ct).ConfigureAwait(false);

            await progress.OnWarmupCompleted(name).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErroredOutcome(name, spec, ex, totalTimer);
        }
    }

    private static async Task<MeasurementOutcome> RunAsyncReturning<T>(string name, Func<Task<T>> body, RunSpec spec, CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        var options = spec.Options;
        var progress = spec.Progress;

        try
        {
            await progress.OnWarmupStarting(name, options.WarmupIterations).ConfigureAwait(false);

            for (var i = 0; i < options.WarmupIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                spec.IterationSetup?.Invoke();
                Consume(await body().ConfigureAwait(false));
                spec.IterationTeardown?.Invoke();
            }

            if (options.Iterations == 0)
            {
                var dryRun = DryRunOutcome(name, spec, totalTimer);
                await progress.OnWarmupCompleted(name).ConfigureAwait(false);
                return dryRun;
            }

            ForceFullGc();

            var outcome = await MeasureAsyncReturning(name, body, spec, ct).ConfigureAwait(false);

            await progress.OnWarmupCompleted(name).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErroredOutcome(name, spec, ex, totalTimer);
        }
    }

    // ---------- Measurement loops ----------

    private static MeasurementOutcome MeasureSyncVoid(string name, Action body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var totalTimer = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            long allocBefore = 0;
            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes();

            var timestamp = Stopwatch.GetTimestamp();
            body();
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes();
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        totalTimer.Stop();
        return BuildOutcome(name, spec, timings, allocations, totalTimer);
    }

    private static MeasurementOutcome MeasureSyncReturning<T>(string name, Func<T> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var totalTimer = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            long allocBefore = 0;
            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes();

            var timestamp = Stopwatch.GetTimestamp();
            Consume(body());
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes();
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        totalTimer.Stop();
        return BuildOutcome(name, spec, timings, allocations, totalTimer);
    }

    private static async Task<MeasurementOutcome> MeasureAsyncVoid(string name, Func<Task> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var totalTimer = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            long allocBefore = 0;
            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes();

            var timestamp = Stopwatch.GetTimestamp();
            await body().ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes();
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        totalTimer.Stop();
        return BuildOutcome(name, spec, timings, allocations, totalTimer);
    }

    private static async Task<MeasurementOutcome> MeasureAsyncReturning<T>(string name, Func<Task<T>> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var totalTimer = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            long allocBefore = 0;
            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes();

            var timestamp = Stopwatch.GetTimestamp();
            Consume(await body().ConfigureAwait(false));
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes();
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        totalTimer.Stop();
        return BuildOutcome(name, spec, timings, allocations, totalTimer);
    }

    // ---------- Outcome builders ----------

    private static MeasurementOutcome DryRunOutcome(string name, RunSpec spec, Stopwatch totalTimer)
    {
        totalTimer.Stop();
        return new MeasurementOutcome
        {
            RawSamples = [],
            Result = new BenchmarkResult
            {
                Name = name,
                Description = spec.Description,
                Mean = 0,
                Median = 0,
                P95 = 0,
                P99 = 0,
                Min = 0,
                Max = 0,
                StandardDeviation = 0,
                StandardError = 0,
                MarginOfError = 0,
                ConfidenceLevel = spec.Options.ConfidenceLevel,
                CoefficientOfVariation = 0,
                MeanAllocatedBytes = null,
                PValue = null,
                IsSignificant = null,
                Errored = false,
                ErrorMessage = null,
                MeasuredIterations = 0,
                WarmupIterations = spec.Options.WarmupIterations,
                RunAt = DateTimeOffset.UtcNow,
                TotalDuration = totalTimer.Elapsed,
                IsBaseline = spec.IsBaseline,
                OutlierMode = spec.Options.OutlierMode,
            },
        };
    }

    private static MeasurementOutcome ErroredOutcome(string name, RunSpec spec, Exception ex, Stopwatch totalTimer)
    {
        totalTimer.Stop();
        var inner = ex is System.Reflection.TargetInvocationException tiex ? (tiex.InnerException ?? tiex) : ex;

        return new MeasurementOutcome
        {
            RawSamples = [],
            Result = BenchmarkResult.CreateErrored(
                name,
                inner.ToString(),
                spec.Description,
                spec.IsBaseline,
                spec.Options.OutlierMode),
        };
    }

    private static MeasurementOutcome BuildOutcome(
        string name,
        RunSpec spec,
        double[] timings,
        long[]? allocations,
        Stopwatch totalTimer)
    {
        var options = spec.Options;
        var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
        var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
        long? meanAllocs = allocations is not null ? (long)allocations.Average() : null;

        return new MeasurementOutcome
        {
            RawSamples = timings,
            Result = new BenchmarkResult
            {
                Name = name,
                Description = spec.Description,
                Mean = stats.Mean,
                Median = stats.Median,
                P95 = stats.P95,
                P99 = stats.P99,
                Min = stats.Min,
                Max = stats.Max,
                StandardDeviation = stats.StandardDeviation,
                StandardError = stats.StandardError,
                MarginOfError = stats.MarginOfError,
                ConfidenceLevel = stats.ConfidenceLevel,
                CoefficientOfVariation = stats.CoefficientOfVariation,
                MeanAllocatedBytes = meanAllocs,
                PValue = null,
                IsSignificant = null,
                Errored = false,
                ErrorMessage = null,
                MeasuredIterations = trimmed.Length,
                WarmupIterations = options.WarmupIterations,
                RunAt = DateTimeOffset.UtcNow,
                TotalDuration = totalTimer.Elapsed,
                IsBaseline = spec.IsBaseline,
                OutlierMode = options.OutlierMode,
            },
        };
    }

    // ---------- JIT-elision sink (private, no public exposure) ----------

    private static volatile object? _hole;
    private static int _holeInt;
    private static long _holeLong;
    private static double _holeDouble;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume<T>(T value) => _hole = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(int value) => System.Threading.Volatile.Write(ref _holeInt, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(long value) => System.Threading.Volatile.Write(ref _holeLong, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(double value) => System.Threading.Volatile.Write(ref _holeDouble, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(bool value) => System.Threading.Volatile.Write(ref _holeInt, value ? 1 : 0);

    // ---------- GC helpers ----------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGen0Collection() => GC.Collect(0, GCCollectionMode.Forced, true);

    private static void ForceFullGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    // ---------- Outlier trimming ----------

    private static double[] ApplyOutlierMode(double[] timings, OutlierMode mode) => mode switch
    {
        OutlierMode.None => SortAndReturn(timings),
        OutlierMode.RemoveTop5Percent => RemoveTopPercent(timings, 0.05),
        OutlierMode.RemoveTop5PercentAndBottom5Percent => RemoveBothPercent(timings, 0.05),
        OutlierMode.IqrFence => RemoveIqrOutliers(timings),
        _ => timings,
    };

    private static double[] SortAndReturn(double[] values)
    {
        Array.Sort(values);
        return values;
    }

    private static double[] RemoveTopPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var keep = (int)Math.Floor(values.Length * (1.0 - fraction));
        return values[..keep];
    }

    private static double[] RemoveBothPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var trimEach = (int)Math.Floor(values.Length * fraction);
        return values[trimEach..(values.Length - trimEach)];
    }

    private static double[] RemoveIqrOutliers(double[] values)
    {
        Array.Sort(values);
        var q1 = Percentile.Compute(values, 0.25);
        var q3 = Percentile.Compute(values, 0.75);
        var iqr = q3 - q1;
        var lower = q1 - 1.5 * iqr;
        var upper = q3 + 1.5 * iqr;
        var filtered = values.Where(v => v >= lower && v <= upper).ToArray();

        return filtered.Length > 0 ? filtered : values;
    }
}
