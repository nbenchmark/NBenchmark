using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NBenchmark.Diagnostics;

internal static class NBenchmarkDiagnostics
{
    internal static readonly ActivitySource ActivitySource = new("NBenchmark");
    internal static readonly Meter Meter = new("NBenchmark");

    private static readonly Histogram<double> HSampleDuration =
        Meter.CreateHistogram<double>("nbenchmark.sample.duration", "ns/op", "Per-op sample duration in nanoseconds");

    private static readonly Histogram<long> HAllocBytes =
        Meter.CreateHistogram<long>("nbenchmark.alloc.bytes_per_op", "B/op", "Per-op allocation delta in bytes");

    private static readonly Counter<long> COutliersRemoved =
        Meter.CreateCounter<long>("nbenchmark.outliers.removed", "samples", "Outlier samples removed");

    private static readonly Counter<long> CJitterSwitches =
        Meter.CreateCounter<long>("nbenchmark.jitter.detector_switches", "switches", "Outlier-detector auto-switches triggered by jitter");

    private static readonly Counter<long> CGcGen0 =
        Meter.CreateCounter<long>("nbenchmark.gc.gen0", "collections", "Generation 0 GC collections during measurement");

    private static readonly Counter<long> CGcGen1 =
        Meter.CreateCounter<long>("nbenchmark.gc.gen1", "collections", "Generation 1 GC collections during measurement");

    private static readonly Counter<long> CGcGen2 =
        Meter.CreateCounter<long>("nbenchmark.gc.gen2", "collections", "Generation 2 GC collections during measurement");

    private static double _ciHalfWidth;
    private static double _jitterMetric;
    private static double _meanPerOpNs;
    private static double _opsPerSecond;
    private static long _sampleCount;
    private static long _lastOutliersRemoved;

    private static Activity? _currentActivity;
    private static Activity? _currentRunActivity;
    private static Activity? _currentSuiteActivity;

    static NBenchmarkDiagnostics()
    {
        Meter.CreateObservableGauge(
            "nbenchmark.ci.relative_half_width",
            () => _ciHalfWidth,
            "ratio",
            "CI relative half-width of the running mean");

        Meter.CreateObservableGauge(
            "nbenchmark.jitter.metric",
            () => _jitterMetric,
            "ratio",
            "Host jitter metric (MAD / median of calibration probes)");

        Meter.CreateObservableGauge(
            "nbenchmark.sample.mean_per_op",
            () => _meanPerOpNs,
            "ns/op",
            "Running mean per-op duration from the measurement phase");

        Meter.CreateObservableGauge(
            "nbenchmark.ops_per_second",
            () => _opsPerSecond,
            "ops/s",
            "Running operations per second (1e9 / mean per-op ns) from the measurement phase");

        Meter.CreateObservableGauge(
            "nbenchmark.samples.count",
            () => _sampleCount,
            "samples",
            "Running sample count");

        Meter.CreateObservableGauge(
            "nbenchmark.outliers.removed_total",
            () => _lastOutliersRemoved,
            "samples",
            "Total outliers removed");
    }

    internal static void RecordSample(double perOpNs, long allocDelta)
    {
        HSampleDuration.Record(perOpNs);
        if (allocDelta >= 0)
            HAllocBytes.Record(allocDelta);
        _sampleCount++;
    }

    internal static void RecordDetectorState(double ciHalfWidth, double mean)
    {
        _ciHalfWidth = ciHalfWidth;
        _meanPerOpNs = mean;
        _opsPerSecond = mean > 0 ? 1e9 / mean : 0;
    }

    internal static void RecordJitterMetric(double metric)
    {
        _jitterMetric = metric;
    }

    internal static void RecordOutliersRemoved(long count)
    {
        _lastOutliersRemoved += count;
        COutliersRemoved.Add(count);
    }

    internal static void ResetBenchmarkState()
    {
        // Only the phase-span activity is per-run state owned by the runner.
        // _currentRunActivity / _currentSuiteActivity are owned by SuiteRunner and the
        // suite/harness entry points respectively; resetting them here would wipe the
        // activity the caller has just opened for this benchmark before its body runs.
        _currentActivity?.Dispose();
        _currentActivity = null;
        _ciHalfWidth = 0;
        _jitterMetric = 0;
        _meanPerOpNs = 0;
        _opsPerSecond = 0;
        _sampleCount = 0;
    }

    internal static void RecordJitterSwitch()
    {
        CJitterSwitches.Add(1);
    }

    internal static void OnSuiteStarting(string suiteName, int benchmarkCount, string? profile = null, string? runtime = null, int? seed = null, string? runOrder = null)
    {
        _currentSuiteActivity?.Dispose();

        var activity = ActivitySource.StartActivity(
            "benchmark.suite",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("nbenchmark.suite.name", suiteName);
            activity.SetTag("nbenchmark.suite.benchmark_count", benchmarkCount);
            if (profile is not null)
                activity.SetTag("nbenchmark.profile", profile);
            if (runtime is not null)
                activity.SetTag("nbenchmark.runtime", runtime);
            if (seed.HasValue)
                activity.SetTag("nbenchmark.seed", seed.Value);
            if (runOrder is not null)
                activity.SetTag("nbenchmark.run_order", runOrder);

            // Resource attributes (commit SHA, branch, CI run id, host, runtime) are stamped on
            // the root span so a backend can join every child span and metric onto the run
            // without each emit point repeating them. Read once per process; cached afterwards.
            foreach (var (key, value) in TelemetryResource.Attributes)
            {
                activity.SetTag(key, value);
            }

            _currentSuiteActivity = activity;
        }
    }

