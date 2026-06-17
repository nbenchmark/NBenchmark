using NBenchmark.Attributes;
using NBenchmark.Discovery;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkDiscovererTests
{
    [Fact]
    public void Discovers_Public_Benchmark_Methods()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);

        var suite = suites.FirstOrDefault(s => s.Type == typeof(PublicBenchmarks));
        Assert.NotNull(suite);
        Assert.Equal(2, suite!.Benchmarks.Count);
    }

    [Fact]
    public void Discovers_Internal_Only_Benchmark_Class()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(InternalBenchmarksMarker).Assembly);

        Assert.Contains(suites, s => s.Type == typeof(InternalBenchmarks));
    }

    [Fact]
    public void Discovers_Method_Level_IsolatedProcess_Attribute()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(IsolatedMethodBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(IsolatedMethodBenchmarks));

        Assert.Equal(IsolationMode.PerBenchmark, suite.Benchmarks.First(b => b.Method.Name == "Isolated").Isolation);
        Assert.Equal(IsolationMode.Default, suite.Benchmarks.First(b => b.Method.Name == "Plain").Isolation);
    }

    [Fact]
    public void Discovers_Method_Level_InProcess_Attribute()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(InProcessMethodBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(InProcessMethodBenchmarks));

        Assert.Equal(IsolationMode.InProcess, suite.Benchmarks.First(b => b.Method.Name == "Forced").Isolation);
        Assert.Equal(IsolationMode.Default, suite.Benchmarks.First(b => b.Method.Name == "Plain").Isolation);
    }

    [Fact]
    public void Class_Level_IsolatedProcess_Attribute_Applies_To_All_Benchmarks()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(IsolatedClassBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(IsolatedClassBenchmarks));

        Assert.All(suite.Benchmarks, b => Assert.Equal(IsolationMode.PerBenchmark, b.Isolation));
    }

    [Fact]
    public void Class_Level_InProcess_Attribute_Applies_To_All_Benchmarks()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(InProcessClassBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(InProcessClassBenchmarks));

        Assert.All(suite.Benchmarks, b => Assert.Equal(IsolationMode.InProcess, b.Isolation));
    }

    [Fact]
    public void Method_Level_Isolation_Overrides_Class_Level_Isolation()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(MixedIsolationBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(MixedIsolationBenchmarks));

        // Class is [IsolatedProcess]; method [InProcess] opts a single benchmark back out.
        Assert.Equal(IsolationMode.InProcess, suite.Benchmarks.First(b => b.Method.Name == "OptedOut").Isolation);
        Assert.Equal(IsolationMode.PerBenchmark, suite.Benchmarks.First(b => b.Method.Name == "Inherited").Isolation);
    }

    [Fact]
    public void Class_Level_IsolatedProcess_Attribute_Is_Inherited_By_Derived_Classes()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(DerivedIsolatedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(DerivedIsolatedBenchmarks));

        Assert.All(suite.Benchmarks, b => Assert.Equal(IsolationMode.PerBenchmark, b.Isolation));
    }

    [Fact]
    public void Caches_Delegates_For_Benchmarks()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(PublicBenchmarks));
        var benchmark = suite.Benchmarks.First();

        Assert.NotNull(benchmark.SyncDelegate);
    }

    [Fact]
    public void Caches_Sync_Delegate_For_Void_Returning_Method()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(PublicBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsNothing");

        Assert.NotNull(benchmark.SyncDelegate);
        var result = benchmark.SyncDelegate!(new PublicBenchmarks());
        Assert.Null(result);
    }

    [Fact]
    public void Caches_Sync_Delegate_For_Value_Returning_Method()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(PublicBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsInt");

        var result = benchmark.SyncDelegate!(new PublicBenchmarks());
        Assert.Equal(42, result);
    }

    [Fact]
    public void Discovers_Setup_And_Teardown_Delegates_Without_Throwing()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(LifecycleBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(LifecycleBenchmarks));

        Assert.NotNull(suite.SetupDelegate);
        Assert.NotNull(suite.TeardownDelegate);

        var benchmark = suite.Benchmarks.First();
        Assert.NotNull(benchmark.IterationSetupDelegate);
        Assert.NotNull(benchmark.IterationTeardownDelegate);

        var instance = new LifecycleBenchmarks();
        suite.SetupDelegate!(instance);
        benchmark.IterationSetupDelegate!(instance);
        benchmark.IterationTeardownDelegate!(instance);
        suite.TeardownDelegate!(instance);

        Assert.Equal(1, instance.SetupCount);
        Assert.Equal(1, instance.IterationSetupCount);
        Assert.Equal(1, instance.IterationTeardownCount);
        Assert.Equal(1, instance.TeardownCount);
    }

    [Fact]
    public async Task Caches_Async_Delegate_And_Result_Consumer()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(AsyncBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(AsyncBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsValueAsync");

        Assert.NotNull(benchmark.AsyncDelegate);
        Assert.NotNull(benchmark.ResultConsumer);

        var instance = new AsyncBenchmarks();
        var task = benchmark.AsyncDelegate!(instance);
        await task;
        benchmark.ResultConsumer!(task);
    }

    [Fact]
    public async Task Caches_Async_Delegate_For_NonGeneric_Task()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(AsyncBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(AsyncBenchmarks));
        var benchmark = suite.Benchmarks.First(m => m.Method.Name == "ReturnsTask");

        Assert.NotNull(benchmark.AsyncDelegate);
        Assert.Null(benchmark.ResultConsumer);

        await benchmark.AsyncDelegate!(new AsyncBenchmarks());
    }

    [Fact]
    public void Expands_BenchmarkArguments_Into_One_Definition_Per_Set()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParameterisedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParameterisedBenchmarks));

        var compute = suite.Benchmarks.Where(b => b.Method.Name == "Compute").ToList();
        Assert.Equal(2, compute.Count);
        Assert.Equal(new[] { "Compute(100)", "Compute(1000)" }, compute.Select(b => b.DisplayName));
    }

    [Fact]
    public void Argument_Bound_Delegate_Invokes_With_The_Bound_Arguments()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParameterisedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParameterisedBenchmarks));
        var benchmark = suite.Benchmarks.First(b => b.DisplayName == "Compute(1000)");

        var result = benchmark.SyncDelegate!(new ParameterisedBenchmarks());
        Assert.Equal(1000, result);
    }

    [Fact]
    public void Formats_Multiple_And_String_Arguments_In_DisplayName()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParameterisedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParameterisedBenchmarks));

        var concat = suite.Benchmarks.First(b => b.Method.Name == "Concat");
        Assert.Equal("Concat(\"a\", 3)", concat.DisplayName);

        var result = concat.SyncDelegate!(new ParameterisedBenchmarks());
        Assert.Equal("aaa", result);
    }

    [Fact]
    public void Discovers_Method_Level_Categories()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(CategorizedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(CategorizedBenchmarks));

        var concat = suite.Benchmarks.First(b => b.DisplayName == "Concat");
        Assert.Equal(["String", "Fast"], concat.Categories);

        var manyConcat = suite.Benchmarks.First(b => b.DisplayName == "ManyConcat");
        Assert.Equal(["String", "Slow"], manyConcat.Categories);
    }

    [Fact]
    public void Class_Level_Categories_Union_With_Method_Level()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(CategorizedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(CategorizedBenchmarks));

        var interpolate = suite.Benchmarks.First(b => b.DisplayName == "Interpolate");
        Assert.Equal(["String", "Fast"], interpolate.Categories);
    }

    [Fact]
    public void Inherited_Class_Level_Categories_Are_Applied()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(DerivedCategorizedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(DerivedCategorizedBenchmarks));

        var declared = suite.Benchmarks.First(b => b.DisplayName == "Declared");
        Assert.Contains("Base", declared.Categories);
    }

    [Fact]
    public void Duplicate_Class_And_Method_Categories_Are_Deduplicated()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(CategorizedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(CategorizedBenchmarks));

        var concat = suite.Benchmarks.First(b => b.DisplayName == "Concat");
        Assert.Single(concat.Categories, c => string.Equals(c, "String", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_Method_Level_Categories_Are_Deduplicated()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(MethodOnlyCategorizedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(MethodOnlyCategorizedBenchmarks));

        var only = suite.Benchmarks.First(b => b.DisplayName == "Only");
        Assert.Equal(["Fast"], only.Categories);
    }

    [Fact]
    public void BenchmarkCategoryAttribute_Rejects_Blank_Name()
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkCategoryAttribute("   "));
    }
}

