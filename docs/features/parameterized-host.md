---
title: "Parameterized benchmarks: Host mode"
description: Run a benchmark body across multiple input values using BenchmarkCase and BenchmarkCases attributes in BenchmarkHost.
order: 3
---

# Parameterized benchmarks: Host mode

Parameterized benchmarks run the same method body across multiple input values, producing one benchmark entry per parameter combination. This is useful for comparing algorithms at different scales, testing multiple configurations, or sweeping a parameter space.

In Host mode, parameterized benchmarks use the `[BenchmarkCase]` and `[BenchmarkCases]` attributes. The method must accept parameters matching the argument types.

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

Each case becomes a separate benchmark entry named `Sort(n=10)`, `Sort(n=1000)`, `Sort(n=100000)`. Multi-parameter methods use method-parameter names in the display name:

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

When the tuple elements are named (e.g. `(int Count, string Order)`), the display name uses those names: `Sort(Count=10, Order=asc)`. Unnamed tuples fall back to the method's own parameter names: `Sort(count=10, order=asc)`.

The source method can be `static` or instance, `public` or `non-public`. A static source is recommended since instance sources receive a bare `Activator.CreateInstance` result at discovery time.

## Choosing between the two

| Use case | Attribute |
|---|---|
| Small literal list (2-5 values) | `[BenchmarkCase]` |
| Generated values, file/database-backed inputs, parameter sweeps, large lists | `[BenchmarkCases]` |
| Named display names for readability in reports | `[BenchmarkCases]` with named tuples |

The two attributes are mutually exclusive on a method. Use one or the other.

## Baselines in host mode

When `[Benchmark(Baseline = true)]` is applied to a parameterized method, **all** expanded cases from that method are marked as baseline:

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

## Significance in host mode

Host mode computes significance **per class**. When a class has parameterized results, comparisons are grouped by `ParameterSet`, so each parameter combination is tested independently. Non-parameterized results in the same class form their own group.

## Host mode filtering

Use `--filter` on the CLI to select specific cases by display name:

```bash
dotnet run -- --filter "Sort*100*"   # runs Sort(n=100) and Sort(n=100000)
```

## Reading the report

Console and Markdown reporters consolidate a parameterized benchmark into a **single comparison table** - one table per class in host mode. Each parameter becomes its own column, and the `Benchmark` column shows the base method name without its parameter suffix:

```text
search benchmarks
Benchmark | size | Median   | Mean     | Ops/s      | Ratio             | Sig | Mag   | Alloc/op
----------+------+----------+----------+------------+-------------------+-----+-------+---------
binary    |   10 |  90.0 ns |  91.2 ns | 11,111,111 | ████ baseline    |  -  |  -    |    32 B
linear    |   10 | 108.0 ns | 109.4 ns |  9,259,259 | █████ 1.20x      |  ✓  | large |    24 B
binary    |  100 | 250.0 ns | 252.1 ns |  4,000,000 | ███████ baseline  |  -  |  -    |    32 B
linear    |  100 | 300.0 ns | 305.7 ns |  3,333,333 | █████████ 1.20x   |  ✓  | large |    24 B
```

Rows are grouped by parameter set in expansion order and sorted by median within each group. To leave room for the parameter columns, parametric tables use the compact labels `Ratio`, `Sig` and `Mag`. When a parameter group holds competing benchmarks, the baseline, ratio, significance (`Sig`) and effect magnitude are computed independently **per parameter group**, so every comparison stays within a single parameter combination.

When a single method is swept across parameter values, every parameter group holds just one benchmark, so there is no within-group comparison. The table instead ranks every row against its fastest point: the `Ratio` column reports each point's scaling factor (the fastest point is the `baseline`), while `Sig` and `Mag` stay `-`, because the engine does not test different workloads against one another. This makes scaling trends easy to read:

```text
LinearSearch benchmarks
Benchmark    | count | Median   | Mean     | Ops/s      | Ratio               | Sig | Mag | Alloc/op
-------------+-------+----------+----------+------------+---------------------+-----+-----+---------
LinearSearch |    10 |  31.2 ns |  32.7 ns | 30,567,164 | █ baseline          |  -  |  -  |     24 B
LinearSearch |   100 | 117.2 ns | 132.2 ns |  7,565,906 | ███ 3.76x           |  -  |  -  |     24 B
LinearSearch |  1000 |  1.40 µs |  1.27 µs |    789,515 | ████████████ 44.87x |  -  |  -  |  4,048 B
```

In a mixed class, a non-parameterized benchmark shows `-` in every parameter column; when no parameter group has a within-group comparison, it joins the table-wide ranking against the fastest row.

CSV and JSON reporters keep one record per result, each carrying its full `ParameterSet`, for machine consumption.

## Accessing results

Each case is a separate `BenchmarkResult` with the display name in the `Name` property and structured values in `ParameterSet`:

```csharp
var results = await BenchmarkHost.Create(args)
    .AddFromAssembly<SortingBenchmarks>()
    .RunAsync();

foreach (var r in results)
{
    Console.WriteLine($"{r.Name}: {r.Median:F0} ns");
    // Names like "Sort(n=10)", "Sort(n=1000)", "Sort(n=100000)"
    // r.ParameterSet carries the parsed parameter names and values.
}
```

## Suite vs. host mode comparison

| Feature | Suite (`WithParameter`) | Host (`[BenchmarkCase]` / `[BenchmarkCases]`) |
|---|---|---|
| Declaration | Fluent lambda + `WithParameter` call | Attribute on method |
| Parameter types | Primitives, enums, strings, null | Any type matching method signature |
| Multi-parameter | `WithParameter<T1, T2>` / `WithParameter<T1, T2, T3>` | Method parameter names or named tuples |
| Display name | `sort(size=10)` | `Sort(n=10)` or `Sort(count=10, order=asc)` |
| Significance | Per-parameter-group | Per-parameter-group within each class |
| Per-case baseline | All expanded variants share baseline flag | All expanded variants share baseline flag |
| Result metadata | `ParameterSet` property on `BenchmarkResult` | `ParameterSet` property on `BenchmarkResult` |
| CLI filtering | N/A (programmatic only) | `--filter` by display name |

## Next steps

- [Parameterized benchmarks: Suite mode](./parameterized-suite.md) - the `WithParameter` fluent API
- [Host mode](../usage-modes/host-mode.md) - attribute-based discovery and CLI
- [Categories](./categories.md) - tag and filter benchmarks
- [Configuration](../reference/configuration.md) - all measurement options