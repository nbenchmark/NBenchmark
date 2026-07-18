using NBenchmark.Attributes;
using NBenchmark.Discovery;
using NBenchmark.Tests.ErrorFixtures;
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

        Assert.Equal(BenchmarkIsolationIntent.PerBenchmark, suite.Benchmarks.First(b => b.Method.Name == "Isolated").Isolation);
        Assert.Equal(BenchmarkIsolationIntent.HarnessDefault, suite.Benchmarks.First(b => b.Method.Name == "Plain").Isolation);
    }

    [Fact]
    public void Discovers_Method_Level_InProcess_Attribute()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(InProcessMethodBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(InProcessMethodBenchmarks));

        Assert.Equal(BenchmarkIsolationIntent.InProcess, suite.Benchmarks.First(b => b.Method.Name == "Forced").Isolation);
        Assert.Equal(BenchmarkIsolationIntent.HarnessDefault, suite.Benchmarks.First(b => b.Method.Name == "Plain").Isolation);
    }

    [Fact]
    public void Class_Level_IsolatedProcess_Attribute_Applies_To_All_Benchmarks()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(IsolatedClassBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(IsolatedClassBenchmarks));

        Assert.All(suite.Benchmarks, b => Assert.Equal(BenchmarkIsolationIntent.PerBenchmark, b.Isolation));
    }

    [Fact]
    public void Class_Level_InProcess_Attribute_Applies_To_All_Benchmarks()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(InProcessClassBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(InProcessClassBenchmarks));

        Assert.All(suite.Benchmarks, b => Assert.Equal(BenchmarkIsolationIntent.InProcess, b.Isolation));
    }

    [Fact]
    public void Method_Level_Isolation_Overrides_Class_Level_Isolation()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(MixedIsolationBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(MixedIsolationBenchmarks));

        Assert.Equal(BenchmarkIsolationIntent.InProcess, suite.Benchmarks.First(b => b.Method.Name == "OptedOut").Isolation);
        Assert.Equal(BenchmarkIsolationIntent.PerBenchmark, suite.Benchmarks.First(b => b.Method.Name == "Inherited").Isolation);
    }

    [Fact]
    public void Class_Level_IsolatedProcess_Attribute_Is_Inherited_By_Derived_Classes()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(DerivedIsolatedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(DerivedIsolatedBenchmarks));

        Assert.All(suite.Benchmarks, b => Assert.Equal(BenchmarkIsolationIntent.PerBenchmark, b.Isolation));
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
    public void Expands_BenchmarkCase_Into_One_Definition_Per_Attribute()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));

        var compute = suite.Benchmarks.Where(b => b.Method.Name == "Compute").ToList();
        Assert.Equal(2, compute.Count);
        Assert.Equal("Compute(n=100)", compute[0].DisplayName);
        Assert.Equal("Compute(n=1000)", compute[1].DisplayName);
    }

    [Fact]
    public void BenchmarkCase_Bound_Delegate_Invokes_With_The_Bound_Values()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));
        var benchmark = suite.Benchmarks.First(b => b.DisplayName == "Compute(n=1000)");

        var result = benchmark.SyncDelegate!(new ParametricBenchmarks());
        Assert.Equal(1000, result);
    }

    [Fact]
    public void Formats_Multiple_And_String_Arguments_In_DisplayName()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));

        var concat = suite.Benchmarks.First(b => b.Method.Name == "Concat");
        Assert.Equal("Concat(value=a, times=3)", concat.DisplayName);

        var result = concat.SyncDelegate!(new ParametricBenchmarks());
        Assert.Equal("aaa", result);
    }

    [Fact]
    public void Expands_BenchmarkCases_Into_One_Definition_Per_Tuple()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));

        var multiply = suite.Benchmarks.Where(b => b.Method.Name == "Multiply").ToList();
        Assert.Equal(3, multiply.Count);
        Assert.Equal("Multiply(a=2, b=3)", multiply[0].DisplayName);
        Assert.Equal("Multiply(a=5, b=7)", multiply[1].DisplayName);
        Assert.Equal("Multiply(a=10, b=20)", multiply[2].DisplayName);
    }

    [Fact]
    public void DisplayName_Uses_Tuple_Element_Names_When_Available()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));

        var multiply = suite.Benchmarks.First(b => b.DisplayName == "Multiply(a=2, b=3)");
        Assert.NotNull(multiply);
    }

    [Fact]
    public void DisplayName_Falls_Back_To_Method_Parameter_Names_For_Unnamed_Tuples()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(UnnamedTupleCaseBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(UnnamedTupleCaseBenchmarks));

        var add = suite.Benchmarks.Where(b => b.Method.Name == "Add").ToList();
        Assert.Equal(2, add.Count);
        Assert.Equal("Add(a=1, b=2)", add[0].DisplayName);
        Assert.Equal("Add(a=3, b=4)", add[1].DisplayName);
    }

    [Fact]
    public void BenchmarkCases_Resolves_Static_And_Instance_Sources()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));

        var divide = suite.Benchmarks.Where(b => b.Method.Name == "Divide").ToList();
        Assert.Equal(2, divide.Count);
        Assert.Equal("Divide(x=10, y=2)", divide[0].DisplayName);
        Assert.Equal("Divide(x=20, y=4)", divide[1].DisplayName);
    }

    [Fact]
    public void Source_Method_Must_Return_IEnumerable_Of_ValueTuple()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new BenchmarkDiscoverer().Discover(typeof(InvalidReturnTypeCasesBenchmarks)));

        Assert.Contains("ValueTuple", ex.Message);
    }

    [Fact]
    public void Source_Tuple_Arity_Must_Match_Method_Arity()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new BenchmarkDiscoverer().Discover(typeof(ArityMismatchCasesBenchmarks)));

        Assert.Contains("parameter", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ArityMismatchCasesBenchmarks.Sum", ex.Message);
        Assert.DoesNotContain("MismatchCases expects", ex.Message);
    }

    [Fact]
    public void Source_Must_Be_Parameterless()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new BenchmarkDiscoverer().Discover(typeof(ParamSourceCasesBenchmarks)));

        Assert.Contains("no parameters", ex.Message);
    }

    [Fact]
    public void BaselineParametric_All_Cases_Share_Baseline_Flag()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(BaselineParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(BaselineParametricBenchmarks));

        var compute = suite.Benchmarks.Where(b => b.Method.Name == "Compute").ToList();
        Assert.Equal(2, compute.Count);
        Assert.All(compute, b => Assert.True(b.IsBaseline));

        var multiply = suite.Benchmarks.Where(b => b.Method.Name == "Multiply").ToList();
        Assert.Equal(2, multiply.Count);
        Assert.All(multiply, b => Assert.True(b.IsBaseline));
    }

    [Fact]
    public void BenchmarkCase_Definition_Includes_ParameterSet()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));

        var compute100 = suite.Benchmarks.First(b => b.DisplayName == "Compute(n=100)");
        Assert.Single(compute100.ParameterSet);
        Assert.Equal("n", compute100.ParameterSet[0].Name);
        Assert.Equal(100, compute100.ParameterSet[0].Value);

        var compute1000 = suite.Benchmarks.First(b => b.DisplayName == "Compute(n=1000)");
        Assert.Single(compute1000.ParameterSet);
        Assert.Equal(1000, compute1000.ParameterSet[0].Value);
    }

    [Fact]
    public void BenchmarkCases_Definition_Includes_ParameterSet()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(ParametricBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(ParametricBenchmarks));

        var multiply = suite.Benchmarks.First(b => b.DisplayName == "Multiply(a=2, b=3)");
        Assert.Equal(2, multiply.ParameterSet.Count);
        Assert.Equal("a", multiply.ParameterSet[0].Name);
        Assert.Equal(2, multiply.ParameterSet[0].Value);
        Assert.Equal("b", multiply.ParameterSet[1].Name);
        Assert.Equal(3, multiply.ParameterSet[1].Value);
    }

    [Fact]
    public void BenchmarkCase_And_BenchmarkCases_Cannot_Coexist()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new BenchmarkDiscoverer().Discover(typeof(ConflictCasesBenchmarks)));

        Assert.Contains("both", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void BenchmarkCategoryAttribute_Rejects_Blank_Name() => Assert.Throws<ArgumentException>(() => new BenchmarkCategoryAttribute("   "));

    [Fact]
    public void Discovers_Class_Level_Runtimes_Attribute()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(RuntimeAttributedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(RuntimeAttributedBenchmarks));

        Assert.Equal([RuntimeMoniker.Net8, RuntimeMoniker.Net9], suite.Runtimes);
    }

    [Fact]
    public void Class_Level_Runtimes_Attribute_Is_Inherited_By_Derived_Classes()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(DerivedRuntimeAttributedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(DerivedRuntimeAttributedBenchmarks));

        Assert.Equal([RuntimeMoniker.Net8, RuntimeMoniker.Net9], suite.Runtimes);
    }

    [Fact]
    public void Derived_Class_Runtimes_Attribute_Overrides_Base()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(OverridingRuntimeAttributedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(OverridingRuntimeAttributedBenchmarks));

        Assert.Equal([RuntimeMoniker.Net10], suite.Runtimes);
    }

    [Fact]
    public void No_Runtimes_Attribute_Returns_Empty_List()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(PublicBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(PublicBenchmarks));

        Assert.Empty(suite.Runtimes);
    }

    [Fact]
    public void Empty_Runtimes_Attribute_Is_NoOp()
    {
        var suites = new BenchmarkDiscoverer().Discover(typeof(EmptyRuntimeAttributedBenchmarks).Assembly);
        var suite = suites.First(s => s.Type == typeof(EmptyRuntimeAttributedBenchmarks));

        Assert.Empty(suite.Runtimes);
    }
}

