using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkStaticTests
{
    [Fact]
    public void Run_Executes_Sync_Benchmark()
    {
        var result = Benchmark.Run(() => Thread.SpinWait(100),
            new MeasurementOptions { WarmupIterations = 1, Iterations = 10, OutlierMode = OutlierMode.None });

        Assert.Equal("Benchmark", result.Name);
        Assert.True(result.Median > 0);
        Assert.Equal(10, result.MeasuredIterations);
        Assert.False(result.Errored);
    }

    [Fact]
    public void Run_With_Func_Executes_And_Measures()
    {
        var result = Benchmark.Run(() =>
            {
                Thread.SpinWait(10);
                return 42;
            },
            new MeasurementOptions { WarmupIterations = 1, Iterations = 10, OutlierMode = OutlierMode.None });

        Assert.True(result.Median > 0);
        Assert.False(result.Errored);
    }

    [Fact]
    public async Task RunAsync_Executes_Async_Benchmark()
    {
        var result = await Benchmark.RunAsync(async () => { await Task.Delay(1); },
            new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None });

        Assert.Equal("Benchmark", result.Name);
        Assert.True(result.Median > 0);
        Assert.False(result.Errored);
    }

    [Fact]
    public async Task RunAsync_With_Func_Executes_And_Measures()
    {
        var result = await Benchmark.RunAsync(async () =>
        {
            await Task.Yield();
            return 42;
        }, new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None });

        Assert.True(result.Median > 0);
        Assert.False(result.Errored);
    }

    [Fact]
    public void RunRaw_Returns_RawSamples()
    {
        var outcome = Benchmark.RunRaw(() => Thread.SpinWait(100),
            new MeasurementOptions { WarmupIterations = 1, Iterations = 20, OutlierMode = OutlierMode.None });

        Assert.Equal(20, outcome.RawSamples.Length);
        Assert.True(outcome.Result.Median > 0);
    }

    [Fact]
    public void Run_With_Custom_Name()
    {
        var result = Benchmark.Run(() => Thread.SpinWait(100),
            new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None },
            "CustomName");

        Assert.Equal("CustomName", result.Name);
    }
}
