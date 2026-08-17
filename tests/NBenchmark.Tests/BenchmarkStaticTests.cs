using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Simple mode's measurement contract - names, sample counts, raw samples - independent of where
///     the measurement ran.
/// </summary>
/// <remarks>
///     Every options record here sets <c>RequireIsolation = false</c>, which is not incidental. This
///     test project deliberately deploys no <c>nbworker</c> beside itself, so every measurement it
///     takes is a refused one; the tests that care about that fact live in
///     <see cref="Workers.RequiredIsolationTests" /> and <see cref="Workers.SimpleModeIsolationTests" />
///     and assert the throw. These are about the numbers, so they opt out of the gate rather than
///     asserting an exception they are not testing.
/// </remarks>
public class BenchmarkStaticTests
{
    [Fact]
    public void Run_Executes_Sync_Benchmark()
    {
        var result = Benchmark.Run(() => Thread.SpinWait(100),
            new MeasurementOptions
            {
                WarmupIterations = 1,
                Iterations = 10,
                OutlierMode = OutlierMode.None,
                RequireIsolation = false,
                // This test is about the sample count contract, not interference rejection - a
                // real OS preemption during a genuinely-timed run on a noisy host would otherwise
                // drop this below 10 and make the assertion flaky.
                Interference = InterferenceOptions.Disabled,
            });

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
            new MeasurementOptions { WarmupIterations = 1, Iterations = 10, OutlierMode = OutlierMode.None, RequireIsolation = false });

        Assert.True(result.Median > 0);
        Assert.False(result.Errored);
    }

    [Fact]
    public async Task RunAsync_Executes_Async_Benchmark()
    {
        var result = await Benchmark.RunAsync(async () => { await Task.Delay(1); },
            new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None, RequireIsolation = false });

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
        }, new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None, RequireIsolation = false });

        Assert.True(result.Median > 0);
        Assert.False(result.Errored);
    }

    [Fact]
    public void RunRaw_Returns_RawSamples()
    {
        var outcome = Benchmark.RunRaw(() => Thread.SpinWait(100),
            new MeasurementOptions { WarmupIterations = 1, Iterations = 20, OutlierMode = OutlierMode.None, RequireIsolation = false });

        Assert.Equal(20, outcome.RawSamples.Length);
        Assert.True(outcome.Result.Median > 0);
    }

    [Fact]
    public void Run_With_Custom_Name()
    {
        var result = Benchmark.Run(() => Thread.SpinWait(100),
            new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None, RequireIsolation = false },
            "CustomName");

        Assert.Equal("CustomName", result.Name);
    }
}
