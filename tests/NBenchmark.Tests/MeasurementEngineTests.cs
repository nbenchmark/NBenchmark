using NBenchmark.Engine;

using Xunit;

namespace NBenchmark.Tests;

public class MeasurementEngineTests
{
    [Fact]
    public async Task Measures_Action_And_Returns_Positive_Timings()
    {
        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.Delay(1),
            new MeasurementOptions { WarmupIterations = 2, Iterations = 5, OutlierMode = OutlierMode.None }
        );
        var result = outcome.Result;

        Assert.True(result.Median > 0);
        Assert.True(result.Mean > 0);
        Assert.Equal(5, result.MeasuredIterations);
    }

    [Fact]
    public async Task Measures_Allocations_When_Enabled()
    {
        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => { _ = new byte[64 * 1024]; return Task.CompletedTask; },
            new MeasurementOptions
            {
                WarmupIterations = 2,
                Iterations = 10,
                MeasureAllocations = true,
            }
        );
        var result = outcome.Result;

        Assert.NotNull(result.MeanAllocatedBytes);
        Assert.True(result.MeanAllocatedBytes >= 1024);
    }

    [Fact]
    public async Task Outlier_Removal_Reduces_Sample_Count()
    {
        var options = new MeasurementOptions
        {
            WarmupIterations = 1,
            Iterations = 100,
            OutlierMode = OutlierMode.RemoveTop5Percent,
        };

        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            options
        );
        var result = outcome.Result;

        Assert.Equal(95, result.MeasuredIterations);
    }

    [Fact]
    public async Task Raw_Samples_Are_Preserved_Pre_Outlier_Removal()
    {
        var options = new MeasurementOptions
        {
            WarmupIterations = 1,
            Iterations = 100,
            OutlierMode = OutlierMode.RemoveTop5Percent,
        };

        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            options
        );

        Assert.Equal(100, outcome.RawSamples.Length);
        Assert.Equal(95, outcome.Result.MeasuredIterations);
    }

    [Fact]
    public async Task Iteration_Setup_Runs_Each_Iteration()
    {
        var callCount = 0;
        Action setup = () => Interlocked.Increment(ref callCount);

        await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            new MeasurementOptions { WarmupIterations = 3, Iterations = 10 },
            iterationSetup: setup
        );

        Assert.Equal(13, callCount);
    }

    [Fact]
    public void Sync_Measure_Returns_Positive_Timings()
    {
        var outcome = MeasurementEngine.MeasureSync(
            "test",
            () => Thread.SpinWait(1000),
            new MeasurementOptions { WarmupIterations = 2, Iterations = 10, OutlierMode = OutlierMode.None }
        );
        var result = outcome.Result;

        Assert.True(result.Median > 0);
        Assert.Equal(10, result.MeasuredIterations);
    }
}
