using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkSuiteTests
{
    [Fact]
    public void Add_Rejects_Duplicate_Names()
    {
        var suite = new BenchmarkSuite("dup");
        suite.Add("foo", () => { });

        Assert.Throws<ArgumentException>(() => suite.Add("foo", () => { }));
    }

    [Fact]
    public async Task RunAsync_Executes_All_Added_Benchmarks()
    {
        var results = await new BenchmarkSuite("capture")
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmup(1)
            .WithIterations(2)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RunAsync_WithBaseline_Significance_Sets_SignificanceVerdict()
    {
        var results = await new BenchmarkSuite("sig")
            .Add("baseline", () => Thread.SpinWait(1000))
            .Add("faster", () => Thread.SpinWait(500))
            .WithBaseline("baseline")
            .WithWarmup(2)
            .WithIterations(20)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var faster = results.Single(r => r.Name == "faster");
        Assert.NotEqual(SignificanceVerdict.NotTested, faster.SignificanceVerdict);
        Assert.NotNull(faster.PValue);
    }

    [Fact]
    public async Task WithBaseline_Not_In_Suite_Throws()
    {
        var suite = new BenchmarkSuite("bad")
            .Add("a", () => { })
            .WithBaseline("missing");

        await Assert.ThrowsAsync<InvalidOperationException>(() => suite.RunAsync());
    }

    [Fact]
    public async Task RunAsync_Captures_Exception_As_Errored_Result()
    {
        var results = await new BenchmarkSuite("boom")
            .Add("explodes", () => throw new InvalidOperationException("nope"))
            .Add("calm", () => { })
            .WithWarmup(1)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var errored = results.Single(r => r.Name == "explodes");
        Assert.True(errored.Errored);
        Assert.Contains("nope", errored.ErrorMessage);

        var calm = results.Single(r => r.Name == "calm");
        Assert.False(calm.Errored);
    }

    [Fact]
    public async Task RunAsync_Emits_OnBenchmarkStarting_For_Each_Benchmark()
    {
        var progress = new CapturingProgress();

        await new BenchmarkSuite("progress")
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .WithProgress(progress)
            .RunAsync();

        Assert.Collection(progress.BenchmarkStarts,
            first =>
            {
                Assert.Equal("a", first.Name);
                Assert.Equal(1, first.Index);
                Assert.Equal(2, first.Total);
            },
            second =>
            {
                Assert.Equal("b", second.Name);
                Assert.Equal(2, second.Index);
                Assert.Equal(2, second.Total);
            });
    }

    [Fact]
    public async Task RunAsync_Emits_OnSuiteStarting_Before_Setup_And_OnSuiteCompleted_After_Teardown()
    {
        var events = new List<string>();

        var suite = new BenchmarkSuite("ordering")
            .Add("work", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithSuiteSetup(() => events.Add("setup"))
            .WithSuiteTeardown(() => events.Add("teardown"))
            .WithProgress(new OrderingProgress(
                onSuiteStarting: () => events.Add("onSuiteStarting"),
                onSuiteCompleted: () => events.Add("onSuiteCompleted")));

        await suite.RunAsync();

        Assert.Equal(new[] { "onSuiteStarting", "setup", "teardown", "onSuiteCompleted" }, events);
    }

    private sealed class OrderingProgress : IBenchmarkProgress
    {
        private readonly Action _onSuiteStarting;
        private readonly Action _onSuiteCompleted;

        public OrderingProgress(Action onSuiteStarting, Action onSuiteCompleted)
        {
            _onSuiteStarting = onSuiteStarting;
            _onSuiteCompleted = onSuiteCompleted;
        }

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) { _onSuiteStarting(); return Task.CompletedTask; }
        public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;
        public Task OnWarmupCompleted(string name) => Task.CompletedTask;
        public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;
        public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;
        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) { _onSuiteCompleted(); return Task.CompletedTask; }
    }

    private sealed class CapturingProgress : IBenchmarkProgress
    {
        public List<(string Name, int Index, int Total)> BenchmarkStarts { get; } = [];
        public List<int> SuiteStartings { get; } = [];
        public List<int> SuiteCompletions { get; } = [];

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
        {
            SuiteStartings.Add(total);
            return Task.CompletedTask;
        }

        public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;

        public Task OnWarmupCompleted(string name) => Task.CompletedTask;

        public Task OnBenchmarkStarting(string name, int index, int total)
        {
            BenchmarkStarts.Add((name, index, total));
            return Task.CompletedTask;
        }

        public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
        {
            SuiteCompletions.Add(results.Count);
            return Task.CompletedTask;
        }
    }
}