using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class ChannelMeasurementObserverTests
{
    private static BenchmarkResult MakeResult(string name)
    {
        return new BenchmarkResult
        {
            Name = name,
            MeanNs = 100.0,
            MedianNs = 95.0,
            MinNs = 80.0,
            MaxNs = 120.0,
            StandardDeviationNs = 5.0,
            Q1Ns = 85.0,
            Q3Ns = 110.0,
            InterquartileRangeNs = 25.0,
            OutliersRemoved = 0,
            SampleCount = 30,
            Skewness = 0.1,
            Kurtosis = 2.8,
            MedianAbsoluteDeviationNs = 3.0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };
    }

    [Fact]
    public void Constructor_Creates_Channel_With_Specified_Capacity()
    {
        var observer = new ChannelMeasurementObserver(64);

        Assert.NotNull(observer.Reader);
    }

    [Fact]
    public void WantsSampleStream_Is_True()
    {
        var observer = new ChannelMeasurementObserver();

        Assert.True(observer.WantsSampleStream);
    }

    [Fact]
    public void OnPhase_Writes_To_Channel()
    {
        var observer = new ChannelMeasurementObserver();
        var phase = new MeasurementPhaseEvent("b", MeasurementPhase.Jitter, PhaseTransition.Starting);

        observer.OnPhase(phase);

        Assert.True(observer.Reader.TryRead(out var ev));
        Assert.Equal(MeasurementEvent.EventKind.Phase, ev.Kind);
        Assert.Equal(phase, ev.PhaseEvent);
    }

    [Fact]
    public void OnSample_Writes_To_Channel()
    {
        var observer = new ChannelMeasurementObserver();
        var sample = new SampleEvent("b", 3, 12.5, 4, 80L, false);

        observer.OnSample(sample);

        Assert.True(observer.Reader.TryRead(out var ev));
        Assert.Equal(MeasurementEvent.EventKind.Sample, ev.Kind);
        Assert.Equal(sample, ev.SampleEvent);
    }

    [Fact]
    public void OnDetector_Writes_To_Channel()
    {
        var observer = new ChannelMeasurementObserver();
        var detector = new DetectorStateEvent("b", MeasurementPhase.Measurement, 32, 100.0, 1.5, 0.02, 4);

        observer.OnDetector(detector);

        Assert.True(observer.Reader.TryRead(out var ev));
        Assert.Equal(MeasurementEvent.EventKind.DetectorState, ev.Kind);
        Assert.Equal(detector, ev.DetectorStateEvent);
    }

    [Fact]
    public void OnResult_Writes_To_Channel()
    {
        var observer = new ChannelMeasurementObserver();
        var result = MakeResult("test");

        observer.OnResult(result);

        Assert.True(observer.Reader.TryRead(out var ev));
        Assert.Equal(MeasurementEvent.EventKind.Result, ev.Kind);
        Assert.Same(result, ev.Result);
    }

    [Fact]
    public void OnResult_Null_Result_Is_Dropped_Not_Enqueued()
    {
        // The interface contract allows a null result (errored pre-runner failure sites can
        // carry null, and NullMeasurementObserver.OnResult is exercised with null! in tests).
        // ChannelMeasurementObserver drops the event rather than enqueuing an ambiguous
        // Kind=Result / Result=null frame that a consumer would have to special-case.
        var observer = new ChannelMeasurementObserver();

        observer.OnResult(null!);

        Assert.False(observer.Reader.TryRead(out _));
    }

    [Fact]
    public void Events_Are_Delivered_In_Order()
    {
        var observer = new ChannelMeasurementObserver();

        observer.OnPhase(new MeasurementPhaseEvent("b", MeasurementPhase.Jitter, PhaseTransition.Starting));
        observer.OnSample(new SampleEvent("b", 0, 1.0, 1, 0, true));
        observer.OnDetector(new DetectorStateEvent("b", MeasurementPhase.Calibration, 1, 1.0, 0.0, 0.0, 1));
        observer.OnSample(new SampleEvent("b", 1, 1.0, 1, 0, false));
        observer.OnPhase(new MeasurementPhaseEvent("b", MeasurementPhase.Measurement, PhaseTransition.Completed, ResolvedK: 1));

        Assert.True(observer.Reader.TryRead(out var e0));
        Assert.Equal(MeasurementEvent.EventKind.Phase, e0.Kind);
        Assert.Equal(MeasurementPhase.Jitter, e0.PhaseEvent.Phase);

        Assert.True(observer.Reader.TryRead(out var e1));
        Assert.Equal(MeasurementEvent.EventKind.Sample, e1.Kind);
        Assert.True(e1.SampleEvent.Warmup);

        Assert.True(observer.Reader.TryRead(out var e2));
        Assert.Equal(MeasurementEvent.EventKind.DetectorState, e2.Kind);

        Assert.True(observer.Reader.TryRead(out var e3));
        Assert.Equal(MeasurementEvent.EventKind.Sample, e3.Kind);
        Assert.False(e3.SampleEvent.Warmup);

        Assert.True(observer.Reader.TryRead(out var e4));
        Assert.Equal(MeasurementEvent.EventKind.Phase, e4.Kind);
        Assert.Equal(MeasurementPhase.Measurement, e4.PhaseEvent.Phase);
        Assert.Equal(PhaseTransition.Completed, e4.PhaseEvent.Transition);
    }

    [Fact]
    public void Complete_Signals_Reader_Completion()
    {
        var observer = new ChannelMeasurementObserver(16);

        observer.OnSample(new SampleEvent("b", 0, 1.0, 1, 0, false));
        observer.Complete();

        Assert.True(observer.Reader.TryRead(out _));
        Assert.False(observer.Reader.TryRead(out _));
        Assert.True(observer.Reader.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void Overflow_Does_Not_Throw_Or_Block()
    {
        // Capacity 2 + DropOldest: after 4 writes the channel holds the 2 newest events
        // (ordinals 2 and 3). The test asserts both that the channel did not throw or block
        // AND that it dropped the oldest two (ordinals 0 and 1), so an implementation that
        // incorrectly dropped everything would fail.
        var observer = new ChannelMeasurementObserver(2);

        observer.OnSample(new SampleEvent("b", 0, 1.0, 1, 0, false));
        observer.OnSample(new SampleEvent("b", 1, 1.0, 1, 0, false));
        observer.OnSample(new SampleEvent("b", 2, 1.0, 1, 0, false));
        observer.OnSample(new SampleEvent("b", 3, 1.0, 1, 0, false));

        var readOrdinals = new List<int>();

        while (observer.Reader.TryRead(out var ev))
        {
            readOrdinals.Add(ev.SampleEvent.Ordinal);
        }

        Assert.Equal([2, 3], readOrdinals);
    }

    [Fact]
    public void Burst_Writes_All_Events_Within_Capacity()
    {
        var observer = new ChannelMeasurementObserver(1024);

        for (var i = 0; i < 100; i++)
        {
            observer.OnSample(new SampleEvent("b", i, i * 1.0, 1, 0, false));
        }

        var count = 0;

        while (observer.Reader.TryRead(out _))
        {
            count++;
        }

        Assert.Equal(100, count);
    }

    [Fact]
    public async Task Async_Consumer_Can_Drain_Channel()
    {
        var observer = new ChannelMeasurementObserver(256);
        var reader = observer.Reader;

        for (var i = 0; i < 50; i++)
        {
            observer.OnPhase(new MeasurementPhaseEvent("b", MeasurementPhase.Warmup, PhaseTransition.Completed, ResolvedWarmup: i));
        }

        observer.Complete();

        var count = 0;

        await foreach (var _ in reader.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(50, count);
    }

    [Fact]
    public async Task Dispose_Completes_Channel_So_Async_Consumer_Stops_Blocking()
    {
        // The harness/suite wrap the resolved observer in a `using`, so Dispose must
        // complete the channel writer. Without this, a reader awaiting ReadAllAsync
        // (or Reader.Completion) would hang indefinitely after the run finishes.
        var observer = new ChannelMeasurementObserver(16);
        var reader = observer.Reader;

        observer.OnSample(new SampleEvent("b", 0, 1.0, 1, 0, false));

        using (observer)
        {
            // The using disposes the observer, which completes the channel writer.
        }

        // The buffered event is still readable after dispose.
        Assert.True(reader.TryRead(out _));
        Assert.False(reader.TryRead(out _));

        // The reader's Completion task is now complete, so an async consumer does not hang.
        Assert.True(reader.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Dispose_Allows_Async_Consumer_To_Drain_And_Exit()
    {
        var observer = new ChannelMeasurementObserver(256);
        var reader = observer.Reader;

        for (var i = 0; i < 10; i++)
        {
            observer.OnSample(new SampleEvent("b", i, i * 1.0, 1, 0, false));
        }

        observer.Dispose();

        var count = 0;

        await foreach (var _ in reader.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(10, count);
    }
}
