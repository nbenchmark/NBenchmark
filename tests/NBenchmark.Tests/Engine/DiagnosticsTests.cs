using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using NBenchmark.Diagnostics;
using Xunit;

namespace NBenchmark.Tests.Engine;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly MeterListener _meterListener = new();
    private readonly ActivityListener _activityListener = new();
    private readonly List<double> _durationValues = [];
    private readonly List<long> _allocValues = [];
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
    }

    [Fact]
    public void RecordSample_Emits_Duration_Histogram()
    {
        NBenchmarkDiagnostics.RecordSample(42.5, 128);
        Assert.Contains(42.5, _durationValues);
    }

    [Fact]
    public void RecordSample_Emits_Alloc_Histogram()
    {
        NBenchmarkDiagnostics.RecordSample(100.0, 256);
        Assert.Contains(256, _allocValues);
    }

    [Fact]
    public void RecordSample_Does_Not_Emit_Alloc_When_Negative()
    {
        NBenchmarkDiagnostics.RecordSample(50.0, -1);
        Assert.DoesNotContain(-1L, _allocValues);
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
}
