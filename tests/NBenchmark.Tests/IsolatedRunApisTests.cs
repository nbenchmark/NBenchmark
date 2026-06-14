using System.Runtime.CompilerServices;
using System.Text.Json;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class IsolatedRunApisTests
{
    private const string RequestPathEnvVar = "NBENCHMARK_ISOLATED_REQUEST_PATH";
    private const string OutputPathEnvVar = "NBENCHMARK_ISOLATED_OUTPUT_PATH";

    [Fact]
    public async Task RunIsolated_ActiveQuickRequest_RunsAndWritesOutcome()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-quick-child-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 1234;
        const string callerMember = "quick-member";

        var request = new IsolatedRunRequest
        {
            Mode = IsolatedRunMode.Quick,
            InvocationOrdinal = 1,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            BenchmarkName = "quick-child",
            Options = new MeasurementOptions
            {
                WarmupIterations = 0,
                Iterations = 3,
                OutlierMode = OutlierMode.None,
            },
        };

        try
        {
            var result = await IsolatedRunContext.WithActiveRequestForTestingAsync(request, outputPath, () =>
                Task.FromResult(Benchmark.RunIsolated(
                    () => Thread.SpinWait(64),
                    new MeasurementOptions
                    {
                        WarmupIterations = 0,
                        Iterations = 1,
                        OutlierMode = OutlierMode.None,
                    },
                    "quick-child",
                    callerFilePath: callerFile,
                    callerLineNumber: callerLine,
                    callerMemberName: callerMember)));

            Assert.False(result.Errored);

            var outcome = await IsolatedProcessRunner.ReadResultAsync(outputPath, CancellationToken.None);
            Assert.Equal("quick-child", outcome.Result.Name);
            Assert.Equal(3, outcome.Result.MeasuredIterations);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RunIsolated_ActiveQuickRequest_RequestedInvocationMismatch_WritesErroredPayload()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-quick-mismatch-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 2345;
        const string callerMember = "quick-mismatch-member";

        var request = new IsolatedRunRequest
        {
            Mode = IsolatedRunMode.Quick,
            InvocationOrdinal = 1,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            BenchmarkName = "some-other-name",
            Options = new MeasurementOptions
            {
                WarmupIterations = 0,
                Iterations = 2,
                OutlierMode = OutlierMode.None,
            },
        };

        var result = await IsolatedRunContext.WithActiveRequestForTestingAsync(request, outputPath, () =>
            Task.FromResult(Benchmark.RunIsolated(
                () => Thread.SpinWait(64),
                new MeasurementOptions
                {
                    WarmupIterations = 0,
                    Iterations = 2,
                    OutlierMode = OutlierMode.None,
                },
                "quick-target",
                callerFilePath: callerFile,
                callerLineNumber: callerLine,
                callerMemberName: callerMember)));

        Assert.True(result.Errored);
        Assert.True(File.Exists(outputPath));

        var outcome = await IsolatedProcessRunner.ReadResultAsync(outputPath, CancellationToken.None);
        Assert.True(outcome.Result.Errored);
        Assert.Contains("Isolated replay mismatch", outcome.Result.ErrorMessage);
    }

    [Fact]
    public async Task RunIsolated_ActiveQuickRequest_DifferentInvocation_RunsInProcessWithoutPayload()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-quick-non-target-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 2468;
        const string callerMember = "quick-non-target-member";

        var request = new IsolatedRunRequest
        {
            Mode = IsolatedRunMode.Quick,
            InvocationOrdinal = 99,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            BenchmarkName = "some-other-name",
            Options = new MeasurementOptions
            {
                WarmupIterations = 0,
                Iterations = 2,
                OutlierMode = OutlierMode.None,
            },
        };

        var result = await IsolatedRunContext.WithActiveRequestForTestingAsync(request, outputPath, () =>
            Task.FromResult(Benchmark.RunIsolated(
                () => Thread.SpinWait(64),
                new MeasurementOptions
                {
                    WarmupIterations = 0,
                    Iterations = 2,
                    OutlierMode = OutlierMode.None,
                },
                "quick-target",
                callerFilePath: callerFile,
                callerLineNumber: callerLine,
                callerMemberName: callerMember)));

        Assert.False(result.Errored);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task RunIsolatedAsync_ActiveSuiteRequest_RunsTargetBenchmarkAndWritesOutcome()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-suite-child-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 3456;
        const string callerMember = "suite-member";

        var suite = new BenchmarkSuite("suite-child")
            .Add("a", () => Thread.SpinWait(64))
            .Add("b", () => Thread.SpinWait(128))
            .WithBaseline("a")
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None);

        var request = new IsolatedRunRequest
        {
            Mode = IsolatedRunMode.Suite,
            InvocationOrdinal = 1,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            BenchmarkName = "b",
            SuiteName = suite.Name,
            Options = new MeasurementOptions
            {
                WarmupIterations = 0,
                Iterations = 4,
                OutlierMode = OutlierMode.None,
            },
        };

        try
        {
            var results = await IsolatedRunContext.WithActiveRequestForTestingAsync(request, outputPath, () =>
                suite.RunIsolatedAsync(
                    CancellationToken.None,
                    callerFile,
                    callerLine,
                    callerMember));

            var only = Assert.Single(results);
            Assert.Equal("b", only.Name);
            Assert.Equal(4, only.MeasuredIterations);

            var outcome = await IsolatedProcessRunner.ReadResultAsync(outputPath, CancellationToken.None);
            Assert.Equal("b", outcome.Result.Name);
            Assert.Equal(4, outcome.Result.MeasuredIterations);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RunIsolated_EnvironmentRequestPath_RunsAndWritesOutcome()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var requestPath = Path.Combine(Path.GetTempPath(), $"nbench-request-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-output-{Guid.NewGuid():N}.json");
        var callerFile = CurrentFilePath();
        const int callerLine = 4567;
        const string callerMember = "env-member";

        var request = new IsolatedRunRequest
        {
            Mode = IsolatedRunMode.Quick,
            InvocationOrdinal = 1,
            CallerFilePath = callerFile,
            CallerLineNumber = callerLine,
            CallerMemberName = callerMember,
            BenchmarkName = "env-quick",
            Options = new MeasurementOptions
            {
                WarmupIterations = 0,
                Iterations = 3,
                OutlierMode = OutlierMode.None,
            },
        };

        var priorRequestPath = Environment.GetEnvironmentVariable(RequestPathEnvVar);
        var priorOutputPath = Environment.GetEnvironmentVariable(OutputPathEnvVar);

        try
        {
            await using (var stream = File.Create(requestPath))
            {
                await JsonSerializer.SerializeAsync(stream, request, cancellationToken: CancellationToken.None);
            }

            Environment.SetEnvironmentVariable(RequestPathEnvVar, requestPath);
            Environment.SetEnvironmentVariable(OutputPathEnvVar, outputPath);

            var result = Benchmark.RunIsolated(
                () => Thread.SpinWait(64),
                new MeasurementOptions
                {
                    WarmupIterations = 0,
                    Iterations = 1,
                    OutlierMode = OutlierMode.None,
                },
                "env-quick",
                callerFilePath: callerFile,
                callerLineNumber: callerLine,
                callerMemberName: callerMember);

            Assert.False(result.Errored);
            Assert.True(File.Exists(outputPath));

            var outcome = await IsolatedProcessRunner.ReadResultAsync(outputPath, CancellationToken.None);
            Assert.Equal("env-quick", outcome.Result.Name);
            Assert.Equal(3, outcome.Result.MeasuredIterations);
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
}
