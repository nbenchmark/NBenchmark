using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class MeasurementObserverEventsTests
{
    [Fact]
    public void NullMeasurementObserver_Is_Singleton_And_NoOp()
    {
        Assert.Same(NullMeasurementObserver.Instance, NullMeasurementObserver.Instance);

        var observer = NullMeasurementObserver.Instance;
        observer.OnPhase(new MeasurementPhaseEvent("b", MeasurementPhase.Jitter, PhaseTransition.Starting));
        observer.OnSample(new SampleEvent("b", 0, 1.0, 1, 0, false));
        observer.OnDetector(new DetectorStateEvent("b", MeasurementPhase.Measurement, 0, 0.0, 0.0, 0.0, 1));
        observer.OnResult(null!);
    }

    [Fact]
    public void SampleEvent_Has_Value_Equality()
    {
        var a = new SampleEvent("bench", 3, 12.5, 4, 80L, false);
        var b = new SampleEvent("bench", 3, 12.5, 4, 80L, false);
        var c = new SampleEvent("bench", 3, 12.5, 4, 80L, true);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(a == b);
        Assert.False(a == c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void MeasurementPhaseEvent_Defaults_Are_Null()
    {
        var e = new MeasurementPhaseEvent("bench", MeasurementPhase.Measurement, PhaseTransition.Starting);

        Assert.Null(e.JitterMetric);
        Assert.False(e.DetectorSwitched);
        Assert.Null(e.ResolvedK);
        Assert.Null(e.ResolvedWarmup);
        Assert.Null(e.WarmupStop);
        Assert.Null(e.SampleStop);
    }

    [Fact]
    public void MeasurementPhaseEvent_Named_Arguments_Populate_Correctly()
    {
        var e = new MeasurementPhaseEvent(
            "b", MeasurementPhase.Jitter, PhaseTransition.Completed,
            0.21, true);

        Assert.Equal("b", e.BenchmarkName);
        Assert.Equal(MeasurementPhase.Jitter, e.Phase);
        Assert.Equal(PhaseTransition.Completed, e.Transition);
        Assert.Equal(0.21, e.JitterMetric);
        Assert.True(e.DetectorSwitched);
        Assert.Null(e.ResolvedK);
    }

    [Fact]
    public void DetectorStateEvent_Has_Value_Equality()
    {
        var a = new DetectorStateEvent("b", MeasurementPhase.Measurement, 32, 100.0, 1.5, 0.02, 4);
        var b = new DetectorStateEvent("b", MeasurementPhase.Measurement, 32, 100.0, 1.5, 0.02, 4);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void RunSpec_Observer_Defaults_To_NullMeasurementObserver()
    {
        var spec = new RunSpec();

        Assert.Same(NullMeasurementObserver.Instance, spec.Observer);
    }

    [Fact]
    public void RunSpec_Observer_Is_Init_Only()
    {
        var captured = new RecordingObserver();
        var spec = new RunSpec { Observer = captured };

        Assert.Same(captured, spec.Observer);
    }

    private sealed class RecordingObserver : IMeasurementObserver
    {
        public void OnPhase(in MeasurementPhaseEvent e)
        {
        }

        public void OnSample(in SampleEvent e)
        {
        }

        public void OnDetector(in DetectorStateEvent e)
        {
        }

        public void OnResult(BenchmarkResult result)
        {
        }
    }
}
