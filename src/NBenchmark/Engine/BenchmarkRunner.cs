using System.Runtime.CompilerServices;
using NBenchmark.Diagnostics;

namespace NBenchmark.Engine;

/// <summary>
///     The single owner of the per-benchmark measurement lifecycle: warmup loop,
///     force-GC, per-iteration timing, allocation measurement, outlier trimming,
///     stats computation, <see cref="BenchmarkResult" /> construction, error
///     translation, JIT-elision, and warmup progress emission.
/// </summary>
public sealed class BenchmarkRunner
{
    // ---------- JIT-elision sink ----------

    private static int _holeInt;
    private static long _holeLong;
    private static double _holeDouble;
    private readonly StopwatchClock _clock;

    public BenchmarkRunner() : this(StopwatchClock.WallClock)
    {
    }

    internal BenchmarkRunner(IClock clock)
    {
        _clock = StopwatchClock.Wrap(clock ?? throw new ArgumentNullException(nameof(clock)));
    }

    public static BenchmarkRunner Instance { get; } = new();

    public MeasurementOutcome Run(string name, Action body, RunSpec spec, CancellationToken ct = default)
        => RunSyncCore(name, spec, ct, body);

    public MeasurementOutcome Run<T>(string name, Func<T> body, RunSpec spec, CancellationToken ct = default)
        => RunSyncCore(name, spec, ct, () => Consume(body()));

    public Task<MeasurementOutcome> RunAsync(string name, Func<Task> body, RunSpec spec, CancellationToken ct = default)
        => RunAsyncCore(name, spec, ct, body);

    public Task<MeasurementOutcome> RunAsync<T>(string name, Func<Task<T>> body, RunSpec spec, CancellationToken ct = default)
        => RunAsyncCore(name, spec, ct, async () => { Consume(await body().ConfigureAwait(false)); });

    // ---------- Unified run cores ----------

    private MeasurementOutcome RunSyncCore(string name, RunSpec spec, CancellationToken ct, Action body)
    {
        var totalStartTimestamp = _clock.GetTimestamp();
        var options = spec.Options;
        var progress = spec.Progress;
        var observer = spec.Observer;

        // The single funnel every measurement in every mode passes through, so this is the one
        // place that can tell whether a requested runtime profile actually took effect. In a
        // child it always has; in the host it never can.
        RuntimeProfileEnvironment.EmitNotAppliedGuidanceOnce(options);

        NBenchmarkDiagnostics.ResetBenchmarkState();

        try
        {
            progress.OnWarmupStarting(name, PlannedWarmup(options)).GetAwaiter().GetResult();

            if (options.Iterations is 0)
            {
                RunFixedWarmupSync(body, spec, ct);
                var dryRun = BuildDryRunOutcome(name, spec, totalStartTimestamp);
                progress.OnWarmupCompleted(name).GetAwaiter().GetResult();
                NBenchmarkDiagnostics.RecordResult(dryRun.Result);
                observer.OnResult(dryRun.Result);
                return dryRun;
            }

            var adaptive = AdaptiveLoop.Run(name, body, spec, _clock, progress, observer, ct);
            var success = BuildSuccessOutcome(name, spec, totalStartTimestamp, adaptive);
            NBenchmarkDiagnostics.RecordResult(success.Result);
            observer.OnResult(success.Result);
            return success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errored = BuildErroredOutcome(name, spec, totalStartTimestamp, ex);
            NBenchmarkDiagnostics.RecordResult(errored.Result);
            observer.OnResult(errored.Result);
            return errored;
        }
    }

    private async Task<MeasurementOutcome> RunAsyncCore(string name, RunSpec spec, CancellationToken ct, Func<Task> body)
    {
        var totalStartTimestamp = _clock.GetTimestamp();
        var options = spec.Options;
        var progress = spec.Progress;
        var observer = spec.Observer;

        // The single funnel every measurement in every mode passes through, so this is the one
        // place that can tell whether a requested runtime profile actually took effect. In a
        // child it always has; in the host it never can.
        RuntimeProfileEnvironment.EmitNotAppliedGuidanceOnce(options);

        NBenchmarkDiagnostics.ResetBenchmarkState();

        try
        {
            await progress.OnWarmupStarting(name, PlannedWarmup(options)).ConfigureAwait(false);

            if (options.Iterations is 0)
            {
                await RunFixedWarmupAsync(body, spec, ct).ConfigureAwait(false);
                var dryRun = BuildDryRunOutcome(name, spec, totalStartTimestamp);
                await progress.OnWarmupCompleted(name).ConfigureAwait(false);
                NBenchmarkDiagnostics.RecordResult(dryRun.Result);
                observer.OnResult(dryRun.Result);
                return dryRun;
            }

            var adaptive = await AdaptiveLoop
                .RunAsync(name, body, spec, _clock, progress, observer, ct)
                .ConfigureAwait(false);

            var success = BuildSuccessOutcome(name, spec, totalStartTimestamp, adaptive);
            NBenchmarkDiagnostics.RecordResult(success.Result);
            observer.OnResult(success.Result);
            return success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errored = BuildErroredOutcome(name, spec, totalStartTimestamp, ex);
            NBenchmarkDiagnostics.RecordResult(errored.Result);
            observer.OnResult(errored.Result);
            return errored;
        }
    }

