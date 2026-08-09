using NBenchmark.Observers;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkSuiteAutoAttachObserverTests : IDisposable
{
    public BenchmarkSuiteAutoAttachObserverTests()
    {
        ObserverRegistry.Reset();
    }

    public void Dispose() => ObserverRegistry.Reset();

    [Fact]
    public async Task AutoAttached_Observer_Receives_Sample_And_Result_Events()
    {
        var capturing = new CapturingAutoObserver("capture");
        ObserverRegistry.RegisterAutoAttach("capture", "Captures events", () => capturing);

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(2, capturing.Results.Count);
        Assert.Contains(capturing.Results, r => r.Name == "a");
        Assert.Contains(capturing.Results, r => r.Name == "b");
        Assert.NotEmpty(capturing.Samples);
    }

    [Fact]
    public async Task Throwing_AutoAttached_Observer_Is_Caught_And_Run_Returns_Normally()
    {
        var thrown = new ThrowingAutoObserver("throws", new InvalidOperationException("boom"));
        var captureAfter = new CapturingAutoObserver("after");
        ObserverRegistry.RegisterAutoAttach("throws", "Throws", () => thrown);
        ObserverRegistry.RegisterAutoAttach("after", "After", () => captureAfter);

        var results = await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.NotEmpty(captureAfter.Results);
    }

    [Fact]
    public async Task Multiple_AutoAttached_Observers_All_Fire_In_Registration_Order()
    {
        var phaseOrder = new List<string>();
        ObserverRegistry.RegisterAutoAttach("first", "1", () => new OrderTrackingObserver("first", phaseOrder));
        ObserverRegistry.RegisterAutoAttach("second", "2", () => new OrderTrackingObserver("second", phaseOrder));
        ObserverRegistry.RegisterAutoAttach("third", "3", () => new OrderTrackingObserver("third", phaseOrder));

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        // Each SuiteCompleted phase event is dispatched to all three observers in
        // registration order. The suite emits exactly one SuiteCompleted sentinel.
        Assert.Equal(["first", "second", "third"], phaseOrder);
    }

    [Fact]
    public async Task Passing_Observer_Cli_For_AutoAttached_Does_Not_Fire_It_Twice()
    {
        // The suite does not parse CLI args; programmatic .WithObserver with the same Name
        // suppresses the auto-attached entry. This is the programmatic-attach dedup path.
        var programmatic = new CapturingAutoObserver("auto");
        var autoFactoryCallCount = 0;

        ObserverRegistry.RegisterAutoAttach(
            "auto",
            "auto",
            () =>
            {
                Interlocked.Increment(ref autoFactoryCallCount);
                return new CapturingAutoObserver("auto");
            });

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithObserver(programmatic)
            .RunAsync();

        // The auto-attached factory is not invoked because the programmatic observer has
        // Name = "auto" and suppresses it.
        Assert.Equal(0, autoFactoryCallCount);
        Assert.Single(programmatic.Results);
    }

    [Fact]
    public async Task Programmatic_Named_Observer_Suppresses_AutoAttached_Of_Same_Name()
    {
        var programmatic = new CapturingAutoObserver("studio");
        var autoFactoryCallCount = 0;

        ObserverRegistry.RegisterAutoAttach(
            "studio",
            "auto",
            () =>
            {
                Interlocked.Increment(ref autoFactoryCallCount);
                return new CapturingAutoObserver("studio");
            });

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithObserver(programmatic)
            .RunAsync();

        Assert.Equal(0, autoFactoryCallCount);
        Assert.Single(programmatic.Results);
    }

    [Fact]
    public async Task Programmatic_Anonymous_Observer_Does_Not_Suppress_AutoAttached()
    {
        var programmatic = new AnonymousObserver();
        var auto = new CapturingAutoObserver("auto");
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => auto);

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithObserver(programmatic)
            .RunAsync();

        // Both fire: the anonymous (Name = null) does not suppress the named auto-attached.
        Assert.NotEmpty(programmatic.Samples);
        Assert.NotEmpty(auto.Samples);
    }

    [Fact]
    public async Task AutoAttached_Observer_Fires_Alongside_Explicit_Programmatic_Observer()
    {
        var explicitObserver = new AnonymousObserver();
        var auto = new CapturingAutoObserver("auto");
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => auto);

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithObserver(explicitObserver)
            .RunAsync();

        Assert.NotEmpty(explicitObserver.Samples);
        Assert.NotEmpty(auto.Samples);
        Assert.Single(auto.Results);
    }

    [Fact]
    public async Task Resolved_Observer_Is_Disposed_After_RunAsync_Success_Path()
    {
        var auto = new CountingAutoObserver("disposable");
        ObserverRegistry.RegisterAutoAttach("disposable", "Disposable", () => auto);

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(1, auto.DisposeCallCount);
    }

    [Fact]
    public async Task SuiteCompleted_Sentinel_Emitted_Exactly_Once_On_Success_Path()
    {
        var capturing = new CapturingAutoObserver("capture");
        ObserverRegistry.RegisterAutoAttach("capture", "Captures events", () => capturing);

        await new BenchmarkSuite("suite").WithRequireIsolation(false)
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var suiteCompletedEvents = capturing.Phases
            .Where(p => p.Phase == MeasurementPhase.SuiteCompleted)
            .ToList();

        Assert.Single(suiteCompletedEvents);
        Assert.True(suiteCompletedEvents[0].Succeeded);
        Assert.Equal(string.Empty, suiteCompletedEvents[0].BenchmarkName);
        Assert.Equal(PhaseTransition.Completed, suiteCompletedEvents[0].Transition);
    }

    [Fact]
    public async Task SuiteCompleted_Sentinel_Emitted_With_Succeeded_False_On_Suite_Setup_Exception()
    {
        var capturing = new CapturingAutoObserver("capture");
        ObserverRegistry.RegisterAutoAttach("capture", "Captures events", () => capturing);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new BenchmarkSuite("crashing-suite").WithRequireIsolation(false)
                .WithSuiteSetup(() => throw new InvalidOperationException("setup boom"))
                .Add("a", () => { })
                .WithWarmup(0)
                .WithIterations(1)
                .WithOutlierMode(OutlierMode.None)
                .RunAsync();
        });

        var suiteCompletedEvents = capturing.Phases
            .Where(p => p.Phase == MeasurementPhase.SuiteCompleted)
            .ToList();

        // The finally emits the sentinel with Succeeded = false because the success path
        // did not reach its emit before the exception.
        Assert.Single(suiteCompletedEvents);
        Assert.False(suiteCompletedEvents[0].Succeeded);
    }

    [Fact]
    public async Task AutoAttached_Observer_Disposed_On_Suite_Setup_Exception()
    {
        var auto = new CountingAutoObserver("disposable");
        ObserverRegistry.RegisterAutoAttach("disposable", "Disposable", () => auto);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await new BenchmarkSuite("crashing-suite").WithRequireIsolation(false)
                .WithSuiteSetup(() => throw new InvalidOperationException("setup boom"))
                .Add("a", () => { })
                .WithWarmup(0)
                .WithIterations(1)
                .WithOutlierMode(OutlierMode.None)
                .RunAsync();
        });

        // The using disposes the observer even on the exception path.
        Assert.Equal(1, auto.DisposeCallCount);
    }
}