[BenchmarkCategory("String")]
public class CategorizedBenchmarks
{
    [Benchmark]
    [BenchmarkCategory("Fast")]
    [BenchmarkCategory("String")]
    public int Concat() => 1;

    [Benchmark]
    [BenchmarkCategory("Fast")]
    public int Interpolate() => 2;

    [Benchmark]
    [BenchmarkCategory("Slow")]
    public int ManyConcat() => 3;
}

[BenchmarkCategory("Base")]
public class BaseCategorizedBenchmarks
{
    [Benchmark]
    public int Inherited() => 1;
}

public class DerivedCategorizedBenchmarks : BaseCategorizedBenchmarks
{
    [Benchmark]
    public int Declared() => 2;
}

public class MethodOnlyCategorizedBenchmarks
{
    [Benchmark]
    [BenchmarkCategory(" Fast ")]
    [BenchmarkCategory("fast")]
    public int Only() => 1;
}

public class PublicBenchmarks
{
    [Benchmark]
    public void ReturnsNothing()
    {
    }

    [Benchmark]
    public int ReturnsInt() => 42;
}

public class IsolatedMethodBenchmarks
{
    [Benchmark]
    [IsolatedProcess]
    public int Isolated() => 1;

    [Benchmark]
    public int Plain() => 2;
}

