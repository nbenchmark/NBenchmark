using NBenchmark;
using NBenchmark.Reporters.Console;

// SuiteParameters demonstrates parameterised benchmarks in Suite mode using
// WithParameter + typed Add lambdas. Each parameter combination produces one
// benchmark entry with a display name like "sort (size=100)".
//
// Parameter sweeps are measured in a worker process like any other suite. The typed lambda
// `(int size) => ...` captures nothing, so it is addressable; the parameter values travel beside
// its address as serialized constants and the worker binds each one before measuring. That means a
// sweep needs no [BenchmarkPlan] factory and no restructuring - the code below is what you would
// have written anyway.
//
// The one limit is the value type: parameters must be primitives, strings, enums, decimal,
// DateTime, DateTimeOffset, TimeSpan or Guid. Anything else has to be built in the measuring
// process, which is what a [BenchmarkPlan] factory or WithState is for.
//
// Run with: dotnet run --project samples/SuiteParameters

// ---------------------------------------------------------------------------
// Example 1: Single int parameter
// ---------------------------------------------------------------------------
// One WithParameter call expands the benchmark across all supplied values.
Console.WriteLine("=== Example 1: Sorting at different sizes ===");
Console.WriteLine();

var sorting = await new BenchmarkSuite("sorting")
    .WithParameter("size", 10, 100, 1000)
    .Add("sort", (int size) =>
    {
        var arr = Enumerable.Range(0, size).Reverse().ToArray();
        Array.Sort(arr);
    })
    .WithRunOrder(RunOrder.Declaration)
    .WithWarmup(3)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

// Printed so the sample asserts its own fidelity rather than leaving it to the report header.
Console.WriteLine();

foreach (var result in sorting)
{
    Console.WriteLine($"  {result.Name}: {result.IsolationStatus} under '{result.RuntimeProfileName}'");
}

Console.WriteLine();

// ---------------------------------------------------------------------------
// Example 2: Two parameters (Cartesian product)
// ---------------------------------------------------------------------------
// Multiple WithParameter calls produce every combination of values.
Console.WriteLine("=== Example 2: Matrix allocation (rows x cols) ===");
Console.WriteLine();

await new BenchmarkSuite("matrix")
    .WithParameter("rows", 10, 100)
    .WithParameter("cols", 5, 50)
    .Add("allocate", (int rows, int cols) => new int[rows, cols])
    .WithRunOrder(RunOrder.Declaration)
    .WithWarmup(3)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();

// ---------------------------------------------------------------------------
// Example 3: Mixed plain and parameterized
// ---------------------------------------------------------------------------
// A suite can contain plain Add calls alongside parameterized ones. Plain
// benchmarks run once; parameterized ones expand per combination.
Console.WriteLine("=== Example 3: Mixed plain + parameterized ===");
Console.WriteLine();

await new BenchmarkSuite("mixed")
    .Add("constant", () => Thread.SpinWait(100_000))
    .WithParameter("count", 50_000, 200_000)
    .Add("variable", (int count) => Thread.SpinWait(count))
    .WithRunOrder(RunOrder.Declaration)
    .WithWarmup(3)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();

// ---------------------------------------------------------------------------
// Example 4: Baseline with parameters and two competing methods
// ---------------------------------------------------------------------------
// WithBaseline uses the original (unexpanded) benchmark name. The baseline
// flag applies to every expanded variant. Significance is computed per
// parameter group, so "linear (size=10)" and "binary (size=10)" are compared
// independently from "linear (size=100)" and "binary (size=100)".
Console.WriteLine("=== Example 4: Linear vs Binary search with baseline ===");
Console.WriteLine();

await new BenchmarkSuite("search")
    .WithParameter("size", 100, 1000, 10_000)
    .Add("linear", (int size) =>
    {
        var data = Enumerable.Range(0, size).ToArray();
        _ = Array.IndexOf(data, data[^1]);
    })
    .Add("binary", (int size) =>
    {
        var data = Enumerable.Range(0, size).ToArray();
        _ = Array.BinarySearch(data, data[^1]);
    })
    .WithBaseline("binary")
    .WithRunOrder(RunOrder.Declaration)
    .WithWarmup(3)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();

// ---------------------------------------------------------------------------
// Example 5: Enum, string, and nullable parameters
// ---------------------------------------------------------------------------
// WithParameter supports primitives, enums, strings, and null.
Console.WriteLine("=== Example 5: Sorting strategies (enum parameter) ===");
Console.WriteLine();

await new BenchmarkSuite("sorting-strategies")
    .WithParameter("order", SortOrder.Ascending, SortOrder.Descending)
    .WithParameter("count", 50, 200)
    .Add("sort", (SortOrder order, int count) =>
    {
        var data = order == SortOrder.Ascending
            ? Enumerable.Range(0, count).ToArray()
            : Enumerable.Range(0, count).Reverse().ToArray();

        Array.Sort(data);
    })
    .WithRunOrder(RunOrder.Declaration)
    .WithWarmup(3)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();

// ---------------------------------------------------------------------------
// Example 6: Value-returning parameterized benchmarks
// ---------------------------------------------------------------------------
// The typed lambda can return a value, which prevents dead-code elimination.
Console.WriteLine("=== Example 6: Computing hash codes ===");
Console.WriteLine();

await new BenchmarkSuite("hashing")
    .WithParameter("length", 10, 100)
    .Add("hash", (int length) =>
    {
        var s = new string('x', length);
        return s.GetHashCode();
    })
    .WithRunOrder(RunOrder.Declaration)
    .WithWarmup(3)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

// ---------------------------------------------------------------------------
// Enum used by Example 5
// ---------------------------------------------------------------------------
public enum SortOrder
{
    Ascending,
    Descending,
}