[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9)]
public class RuntimeAttributedBenchmarks
{
    [Benchmark]
    public int A() => 1;

    [Benchmark]
    public int B() => 2;
}

[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9)]
public class BaseRuntimeAttributedBenchmarks
{
    [Benchmark]
    public int Inherited() => 1;
}

public class DerivedRuntimeAttributedBenchmarks : BaseRuntimeAttributedBenchmarks
{
    [Benchmark]
    public int Declared() => 2;
}

[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9)]
public class BaseOverridingRuntimeAttributedBenchmarks
{
    [Benchmark]
    public int Inherited() => 1;
}

[Runtimes(RuntimeMoniker.Net10)]
public class OverridingRuntimeAttributedBenchmarks : BaseOverridingRuntimeAttributedBenchmarks
{
    [Benchmark]
    public int Declared() => 2;
}

[Runtimes]
public class EmptyRuntimeAttributedBenchmarks
{
    [Benchmark]
    public int A() => 1;
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

public class ParametricBenchmarks
{
    [BenchmarkCase(100)]
    [BenchmarkCase(1000)]
    [Benchmark]
    public int Compute(int n) => n;

    [BenchmarkCase("a", 3)]
    [Benchmark]
    public string Concat(string value, int times) => string.Concat(Enumerable.Repeat(value, times));

