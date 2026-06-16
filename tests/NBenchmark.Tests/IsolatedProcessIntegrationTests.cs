using NBenchmark.Engine;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class IsolatedProcessIntegrationTests
{
    [Fact]
    public async Task SuiteWithIsolation_FakeLauncher_CompletesFullRoundTrip()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var suite = new BenchmarkSuite("fake-isolated-suite")
            .Add("slow", () => Thread.SpinWait(64))
            .Add("fast", () => Thread.SpinWait(32))
            .WithBaseline("fast")
            .WithWarmup(0)
            .WithIterations(10)
            .WithOutlierMode(OutlierMode.None)
            .WithIsolation();

        using (WithFakeLauncher(SimulateChildRun))
        {
            var results = await suite.RunAsync();

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.Name == "slow");
            Assert.Contains(results, r => r.Name == "fast");
            Assert.All(results, r => Assert.Equal(10, r.MeasuredIterations));
            Assert.All(results, r => Assert.False(r.Errored));
        }
    }

    [Fact]
    public async Task SuiteWithIsolation_FakeLauncher_AppliesSignificanceAndReporters()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var seenResults = new List<BenchmarkResult>();
        var reporter = new FakeReporter(r => seenResults.AddRange(r));

        var suite = new BenchmarkSuite("fake-isolated-sig")
            .Add("slow", () => Thread.SpinWait(64))
            .Add("fast", () => Thread.SpinWait(32))
            .WithBaseline("fast")
            .WithWarmup(0)
            .WithIterations(10)
            .WithOutlierMode(OutlierMode.None)
            .WithReporter(reporter)
            .WithIsolation();

        using (WithFakeLauncher(SimulateChildRun))
        {
            var results = await suite.RunAsync();

            Assert.Equal(2, results.Count);

            Assert.True(seenResults.Count > 0,
                "Reporter should have been invoked after the isolated run");

            var slowResult = results.Single(r => r.Name == "slow");
            var fastResult = results.Single(r => r.Name == "fast");

            Assert.True(fastResult.IsBaseline);
            Assert.True(slowResult.PValue <= 0.05,
                $"Expected slow benchmark to be significantly slower than fast; got p={slowResult.PValue}");
        }
    }

    [Fact]
    public async Task SuiteWithIsolation_FakeLauncher_ChildFailure_ReturnsErroredResults()
    {
        IsolatedRunContext.ResetInvocationOrdinalsForTesting();

        var suite = new BenchmarkSuite("fake-isolated-fail")
            .Add("a", () => Thread.SpinWait(10))
            .Add("b", () => Thread.SpinWait(20))
            .WithWarmup(0)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .WithIsolation();

        using (WithFakeLauncher((_, _) =>
            Task.FromResult<IReadOnlyList<IsolatedResultItem>>(
            [
                new IsolatedResultItem
                {
                    Result = new BenchmarkResult
                    {
                        Name = "a",
                        Mean = 100,
                        Median = 100,
                        P95 = 100,
                        P99 = 100,
                        Min = 100,
                        Max = 100,
                        StandardDeviation = 0,
                        Q1 = 100,
                        Q3 = 100,
                        InterquartileRange = 0,
                        OutliersRemoved = 0,
                        N = 5,
                        Skewness = 0,
                        Kurtosis = 0,
                        Mad = 0,
                        AllocMedian = null,
                        AllocP95 = null,
                        AllocMax = null,
                        Errored = false,
                    },
                    RawSamples = [100, 100, 100, 100, 100],
                },
            ])))
        {
            var results = await suite.RunAsync();

            Assert.Equal(2, results.Count);

            var aResult = results.Single(r => r.Name == "a");
            Assert.False(aResult.Errored);
            Assert.Equal(100, aResult.Mean);

            var bResult = results.Single(r => r.Name == "b");
            Assert.True(bResult.Errored);
            Assert.Contains("did not return a result", bResult.ErrorMessage);
        }
    }

    private static async Task<IReadOnlyList<IsolatedResultItem>> SimulateChildRun(
        IsolatedRunRequest request,
        CancellationToken ct)
    {
        var items = new List<IsolatedResultItem>();

        foreach (var name in request.BenchmarkDisplayNames)
        {
            var fullName = string.IsNullOrEmpty(request.DisplayPrefix)
                ? name
                : $"{request.DisplayPrefix}.{name}";

            var outcome = BenchmarkRunner.Instance.Run(fullName,
                () => Thread.SpinWait(name == "slow" ? 64 : name == "fast" ? 32 : name == "a" ? 10 : 20),
                new RunSpec
                {
                    Options = new MeasurementOptions
                    {
                        WarmupIterations = 0,
                        Iterations = 10,
                        OutlierMode = OutlierMode.None,
                    },
                }, ct);

            items.Add(new IsolatedResultItem
            {
                Result = outcome.Result,
                RawSamples = outcome.RawSamples,
            });
        }

        return items;
    }

    private static IDisposable WithFakeLauncher(
        Func<IsolatedRunRequest, CancellationToken, Task<IReadOnlyList<IsolatedResultItem>>> handler)
    {
        var prior = ChildProcessLauncher.Current;
        ChildProcessLauncher.Current = new FakeProcessLauncher(handler);
        return new Restorer(prior);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly IProcessLauncher _prior;
        private bool _disposed;

        public Restorer(IProcessLauncher prior)
        {
            _prior = prior;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            ChildProcessLauncher.Current = _prior;
            _disposed = true;
        }
    }

    private sealed class FakeReporter : IReporter
    {
        private readonly Action<IReadOnlyList<BenchmarkResult>> _onReport;

        public FakeReporter(Action<IReadOnlyList<BenchmarkResult>> onReport)
        {
            _onReport = onReport;
        }

        public string Name => "fake";
        public ReportDetail Detail { get; set; }

        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
        {
            _onReport(results);
            return Task.CompletedTask;
        }
    }
}
