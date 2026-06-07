using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class EnvelopeTests
{
    [Fact]
    public async Task FromDiscovered_Sync_Void_Method_Runs_To_Completion()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(PublicBenchmarks), nameof(PublicBenchmarks.ReturnsNothing));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(PublicBenchmarks), new PublicBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.Equal($"{nameof(PublicBenchmarks)}.ReturnsNothing", envelope.Name);
        Assert.False(outcome.Result.Errored);
        Assert.Equal(envelope.Name, outcome.Result.Name);
    }

    [Fact]
    public async Task FromDiscovered_Sync_Returning_Method_Captures_Result()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(PublicBenchmarks), nameof(PublicBenchmarks.ReturnsInt));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(PublicBenchmarks), new PublicBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public async Task FromDiscovered_Async_NonGeneric_RunAsync_Can_Be_Awaited()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(AsyncBenchmarks), nameof(AsyncBenchmarks.ReturnsTask));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(AsyncBenchmarks), new AsyncBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public async Task FromDiscovered_Async_Generic_With_ResultExtractor_Extracts_Result()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(AsyncBenchmarks), nameof(AsyncBenchmarks.ReturnsValueAsync));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(AsyncBenchmarks), new AsyncBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public void FromDiscovered_Prefixes_Name_With_Class_And_Reads_Baseline()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(BaselineBenchmarks), nameof(BaselineBenchmarks.Fast));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(BaselineBenchmarks), new BaselineBenchmarks());

        Assert.Equal($"{nameof(BaselineBenchmarks)}.Fast", envelope.Name);
        Assert.True(envelope.IsBaseline);
        Assert.Equal("the baseline", envelope.Description);
    }

    private static RunSpec MinimalSpec() => new()
    {
        Options = new MeasurementOptions { Iterations = 0, WarmupIterations = 0 },
    };

    private sealed class BaselineBenchmarks
    {
        [NBenchmark.Attributes.Benchmark(Baseline = true, Description = "the baseline")]
        public int Fast() => 1;
    }
}

internal static class TestReflectionHelper
{
    public static NBenchmark.Discovery.BenchmarkMethodDefinition ResolveMethod(Type type, string methodName)
    {
        var discoverer = new NBenchmark.Discovery.BenchmarkDiscoverer();
        var suite = discoverer.Discover(type.Assembly).First(s => s.Type == type);
        return suite.Benchmarks.First(m => m.Method.Name == methodName);
    }
}
