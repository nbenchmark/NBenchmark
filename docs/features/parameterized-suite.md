---
title: "Parameterized benchmarks: Suite mode"
description: Run a benchmark body across multiple input values using WithParameter and typed Add lambdas in BenchmarkSuite.
order: 2
---

# Parameterized benchmarks: Suite mode

Parameterized benchmarks run the same method body across multiple input values, producing one benchmark entry per parameter combination. This is useful for comparing algorithms at different scales, testing multiple configurations, or sweeping a parameter space.

In Suite mode, parameterized benchmarks use `WithParameter` plus a typed `Add` lambda.

## `WithParameter`

Pass a name and values to `WithParameter`, then use a typed lambda in `Add`:

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("sorting")
    .WithParameter("size", 10, 100, 1000)
    .Add("sort", (int size) =>
    {
        var arr = Enumerable.Range(0, size).Reverse().ToArray();
        Array.Sort(arr);
    })
    .WithRunOrder(RunOrder.Declaration)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Produces three benchmarks:
//   sort(size=10)
//   sort(size=100)
//   sort(size=1000)
```

The `Add` lambda accepts one parameter whose type matches the `WithParameter` type argument. Return a value to prevent dead-code elimination:

```csharp
suite.Add("hash", (int size) => ComputeHash(size));
```

## Multiple parameters

Call `WithParameter` once per parameter. The suite generates the Cartesian product of all values:

```csharp
var results = await new BenchmarkSuite("matrix")
    .WithParameter("rows", 10, 100)
    .WithParameter("cols", 5, 50)
    .Add("allocate", (int rows, int cols) => new int[rows, cols])
    .WithRunOrder(RunOrder.Declaration)
    .RunAsync();

// Produces four benchmarks:
//   allocate(rows=10, cols=5)
//   allocate(rows=10, cols=50)
//   allocate(rows=100, cols=5)
//   allocate(rows=100, cols=50)
```

Multi-parameter `Add` overloads accept up to three lambda parameters:

```csharp
suite.Add("work", (int a, int b) => a + b);
suite.Add("work", (int a, int b, int c) => a + b + c);
```

Async and value-returning overloads follow the same pattern as non-parameterized benchmarks:

```csharp
suite.Add("async", async (int size) => await FetchAsync(size));
suite.Add("compute", (int size) => ComputeHash(size));
```

Per-benchmark setup and teardown are also supported on parameterized overloads:

```csharp
suite.Add("db", (int poolSize) => QueryDb(poolSize),
    setup: () => OpenConnection(),
    teardown: () => CloseConnection());
```

## Mixed parameterized and non-parameterized benchmarks

A suite can contain both plain `Add` calls and parameterized `Add` calls. Plain benchmarks run once; parameterized benchmarks expand per parameter combination. All benchmarks share the same `MeasurementOptions`:

```csharp
var results = await new BenchmarkSuite("mixed")
    .Add("plain", () => DoWork())
    .WithParameter("size", 10, 100)
    .Add("param", (int size) => DoWork(size))
    .WithRunOrder(RunOrder.Declaration)
    .RunAsync();

// Produces three benchmarks:
//   plain
//   param(size=10)
//   param(size=100)
```

## Supported parameter types

`WithParameter` accepts primitives, enums, strings, and `null`:

```csharp
suite.WithParameter("value", (string?)null, "hello");
suite.WithParameter("mode", FileMode.Read, FileMode.Write);
suite.WithParameter("count", 1, 10, 100);
```

The following types are supported: `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `char`, `string`, and any `enum`. Passing an unsupported type (e.g. a custom class) throws `ArgumentException`.

## Baselines with parameters

`WithBaseline` uses the **original** benchmark name (before expansion), and the baseline flag applies to every expanded variant:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("linear", (int size) => LinearSearch(size))
    .Add("binary", (int size) => BinarySearch(size))
    .WithBaseline("linear")
    .RunAsync();

// "linear(size=10)" and "linear(size=100)" are both baselines.
// Significance is computed separately within each parameter group.
```

## Significance with parameters

Significance testing groups results by parameter set. Each group is compared independently, so results for `size=10` are only compared against other `size=10` benchmarks, not against `size=100` benchmarks. Non-parameterized benchmarks in the same suite form a single group.

This means a parameterized suite with `N` parameter combinations and `M` benchmark methods produces `N` separate significance comparisons, each over `M` benchmarks - rather than one flat comparison over `N * M` results.

## Categories with parameters

Use `categories` on parameterized `Add` overloads, then filter with `WithCategoryFilter`:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("linear", (int size) => LinearSearch(size), categories: ["Brute"])
    .Add("binary", (int size) => BinarySearch(size), categories: ["Smart"])
    .WithCategoryFilter(include: ["Smart"])
    .RunAsync();

// Only runs "binary(size=10)" and "binary(size=100)"
```

Categories on parameterized benchmarks work identically to categories on non-parameterized benchmarks. See [Categories](./categories.md) for the full filtering model.

## Unique names after expansion

Each expanded name must be unique. Duplicate parameter values produce duplicate names and throw `ArgumentException` at run time:

```csharp
// This throws ArgumentException: "sort(size=10)" appears twice
suite.WithParameter("size", 10, 10)
     .Add("sort", (int size) => Sort(size));
```

## Run order with parameters

When `RunOrder.Random` is used with parameterized benchmarks, the suite shuffles benchmarks **within each parameter group** while keeping groups together. This ensures that results for the same parameter set stay comparable. Non-parameterized benchmarks are shuffled independently by `SuiteRunner` as usual. `RunOrder.Declaration` preserves the expansion order.

```csharp
// Random order shuffles within each parameter group:
await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("linear", (int size) => LinearSearch(size))
    .Add("binary", (int size) => BinarySearch(size))
    .WithRunOrder(RunOrder.Random)  // shuffles within each size group
    .RunAsync();
```

## Process isolation

Isolated runs always execute in declaration order, regardless of `WithRunOrder`. See [Isolated Runs](./isolated-runs.md) for the full model.

## Reading the report

Console and Markdown reporters consolidate a parameterized benchmark into a **single comparison table** - one table for the whole suite. Each parameter becomes its own column, and the `Benchmark` column shows the base method name without its parameter suffix:

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

In a mixed suite, a non-parameterized benchmark shows `-` in every parameter column; when no parameter group has a within-group comparison, it joins the table-wide ranking against the fastest row.

CSV and JSON reporters keep one record per result, each carrying its full `ParameterSet`, for machine consumption.

## Accessing results

Each expanded benchmark produces its own `BenchmarkResult` with the `ParameterSet` property set:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("binary", (int size) => BinarySearch(size))
    .RunAsync();

foreach (var r in results)
{
    Console.WriteLine($"{r.Name}: {r.Median:F0} ns");
    // r.ParameterSet[0].Name  -> "size"
    // r.ParameterSet[0].Value -> 10 or 100
}
```

## Next steps

- [Parameterized benchmarks: Harness mode](./parameterized-harness.md) - the `[BenchmarkCase]` / `[BenchmarkCases]` attribute API
- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Categories](./categories.md) - tag and filter benchmarks
- [Configuration](../reference/configuration.md) - all measurement options