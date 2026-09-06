using NBenchmark;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class EnvelopeTests
{
    [Fact]
    public async Task FromDiscovered_Sync_Void_Method_Runs_To_Completion()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(PublicBenchmarks), nameof(PublicBenchmarks.ReturnsNothing));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(PublicBenchmarks), () => new PublicBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.Equal($"{nameof(PublicBenchmarks)}.ReturnsNothing", envelope.Name);
        Assert.False(outcome.Result.Errored);
        Assert.Equal(envelope.Name, outcome.Result.Name);
    }

    [Fact]
    public async Task FromDiscovered_Sync_Returning_Method_Captures_Result()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(PublicBenchmarks), nameof(PublicBenchmarks.ReturnsInt));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(PublicBenchmarks), () => new PublicBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public async Task FromDiscovered_Async_NonGeneric_RunAsync_Can_Be_Awaited()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(AsyncBenchmarks), nameof(AsyncBenchmarks.ReturnsTask));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(AsyncBenchmarks), () => new AsyncBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public async Task FromDiscovered_Async_Generic_With_ResultConsumer_Consumes_Result()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(AsyncBenchmarks), nameof(AsyncBenchmarks.ReturnsValueAsync));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(AsyncBenchmarks), () => new AsyncBenchmarks());

        var outcome = await envelope.RunAsync(MinimalSpec(), CancellationToken.None);

        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public void FromDiscovered_Prefixes_Name_With_Class_And_Reads_Baseline()
    {
        var method = TestReflectionHelper.ResolveMethod(typeof(BaselineBenchmarks), nameof(BaselineBenchmarks.Fast));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(BaselineBenchmarks), () => new BaselineBenchmarks());

        Assert.Equal($"{nameof(BaselineBenchmarks)}.Fast", envelope.Name);
        Assert.True(envelope.IsBaseline);
        Assert.Equal("the baseline", envelope.Description);
    }

    [Fact]
    public async Task FromDiscovered_Applies_PerMethod_Iteration_And_Warmup_Overrides()
    {
        var instance = new AttributeOverrideBenchmarks();
        var method = TestReflectionHelper.ResolveMethod(typeof(AttributeOverrideBenchmarks), nameof(AttributeOverrideBenchmarks.Work));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(AttributeOverrideBenchmarks), () => instance);

        var outcome = await envelope.RunAsync(new RunSpec
        {
            Options = new MeasurementOptions
            {
                Samples = 5,
                WarmupSamples = 1,
                OpsPerSample = 1,
                OutlierMode = OutlierMode.None,
            },
        }, CancellationToken.None);

        Assert.Equal(2, outcome.Result.SampleCount);
        Assert.Equal(3, outcome.Result.WarmupSamples);
        Assert.Equal(5, instance.InvocationCount);
    }

    [Fact]
    public async Task FromDiscovered_DryRun_Spec_Does_Not_Apply_PerMethod_Overrides()
    {
        var instance = new AttributeOverrideBenchmarks();
        var method = TestReflectionHelper.ResolveMethod(typeof(AttributeOverrideBenchmarks), nameof(AttributeOverrideBenchmarks.Work));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(AttributeOverrideBenchmarks), () => instance);

        var outcome = await envelope.RunAsync(new RunSpec
        {
            Options = new MeasurementOptions
            {
                Samples = 0,
                WarmupSamples = 0,
                OutlierMode = OutlierMode.None,
            },
        }, CancellationToken.None);

        Assert.Equal(0, outcome.Result.SampleCount);
        Assert.Equal(0, outcome.Result.WarmupSamples);
        Assert.Equal(0, instance.InvocationCount);
    }

    [Fact]
    public async Task FromDiscovered_Sync_Returning_Method_Consumes_ReturnValue()
    {
        var instance = new SyncReturningBenchmarks();
        var method = TestReflectionHelper.ResolveMethod(typeof(SyncReturningBenchmarks), nameof(SyncReturningBenchmarks.Compute));
        var envelope = BenchmarkEnvelope.FromDiscovered(method, nameof(SyncReturningBenchmarks), () => instance);

        var outcome = await envelope.RunAsync(new RunSpec
        {
            Options = new MeasurementOptions
            {
                Samples = 3,
                WarmupSamples = 1,
                OpsPerSample = 1,
                OutlierMode = OutlierMode.None,
            },
        }, CancellationToken.None);

        Assert.False(outcome.Result.Errored);

        Assert.True(instance.InvocationCount > 0,
            "Sync-returning method was not invoked. JIT may have elided the computation.");
    }

    private static RunSpec MinimalSpec() => new()
    {
        Options = new MeasurementOptions { Samples = 0, WarmupSamples = 0 },
    };

    private sealed class BaselineBenchmarks
    {
        [Benchmark(Baseline = true, Description = "the baseline")]
        public int Fast() => 1;
    }

    private sealed class AttributeOverrideBenchmarks
    {
        public int InvocationCount;

        [Benchmark(Samples = 2, WarmupSamples = 3)]
        public void Work() => InvocationCount++;
    }

    private sealed class SyncReturningBenchmarks
    {
        public int InvocationCount;

        [Benchmark(Samples = 3, WarmupSamples = 1)]
        public int Compute()
        {
            InvocationCount++;
            return 42;
        }
    }
}

internal static class TestReflectionHelper
{
    public static BenchmarkMethodDefinition ResolveMethod(Type type, string methodName)
    {
        var discoverer = new BenchmarkDiscoverer();
        var suite = discoverer.Discover(type.Assembly).First(s => s.Type == type);
        return suite.Benchmarks.First(m => m.Method.Name == methodName);
    }
}
