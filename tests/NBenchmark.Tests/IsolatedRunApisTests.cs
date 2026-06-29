using System.Runtime.CompilerServices;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class IsolatedRunApisTests
{
    private const string RequestPathEnvVar = "NBENCHMARK_ISOLATED_REQUEST_PATH";
    private const string OutputPathEnvVar = "NBENCHMARK_ISOLATED_OUTPUT_PATH";

    [Fact]
    public async Task ActiveSuiteRequest_MatchingCallsite_RunsAllBenchmarks_AndWritesPayload()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-suite-child-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 3456;
        const string callerMember = "suite-target";

        var suite = new BenchmarkSuite("suite-child-match")
            .Add("a", () => Thread.SpinWait(64))
            .Add("b", () => Thread.SpinWait(128))
            .WithBaseline("a")
            .WithWarmup(0)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .WithIsolation();

        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Suite,
            InvocationOrdinal = 1,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            SuiteName = suite.Name,
        };

        try
        {
            var results = await IsolatedRunContext.WithActiveRequestForTestingAsync(request, outputPath, () =>
                suite.RunAsync(CancellationToken.None, callerFile, callerLine, callerMember));

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.Name == "a");
            Assert.Contains(results, r => r.Name == "b");
            Assert.All(results, r => Assert.Equal(5, r.MeasuredIterations));

            var items = await ChildProcessLauncher.ReadPayloadAsync(outputPath, CancellationToken.None);
            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.Result.Name == "a");
            Assert.Contains(items, i => i.Result.Name == "b");
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ActiveSuiteRequest_NonMatchingCallsite_RunsInProcess_WithoutWritingPayload()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-suite-nontarget-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 4567;
        const string callerMember = "suite-nontarget";

        var suite = new BenchmarkSuite("suite-child-nontarget")
            .Add("a", () => Thread.SpinWait(64))
            .Add("b", () => Thread.SpinWait(128))
            .WithBaseline("a")
            .WithWarmup(0)
            .WithIterations(3)
            .WithOutlierMode(OutlierMode.None)
            .WithIsolation();

        // A request whose invocation ordinal does not match the call below, simulating a
        // sibling suite's child: the suite still runs in-process, but writes no payload.
        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Suite,
            InvocationOrdinal = 99,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            SuiteName = suite.Name,
        };

        var results = await IsolatedRunContext.WithActiveRequestForTestingAsync(request, outputPath, () =>
            suite.RunAsync(CancellationToken.None, callerFile, callerLine, callerMember));

        Assert.Equal(2, results.Count);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task EnvironmentRequestPath_SuiteChild_ReadsRequest_AndWritesPayload()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var requestPath = Path.Combine(Path.GetTempPath(), $"nbench-request-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-output-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 5678;
        const string callerMember = "suite-env";

        var suite = new BenchmarkSuite("suite-child-env")
            .Add("a", () => Thread.SpinWait(64))
            .Add("b", () => Thread.SpinWait(128))
            .WithBaseline("a")
            .WithWarmup(0)
            .WithIterations(4)
            .WithOutlierMode(OutlierMode.None)
            .WithIsolation();

        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Suite,
            InvocationOrdinal = 1,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            SuiteName = suite.Name,
        };

        var priorRequestPath = Environment.GetEnvironmentVariable(RequestPathEnvVar);
        var priorOutputPath = Environment.GetEnvironmentVariable(OutputPathEnvVar);

        try
        {
            await ChildProcessLauncher.WriteRequestAsync(requestPath, request, CancellationToken.None);

            Environment.SetEnvironmentVariable(RequestPathEnvVar, requestPath);
            Environment.SetEnvironmentVariable(OutputPathEnvVar, outputPath);

            var results = await suite.RunAsync(CancellationToken.None, callerFile, callerLine, callerMember);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(4, r.MeasuredIterations));
            Assert.True(File.Exists(outputPath));

            var items = await ChildProcessLauncher.ReadPayloadAsync(outputPath, CancellationToken.None);
            Assert.Equal(2, items.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RequestPathEnvVar, priorRequestPath);
            Environment.SetEnvironmentVariable(OutputPathEnvVar, priorOutputPath);

            if (File.Exists(requestPath))
                File.Delete(requestPath);

            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static string CurrentFilePath([CallerFilePath] string path = "") => path;

    [Fact]
    public async Task IsolatedRunRequest_ObserverNames_RoundTrip_Through_Serialization()
    {
        // ObserverNames is the field that lets a parent forward its --observer names to an
        // isolated child. The child resolves each name through ObserverRegistry, so the names
        // must survive the JSON round-trip the ChildProcessLauncher uses for the request file.
        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Host,
            DeclaringTypeFullName = "MyClass",
            BenchmarkDisplayNames = ["MethodA", "MethodB"],
            ObserverNames = ["live", "logging"],
        };

        var path = Path.Combine(Path.GetTempPath(), $"nbench-obs-request-{Guid.NewGuid():N}.json");

        try
        {
            await ChildProcessLauncher.WriteRequestAsync(path, request, CancellationToken.None);
            var read = await ChildProcessLauncher.ReadRequestAsync(path);

            Assert.Equal(IsolatedRunKind.Host, read.Kind);
            Assert.Equal("MyClass", read.DeclaringTypeFullName);
            Assert.Equal(["live", "logging"], read.ObserverNames);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task IsolatedRunRequest_ObserverNames_Defaults_To_Empty()
    {
        // When the parent does not forward any observers, the field defaults to empty so the
        // child runs with NullMeasurementObserver.Instance (no hot-path cost).
        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Suite,
            SuiteName = "test",
        };

        var path = Path.Combine(Path.GetTempPath(), $"nbench-obs-empty-{Guid.NewGuid():N}.json");

        try
        {
            await ChildProcessLauncher.WriteRequestAsync(path, request, CancellationToken.None);
            var read = await ChildProcessLauncher.ReadRequestAsync(path);

            Assert.Empty(read.ObserverNames);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
