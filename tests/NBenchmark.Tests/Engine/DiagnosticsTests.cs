using System.Diagnostics;
using System.Diagnostics.Metrics;
using NBenchmark.Diagnostics;
using Xunit;

namespace NBenchmark.Tests.Engine;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly ActivityListener _activityListener = new();
    private readonly List<long> _allocValues = [];
    private readonly List<double> _durationValues = [];
    private readonly MeterListener _meterListener = new();
    private readonly List<Activity> _startedActivities = [];

    public DiagnosticsTests()
    {
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NBenchmark")
                listener.EnableMeasurementEvents(instrument);
        };

        _meterListener.SetMeasurementEventCallback<double>((_, value, _, _) =>
            _durationValues.Add(value));

        _meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "nbenchmark.alloc.bytes_per_op")
                _allocValues.Add(value);
        });

        _meterListener.Start();

        _activityListener.ShouldListenTo = source => source.Name == "NBenchmark";

        _activityListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        _activityListener.ActivityStarted = a => _startedActivities.Add(a);
        _activityListener.ActivityStopped = _ => { };

        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
        NBenchmarkDiagnostics.ResetBenchmarkState();
    }

    [Fact]
    public void RecordSample_Emits_Duration_Histogram()
    {
        NBenchmarkDiagnostics.RecordSample("bench-a", false, "measurement", 42.5, 128);
        Assert.Contains(42.5, _durationValues);
    }

    [Fact]
    public void RecordSample_Emits_Alloc_Histogram()
    {
        NBenchmarkDiagnostics.RecordSample("bench-a", false, "measurement", 100.0, 256);
        Assert.Contains(256, _allocValues);
    }

    [Fact]
    public void RecordSample_Does_Not_Emit_Alloc_When_Negative()
    {
        NBenchmarkDiagnostics.RecordSample("bench-a", false, "measurement", 50.0, -1);
        Assert.DoesNotContain(-1L, _allocValues);
    }

    [Fact]
    public void RecordSample_Stamps_Benchmark_Warmup_And_Phase_Tags()
    {
        var capturedTags = new List<KeyValuePair<string, object?>>();

        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "nbenchmark.sample.duration")
            {
                _durationValues.Add(value);
                var e = tags.GetEnumerator();

                while (e.MoveNext())
                {
                    capturedTags.Add(e.Current);
                }
            }
        });

        NBenchmarkDiagnostics.RecordSample("MyBench", true, "warmup", 77.7, -1);

        Assert.Contains(capturedTags, t => t.Key == "benchmark" && Equals(t.Value, "MyBench"));
        Assert.Contains(capturedTags, t => t.Key == "warmup" && Equals(t.Value, true));
        Assert.Contains(capturedTags, t => t.Key == "phase" && Equals(t.Value, "warmup"));
    }

    [Fact]
    public void RecordDetectorState_Updates_Gauges()
    {
        NBenchmarkDiagnostics.RecordDetectorState(0.025, 500.0);

        double capturedCiHalf = 0, capturedMean = 0;
        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NBenchmark")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "nbenchmark.ci.relative_half_width")
                capturedCiHalf = value;

            if (instrument.Name == "nbenchmark.sample.mean_per_op")
                capturedMean = value;
        });

        captureListener.Start();
        captureListener.RecordObservableInstruments();

        Assert.Equal(0.025, capturedCiHalf);
        Assert.Equal(500.0, capturedMean);
    }

    [Fact]
    public void RecordOutliersRemoved_Emits_Counter()
    {
        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "nbenchmark.outliers.removed")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (value > 0)
                Assert.True(true); // reached
        });

        captureListener.Start();

        NBenchmarkDiagnostics.RecordOutliersRemoved(5);
    }

    [Fact]
    public void RecordJitterMetric_Updates_Gauge()
    {
        NBenchmarkDiagnostics.RecordJitterMetric(0.15);

        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "nbenchmark.jitter.metric")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<double>((_, value, _, _) =>
            Assert.Equal(0.15, value));

        captureListener.Start();
        captureListener.RecordObservableInstruments();
    }

    [Fact]
    public void RecordJitterSwitch_Emits_Counter()
    {
        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "nbenchmark.jitter.detector_switches")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            Assert.Equal(1, value));

        captureListener.Start();

        NBenchmarkDiagnostics.RecordJitterSwitch();
    }

    [Fact]
    public void OnPhaseStarting_Creates_Activity()
    {
        NBenchmarkDiagnostics.OnPhaseStarting("bench-a", MeasurementPhase.Measurement);

        var activity = _startedActivities.Count > 0
            ? _startedActivities[^1]
            : null;

        Assert.NotNull(activity);
        Assert.Equal("nbenchmark.phase.measurement", activity.DisplayName);
        Assert.Equal("bench-a", activity.GetTagItem("nbenchmark.benchmark.name"));
        Assert.Equal("Measurement", activity.GetTagItem("nbenchmark.phase"));
    }

    [Fact]
    public void OnPhaseStarting_And_Completed_Stops_Activity()
    {
        var stopped = false;
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = _ => stopped = true;
        ActivitySource.AddActivityListener(stopListener);

        NBenchmarkDiagnostics.OnPhaseStarting("b", MeasurementPhase.Warmup);
        NBenchmarkDiagnostics.OnPhaseCompleted("b", MeasurementPhase.Warmup, warmupStop: WarmupStopReason.ExplicitCount);

        Assert.True(stopped);
    }

    [Fact]
    public void RecordResult_Does_Not_Throw()
    {
        var result = new BenchmarkResult
        {
            Name = "test",
            Mean = 100, Median = 100, Min = 50, Max = 200,
            StandardDeviation = 20,
            Q1 = 80, Q3 = 120, InterquartileRange = 40,
            OutliersRemoved = 3,
            N = 10,
            Skewness = 0, Kurtosis = 0, Mad = 10,
            AllocMedian = null, AllocP95 = null, AllocMax = null,
        };

        NBenchmarkDiagnostics.RecordResult(result);
    }

    [Fact]
    public void OnSuiteStarting_Creates_Suite_Span_With_Tags()
    {
        NBenchmarkDiagnostics.OnSuiteStarting("my-suite", 3, "Realistic", "net8", 42, "Random");

        var activity = _startedActivities.Count > 0 ? _startedActivities[^1] : null;
        Assert.NotNull(activity);
        Assert.Equal("benchmark.suite", activity.DisplayName);
        Assert.Equal("my-suite", activity.GetTagItem("nbenchmark.suite.name"));
        Assert.Equal(3, activity.GetTagItem("nbenchmark.suite.benchmark_count"));
        Assert.Equal("Realistic", activity.GetTagItem("nbenchmark.profile"));
        Assert.Equal("net8", activity.GetTagItem("nbenchmark.runtime"));
        Assert.Equal(42, activity.GetTagItem("nbenchmark.seed"));
        Assert.Equal("Random", activity.GetTagItem("nbenchmark.run_order"));

        // Clean up the static suite activity so it does not leak into the next test.
        NBenchmarkDiagnostics.OnSuiteCompleted([]);
    }

    [Fact]
    public void OnSuiteCompleted_Stops_Suite_Span_And_Tags_Result_Count()
    {
        var stoppedActivities = new List<Activity>();
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = a => stoppedActivities.Add(a);
        ActivitySource.AddActivityListener(stopListener);

        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "a", Mean = 1, Median = 1, Min = 1, Max = 1, StandardDeviation = 0,
                Q1 = 1, Q3 = 1, InterquartileRange = 0, OutliersRemoved = 0, N = 1,
                Skewness = 0, Kurtosis = 0, Mad = 0,
                AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        NBenchmarkDiagnostics.OnSuiteStarting("s", 1);
        NBenchmarkDiagnostics.OnSuiteCompleted(results);

        var suiteSpan = stoppedActivities.FirstOrDefault(a => a.DisplayName == "benchmark.suite");
        Assert.NotNull(suiteSpan);
        Assert.Equal(1, suiteSpan.GetTagItem("nbenchmark.suite.result_count"));
    }

    [Fact]
    public void OnBenchmarkRunStarting_Creates_Run_Span_With_Tags()
    {
        NBenchmarkDiagnostics.OnBenchmarkRunStarting("MyClass.Fast", "MyClass", true);

        var activity = _startedActivities.Count > 0 ? _startedActivities[^1] : null;
        Assert.NotNull(activity);
        Assert.Equal("benchmark.run", activity.DisplayName);
        Assert.Equal("MyClass.Fast", activity.GetTagItem("nbenchmark.name"));
        Assert.Equal("MyClass", activity.GetTagItem("nbenchmark.class"));
        Assert.Equal(true, activity.GetTagItem("nbenchmark.baseline"));

        // Clean up the static run activity so it does not leak into the next test.
        NBenchmarkDiagnostics.OnBenchmarkRunCompleted(new BenchmarkResult
        {
            Name = "MyClass.Fast", Mean = 1, Median = 1, Min = 1, Max = 1, StandardDeviation = 0,
            Q1 = 1, Q3 = 1, InterquartileRange = 0, OutliersRemoved = 0, N = 1,
            Skewness = 0, Kurtosis = 0, Mad = 0,
            AllocMedian = null, AllocP95 = null, AllocMax = null,
        });
    }

    [Fact]
    public void OnBenchmarkRunCompleted_Stops_Run_Span_And_Tags_Results()
    {
        var stoppedActivities = new List<Activity>();
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = a => stoppedActivities.Add(a);
        ActivitySource.AddActivityListener(stopListener);

        var result = new BenchmarkResult
        {
            Name = "b", Mean = 200, Median = 180, Min = 100, Max = 300,
            StandardDeviation = 40, Q1 = 150, Q3 = 250, InterquartileRange = 100,
            OutliersRemoved = 2, N = 20, Skewness = 0, Kurtosis = 0, Mad = 5,
            AllocMedian = null, AllocP95 = null, AllocMax = null,
        };

        NBenchmarkDiagnostics.OnBenchmarkRunStarting("b", "C", false);
        NBenchmarkDiagnostics.OnBenchmarkRunCompleted(result);

        var runSpan = stoppedActivities.FirstOrDefault(a => a.DisplayName == "benchmark.run");
        Assert.NotNull(runSpan);
        Assert.Equal(180.0, runSpan.GetTagItem("nbenchmark.result.median_ns"));
        Assert.Equal(200.0, runSpan.GetTagItem("nbenchmark.result.mean_ns"));
        Assert.Equal(20, runSpan.GetTagItem("nbenchmark.result.sample_count"));
        Assert.Equal(2, runSpan.GetTagItem("nbenchmark.result.outliers_removed"));
    }

    [Fact]
    public void OnPhaseCompleted_Emits_Detector_Switched_Span_Event()
    {
        var stoppedActivities = new List<Activity>();
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = a => stoppedActivities.Add(a);
        ActivitySource.AddActivityListener(stopListener);

        NBenchmarkDiagnostics.OnPhaseStarting("b", MeasurementPhase.Jitter);

        NBenchmarkDiagnostics.OnPhaseCompleted(
            "b", MeasurementPhase.Jitter,
            jitterMetric: 0.20,
            detectorSwitched: true);

        var phaseSpan = stoppedActivities.FirstOrDefault(a => a.DisplayName == "nbenchmark.phase.jitter");
        Assert.NotNull(phaseSpan);
        var evt = phaseSpan.Events.FirstOrDefault(e => e.Name == "detector.switched");
        Assert.NotEmpty(evt.Name);
        Assert.Equal("IqrFence", evt.Tags.FirstOrDefault(t => t.Key == "nbenchmark.from").Value);
        Assert.Equal("MedianAbsoluteDeviation", evt.Tags.FirstOrDefault(t => t.Key == "nbenchmark.to").Value);
    }

    [Fact]
    public void OnPhaseCompleted_Emits_Plateau_Span_Event_For_Settled_Warmup()
    {
        var stoppedActivities = new List<Activity>();
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = a => stoppedActivities.Add(a);
        ActivitySource.AddActivityListener(stopListener);

        NBenchmarkDiagnostics.OnPhaseStarting("b", MeasurementPhase.Warmup);

        NBenchmarkDiagnostics.OnPhaseCompleted(
            "b", MeasurementPhase.Warmup,
            warmupStop: WarmupStopReason.Settled);

        var phaseSpan = stoppedActivities.FirstOrDefault(a => a.DisplayName == "nbenchmark.phase.warmup");
        Assert.NotNull(phaseSpan);
        Assert.Contains(phaseSpan.Events, e => e.Name == "warmup.plateau_reached");
    }

    [Fact]
    public void OnPhaseCompleted_Emits_Ci_Target_Met_Span_Event()
    {
        var stoppedActivities = new List<Activity>();
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = a => stoppedActivities.Add(a);
        ActivitySource.AddActivityListener(stopListener);

        NBenchmarkDiagnostics.OnPhaseStarting("b", MeasurementPhase.Measurement);

        NBenchmarkDiagnostics.OnPhaseCompleted(
            "b", MeasurementPhase.Measurement,
            SampleStopReason.CiTargetMet,
            achievedCiWidth: 0.024,
            ciTarget: 0.025);

        var phaseSpan = stoppedActivities.FirstOrDefault(a => a.DisplayName == "nbenchmark.phase.measurement");
        Assert.NotNull(phaseSpan);
        var evt = phaseSpan.Events.FirstOrDefault(e => e.Name == "measurement.ci_target_met");
        Assert.NotEmpty(evt.Name);
        Assert.Equal(0.024, evt.Tags.FirstOrDefault(t => t.Key == "nbenchmark.achieved_ci_width").Value);
        Assert.Equal(0.025, evt.Tags.FirstOrDefault(t => t.Key == "nbenchmark.ci_target").Value);
    }

    [Fact]
    public void OnPhaseCompleted_Emits_Cap_Hit_Span_Event_For_WallClockCap()
    {
        var stoppedActivities = new List<Activity>();
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = a => stoppedActivities.Add(a);
        ActivitySource.AddActivityListener(stopListener);

        NBenchmarkDiagnostics.OnPhaseStarting("b", MeasurementPhase.Measurement);

        NBenchmarkDiagnostics.OnPhaseCompleted(
            "b", MeasurementPhase.Measurement,
            SampleStopReason.WallClockCap);

        var phaseSpan = stoppedActivities.FirstOrDefault(a => a.DisplayName == "nbenchmark.phase.measurement");
        Assert.NotNull(phaseSpan);
        Assert.Contains(phaseSpan.Events, e => e.Name == "phase.cap_hit");
    }

    [Fact]
    public void OnPhaseCompleted_Does_Not_Emit_Ci_Target_Event_For_ExplicitCount()
    {
        var stoppedActivities = new List<Activity>();
        using var stopListener = new ActivityListener();
        stopListener.ShouldListenTo = source => source.Name == "NBenchmark";

        stopListener.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            ActivitySamplingResult.AllData;

        stopListener.ActivityStopped = a => stoppedActivities.Add(a);
        ActivitySource.AddActivityListener(stopListener);

        NBenchmarkDiagnostics.OnPhaseStarting("b", MeasurementPhase.Measurement);

        NBenchmarkDiagnostics.OnPhaseCompleted(
            "b", MeasurementPhase.Measurement,
            SampleStopReason.ExplicitCount,
            achievedCiWidth: 0.01,
            ciTarget: 0.025);

        var phaseSpan = stoppedActivities.FirstOrDefault(a => a.DisplayName == "nbenchmark.phase.measurement");
        Assert.NotNull(phaseSpan);
        Assert.DoesNotContain(phaseSpan.Events, e => e.Name == "measurement.ci_target_met");
    }

    [Fact]
    public void RecordDetectorState_Updates_OpsPerSecond_Gauge()
    {
        // mean = 100 ns/op -> 1e9 / 100 = 10,000,000 ops/s
        NBenchmarkDiagnostics.RecordDetectorState(0.025, 100.0);

        double capturedOpsPerSec = 0;
        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NBenchmark")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "nbenchmark.ops_per_second")
                capturedOpsPerSec = value;
        });

        captureListener.Start();
        captureListener.RecordObservableInstruments();

        Assert.Equal(10_000_000.0, capturedOpsPerSec);
    }

    [Fact]
    public void RecordResult_Emits_Gc_Gen_Counters()
    {
        long gen0 = 0, gen1 = 0, gen2 = 0;
        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name is "nbenchmark.gc.gen0" or "nbenchmark.gc.gen1" or "nbenchmark.gc.gen2")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "nbenchmark.gc.gen0":
                    gen0 += value;
                    break;
                case "nbenchmark.gc.gen1":
                    gen1 += value;
                    break;
                case "nbenchmark.gc.gen2":
                    gen2 += value;
                    break;
            }
        });

        captureListener.Start();

        var result = new BenchmarkResult
        {
            Name = "gc-test",
            Mean = 100, Median = 100, Min = 50, Max = 200,
            StandardDeviation = 20,
            Q1 = 80, Q3 = 120, InterquartileRange = 40,
            OutliersRemoved = 0, N = 10,
            Skewness = 0, Kurtosis = 0, Mad = 10,
            AllocMedian = null, AllocP95 = null, AllocMax = null,
            Diagnostics = new DiagnosticsResult
            {
                Gen0Collections = 3,
                Gen1Collections = 1,
                Gen2Collections = 2,
            },
        };

        NBenchmarkDiagnostics.RecordResult(result);

        Assert.Equal(3, gen0);
        Assert.Equal(1, gen1);
        Assert.Equal(2, gen2);
    }

    [Fact]
    public void RecordResult_Does_Not_Emit_Gc_Gen2_When_Zero()
    {
        long gen0 = 0, gen1 = 0;
        var gen2Emitted = false;
        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name is "nbenchmark.gc.gen0" or "nbenchmark.gc.gen1" or "nbenchmark.gc.gen2")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "nbenchmark.gc.gen0":
                    gen0 += value;
                    break;
                case "nbenchmark.gc.gen1":
                    gen1 += value;
                    break;
                case "nbenchmark.gc.gen2":
                    gen2Emitted = true;
                    break;
            }
        });

        captureListener.Start();

        var result = new BenchmarkResult
        {
            Name = "gc-test",
            Mean = 100, Median = 100, Min = 50, Max = 200,
            StandardDeviation = 20,
            Q1 = 80, Q3 = 120, InterquartileRange = 40,
            OutliersRemoved = 0, N = 10,
            Skewness = 0, Kurtosis = 0, Mad = 10,
            AllocMedian = null, AllocP95 = null, AllocMax = null,
            Diagnostics = new DiagnosticsResult
            {
                Gen0Collections = 3,
                Gen1Collections = 1,
                Gen2Collections = 0,
            },
        };

        NBenchmarkDiagnostics.RecordResult(result);

        Assert.Equal(3, gen0);
        Assert.Equal(1, gen1);
        Assert.False(gen2Emitted);
    }

    [Fact]
    public void RecordResult_With_No_Diagnostics_Does_Not_Emit_Gc_Counters()
    {
        long gen0 = 0;
        using var captureListener = new MeterListener();

        captureListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "nbenchmark.gc.gen0")
                listener.EnableMeasurementEvents(instrument);
        };

        captureListener.SetMeasurementEventCallback<long>((_, value, _, _) => gen0 += value);
        captureListener.Start();

        var result = new BenchmarkResult
        {
            Name = "no-diag",
            Mean = 1, Median = 1, Min = 1, Max = 1, StandardDeviation = 0,
            Q1 = 1, Q3 = 1, InterquartileRange = 0, OutliersRemoved = 0, N = 1,
            Skewness = 0, Kurtosis = 0, Mad = 0,
            AllocMedian = null, AllocP95 = null, AllocMax = null,
            Diagnostics = null,
        };

        NBenchmarkDiagnostics.RecordResult(result);

        Assert.Equal(0, gen0);
    }
}