    [BenchmarkCases(nameof(MultiplyCases))]
    [Benchmark]
    public int Multiply(int a, int b) => a * b;

    public static IEnumerable<(int a, int b)> MultiplyCases()
    {
        yield return (2, 3);
        yield return (5, 7);
        yield return (10, 20);
    }

    [BenchmarkCases(nameof(DivideCases))]
    [Benchmark]
    public int Divide(int x, int y) => x / y;

    public IEnumerable<(int x, int y)> DivideCases()
    {
        yield return (10, 2);
        yield return (20, 4);
    }
}

public class UnnamedTupleCaseBenchmarks
{
    [BenchmarkCases(nameof(AddCases))]
    [Benchmark]
    public int Add(int a, int b) => a + b;

    public static IEnumerable<ValueTuple<int, int>> AddCases()
    {
        yield return (1, 2);
        yield return (3, 4);
    }
}

public class BaselineParametricBenchmarks
{
    [BenchmarkCase(10)]
    [BenchmarkCase(100)]
    [Benchmark(Baseline = true)]
    public int Compute(int n) => n;

    [BenchmarkCases(nameof(Sizes))]
    [Benchmark(Baseline = true)]
    public int Multiply(int a, int b) => a * b;

    public static IEnumerable<(int a, int b)> Sizes()
    {
        yield return (2, 3);
        yield return (5, 7);
    }
}
