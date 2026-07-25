using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class WarmupCurveRecorderTests
{
    [Fact]
    public void RetainsEveryBatchUntilCapacity()
    {
        var recorder = new WarmupCurveRecorder(batchSize: 8);

        for (var i = 0; i < WarmupCurveRecorder.Capacity; i++)
            recorder.Add(i);

        var curve = recorder.ToArray();

        Assert.Equal(WarmupCurveRecorder.Capacity, curve.Length);
        Assert.Equal(0, curve[0]);
        Assert.Equal(WarmupCurveRecorder.Capacity - 1, curve[^1]);
        // No decimation yet, so the spacing is still one batch.
        Assert.Equal(8, recorder.SampleInterval);
    }

    [Fact]
    public void NeverExceedsCapacityHoweverLongWarmupRuns()
    {
        var recorder = new WarmupCurveRecorder(batchSize: 8);

        // A fast body can warm up for tens of thousands of samples; memory must stay bounded.
        for (var i = 0; i < 100_000; i++)
            recorder.Add(i);

        Assert.True(recorder.ToArray().Length <= WarmupCurveRecorder.Capacity);
    }

    [Fact]
    public void DecimationDoublesTheReportedSampleInterval()
    {
        var recorder = new WarmupCurveRecorder(batchSize: 4);

        // One decimation pass happens on the batch after the buffer first fills.
        for (var i = 0; i <= WarmupCurveRecorder.Capacity; i++)
            recorder.Add(i);

        // Spacing is batchSize * stride, and the stride has doubled once.
        Assert.Equal(8, recorder.SampleInterval);
    }

    [Fact]
    public void RetainedPointsStayEvenlySpaced()
    {
        var recorder = new WarmupCurveRecorder(batchSize: 1);

        // Feed the batch ordinal as the value so each retained point identifies itself.
        for (var i = 0; i < WarmupCurveRecorder.Capacity * 5; i++)
            recorder.Add(i);

        var curve = recorder.ToArray();
        Assert.True(curve.Length >= 2);

        // With batchSize 1 the interval is the stride, and every retained ordinal must be a
        // multiple of it — that is what keeps the curve's shape readable after decimation.
        var interval = recorder.SampleInterval;
        for (var i = 0; i < curve.Length; i++)
        {
            Assert.Equal(0, curve[i] % interval);
            Assert.Equal(i * interval, curve[i]);
        }
    }

    [Fact]
    public void PreservesTheShapeOfADecayCurve()
    {
        var recorder = new WarmupCurveRecorder(batchSize: 1);

        // A tier-up-shaped decay: a fast drop, then flat. After decimation the first retained point
        // must still be far above the last, or the curve has lost the story it exists to tell.
        for (var i = 0; i < 10_000; i++)
            recorder.Add(Math.Max(100.0, 1000.0 - i * 5.0));

        var curve = recorder.ToArray();

        Assert.True(curve[0] > curve[^1] * 5, $"expected a visible decay, got {curve[0]} → {curve[^1]}");
        Assert.Equal(100.0, curve[^1]);
    }

    [Fact]
    public void IsEmptyBeforeAnyBatchCompletes()
    {
        Assert.Empty(new WarmupCurveRecorder(batchSize: 8).ToArray());
    }
}
