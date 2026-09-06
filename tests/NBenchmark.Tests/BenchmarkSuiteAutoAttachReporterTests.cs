using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkSuiteAutoAttachReporterTests : IDisposable
{
    public BenchmarkSuiteAutoAttachReporterTests()
    {
        ReporterRegistry.Reset();
    }

    public void Dispose() => ReporterRegistry.Reset();

    [Fact]
    public async Task AutoAttached_Reporter_Receives_Result_List()
    {
        var capturing = new CapturingAutoReporter("capture");
        ReporterRegistry.RegisterAutoAttach("capture", "Captures results", _ => capturing);

        var results = await new BenchmarkSuite("suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmupSamples(1)
            .WithSamples(2)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.Equal(1, capturing.CallCount);
        Assert.Equal(2, capturing.Results.Count);
        var names = capturing.Results.Select(r => r.Name).ToList();
        Assert.Contains("a", names);
        Assert.Contains("b", names);
    }

    [Fact]
    public async Task AutoAttached_Reporter_Fires_After_Explicit_Reporters()
    {
        var order = new List<string>();
        var explicitReporter = new OrderTrackingReporter("explicit", order);
        ReporterRegistry.RegisterAutoAttach("auto", "Auto", _ => new OrderTrackingReporter("auto", order));

        await new BenchmarkSuite("suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .WithWarmupSamples(1)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithReporter(explicitReporter)
            .RunAsync();

        Assert.Equal(["explicit", "auto"], order);
    }

    [Fact]
    public async Task Throwing_AutoAttached_Reporter_Is_Caught_And_Run_Returns_Normally()
    {
        var thrown = new ThrowingAutoReporter("throws");
        var captureAfter = new CapturingAutoReporter("after");
        ReporterRegistry.RegisterAutoAttach("throws", "Throws", _ => thrown);
        ReporterRegistry.RegisterAutoAttach("after", "After", _ => captureAfter);

        var results = await new BenchmarkSuite("suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .WithWarmupSamples(1)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(1, thrown.CallCount);
        Assert.Equal(1, captureAfter.CallCount);
        Assert.Single(captureAfter.Results);

        // The run returned normally despite the throwing auto-attached reporter.
        Assert.Single(results);
    }

    [Fact]
    public async Task Multiple_AutoAttached_Reporters_All_Fire_In_Registration_Order()
    {
        var order = new List<string>();
        ReporterRegistry.RegisterAutoAttach("first", "1", _ => new OrderTrackingReporter("first", order));
        ReporterRegistry.RegisterAutoAttach("second", "2", _ => new OrderTrackingReporter("second", order));
        ReporterRegistry.RegisterAutoAttach("third", "3", _ => new OrderTrackingReporter("third", order));

        await new BenchmarkSuite("suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .WithWarmupSamples(1)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public async Task Explicit_Reporter_Instance_With_Same_Name_As_AutoAttached_Does_Not_Fire_Twice()
    {
        var callCount = 0;
        var explicitInstance = new CountingAutoReporter("dedup", () => callCount++);
        ReporterRegistry.RegisterAutoAttach("dedup", "auto", _ => new CountingAutoReporter("dedup", () => callCount++));

        await new BenchmarkSuite("suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .WithWarmupSamples(1)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithReporter(explicitInstance)
            .RunAsync();

        // One fire from the explicit instance; zero from the auto-attached one (dedup'd out by name).
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task AutoAttached_Reporter_Fires_Once_Per_RunAsync_Not_Per_Benchmark()
    {
        var callCount = 0;
        ReporterRegistry.RegisterAutoAttach("count", "Counts calls", _ => new CountingAutoReporter("count", () => callCount++));

        await new BenchmarkSuite("suite").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .Add("b", () => { })
            .Add("c", () => { })
            .WithWarmupSamples(1)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        // One fire for the whole run, not three.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task AutoAttached_Reporter_Fires_On_Isolated_Path()
    {
        var capturing = new CapturingAutoReporter("capture");
        ReporterRegistry.RegisterAutoAttach("capture", "Captures", _ => capturing);

        var results = await new BenchmarkSuite("isolated").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .WithWarmupSamples(1)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.Equal(1, capturing.CallCount);
        Assert.Single(capturing.Results);
    }
}
