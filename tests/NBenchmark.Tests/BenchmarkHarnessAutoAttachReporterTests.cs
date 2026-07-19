using NBenchmark.Attributes;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class BenchmarkHarnessAutoAttachReporterTests : IDisposable
{
    public BenchmarkHarnessAutoAttachReporterTests()
    {
        ReporterRegistry.Reset();
    }

    public void Dispose() => ReporterRegistry.Reset();

    [Fact]
    public async Task AutoAttached_Reporter_Receives_Result_List()
    {
        var capturing = new CapturingAutoReporter("capture");
        ReporterRegistry.RegisterAutoAttach("capture", "Captures results", (_, _) => capturing);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        var names = capturing.Results.Select(r => r.Name).ToList();
        Assert.NotEmpty(names);
        Assert.Contains("TestBenchmarks.Fast", names);
        Assert.Contains("TestBenchmarks.FastBaseline", names);
    }

    [Fact]
    public async Task AutoAttached_Reporter_Fires_After_Explicit_Reporters()
    {
        var order = new List<string>();
        var explicitReporter = new OrderTrackingReporter("explicit", order);
        var autoReporter = new OrderTrackingReporter("auto", order);
        ReporterRegistry.RegisterAutoAttach("auto", "Auto", (_, _) => autoReporter);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithReporter(explicitReporter)
                .WithIsolation(false)
                .RunAsync();
        });

        Assert.Equal(["explicit", "auto"], order);
    }

    [Fact]
    public async Task Throwing_AutoAttached_Reporter_Is_Caught_And_Run_Returns_Normally()
    {
        var thrown = new ThrowingAutoReporter("throws");
        var captureAfter = new CapturingAutoReporter("after");
        ReporterRegistry.RegisterAutoAttach("throws", "Throws", (_, _) => thrown);
        ReporterRegistry.RegisterAutoAttach("after", "After", (_, _) => captureAfter);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        Assert.Equal(1, thrown.CallCount);
        Assert.Equal(1, captureAfter.CallCount);
        Assert.NotEmpty(captureAfter.Results);
    }

    [Fact]
    public async Task Multiple_AutoAttached_Reporters_All_Fire_In_Registration_Order()
    {
        var order = new List<string>();
        ReporterRegistry.RegisterAutoAttach("first", "1", (_, _) => new OrderTrackingReporter("first", order));
        ReporterRegistry.RegisterAutoAttach("second", "2", (_, _) => new OrderTrackingReporter("second", order));
        ReporterRegistry.RegisterAutoAttach("third", "3", (_, _) => new OrderTrackingReporter("third", order));

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public async Task Explicit_Reporter_Instance_With_Same_Name_As_AutoAttached_Does_Not_Fire_Twice()
    {
        var callCount = 0;
        var explicitInstance = new CountingAutoReporter("dedup", () => callCount++);
        // Register an auto-attached reporter with the same canonical name. The dedup contract:
        // when the user adds an explicit reporter instance whose Name matches an auto-attached
        // reporter, the auto-attached one is skipped (not double-fired). This mirrors the case
        // where a user references NBenchmark.Studio (auto-attached "studio") and also calls
        // .WithReporter(new StudioReporter()) manually.
        ReporterRegistry.RegisterAutoAttach("dedup", "auto", (_, _) => new CountingAutoReporter("dedup", () => callCount++));

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithReporter(explicitInstance)
                .WithIsolation(false)
                .RunAsync();
        });

        // One fire from the explicit instance; zero from the auto-attached one (dedup'd out by name).
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task AutoAttached_Reporter_Fires_Once_For_MultiLaunch_With_Aggregated_List()
    {
        var callCount = 0;
        var observedResultCounts = new List<int>();
        var capturing = new CountingAutoReporter(
            "capture",
            () => callCount++,
            results => observedResultCounts.Add(results.Count));

        ReporterRegistry.RegisterAutoAttach("capture", "Captures", (_, _) => capturing);

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "2"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        // Fires once with the post-aggregation list (not per launch).
        Assert.Equal(1, callCount);
        Assert.Single(observedResultCounts);
        // TestBenchmarks has two methods; the aggregated list should have exactly 2 entries.
        Assert.Equal(2, observedResultCounts[0]);
    }

    [Fact]
    public void Help_Output_Lists_AutoAttached_Reporters_In_Separate_Section()
    {
        ReporterRegistry.RegisterAutoAttach("studio", "Studio inbox", (_, _) => new CountingAutoReporter("studio", () => { }));

        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHarness.Create(["--help"]).RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("auto-attached: studio", stdout);
    }

    [Fact]
    public void Help_Output_Omits_AutoAttached_Section_When_None_Registered()
    {
        // After Reset() in the ctor, AutoAttached is empty.
        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHarness.Create(["--help"]).RunAsync().GetAwaiter().GetResult();
        });

        Assert.DoesNotContain("auto-attached:", stdout);
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

    private static string CaptureConsoleOutput(Action action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return sw.ToString();
    }
}