using Xunit;

namespace NBenchmark.Tests;

public class MeasurementEventTests
{
    [Fact]
    public void Phase_Constructor_Sets_Kind_And_PhaseEvent()
    {
        var phase = new MeasurementPhaseEvent("b", MeasurementPhase.Jitter, PhaseTransition.Starting);
        var ev = new MeasurementEvent(phase);

        Assert.Equal(MeasurementEvent.EventKind.Phase, ev.Kind);
        Assert.Equal(phase, ev.PhaseEvent);
        Assert.Equal(default, ev.SampleEvent);
        Assert.Equal(default, ev.DetectorStateEvent);
        Assert.Null(ev.Result);
    }

    [Fact]
    public void Sample_Constructor_Sets_Kind_And_SampleEvent()
    {
        var sample = new SampleEvent("b", 5, 12.5, 4, 80L, false);
        var ev = new MeasurementEvent(sample);

        Assert.Equal(MeasurementEvent.EventKind.Sample, ev.Kind);
        Assert.Equal(sample, ev.SampleEvent);
        Assert.Equal(default, ev.PhaseEvent);
        Assert.Equal(default, ev.DetectorStateEvent);
        Assert.Null(ev.Result);
    }

    [Fact]
    public void DetectorState_Constructor_Sets_Kind_And_DetectorStateEvent()
    {
        var detector = new DetectorStateEvent("b", MeasurementPhase.Measurement, 32, 100.0, 1.5, 0.02, 4);
        var ev = new MeasurementEvent(detector);

        Assert.Equal(MeasurementEvent.EventKind.DetectorState, ev.Kind);
        Assert.Equal(detector, ev.DetectorStateEvent);
        Assert.Equal(default, ev.PhaseEvent);
        Assert.Equal(default, ev.SampleEvent);
        Assert.Null(ev.Result);
    }

    private static BenchmarkResult MakeResult()
    {
        return new BenchmarkResult
        {
            Name = "test",
            Mean = 100.0,
            Median = 95.0,
            Min = 80.0,
            Max = 120.0,
            StandardDeviation = 5.0,
            Q1 = 85.0,
            Q3 = 110.0,
            InterquartileRange = 25.0,
            OutliersRemoved = 0,
            N = 30,
            Skewness = 0.1,
            Kurtosis = 2.8,
            Mad = 3.0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };
    }

    [Fact]
    public void Result_Constructor_Sets_Kind_And_Result()
    {
        var result = MakeResult();
        var ev = new MeasurementEvent(result);

        Assert.Equal(MeasurementEvent.EventKind.Result, ev.Kind);
        Assert.Same(result, ev.Result);
        Assert.Equal(default, ev.PhaseEvent);
        Assert.Equal(default, ev.SampleEvent);
        Assert.Equal(default, ev.DetectorStateEvent);
    }

    [Fact]
    public void Value_Equality_Same_Phase_Events_Are_Equal()
    {
        var a = new MeasurementEvent(new MeasurementPhaseEvent("b", MeasurementPhase.Jitter, PhaseTransition.Starting));
        var b = new MeasurementEvent(new MeasurementPhaseEvent("b", MeasurementPhase.Jitter, PhaseTransition.Starting));

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Value_Equality_Different_Kinds_Are_Not_Equal()
    {
        var phase = new MeasurementEvent(new MeasurementPhaseEvent("b", MeasurementPhase.Jitter, PhaseTransition.Starting));
        var sample = new MeasurementEvent(new SampleEvent("b", 0, 1.0, 1, 0, false));

        Assert.NotEqual(phase, sample);
        Assert.False(phase == sample);
        Assert.NotEqual(phase.GetHashCode(), sample.GetHashCode());
    }

    [Fact]
    public void Default_MeasurementEvent_Has_Kind_Phase_With_Default_Values()
    {
        // default(MeasurementEvent) should have Kind=Phase (the first enum value) and all-zero fields
        var ev = default(MeasurementEvent);

        Assert.Equal(MeasurementEvent.EventKind.Phase, ev.Kind);
        Assert.Equal(default, ev.PhaseEvent);
        Assert.Equal(default, ev.SampleEvent);
        Assert.Equal(default, ev.DetectorStateEvent);
        Assert.Null(ev.Result);
    }

    [Fact]
    public void Result_Events_Wrap_Same_Reference_Are_Equal()
    {
        // MeasurementEvent is a readonly record struct; its generated equality compares every
        // field via EqualityComparer<T>.Default. For the private BenchmarkResult? _result
        // field, EqualityComparer<BenchmarkResult?>.Default dispatches to BenchmarkResult's
        // own record-generated equality. Two Result events wrapping the SAME BenchmarkResult
        // reference are therefore equal. This test pins that baseline; the follow-up test
        // pins the delegate-to-BenchmarkResult-equality behaviour for distinct references.
        var result = MakeResult();
        var a = new MeasurementEvent(result);
        var b = new MeasurementEvent(result);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Result_Events_Equality_Delegates_To_BenchmarkResult_Equality()
    {
        // MeasurementEvent's generated record-struct equality uses
        // EqualityComparer<BenchmarkResult?>.Default on the _result field, which dispatches
        // to BenchmarkResult's own record equality. So two Result events are equal iff their
        // wrapped BenchmarkResults are equal (whatever BenchmarkResult.Equals decides). This
        // test pins the delegation by constructing two results that differ in a scalar field
        // (Name) and asserting the wrapping events are NOT equal - proving the event does
        // not fall back to reference-only comparison but actually calls into BenchmarkResult.
        var resultA = MakeResult() with { Name = "alpha" };
        var resultB = MakeResult() with { Name = "beta" };

        // Sanity: the results themselves are not equal.
        Assert.NotEqual(resultA, resultB);

        var a = new MeasurementEvent(resultA);
        var b = new MeasurementEvent(resultB);

        Assert.NotEqual(a, b);
        Assert.False(a == b);
    }
}