    internal static void OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
    {
        var activity = _currentSuiteActivity;
        _currentSuiteActivity = null;

        if (activity is null)
            return;

        activity.SetTag("nbenchmark.suite.result_count", results.Count);
        activity.Dispose();
    }

    internal static void OnBenchmarkRunStarting(string benchmarkName, string className, bool isBaseline, IReadOnlyList<BenchmarkParameter>? parameters = null)
    {
        _currentRunActivity?.Dispose();

        var activity = ActivitySource.StartActivity(
            "benchmark.run",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("nbenchmark.name", benchmarkName);
            activity.SetTag("nbenchmark.class", className);
            if (isBaseline)
                activity.SetTag("nbenchmark.baseline", true);
            if (parameters is { Count: > 0 })
                activity.SetTag("nbenchmark.parameter_set", string.Join(",", parameters.Select(p => $"{p.Name}={p.Value}")));
            _currentRunActivity = activity;
        }
    }

    internal static void OnBenchmarkRunCompleted(BenchmarkResult result)
    {
        var activity = _currentRunActivity;
        _currentRunActivity = null;

        if (activity is null)
            return;

        activity.SetTag("nbenchmark.result.median_ns", result.Median);
        activity.SetTag("nbenchmark.result.mean_ns", result.Mean);
        activity.SetTag("nbenchmark.result.sample_count", result.N);
        activity.SetTag("nbenchmark.result.outliers_removed", result.OutliersRemoved);

        activity.Dispose();
    }

    internal static void OnPhaseStarting(string benchmarkName, MeasurementPhase phase)
    {
        _currentActivity?.Dispose();

        var activity = ActivitySource.StartActivity(
            $"nbenchmark.phase.{phase.ToString().ToLowerInvariant()}",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("nbenchmark.benchmark.name", benchmarkName);
            activity.SetTag("nbenchmark.phase", phase.ToString());
            _currentActivity = activity;
        }
    }

    internal static void OnPhaseCompleted(
        string benchmarkName,
        MeasurementPhase phase,
        SampleStopReason? sampleStop = null,
        WarmupStopReason? warmupStop = null,
        int resolvedK = 0,
        int resolvedWarmup = 0,
        double? jitterMetric = null,
        bool? detectorSwitched = null,
        double? achievedCiWidth = null,
        double? ciTarget = null)
    {
        var activity = _currentActivity;
        _currentActivity = null;

        if (activity is null)
            return;

        activity.SetTag("nbenchmark.benchmark.name", benchmarkName);
        activity.SetTag("nbenchmark.phase", phase.ToString());

        if (sampleStop.HasValue)
            activity.SetTag("nbenchmark.sample_stop_reason", sampleStop.Value.ToString());
        if (warmupStop.HasValue)
            activity.SetTag("nbenchmark.warmup_stop_reason", warmupStop.Value.ToString());
        if (resolvedK > 0)
            activity.SetTag("nbenchmark.resolved_k", resolvedK);
        if (resolvedWarmup > 0)
            activity.SetTag("nbenchmark.resolved_warmup", resolvedWarmup);
        if (jitterMetric.HasValue)
            activity.SetTag("nbenchmark.jitter_metric", jitterMetric.Value);
        if (detectorSwitched.HasValue)
            activity.SetTag("nbenchmark.detector_switched", detectorSwitched.Value);

        // Span events: discrete annotations on the phase span that explain *why* a phase ended.
        // A trace UI renders these as markers on the flame-graph row, making the autotune
        // decision visible at a glance.
        if (detectorSwitched is true)
            activity.AddEvent(new ActivityEvent("detector.switched", tags: new ActivityTagsCollection
            {
                { "nbenchmark.from", "IqrFence" },
                { "nbenchmark.to", "MedianAbsoluteDeviation" },
                { "nbenchmark.jitter_metric", jitterMetric ?? 0 },
            }));

        if (warmupStop == WarmupStopReason.Settled)
            activity.AddEvent(new ActivityEvent("warmup.plateau_reached"));

        if (sampleStop == SampleStopReason.CiTargetMet && achievedCiWidth.HasValue && ciTarget.HasValue)
            activity.AddEvent(new ActivityEvent("measurement.ci_target_met", tags: new ActivityTagsCollection
            {
                { "nbenchmark.achieved_ci_width", achievedCiWidth.Value },
                { "nbenchmark.ci_target", ciTarget.Value },
            }));

        if (sampleStop == SampleStopReason.WallClockCap || warmupStop == WarmupStopReason.WallClockCap)
            activity.AddEvent(new ActivityEvent("phase.cap_hit"));

        activity.Dispose();
    }

    internal static void RecordResult(BenchmarkResult result)
    {
        var outliers = result.OutliersRemoved;
        RecordOutliersRemoved(outliers);

        // GC collection counters: emitted from the post-run DiagnosticsResult so the counter
        // reflects the measurement-phase delta (DiagnosticMeter computes Gen0/1/2 as
        // after - before across the measured loop).
        if (result.Diagnostics is { } diag)
        {
            if (diag.Gen0Collections is { } gen0 && gen0 > 0)
                CGcGen0.Add(gen0);
            if (diag.Gen1Collections is { } gen1 && gen1 > 0)
                CGcGen1.Add(gen1);
            if (diag.Gen2Collections is { } gen2 && gen2 > 0)
                CGcGen2.Add(gen2);
        }
    }
}
