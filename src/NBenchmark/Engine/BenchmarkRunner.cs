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
                totalTimer.Stop();
                var dryRun = OutcomeBuilder.Build(
                    new OutcomeInput.DryRun(), name, spec.Description, spec.IsBaseline,
                    spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
                progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = MeasureSyncVoid(body, spec, ct);
            totalTimer.Stop();
            var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
            var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
            var outcome = OutcomeBuilder.Build(
                new OutcomeInput.Success(stats, trimmed.Length, allocations, timings),
                name, spec.Description, spec.IsBaseline, spec.Options,
                totalTimer.Elapsed, measuredDuration);

            progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            totalTimer.Stop();
            return OutcomeBuilder.Build(
                new OutcomeInput.Errored(ex), name, spec.Description, spec.IsBaseline,
                spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
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
                totalTimer.Stop();
                var dryRun = OutcomeBuilder.Build(
                    new OutcomeInput.DryRun(), name, spec.Description, spec.IsBaseline,
                    spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
                progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = MeasureSyncReturning<T>(body, spec, ct);
            totalTimer.Stop();
            var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
            var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
            var outcome = OutcomeBuilder.Build(
                new OutcomeInput.Success(stats, trimmed.Length, allocations, timings),
                name, spec.Description, spec.IsBaseline, spec.Options,
                totalTimer.Elapsed, measuredDuration);

            progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            totalTimer.Stop();
            return OutcomeBuilder.Build(
                new OutcomeInput.Errored(ex), name, spec.Description, spec.IsBaseline,
                spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
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
                totalTimer.Stop();
                var dryRun = OutcomeBuilder.Build(
                    new OutcomeInput.DryRun(), name, spec.Description, spec.IsBaseline,
                    spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
                await progress.OnWarmupCompleted(name).ConfigureAwait(false);
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = await MeasureAsyncVoid(body, spec, ct).ConfigureAwait(false);
            totalTimer.Stop();
            var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
            var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
            var outcome = OutcomeBuilder.Build(
                new OutcomeInput.Success(stats, trimmed.Length, allocations, timings),
                name, spec.Description, spec.IsBaseline, spec.Options,
                totalTimer.Elapsed, measuredDuration);

            await progress.OnWarmupCompleted(name).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            totalTimer.Stop();
            return OutcomeBuilder.Build(
                new OutcomeInput.Errored(ex), name, spec.Description, spec.IsBaseline,
                spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
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
                totalTimer.Stop();
                var dryRun = OutcomeBuilder.Build(
                    new OutcomeInput.DryRun(), name, spec.Description, spec.IsBaseline,
                    spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
                await progress.OnWarmupCompleted(name).ConfigureAwait(false);
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = await MeasureAsyncReturning<T>(body, spec, ct).ConfigureAwait(false);
            totalTimer.Stop();
            var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
            var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);
            var outcome = OutcomeBuilder.Build(
                new OutcomeInput.Success(stats, trimmed.Length, allocations, timings),
                name, spec.Description, spec.IsBaseline, spec.Options,
                totalTimer.Elapsed, measuredDuration);

            await progress.OnWarmupCompleted(name).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            totalTimer.Stop();
            return OutcomeBuilder.Build(
                new OutcomeInput.Errored(ex), name, spec.Description, spec.IsBaseline,
                spec.Options, totalTimer.Elapsed, TimeSpan.Zero);
        }
    }

    // ---------- Measurement loops ----------

    private static (double[] timings, long[]? allocations, TimeSpan measuredDuration) MeasureSyncVoid(Action body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopTimer = Stopwatch.StartNew();

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

        loopTimer.Stop();
        return (timings, allocations, loopTimer.Elapsed);
    }

    private static (double[] timings, long[]? allocations, TimeSpan measuredDuration) MeasureSyncReturning<T>(Func<T> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopTimer = Stopwatch.StartNew();

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

        loopTimer.Stop();
        return (timings, allocations, loopTimer.Elapsed);
    }

    private static async Task<(double[] timings, long[]? allocations, TimeSpan measuredDuration)> MeasureAsyncVoid(Func<Task> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopTimer = Stopwatch.StartNew();

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

        loopTimer.Stop();
        return (timings, allocations, loopTimer.Elapsed);
    }

    private static async Task<(double[] timings, long[]? allocations, TimeSpan measuredDuration)> MeasureAsyncReturning<T>(Func<Task<T>> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopTimer = Stopwatch.StartNew();

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

        loopTimer.Stop();
        return (timings, allocations, loopTimer.Elapsed);
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