    // ---------- Dry-run warmup ----------

    // A dry-run still exercises any pinned warmup count once, so configuration smoke tests run the
    // body the way a measured run would. Auto warmup resolves to zero here (there is nothing to
    // measure against). Calibration is skipped, so K is 1.
    private static void RunFixedWarmupSync(Action body, RunSpec spec, CancellationToken ct)
    {
        var warmup = spec.Options.WarmupIterations ?? 0;

        for (var i = 0; i < warmup; i++)
        {
            ct.ThrowIfCancellationRequested();
            spec.IterationSetup?.Invoke();
            body();
            spec.IterationTeardown?.Invoke();
        }
    }

    private static async Task RunFixedWarmupAsync(Func<Task> body, RunSpec spec, CancellationToken ct)
    {
        var warmup = spec.Options.WarmupIterations ?? 0;

        for (var i = 0; i < warmup; i++)
        {
            ct.ThrowIfCancellationRequested();
            spec.IterationSetup?.Invoke();
            await body().ConfigureAwait(false);
            spec.IterationTeardown?.Invoke();
        }
    }

    // The warmup total for the progress callback: the pinned value, or 0 (indeterminate) when
    // warmup length is decided at runtime by the plateau rule. Progress UIs treat a non-positive
    // total as an indeterminate phase.
    private static int PlannedWarmup(MeasurementOptions options)
        => options.WarmupIterations ?? 0;