public class InProcessMethodBenchmarks
{
    [Benchmark]
    [InProcess]
    public int Forced() => 1;

    [Benchmark]
    public int Plain() => 2;
}

[IsolatedProcess]
public class IsolatedClassBenchmarks
{
    [Benchmark]
    public int A() => 1;

    [Benchmark]
    public int B() => 2;
}

[InProcess]
public class InProcessClassBenchmarks
{
    [Benchmark]
    public int A() => 1;

    [Benchmark]
    public int B() => 2;
}

[IsolatedProcess]
public class MixedIsolationBenchmarks
{
    [Benchmark]
    [InProcess]
    public int OptedOut() => 1;

    [Benchmark]
    public int Inherited() => 2;
}

[IsolatedProcess]
public class BaseIsolatedBenchmarks
{
    [Benchmark]
    public int Inherited() => 1;
}

public class DerivedIsolatedBenchmarks : BaseIsolatedBenchmarks
{
    [Benchmark]
    public int Declared() => 2;
}

public class LifecycleBenchmarks
{
    public int IterationSetupCount;
    public int IterationTeardownCount;
    public int SetupCount;
    public int TeardownCount;

    [BenchmarkSetup]
    public void Setup() => SetupCount++;

    [BenchmarkTeardown]
    public void Teardown() => TeardownCount++;

    [BenchmarkIterationSetup]
    public void IterationSetup() => IterationSetupCount++;

    [BenchmarkIterationTeardown]
    public void IterationTeardown() => IterationTeardownCount++;

    [Benchmark]
    public int Work() => 1;
}

public class AsyncBenchmarks
{
    [Benchmark]
    public async Task<int> ReturnsValueAsync()
    {
        await Task.Yield();
        return 7;
    }

    [Benchmark]
    public Task ReturnsTask() => Task.CompletedTask;
}

internal class InternalBenchmarks
{
    [Benchmark]
    internal void Hidden()
    {
    }
}

internal static class InternalBenchmarksMarker
{
}

public class ParameterisedBenchmarks
{
    [BenchmarkArguments(100)]
    [BenchmarkArguments(1000)]
    [Benchmark]
    public int Compute(int n) => n;

    [BenchmarkArguments("a", 3)]
    [Benchmark]
    public string Concat(string value, int times) => string.Concat(Enumerable.Repeat(value, times));
}
