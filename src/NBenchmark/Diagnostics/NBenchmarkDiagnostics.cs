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

    private static double _ciHalfWidth;
    private static double _jitterMetric;
    private static double _meanPerOpNs;
    private static long _sampleCount;
    private static long _lastOutliersRemoved;

    private static Activity? _currentActivity;

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
        _currentActivity?.Dispose();
        _currentActivity = null;
        _ciHalfWidth = 0;
        _jitterMetric = 0;
        _meanPerOpNs = 0;
        _sampleCount = 0;
    }

    internal static void RecordJitterSwitch()
    {
        CJitterSwitches.Add(1);
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
        bool? detectorSwitched = null)
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

        activity.Dispose();
    }

    internal static void RecordResult(BenchmarkResult result)
    {
        var outliers = result.OutliersRemoved;
        RecordOutliersRemoved(outliers);
    }
}
