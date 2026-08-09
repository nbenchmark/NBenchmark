using Xunit;

namespace NBenchmark.Tests;

public class ParametricSuiteTests
{
    [Fact]
    public async Task WithParameter_Expands_Benchmarks_Per_Value()
    {
        var results = await new BenchmarkSuite("parametric").WithRequireIsolation(false)
            .WithParameter("size", 10, 100)
            .Add("constant", (int size) => size)
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "constant(size=10)");
        Assert.Contains(results, r => r.Name == "constant(size=100)");
    }

    [Fact]
    public async Task WithParameter_Result_Contains_ParameterSet()
    {
        var results = await new BenchmarkSuite("parametric").WithRequireIsolation(false)
            .WithParameter("size", 10)
            .Add("constant", (int size) => size)
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var result = Assert.Single(results);
        Assert.Single(result.ParameterSet);
        Assert.Equal("size", result.ParameterSet[0].Name);
        Assert.Equal(10, result.ParameterSet[0].Value);
    }

    [Fact]
    public async Task WithParameter_Multiple_Parameters_Expands_Combinatorially()
    {
        var results = await new BenchmarkSuite("parametric").WithRequireIsolation(false)
            .WithParameter("a", 1, 2)
            .WithParameter("b", 10, 20)
            .Add("sum", (int a, int b) => a + b)
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .RunAsync();

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.Name == "sum(a=1, b=10)");
        Assert.Contains(results, r => r.Name == "sum(a=1, b=20)");
        Assert.Contains(results, r => r.Name == "sum(a=2, b=10)");
        Assert.Contains(results, r => r.Name == "sum(a=2, b=20)");
    }

    [Fact]
    public async Task WithParameter_Mixed_Parameterized_And_Non_Parameterized()
    {
        var results = await new BenchmarkSuite("mixed").WithRequireIsolation(false)
            .Add("plain", () => { })
            .WithParameter("size", 10)
            .Add("param", (int size) => size)
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "plain");
        Assert.Contains(results, r => r.Name == "param(size=10)");
    }

    [Fact]
    public async Task WithParameter_Per_Parameter_Baseline_Applies_To_Each_Group()
    {
        var results = await new BenchmarkSuite("per-param-baseline").WithRequireIsolation(false)
            .WithParameter("size", 10, 100)
            .Add("baseline", (int size) => Thread.SpinWait(500))
            .Add("faster", (int size) => Thread.SpinWait(100))
            .WithBaseline("baseline")
            .WithWarmup(1)
            .WithIterations(20)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .RunAsync();

        var baselineResults = results.Where(r => r.IsBaseline).ToList();
        Assert.Equal(2, baselineResults.Count);
        Assert.Contains(baselineResults, r => r.Name == "baseline(size=10)");
        Assert.Contains(baselineResults, r => r.Name == "baseline(size=100)");

        var faster = results.Single(r => r.Name == "faster(size=10)");
        Assert.NotEqual(SignificanceVerdict.NotTested, faster.SignificanceVerdict);
    }

    [Fact]
    public async Task WithParameter_Significance_Computed_Per_Parameter_Group()
    {
        var results = await new BenchmarkSuite("per-param-sig").WithRequireIsolation(false)
            .WithParameter("size", 10, 100)
            .Add("slow", (int size) => Thread.SpinWait(1000))
            .Add("fast", (int size) => Thread.SpinWait(100))
            .WithWarmup(1)
            .WithIterations(20)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .RunAsync();

        var slow10 = results.Single(r => r.Name == "slow(size=10)");
        var slow100 = results.Single(r => r.Name == "slow(size=100)");

        Assert.NotEqual(SignificanceVerdict.NotTested, slow10.SignificanceVerdict);
        Assert.NotEqual(SignificanceVerdict.NotTested, slow100.SignificanceVerdict);
    }

    [Fact]
    public async Task WithParameter_Progress_Callbacks_Fire_For_Each_Expanded_Benchmark()
    {
        var progress = new CapturingProgress();

        var results = await new BenchmarkSuite("progress").WithRequireIsolation(false)
            .WithParameter("size", 10, 100)
            .Add("work", (int size) => size)
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .WithProgress(progress)
            .RunAsync();

        Assert.Equal(2, progress.BenchmarkCompletions.Count);
        Assert.Contains(progress.BenchmarkCompletions, r => r.Name == "work(size=10)");
        Assert.Contains(progress.BenchmarkCompletions, r => r.Name == "work(size=100)");

        var work10 = progress.BenchmarkCompletions.Single(r => r.Name == "work(size=10)");
        Assert.Single(work10.ParameterSet);
        Assert.Equal("size", work10.ParameterSet[0].Name);
        Assert.Equal(10, work10.ParameterSet[0].Value);

        var work100 = progress.BenchmarkCompletions.Single(r => r.Name == "work(size=100)");
        Assert.Single(work100.ParameterSet);
        Assert.Equal("size", work100.ParameterSet[0].Name);
        Assert.Equal(100, work100.ParameterSet[0].Value);
    }

    [Fact]
    public void WithParameter_Duplicate_Names_After_Expansion_Throws()
    {
        var suite = new BenchmarkSuite("duplicate").WithRequireIsolation(false)
            .WithParameter("x", 1, 1)
            .Add("bench", (int x) => x);

        Assert.Throws<ArgumentException>(() => suite.RunAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void WithParameter_Missing_WithoutParameter_Throws()
    {
        var suite = new BenchmarkSuite("missing").WithRequireIsolation(false)
            .Add("bench", (int size) => size);

        Assert.Throws<InvalidOperationException>(() => suite.RunAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void WithParameter_No_Parameterized_Benchmarks_Throws()
    {
        var suite = new BenchmarkSuite("no-benches").WithRequireIsolation(false)
            .Add("plain", () => { })
            .WithParameter("size", 10);

        Assert.Throws<InvalidOperationException>(() => suite.RunAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void WithParameter_Unsupported_Type_Throws()
    {
        var suite = new BenchmarkSuite("unsupported").WithRequireIsolation(false);
        Assert.Throws<ArgumentException>(() => suite.WithParameter("x", new UnsupportedParameter()));
    }

    [Fact]
    public void WithParameter_Type_Mismatch_Throws()
    {
        var suite = new BenchmarkSuite("mismatch").WithRequireIsolation(false)
            .WithParameter("x", 1)
            .Add("bench", (string x) => x.Length);

        Assert.Throws<InvalidOperationException>(() => suite.RunAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task WithParameter_Null_Value_Is_Supported()
    {
        var results = await new BenchmarkSuite("nullable").WithRequireIsolation(false)
            .WithParameter("value", (string?)null)
            .Add("work", (string? value) => value?.Length ?? 0)
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var result = Assert.Single(results);
        Assert.Equal("work(value=null)", result.Name);
        Assert.Null(result.ParameterSet[0].Value);
    }

    [Fact]
    public async Task WithParameter_Enum_Value_Is_Supported()
    {
        var results = await new BenchmarkSuite("enum").WithRequireIsolation(false)
            .WithParameter("mode", TestMode.A, TestMode.B)
            .Add("work", (TestMode mode) => (int)mode)
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Name == "work(mode=A)");
        Assert.Contains(results, r => r.Name == "work(mode=B)");
    }

    [Fact]
    public void BenchmarkParameter_GetKey_Distinguishes_Null_And_Empty_String()
    {
        var nullKey = BenchmarkParameter.GetKey([new BenchmarkParameter("value", null)]);
        var emptyKey = BenchmarkParameter.GetKey([new BenchmarkParameter("value", "")]);

        Assert.NotEqual(nullKey, emptyKey);
    }

    [Fact]
    public void BenchmarkParameter_GetKey_Does_Not_Collide_When_Values_Contain_Separators()
    {
        var keyWithSeparators = BenchmarkParameter.GetKey([new BenchmarkParameter("a", "x\u001Fb=y")]);

        var splitKey = BenchmarkParameter.GetKey([
            new BenchmarkParameter("a", "x"),
            new BenchmarkParameter("b", "y"),
        ]);

        Assert.NotEqual(keyWithSeparators, splitKey);
    }

    private sealed class UnsupportedParameter;

    private enum TestMode
    {
        A,
        B,
    }

    private sealed class CapturingProgress : IBenchmarkProgress
    {
        public List<BenchmarkResult> BenchmarkCompletions { get; } = [];

        public Task OnSuiteStarting(IReadOnlyList<string> names, int count) => Task.CompletedTask;
        public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;
        public Task OnWarmupCompleted(string name) => Task.CompletedTask;
        public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;
        public Task OnIterationCompleted(string name, int iteration, int totalIterations) => Task.CompletedTask;

        public Task OnBenchmarkCompleted(BenchmarkResult result)
        {
            BenchmarkCompletions.Add(result);
            return Task.CompletedTask;
        }

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) => Task.CompletedTask;
    }
}
