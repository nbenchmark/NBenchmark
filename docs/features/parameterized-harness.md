---
title: "Parameterized benchmarks: Harness mode"
description: Run a benchmark body across multiple input values using BenchmarkCase and BenchmarkCases attributes in BenchmarkHarness.
order: 4
---

# Parameterized benchmarks: Harness mode

Parameterized benchmarks run the same method body across multiple input values, producing one benchmark entry per parameter combination. This is useful for comparing algorithms at different scales, testing multiple configurations, or sweeping a parameter space.

In Harness mode, parameterized benchmarks use the `[BenchmarkCase]` and `[BenchmarkCases]` attributes. The method must accept parameters that match the argument types.

## `[BenchmarkCase]` - inline literal cases

Apply the attribute multiple times, once per argument set:

```csharp
using NBenchmark.Attributes;

public class SortingBenchmarks
{
    [BenchmarkCase(10)]
    [BenchmarkCase(1_000)]
    [BenchmarkCase(100_000)]
    [Benchmark]
    public void Sort(int n)
    {
        var arr = Enumerable.Range(0, n).Reverse().ToArray();
        Array.Sort(arr);
    }
}
```

Each case becomes a separate benchmark entry named `Sort(n=10)`, `Sort(n=1000)`, and `Sort(n=100000)`. For methods with multiple parameters, the engine uses the method-parameter names in the display name:

```csharp
[BenchmarkCase(100, "asc")]
[BenchmarkCase(100, "desc")]
[BenchmarkCase(10_000, "asc")]
[BenchmarkCase(10_000, "desc")]
[Benchmark]
public void Sort(int count, string order)
{
    var data = order == "desc"
        ? Enumerable.Range(0, count).Reverse().ToArray()
        : Enumerable.Range(0, count).ToArray();
    Array.Sort(data);
}
// Names: Sort(count=100, order=asc), Sort(count=100, order=desc), Sort(count=10000, order=asc), Sort(count=10000, order=desc)
```

## `[BenchmarkCases]` - programmatic case sources

For generated values, file-backed inputs, or large parameter sweeps, reference a source method that yields named value tuples:

```csharp
[BenchmarkCases(nameof(SortCases))]
[Benchmark]
public void Sort(int count, string order)
{
    var data = order == "desc"
        ? Enumerable.Range(0, count).Reverse().ToArray()
        : Enumerable.Range(0, count).ToArray();
    Array.Sort(data);
}

public static IEnumerable<(int Count, string Order)> SortCases()
{
    yield return (10, "asc");
    yield return (10, "desc");
    yield return (1_000, "asc");
    yield return (1_000, "desc");
}
```

If the tuple elements are named (such as `(int Count, string Order)`), the display name uses those names: `Sort(Count=10, Order=asc)`. Unnamed tuples fall back to the method's own parameter names: `Sort(count=10, order=asc)`.

The source method can be `static` or instance, and `public` or `non-public`. Using a static source is recommended because instance sources receive a bare `Activator.CreateInstance` result at discovery time.

## Choosing an attribute

| Use case | Attribute |
| --- | --- |
| Small literal list (2-5 values) | `[BenchmarkCase]` |
| Generated values, file/database-backed inputs, parameter sweeps, or large lists | `[BenchmarkCases]` |
| Named display names for readability in reports | `[BenchmarkCases]` with named tuples |

The two attributes are mutually exclusive on a method; use only one.

## Baselines in harness mode

When `[Benchmark(Baseline = true)]` is applied to a parameterized method, the engine marks **all** expanded cases from that method as baselines:

```csharp
[BenchmarkCase(10)]
[BenchmarkCase(100)]
[Benchmark(Baseline = true)]
public void LinearSearch(int size) => Search(size);

[BenchmarkCase(10)]
[BenchmarkCase(100)]
[Benchmark]
public void BinarySearch(int size) => Search(size);
```

## Significance in harness mode

Harness mode computes significance **per class**. When a class has parameterized results, the engine groups comparisons by `ParameterSet`, so each parameter combination is tested independently. Non-parameterized results in the same class form their own group.

## Harness mode filtering

Use the `--filter` flag on the CLI to select specific cases by display name:

```bash
dotnet run -- --filter "Sort*100*"   # Runs Sort(n=100) and Sort(n=100000)
```

## Reading the report

Console and Markdown reporters consolidate a parameterized benchmark into a **single comparison table** - one table per class in harness mode. Each parameter becomes its own column, and the `Benchmark` column shows the base method name without its parameter suffix. 

The table shape, the per-parameter-group baseline/ratio/significance rules, and the single-method sweep ranking are identical to Suite mode. For a full explanation and examples, see [Reading the report](./parameterized-suite.md#reading-the-report). The only difference is the grouping: one table per class rather than one table for the whole suite.

CSV and JSON reporters provide one record per result, each carrying its full `ParameterSet`.

## Accessing results

Each case is a separate `BenchmarkResult` with the display name in the `Name` property and structured values in `ParameterSet`:

```csharp
var results = await BenchmarkHarness.Create(args)
    .AddFromAssembly<SortingBenchmarks>()
    .RunAsync();

foreach (var r in results)
{
    Console.WriteLine($"{r.Name}: {r.Median:F0} ns");
    // Names: "Sort(n=10)", "Sort(n=1000)", "Sort(n=100000)"
    // r.ParameterSet carries the parsed parameter names and values.
}
```

## Suite vs. Harness mode comparison

| Feature | Suite (`WithParameter`) | Harness (`[BenchmarkCase]` / `[BenchmarkCases]`) |
| --- | --- | --- |
| Declaration | Fluent lambda + `WithParameter` call | Attribute on method |
| Parameter types | Primitives, enums, strings, null | Any type matching method signature |
| Multi-parameter | `WithParameter<T1, T2>` / `WithParameter<T1, T2, T3>` | Method parameter names or named tuples |
| Display name | `sort(size=10)` | `Sort(n=10)` or `Sort(count=10, order=asc)` |
| Significance | Per-parameter-group | Per-parameter-group within each class |
| Per-case baseline | All expanded variants share baseline flag | All expanded variants share baseline flag |
| Result metadata | `ParameterSet` property on `BenchmarkResult` | `ParameterSet` property on `BenchmarkResult` |
| CLI filtering | N/A (programmatic only) | `--filter` by display name |

## See also

For more information, see the following pages:

- [Parameterized benchmarks: Suite mode](./parameterized-suite.md) - The `WithParameter` fluent API.
- [Harness mode](../usage-modes/harness-mode.md) - Attribute-based discovery and CLI.
- [Categories](./categories.md) - How to tag and filter benchmarks.
- [Configuration](../reference/configuration.md) - All measurement options.
