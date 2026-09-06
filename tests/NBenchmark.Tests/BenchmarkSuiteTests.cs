using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkSuiteTests
{
    [Fact]
    public void Add_Rejects_Duplicate_Names()
    {
        var suite = new BenchmarkSuite("dup").WithIsolation(Isolation.Preferred);
        suite.Add("foo", () => { });

        Assert.Throws<ArgumentException>(() => suite.Add("foo", () => { }));
    }

    [Fact]
    public async Task RunAsync_Executes_All_Added_Benchmarks()
    {
        var results = await new BenchmarkSuite("capture").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmupSamples(1)
            .WithSamples(2)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RunAsync_WithBaseline_Significance_Sets_SignificanceVerdict()
    {
        var results = await new BenchmarkSuite("sig").WithIsolation(Isolation.Preferred)
            .Add("baseline", () => Thread.SpinWait(1000))
            .Add("faster", () => Thread.SpinWait(500))
            .WithBaseline("baseline")
            .WithWarmupSamples(2)
            .WithSamples(20)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var faster = results.Single(r => r.Name == "faster");
        Assert.NotEqual(SignificanceVerdict.NotTested, faster.SignificanceVerdict);
        Assert.NotNull(faster.PValue);
    }

    [Fact]
    public async Task WithBaseline_Not_In_Suite_Throws()
    {
        var suite = new BenchmarkSuite("bad").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .WithBaseline("missing");

        await Assert.ThrowsAsync<BenchmarkConfigurationException>(() => suite.RunAsync());
    }

    [Fact]
    public async Task RunAsync_Captures_Exception_As_Errored_Result()
    {
        var results = await new BenchmarkSuite("boom").WithIsolation(Isolation.Preferred)
            .Add("explodes", () => throw new InvalidOperationException("nope"))
            .Add("calm", () => { })
            .WithWarmupSamples(1)
            .WithSamples(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var errored = results.Single(r => r.Name == "explodes");
        Assert.True(errored.Errored);
        Assert.Contains("nope", errored.ErrorMessage);

        var calm = results.Single(r => r.Name == "calm");
        Assert.False(calm.Errored);
    }

    [Fact]
    public async Task RunAsync_Cancellation_Still_Runs_Suite_Teardown()
    {
        var teardownRan = false;
        using var cts = new CancellationTokenSource();

        var suite = new BenchmarkSuite("cancel-teardown").WithIsolation(Isolation.Preferred)
            .Add("self-cancelling", () => cts.Cancel())
            .WithWarmupSamples(0)
            .WithSamples(5)
            .WithOutlierMode(OutlierMode.None)
            .WithSuiteTeardown(() => teardownRan = true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suite.RunAsync(cts.Token));

        Assert.True(teardownRan, "Suite teardown must run even when the run is cancelled.");
    }

    [Fact]
    public async Task RunAsync_Emits_OnBenchmarkStarting_For_Each_Benchmark()
    {
        var progress = new CapturingProgress();

        await new BenchmarkSuite("progress").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmupSamples(0)
            .WithSamples(1)
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

        var suite = new BenchmarkSuite("ordering").WithIsolation(Isolation.Preferred)
            .Add("work", () => { })
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithSuiteSetup(() => events.Add("setup"))
            .WithSuiteTeardown(() => events.Add("teardown"))
            .WithProgress(new OrderingProgress(
                () => events.Add("onSuiteStarting"),
                () => events.Add("onSuiteCompleted")));

        await suite.RunAsync();

        Assert.Equal(new[] { "onSuiteStarting", "setup", "teardown", "onSuiteCompleted" }, events);
    }

    [Fact]
    public async Task Add_WithCategories_Carries_Categories_Through_To_Result()
    {
        var results = await new BenchmarkSuite("tagged").WithIsolation(Isolation.Preferred)
            .Add("fast", () => { }, categories: ["Fast"])
            .Add("slow", () => { }, categories: ["Slow"])
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var fast = results.Single(r => r.Name == "fast");
        var slow = results.Single(r => r.Name == "slow");

        Assert.Equal(["Fast"], fast.Categories);
        Assert.Equal(["Slow"], slow.Categories);
    }

    [Fact]
    public async Task WithCategories_Applies_To_Subsequent_Adds()
    {
        var results = await new BenchmarkSuite("batch").WithIsolation(Isolation.Preferred)
            .WithCategories("Shared")
            .Add("a", () => { })
            .Add("b", () => { }, categories: ["Extra"])
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var a = results.Single(r => r.Name == "a");
        var b = results.Single(r => r.Name == "b");

        Assert.Equal(["Shared"], a.Categories);
        Assert.Equal(["Extra"], b.Categories);
    }

    [Fact]
    public async Task WithCategories_Trims_And_Deduplicates_CaseInsensitive()
    {
        var results = await new BenchmarkSuite("normalized").WithIsolation(Isolation.Preferred)
            .WithCategories(" Fast ", "fast")
            .Add("x", () => { })
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(["Fast"], results[0].Categories);
    }

    [Fact]
    public void Add_WithBlankCategory_Throws()
    {
        var suite = new BenchmarkSuite("bad-categories").WithIsolation(Isolation.Preferred);
        Assert.Throws<ArgumentException>(() => suite.Add("x", () => { }, categories: [" "]));
    }

    [Fact]
    public void WithCategoryFilter_WithBlankCategory_Throws()
    {
        var suite = new BenchmarkSuite("bad-filter").WithIsolation(Isolation.Preferred);
        Assert.Throws<ArgumentException>(() => suite.FilterCategories([" "]));
    }

    [Fact]
    public async Task WithCategoryFilter_Include_Excludes_Untagged_And_NonMatching()
    {
        var results = await new BenchmarkSuite("filter").WithIsolation(Isolation.Preferred)
            .Add("fast", () => { }, categories: ["Fast"])
            .Add("slow", () => { }, categories: ["Slow"])
            .Add("untagged", () => { })
            .FilterCategories(["Fast"])
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Single(results);
        Assert.Equal("fast", results[0].Name);
    }

    [Fact]
    public async Task WithCategoryFilter_Exclude_Removes_Matching()
    {
        var results = await new BenchmarkSuite("filter").WithIsolation(Isolation.Preferred)
            .Add("fast", () => { }, categories: ["Fast"])
            .Add("slow", () => { }, categories: ["Slow"])
            .Add("untagged", () => { })
            .FilterCategories(exclude: ["Slow"])
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, r => r.Name == "slow");
    }

    [Fact]
    public async Task WithCategoryFilter_Multiple_Includes_Are_OR()
    {
        var results = await new BenchmarkSuite("filter").WithIsolation(Isolation.Preferred)
            .Add("a", () => { }, categories: ["A"])
            .Add("b", () => { }, categories: ["B"])
            .Add("c", () => { }, categories: ["C"])
            .FilterCategories(["A", "B"])
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "a");
        Assert.Contains(results, r => r.Name == "b");
    }

    [Fact]
    public async Task RunAsync_Random_Order_Shuffles_NonParameterized_Suite()
    {
        var progress = new CapturingProgress();

        var results = await new BenchmarkSuite("shuffle").WithIsolation(Isolation.Preferred)
            .Add("a", () => { })
            .Add("b", () => { })
            .Add("c", () => { })
            .Add("d", () => { })
            .Add("e", () => { })
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Random)
            .WithProgress(progress)
            .RunAsync();

        Assert.Equal(5, results.Count);
        Assert.Equal(5, progress.BenchmarkStarts.Count);

        var names = progress.BenchmarkStarts.Select(s => s.Name).ToList();
        Assert.Equal(5, names.Distinct().Count());

        var declarationOrder = new[] { "a", "b", "c", "d", "e" };
        Assert.NotEqual(declarationOrder, names);
    }

    private sealed class OrderingProgress : IBenchmarkProgress
    {
        private readonly Action _onSuiteCompleted;
        private readonly Action _onSuiteStarting;

        public OrderingProgress(Action onSuiteStarting, Action onSuiteCompleted)
        {
            _onSuiteStarting = onSuiteStarting;
            _onSuiteCompleted = onSuiteCompleted;
        }

        public Task OnSuiteStartingAsync(
        IReadOnlyList<string> benchmarkNames, int total, CancellationToken cancellationToken)
        {
            _onSuiteStarting();
            return Task.CompletedTask;
        }

        public Task OnWarmupStartingAsync(string name, int totalWarmupSamples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnWarmupCompletedAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnBenchmarkStartingAsync(string name, int index, int total, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnSampleCompletedAsync(
        string name, int sample, int totalSamples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OnBenchmarkCompletedAsync(BenchmarkResult result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnSuiteCompletedAsync(
        IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
        {
            _onSuiteCompleted();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingProgress : IBenchmarkProgress
    {
        public List<(string Name, int Index, int Total)> BenchmarkStarts { get; } = [];
        public List<int> SuiteStartings { get; } = [];
        public List<int> SuiteCompletions { get; } = [];

        public Task OnSuiteStartingAsync(
        IReadOnlyList<string> benchmarkNames, int total, CancellationToken cancellationToken)
        {
            SuiteStartings.Add(total);
            return Task.CompletedTask;
        }

        public Task OnWarmupStartingAsync(string name, int totalWarmupSamples, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnWarmupCompletedAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnBenchmarkStartingAsync(string name, int index, int total, CancellationToken cancellationToken)
        {
            BenchmarkStarts.Add((name, index, total));
            return Task.CompletedTask;
        }

        public Task OnSampleCompletedAsync(
        string name, int sample, int totalSamples, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnBenchmarkCompletedAsync(BenchmarkResult result, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnSuiteCompletedAsync(
        IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
        {
            SuiteCompletions.Add(results.Count);
            return Task.CompletedTask;
        }
    }
}
