using System.Runtime.CompilerServices;

namespace NBenchmark.Engine;

/// <summary>
///     The single owner of the per-benchmark measurement lifecycle: warmup loop,
///     force-GC, per-iteration timing, allocation measurement, outlier trimming,
///     stats computation, <see cref="BenchmarkResult" /> construction, error
///     translation, JIT-elision, and warmup progress emission. Quick/Suite/Host mode
///     points are thin adapters on top of this module.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly IClock _clock;

    public static BenchmarkRunner Instance { get; } = new();

    public BenchmarkRunner() : this(StopwatchClock.Instance)
    {
    }

    internal BenchmarkRunner(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public MeasurementOutcome Run(string name, Action body, RunSpec spec, CancellationToken ct = default)
        => RunSyncVoid(name, body, spec, ct);

    public MeasurementOutcome Run<T>(string name, Func<T> body, RunSpec spec, CancellationToken ct = default)
        => RunSyncReturning(name, body, spec, ct);

    public Task<MeasurementOutcome> RunAsync(string name, Func<Task> body, RunSpec spec, CancellationToken ct = default)
        => RunAsyncVoid(name, body, spec, ct);

    public Task<MeasurementOutcome> RunAsync<T>(string name, Func<Task<T>> body, RunSpec spec, CancellationToken ct = default)
        => RunAsyncReturning(name, body, spec, ct);

    // ---------- Sync cores ----------

    private MeasurementOutcome RunSyncVoid(string name, Action body, RunSpec spec, CancellationToken ct)
    {
        var totalStartTimestamp = _clock.GetTimestamp();
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
                var dryRun = BuildDryRunOutcome(name, spec, totalStartTimestamp);
                progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = MeasureSyncVoid(body, spec, ct);
            var outcome = BuildSuccessOutcome(name, spec, totalStartTimestamp, timings, allocations, measuredDuration);

            progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildErroredOutcome(name, spec, totalStartTimestamp, ex);
        }
    }

    private MeasurementOutcome RunSyncReturning<T>(string name, Func<T> body, RunSpec spec, CancellationToken ct)
    {
        var totalStartTimestamp = _clock.GetTimestamp();
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
                var dryRun = BuildDryRunOutcome(name, spec, totalStartTimestamp);
                progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = MeasureSyncReturning<T>(body, spec, ct);
            var outcome = BuildSuccessOutcome(name, spec, totalStartTimestamp, timings, allocations, measuredDuration);

            progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildErroredOutcome(name, spec, totalStartTimestamp, ex);
        }
    }

    // ---------- Async cores ----------

    private async Task<MeasurementOutcome> RunAsyncVoid(string name, Func<Task> body, RunSpec spec, CancellationToken ct)
    {
        var totalStartTimestamp = _clock.GetTimestamp();
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
                var dryRun = BuildDryRunOutcome(name, spec, totalStartTimestamp);
                await progress.OnWarmupCompleted(name).ConfigureAwait(false);
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = await MeasureAsyncVoid(body, spec, ct).ConfigureAwait(false);
            var outcome = BuildSuccessOutcome(name, spec, totalStartTimestamp, timings, allocations, measuredDuration);

            await progress.OnWarmupCompleted(name).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildErroredOutcome(name, spec, totalStartTimestamp, ex);
        }
    }

    private async Task<MeasurementOutcome> RunAsyncReturning<T>(string name, Func<Task<T>> body, RunSpec spec, CancellationToken ct)
    {
        var totalStartTimestamp = _clock.GetTimestamp();
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
                var dryRun = BuildDryRunOutcome(name, spec, totalStartTimestamp);
                await progress.OnWarmupCompleted(name).ConfigureAwait(false);
                return dryRun;
            }

            ForceFullGc();

            var (timings, allocations, measuredDuration) = await MeasureAsyncReturning<T>(body, spec, ct).ConfigureAwait(false);
            var outcome = BuildSuccessOutcome(name, spec, totalStartTimestamp, timings, allocations, measuredDuration);

            await progress.OnWarmupCompleted(name).ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BuildErroredOutcome(name, spec, totalStartTimestamp, ex);
        }
    }

    // ---------- Measurement loops ----------

    private (double[] timings, long[]? allocations, TimeSpan measuredDuration) MeasureSyncVoid(Action body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopStartTimestamp = _clock.GetTimestamp();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            AllocationSnapshot allocBefore = default;
            if (options.MeasureAllocations)
                allocBefore = CaptureAllocationSnapshot();

            var timestamp = _clock.GetTimestamp();
            body();
            var elapsed = _clock.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
                allocations[i] = ComputeAllocationDelta(allocBefore);

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        return (timings, allocations, _clock.GetElapsedTime(loopStartTimestamp));
    }

    private (double[] timings, long[]? allocations, TimeSpan measuredDuration) MeasureSyncReturning<T>(Func<T> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopStartTimestamp = _clock.GetTimestamp();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            AllocationSnapshot allocBefore = default;
            if (options.MeasureAllocations)
                allocBefore = CaptureAllocationSnapshot();

            var timestamp = _clock.GetTimestamp();
            Consume(body());
            var elapsed = _clock.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
                allocations[i] = ComputeAllocationDelta(allocBefore);

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        return (timings, allocations, _clock.GetElapsedTime(loopStartTimestamp));
    }

    private async Task<(double[] timings, long[]? allocations, TimeSpan measuredDuration)> MeasureAsyncVoid(Func<Task> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopStartTimestamp = _clock.GetTimestamp();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            AllocationSnapshot allocBefore = default;
            if (options.MeasureAllocations)
                allocBefore = CaptureAllocationSnapshot();

            var timestamp = _clock.GetTimestamp();
            await body().ConfigureAwait(false);
            var elapsed = _clock.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
                allocations[i] = ComputeAllocationDelta(allocBefore);

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        return (timings, allocations, _clock.GetElapsedTime(loopStartTimestamp));
    }

    private async Task<(double[] timings, long[]? allocations, TimeSpan measuredDuration)> MeasureAsyncReturning<T>(Func<Task<T>> body, RunSpec spec, CancellationToken ct)
    {
        var options = spec.Options;
        var iterations = options.Iterations;
        var timings = new double[iterations];
        var allocations = options.MeasureAllocations ? new long[iterations] : null;
        var loopStartTimestamp = _clock.GetTimestamp();

        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            spec.IterationSetup?.Invoke();

            AllocationSnapshot allocBefore = default;
            if (options.MeasureAllocations)
                allocBefore = CaptureAllocationSnapshot();

            var timestamp = _clock.GetTimestamp();
            Consume(await body().ConfigureAwait(false));
            var elapsed = _clock.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
                allocations[i] = ComputeAllocationDelta(allocBefore);

            spec.IterationTeardown?.Invoke();
            timings[i] = elapsed.TotalNanoseconds;
        }

        return (timings, allocations, _clock.GetElapsedTime(loopStartTimestamp));
    }

    private MeasurementOutcome BuildDryRunOutcome(string name, RunSpec spec, long totalStartTimestamp)
    {
        return OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            name,
            spec.Description,
            spec.IsBaseline,
            spec.Options,
            _clock.GetElapsedTime(totalStartTimestamp),
            TimeSpan.Zero);
    }

    private MeasurementOutcome BuildSuccessOutcome(
        string name,
        RunSpec spec,
        long totalStartTimestamp,
        double[] timings,
        long[]? allocations,
        TimeSpan measuredDuration)
    {
        var pipeline = StatsPipeline.Run(timings, allocations, spec.Options);
        return OutcomeBuilder.Build(
            new RunOutcome.Success(pipeline, timings),
            name,
            spec.Description,
            spec.IsBaseline,
            spec.Options,
            _clock.GetElapsedTime(totalStartTimestamp),
            measuredDuration);
    }

    private MeasurementOutcome BuildErroredOutcome(string name, RunSpec spec, long totalStartTimestamp, Exception ex)
    {
        return OutcomeBuilder.Build(
            new RunOutcome.Errored(ex),
            name,
            spec.Description,
            spec.IsBaseline,
            spec.Options,
            _clock.GetElapsedTime(totalStartTimestamp),
            TimeSpan.Zero);
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

    /// <summary>
    ///     Returns a typed result-consumer delegate that writes a value to the
    ///     JIT-elision sink without boxing value types. Used by the discovery
    ///     pipeline to avoid per-iteration allocation overhead for
    ///     <c>Task&lt;T&gt;</c>-returning benchmarks.
    /// </summary>
    public static Action<T> GetResultConsumer<T>() =>
        JitSinkCache<T>.Instance;

    private static class JitSinkCache<T>
    {
        public static readonly Action<T> Instance = CreateTypedConsumer();

        private static Action<T> CreateTypedConsumer()
        {
            if (typeof(T) == typeof(int))
                return (Action<T>)(object)(Action<int>)(static v => Consume(v));
            if (typeof(T) == typeof(long))
                return (Action<T>)(object)(Action<long>)(static v => Consume(v));
            if (typeof(T) == typeof(double))
                return (Action<T>)(object)(Action<double>)(static v => Consume(v));
            if (typeof(T) == typeof(bool))
                return (Action<T>)(object)(Action<bool>)(static v => Consume(v));
            return static v => Consume(v);
        }
    }

    private readonly record struct AllocationSnapshot(long ThreadBytes, long ProcessBytes, int ThreadId);

    private static AllocationSnapshot CaptureAllocationSnapshot()
    {
        return new AllocationSnapshot(
            GC.GetAllocatedBytesForCurrentThread(),
            GC.GetTotalAllocatedBytes(),
            Environment.CurrentManagedThreadId);
    }

    private static long ComputeAllocationDelta(AllocationSnapshot before)
    {
        if (Environment.CurrentManagedThreadId == before.ThreadId)
            return Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before.ThreadBytes);

        // Async continuations may resume on a different worker thread.
        // Fall back to process-wide allocation delta in that case.
        return Math.Max(0, GC.GetTotalAllocatedBytes() - before.ProcessBytes);
    }

    // ---------- GC helpers ----------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGen0Collection() => GC.Collect(0, GCCollectionMode.Forced, true);

    private static void ForceFullGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }
}
