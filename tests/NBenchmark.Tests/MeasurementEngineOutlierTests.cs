using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class MeasurementEngineOutlierTests
{
    [Fact]
    public async Task IqrFence_Outlier_Mode_Removes_Outliers()
    {
        var options = new MeasurementOptions
        {
            WarmupIterations = 1,
            Iterations = 100,
            OutlierMode = OutlierMode.IqrFence,
        };

        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            options
        );

        Assert.True(outcome.Result.MeasuredIterations <= 100);
        Assert.True(outcome.Result.MeasuredIterations > 0);
        Assert.Equal(100, outcome.RawSamples.Length);
    }

    [Fact]
    public async Task RemoveBoth5Percent_Outlier_Mode_Trims_Both_Ends()
    {
        var options = new MeasurementOptions
        {
            WarmupIterations = 1,
            Iterations = 100,
            OutlierMode = OutlierMode.RemoveTop5PercentAndBottom5Percent,
        };

        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            options
        );

        Assert.Equal(90, outcome.Result.MeasuredIterations);
        Assert.Equal(100, outcome.RawSamples.Length);
    }

    [Fact]
    public async Task None_Outlier_Mode_Keeps_All_Samples()
    {
        var options = new MeasurementOptions
        {
            WarmupIterations = 1,
            Iterations = 50,
            OutlierMode = OutlierMode.None,
        };

        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            options
        );

        Assert.Equal(50, outcome.Result.MeasuredIterations);
        Assert.Equal(50, outcome.RawSamples.Length);
    }

    [Fact]
    public async Task Iteration_Teardown_Runs_Each_Iteration()
    {
        var teardownCount = 0;
        Action teardown = () => Interlocked.Increment(ref teardownCount);

        await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            new MeasurementOptions { WarmupIterations = 2, Iterations = 5 },
            iterationTeardown: teardown
        );

        Assert.Equal(7, teardownCount);
    }

    [Fact]
    public void Sync_Measure_With_Allocations_Measures_Memory()
    {
        var outcome = MeasurementEngine.MeasureSync(
            "test",
            () => { _ = new byte[1024]; },
            new MeasurementOptions
            {
                WarmupIterations = 1,
                Iterations = 10,
                MeasureAllocations = true,
                OutlierMode = OutlierMode.None,
            }
        );

        Assert.NotNull(outcome.Result.MeanAllocatedBytes);
        Assert.True(outcome.Result.MeanAllocatedBytes >= 1024);
    }

    [Fact]
    public void Sync_Measure_Result_Has_Valid_Timestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var outcome = MeasurementEngine.MeasureSync(
            "test",
            () => Thread.SpinWait(100),
            new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None }
        );
        var after = DateTimeOffset.UtcNow;

        Assert.True(outcome.Result.RunAt >= before);
        Assert.True(outcome.Result.RunAt <= after);
    }

    [Fact]
    public void Sync_Measure_Result_Has_Positive_Duration()
    {
        var outcome = MeasurementEngine.MeasureSync(
            "test",
            () => Thread.SpinWait(100),
            new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None }
        );

        Assert.True(outcome.Result.TotalDuration > TimeSpan.Zero);
    }
}