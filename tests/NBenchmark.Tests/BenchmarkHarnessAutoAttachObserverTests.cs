using NBenchmark.Attributes;
using NBenchmark.Observers;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class BenchmarkHarnessAutoAttachObserverTests : IDisposable
{
    public BenchmarkHarnessAutoAttachObserverTests()
    {
        ObserverRegistry.Reset();
    }

    public void Dispose() => ObserverRegistry.Reset();

    [Fact]
    public async Task AutoAttached_Observer_Receives_Sample_And_Result_Events()
    {
        var capturing = new CapturingAutoObserver("capture");
        ObserverRegistry.RegisterAutoAttach("capture", "Captures events", () => capturing);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        // TestBenchmarks has two methods (Fast, FastBaseline); the observer sees both results.
        Assert.Equal(2, capturing.Results.Count);
        Assert.Contains(capturing.Results, r => r.Name == "TestBenchmarks.Fast");
        Assert.Contains(capturing.Results, r => r.Name == "TestBenchmarks.FastBaseline");

        // The observer receives sample events (one measured iteration each).
        Assert.NotEmpty(capturing.Samples);
    }

    [Fact]
    public async Task Throwing_AutoAttached_Observer_Is_Caught_And_Run_Returns_Normally()
    {
        var thrown = new ThrowingAutoObserver("throws", new InvalidOperationException("boom"));
        var captureAfter = new CapturingAutoObserver("after");
        ObserverRegistry.RegisterAutoAttach("throws", "Throws", () => thrown);
        ObserverRegistry.RegisterAutoAttach("after", "After", () => captureAfter);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        // The throwing observer's exceptions are caught by the composite; the run completes.
        // The "after" observer still receives events despite the throwing one.
        Assert.NotEmpty(captureAfter.Results);
    }

    [Fact]
    public async Task Multiple_AutoAttached_Observers_All_Fire_In_Registration_Order()
    {
        var phaseOrder = new List<string>();
        ObserverRegistry.RegisterAutoAttach("first", "1", () => new OrderTrackingObserver("first", phaseOrder));
        ObserverRegistry.RegisterAutoAttach("second", "2", () => new OrderTrackingObserver("second", phaseOrder));
        ObserverRegistry.RegisterAutoAttach("third", "3", () => new OrderTrackingObserver("third", phaseOrder));

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        // Each SuiteCompleted phase event is dispatched to all three observers in registration
        // order. The harness emits exactly one SuiteCompleted sentinel on the success path.
        Assert.Equal(["first", "second", "third"], phaseOrder);
    }

    [Fact]
    public async Task Passing_Observer_Cli_For_AutoAttached_Name_Resolves_And_Dedups_Auto_Attach()
    {
        // The contract: --observer <name> resolves an auto-attached observer (TryCreate
        // checks both lists), and the ResolveObserver dedup suppresses the auto-attached
        // entry of the same name so the observer fires once, not twice.
        var factoryCallCount = 0;

        ObserverRegistry.RegisterAutoAttach(
            "auto",
            "auto",
            () =>
            {
                Interlocked.Increment(ref factoryCallCount);
                return new CountingAutoObserver("auto");
            });

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create([
                    "--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1", "--observer", "auto",
                ])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        // TryCreate("auto") in BenchmarkHarness.Create resolves the auto-attached entry
        // (1 factory call). CliArgs.Parse validates via IsRegistered (no factory call).
        // The ResolveObserver dedup sees "auto" in _observers (from TryCreate) and
        // suppresses the auto-attached entry, so the factory is called exactly once total.
        Assert.Equal(1, factoryCallCount);
    }

    [Fact]
    public async Task Explicit_Register_And_AutoAttach_Different_Names_Both_Fire()
    {
        // The realistic composition: the user registers "explicit" explicitly via Register
        // and passes --observer explicit; a package auto-attaches "other". Both fire.
        var explicitObserver = new CapturingAutoObserver("explicit");

        ObserverRegistry.Register(
            "explicit",
            "explicit",
            () => explicitObserver);

        var auto = new CapturingAutoObserver("other");
        ObserverRegistry.RegisterAutoAttach("other", "auto", () => auto);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create([
                    "--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1", "--observer", "explicit",
                ])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        // The explicit "explicit" is resolved via TryCreate. The auto-attached "other"
        // fires alongside (not dedup'd - different name). Both see the two results.
        Assert.Equal(2, explicitObserver.Results.Count);
        Assert.Equal(2, auto.Results.Count);
    }

    [Fact]
    public async Task Programmatic_And_Cli_Observer_Same_Name_Does_Not_Duplicate()
    {
        // The user passes --observer x AND .WithObserver(new XObserver()). Both resolve to
        // Name = "x". The ResolveObserver dedup keeps the first (programmatic) instance and
        // drops the CLI-duplicated one, so the observer fires once, not twice.
        var cliCallCount = 0;

        ObserverRegistry.Register(
            "dup",
            "explicit",
            () =>
            {
                Interlocked.Increment(ref cliCallCount);
                return new CountingAutoObserver("dup");
            });

        // Add a programmatic observer with the same Name = "dup" before the CLI resolves it.
        // The harness's .WithObserver adds to _observers; BenchmarkHarness.Create(args) also
        // resolves --observer dup via TryCreate and adds to _observers. The dedup in
        // ResolveObserver keeps the first (programmatic) and drops the CLI duplicate.
        var programmatic = new CountingAutoObserver("dup");

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create([
                    "--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1", "--observer", "dup",
                ])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithObserver(programmatic)
                .RunAsync();
        });

        // The programmatic instance is kept; the CLI-duplicated instance (from TryCreate)
        // is dedup'd out by name. Only the programmatic observer's Dispose is called once.
        Assert.Equal(1, programmatic.DisposeCallCount);

        // The CLI factory was called once by TryCreate in BenchmarkHarness.Create, but the
        // result was dedup'd out of the composite. The auto-attached factory is not called.
        Assert.Equal(1, cliCallCount);
    }

    [Fact]
    public async Task Programmatic_Named_Observer_Suppresses_AutoAttached_Of_Same_Name()
    {
        var programmatic = new CapturingAutoObserver("studio");

        ObserverRegistry.RegisterAutoAttach(
            "studio",
            "auto",
            () => new CapturingAutoObserver("studio"));

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithObserver(programmatic)
                .RunAsync();
        });

        // The programmatic instance (Name = "studio") suppresses the auto-attached "studio"
        // entry. The programmatic observer receives the events; no double-attach.
        Assert.Equal(2, programmatic.Results.Count);
    }

    [Fact]
    public async Task Programmatic_Anonymous_Observer_Does_Not_Suppress_AutoAttached()
    {
        var programmatic = new AnonymousObserver();
        var auto = new CapturingAutoObserver("auto");
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => auto);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithObserver(programmatic)
                .RunAsync();
        });

        // The anonymous programmatic observer (Name = null) does not suppress the
        // auto-attached "auto" entry. Both fire.
        Assert.NotEmpty(programmatic.Samples);
        Assert.NotEmpty(auto.Samples);
    }

    [Fact]
    public async Task AutoAttached_Observer_Fires_Alongside_Explicit_Programmatic_Observer()
    {
        var explicitObserver = new AnonymousObserver();
        var auto = new CapturingAutoObserver("auto");
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => auto);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithObserver(explicitObserver)
                .RunAsync();
        });

        // Both the explicit programmatic observer and the auto-attached one receive events.
        Assert.NotEmpty(explicitObserver.Samples);
        Assert.NotEmpty(auto.Samples);
        Assert.Equal(2, auto.Results.Count);
    }

    [Fact]
    public async Task Resolved_Observer_Is_Disposed_After_RunAsync_Success_Path()
    {
        var auto = new CountingAutoObserver("disposable");
        ObserverRegistry.RegisterAutoAttach("disposable", "Disposable", () => auto);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        Assert.Equal(1, auto.DisposeCallCount);
    }

    [Fact]
    public async Task SuiteCompleted_Sentinel_Emitted_Exactly_Once_On_Success_Path()
    {
        var capturing = new CapturingAutoObserver("capture");
        ObserverRegistry.RegisterAutoAttach("capture", "Captures events", () => capturing);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        var suiteCompletedEvents = capturing.Phases
            .Where(p => p.Phase == MeasurementPhase.SuiteCompleted)
            .ToList();

        Assert.Single(suiteCompletedEvents);
        Assert.True(suiteCompletedEvents[0].Succeeded);
        Assert.Equal(string.Empty, suiteCompletedEvents[0].BenchmarkName);
        Assert.Equal(PhaseTransition.Completed, suiteCompletedEvents[0].Transition);
    }

    [Fact]
    public async Task SuiteCompleted_Sentinel_Emitted_On_Success_Path_Even_With_Errored_Benchmark()
    {
        // A benchmark class whose constructor throws is treated by the harness as an errored
        // result (not a harness-level exception), so the run completes normally via the
        // success path. The success-path sentinel fires with Succeeded = true.
        var capturing = new CapturingAutoObserver("capture");
        ObserverRegistry.RegisterAutoAttach("capture", "Captures events", () => capturing);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "ThrowingCtorBenchmarks.*", "--in-process", "--launch-count", "1", "--warmup", "0", "--iterations", "1"])
                .AddFromAssembly<ThrowingCtorBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        var suiteCompletedEvents = capturing.Phases
            .Where(p => p.Phase == MeasurementPhase.SuiteCompleted)
            .ToList();

        Assert.Single(suiteCompletedEvents);
        Assert.True(suiteCompletedEvents[0].Succeeded);
    }

    private static async Task CaptureConsoleOutputAsync(Func<Task> action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}

public class ThrowingCtorBenchmarks
{
    public ThrowingCtorBenchmarks()
    {
        throw new InvalidOperationException("Constructor boom");
    }

    [Benchmark]
    public int Fast() => 1 + 1;
}