    private MeasurementOutcome BuildDryRunOutcome(string name, RunSpec spec, long totalStartTimestamp)
    {
        return OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            name,
            spec.ClassName,
            spec.Description,
            spec.IsBaseline,
            spec.Options,
            _clock.GetElapsedTime(totalStartTimestamp),
            TimeSpan.Zero,
            spec.Options.WarmupIterations ?? 0,
            null,
            spec.Categories);
    }

    private MeasurementOutcome BuildSuccessOutcome(
        string name,
        RunSpec spec,
        long totalStartTimestamp,
        AdaptiveResult adaptive)
    {
        if (spec.Options.AutoTune.CapBehavior == AutoTuneCapBehavior.Error
            && (adaptive.Diagnostic.SampleStop == SampleStopReason.WallClockCap
                || adaptive.Diagnostic.SampleStop == SampleStopReason.GraceCapExhausted
                || adaptive.Diagnostic.WarmupStop == WarmupStopReason.WallClockCap))
        {
            var ex = new InvalidOperationException(
                FormatCapError(adaptive.Diagnostic, spec.Options.AutoTune.MaxTuningTime));

            return BuildErroredOutcome(name, spec, totalStartTimestamp, ex);
        }

        // When the adaptive loop auto-switched the outlier detector (Phase 0 jitter calibration
        // detected a noisy host), build an effective options record with the switched detector
        // pinned so the stats pipeline trims with MAD and the result's OutlierDetector name
        // reflects the switch. The effective options are local to this path; the caller's spec
        // is never mutated.
        var effectiveOptions = adaptive.EffectiveOutlierDetector is { } switchedDetector
            ? spec.Options with { OutlierDetector = switchedDetector }
            : spec.Options;

        // Per-sample GC deltas (Gen0+Gen1+Gen2), aligned 1:1 with the measured timings, let the
        // pipeline annotate which trimmed outliers coincided with a collection. Only built when GC
        // counts were actually collected.
        var perSampleGcCounts = BuildPerSampleGcCounts(adaptive, spec.Options.Diagnostics);

        var pipeline = StatsPipeline.Run(
            adaptive.PerOpTimings, adaptive.PerOpAllocations, effectiveOptions, perSampleGcCounts);
        var mergedWarnings = MergeWarnings(pipeline.Warnings, adaptive.Warnings);
        mergedWarnings = MergeWarnings(
            mergedWarnings,
            BuildMidBatchGcWarnings(effectiveOptions, adaptive.Diagnostic.OpsPerSample, pipeline.MeanAllocatedBytes));
        var diagnosticsResult = BuildDiagnosticsResult(adaptive, spec.Options.Diagnostics);
        var mergedPipeline = pipeline with { Warnings = mergedWarnings, DiagnosticsResult = diagnosticsResult };

        return OutcomeBuilder.Build(
            new RunOutcome.Success(mergedPipeline, adaptive.PerOpTimings),
            name,
            spec.ClassName,
            spec.Description,
            spec.IsBaseline,
            effectiveOptions,
            _clock.GetElapsedTime(totalStartTimestamp),
            adaptive.MeasuredDuration,
            adaptive.ResolvedWarmup,
            adaptive.Diagnostic,
            spec.Categories);
    }

    private static int[]? BuildPerSampleGcCounts(AdaptiveResult adaptive, DiagnosticsOptions opts)
    {
        if (!opts.GcCollectionCounts || adaptive.PerOpDiagnostics is not { } diags || diags.Length == 0)
            return null;

        var counts = new int[diags.Length];

        for (var i = 0; i < diags.Length; i++)
        {
            counts[i] = diags[i].Gen0 + diags[i].Gen1 + diags[i].Gen2;
        }

        return counts;
    }

    private static DiagnosticsResult? BuildDiagnosticsResult(AdaptiveResult adaptive, DiagnosticsOptions opts)
    {
        if (!opts.Any)
            return null;

        var diags = adaptive.PerOpDiagnostics;
        var measuredOps = (long)adaptive.Diagnostic.ResolvedSamples * adaptive.Diagnostic.OpsPerSample;

        if (measuredOps <= 0)
            return null;

        long sumGen0 = 0, sumGen1 = 0, sumGen2 = 0;
        long sumCpuTicks = 0;

        if (diags is not null)
        {
            for (var i = 0; i < diags.Length; i++)
            {
                sumGen0 += diags[i].Gen0;
                sumGen1 += diags[i].Gen1;
                sumGen2 += diags[i].Gen2;
                sumCpuTicks += diags[i].CpuTimeTicks;
            }
        }

        var mode = opts.ToMode();

        return new DiagnosticsResult
        {
            Gen0Collections = opts.GcCollectionCounts ? sumGen0 : null,
            Gen1Collections = opts.GcCollectionCounts ? sumGen1 : null,
            Gen2Collections = opts.GcCollectionCounts ? sumGen2 : null,
            HeapCommittedBytes = adaptive.HeapInfo?.CommittedBytes,
            HeapFragmentedBytes = adaptive.HeapInfo?.FragmentedBytes,
            ExceptionCountPerOp = opts.Exceptions && adaptive.ExceptionCount.HasValue
                ? (double)adaptive.ExceptionCount.Value / measuredOps
                : null,
            CpuTimeNsPerOp = opts.CpuTime
                ? sumCpuTicks * 100.0 / measuredOps
                : null,
            CpuWallRatio = opts.CpuTime && adaptive.MeasuredDuration.Ticks > 0
                ? (double)sumCpuTicks / adaptive.MeasuredDuration.Ticks
                : null,
            Mode = mode,
        };
    }

    private static string FormatCapError(AutoTuneDiagnostic diagnostic, TimeSpan maxTuningTime)
    {
        // Measurement can hit either the base cap or the grace ceiling; both mean it ran out of
        // time. Warmup only ever hits its budget share (reported as WallClockCap).
        var warmupCapped = diagnostic.WarmupStop == WarmupStopReason.WallClockCap;
        var measurementCapped = diagnostic.SampleStop
            is SampleStopReason.WallClockCap or SampleStopReason.GraceCapExhausted;

        var phase = warmupCapped && measurementCapped
            ? "Warmup and measurement"
            : measurementCapped
                ? "Measurement"
                : "Warmup";

        return $"{phase} stopped at the wall-clock tuning cap ({BenchmarkFormatter.FormatDuration(maxTuningTime)}) "
               + "before reaching the requested precision. "
               + "Use --autotune-cap-behavior warn to accept under-sampled results, "
               + "or increase --max-tuning-time / pin --iterations / pin --warmup.";
    }

    // Under a per-iteration forced GC (the Independent profile), the collection runs once per
    // sample - before the K-batch and outside the timed window. When K > 1 and the body allocates,
    // allocations accumulate across the batch and a GC can land mid-batch, inside the timed window,
    // reintroducing exactly the pause the forced GC was meant to exclude. Auto-K is now allowed
    // under Independent, so surface this so the user can pin K = 1 when it matters.
    private static IReadOnlyList<string> BuildMidBatchGcWarnings(
        MeasurementOptions options, int opsPerSample, long? meanAllocatedBytes)
    {
        if (options.ForceGcBeforeEachIteration && opsPerSample > 1 && meanAllocatedBytes is > 0)
        {
            return
            [
                $"ops-per-sample K = {opsPerSample} and the body allocates "
                + $"~{BenchmarkFormatter.FormatAlloc(meanAllocatedBytes.Value)}/op under a per-iteration forced GC; "
                + "a collection can occur inside a timed K-batch. Pin --ops-per-sample 1 to keep each timed sample GC-free.",
            ];
        }

        return [];
    }

    private static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> pipelineWarnings, IReadOnlyList<string> adaptiveWarnings)
    {
        if (pipelineWarnings.Count == 0)
            return adaptiveWarnings;

        if (adaptiveWarnings.Count == 0)
            return pipelineWarnings;

        var merged = new List<string>(pipelineWarnings.Count + adaptiveWarnings.Count);
        merged.AddRange(pipelineWarnings);
        merged.AddRange(adaptiveWarnings);
        return merged;
    }

    private MeasurementOutcome BuildErroredOutcome(string name, RunSpec spec, long totalStartTimestamp, Exception ex)
    {
        return OutcomeBuilder.Build(
            new RunOutcome.Errored(ex),
            name,
            spec.ClassName,
            spec.Description,
            spec.IsBaseline,
            spec.Options,
            _clock.GetElapsedTime(totalStartTimestamp),
            TimeSpan.Zero,
            spec.Options.WarmupIterations ?? 0,
            null,
            spec.Categories);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(int value) => Volatile.Write(ref _holeInt, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(long value) => Volatile.Write(ref _holeLong, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(double value) => Volatile.Write(ref _holeDouble, value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(bool value) => Volatile.Write(ref _holeInt, value ? 1 : 0);

    /// <summary>
    ///     Generic JIT-elision sink. Writes <paramref name="value" /> into a
    ///     static <typeparamref name="T" /> field of the closed generic
    ///     <see cref="JitSinkCache{T}" /> - no boxing for value types, no
    ///     allocation for reference types. Used directly by the runner's
    ///     <c>Consume(body())</c> call sites; the same delegate is exposed
    ///     through <see cref="GetResultConsumer{T}" /> for the discovery pipeline.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume<T>(T value) => JitSinkCache<T>._hole = value;

    /// <summary>
    ///     Returns a typed result-consumer delegate that writes a value to the
    ///     JIT-elision sink without boxing. The delegate is built once per
    ///     closed generic <typeparamref name="T" /> and cached for the lifetime
    ///     of the process. Used by the discovery pipeline for
    ///     <c>Func&lt;Task&lt;T&gt;&gt;</c>-returning benchmark bodies.
    /// </summary>
    public static Action<T> GetResultConsumer<T>() =>
        JitSinkCache<T>.Instance;

    /// <summary>
    ///     The last value the generic sink received for <typeparamref name="T" />.
    /// </summary>
    /// <remarks>
    ///     A test seam. Whether a body's return value actually reaches the sink is otherwise
    ///     unobservable, and it is the difference between measuring the body and measuring an empty
    ///     loop the JIT was free to delete - so it needs an assertion, not an assumption.
    /// </remarks>
    internal static T? LastConsumed<T>() => JitSinkCache<T>._hole;

    private static class JitSinkCache<T>
    {
        public static readonly Action<T> Instance = CreateTypedConsumer();

        // Closed-generic static field - one instance per T. Reference-type T
        // stores a reference (no alloc); value-type T stores the value
        // directly (no box on assign). This sink is best-effort anti-elision
        // state and not a synchronization primitive.
        public static T? _hole;

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

            // For every other T (reference types, structs, decimals, enums,
            // etc.), this static delegate writes directly to the closed-generic
            // JIT sink field with no boxing and no runtime expression compile.
            return static v => Consume(v);
        }
    }
}
