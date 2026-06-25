---
title: "Suite mode: BenchmarkSuite"
description: Compare multiple implementations side-by-side using the fluent BenchmarkSuite API.
order: 2
---

# Suite mode: BenchmarkSuite

> **Advanced features:** [Parameterized benchmarks](../features/parameterized-suite.md), [categories](../features/categories.md), [isolated runs](../features/isolated-runs.md), [multi-runtime comparison](../features/multi-runtime.md), and [multiple launches](../features/multiple-launches.md) are covered in the Features section.

`BenchmarkSuite` is a fluent builder for running several benchmarks in the same run and comparing them. It handles run ordering, significance testing, setup and teardown, and reporter output automatically.

## Minimal example

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("sorting")
    .Add("bubble", () =>
    {
        var arr = Enumerable.Range(0, 100).Reverse().ToArray();
        Array.Sort(arr);
    })
    .Add("linq", () =>
    {
        _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray();
    })
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## Adding benchmarks

### Synchronous

```csharp
suite.Add("name", () => DoWork());

// Return a value - prevents dead-code elimination
suite.Add("name", () => ComputeHash(data));
```

### Async

```csharp
suite.Add("name", async () => await FetchDataAsync());

// Async with return value
suite.Add("name", async () => await ComputeAsync(input));
```

### With per-benchmark setup and teardown

The optional `setup` and `teardown` callbacks run before and after **each iteration**:

```csharp
suite.Add(
    name: "db query",
    action: () => db.Execute("SELECT 1"),
    setup: () => db.BeginTransaction(),
    teardown: () => db.Rollback()
);
```

> [!WARNING]
> Setup and teardown time is **not** included in the measurement. Only the `action` is timed.

### With categories

Tag a benchmark with categories and filter the suite before running. See [Categories](../features/categories.md) for the full filtering model.

```csharp
var results = await new BenchmarkSuite("sorting")
    .Add("bubble", () => { }, categories: ["Classic"])
    .Add("linq", () => { }, categories: ["Modern"])
    .WithCategoryFilter(include: ["Classic"])
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

Use `.WithCategories(params string[])` to apply categories to every subsequent `.Add` call:

```csharp
await new BenchmarkSuite("string")
    .WithCategories("String")
    .Add("concat", () => "a" + "b")
    .Add("interpolate", () => $"a { "b" }")
    .RunAsync();
```

### Benchmark names must be unique

Each name within a suite must be distinct. The significance test keys raw samples by name, so duplicates would corrupt the results.

```csharp
// This throws ArgumentException:
suite.Add("sort", SortA).Add("sort", SortB);
```

## Fluent configuration

All configuration methods return `this`, so they can be chained:

```csharp
await new BenchmarkSuite("name")
    .Add(...)
    .Add(...)
    .WithBaseline("name")           // which benchmark is the 1.00x reference
    .WithParameter("size", 10, 100) // expand parameterized benchmarks across values
    .WithIterations(200)            // pin measured samples (default: auto)
    .WithWarmup(25)                 // pin warmup samples (default: auto)
    .WithLaunchCount(3)             // repeat each benchmark 3 times as separate launches (default: 1)
    .WithAllocations()              // enable allocation tracking
    .WithOutlierMode(OutlierMode.IqrFence)   // default
    .WithOutlierDetector(new MyDetector())   // custom IOutlierDetector (overrides WithOutlierMode)
    .WithConfidenceLevel(0.99)      // default: 0.95
    .WithSignificanceLevel(0.05)    // alpha for the significance test; default: 0.05
    .WithSignificance(false)        // disable significance testing
    .WithSignificanceTest(new MyTest())   // custom ISignificanceTest
    .WithRunOrder(RunOrder.Declaration)   // default: RunOrder.Random
    .WithSuiteSetup(() => { })      // runs once before all benchmarks
    .WithSuiteTeardown(() => { })   // runs once after all benchmarks
    .WithIsolation()                // run the whole suite in one clean child process
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

See [Configuration](../reference/configuration.md) for details on every option.

## Custom statistics

The suite uses the same pluggable statistics as the rest of the engine. By default it trims outliers with the IQR fence and tests significance with `DefaultSignificanceTest` - Mann-Whitney U for two benchmarks, the Kruskal-Wallis omnibus test (followed by post-hoc pairwise Mann-Whitney U with Holm-Bonferroni correction) for three or more. Override either when your data needs it:

```csharp
using NBenchmark.Stats;

await new BenchmarkSuite("latency")
    .Add("a", RunA)
    .Add("b", RunB)
    .Add("c", RunC)
    .WithOutlierDetector(new KeepFastestDetector(0.90))   // custom trimming
    .WithSignificanceTest(new MedianRatioSignificanceTest(25))   // custom significance rule
    .RunAsync();
```

`WithOutlierDetector` takes priority over `WithOutlierMode`. See [Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors) and [Custom significance tests](../statistics/significance.md#custom-significance-tests) for the interfaces and contracts.

## Setting a baseline

Call `WithBaseline("name")` to designate one benchmark as the reference point. The **Ratio** column in the output shows how fast each other benchmark is relative to the baseline, and significance is tested against it.

If no baseline is set, NBenchmark uses the benchmark with the lowest median as the implicit baseline for ratio calculations.

## Suite setup and teardown

`WithSuiteSetup` and `WithSuiteTeardown` run once around the entire suite - useful for starting a server, opening a connection, or initialising shared state:

```csharp
await new BenchmarkSuite("http")
    .WithSuiteSetup(() => server.Start())
    .WithSuiteTeardown(() => server.Stop())
    .Add("get", async () => await httpClient.GetStringAsync("/"))
    .Add("post", async () => await httpClient.PostAsync("/", content))
    .RunAsync();
```

Once suite setup has succeeded, suite teardown is **guaranteed to run** - even when the run is cancelled through a `CancellationToken` - so resources opened in setup are always released.

## Multi-runtime comparison

Use `WithRuntimes` to run the same benchmarks across multiple .NET runtimes and compare results side-by-side. See [Multi-runtime comparison](../features/multi-runtime.md) for the full guide.

## Process isolation

Call `WithIsolation()` to run the **entire suite** in a single freshly spawned child process, so runtime state (JIT warmup, GC pressure, thread-pool state) from the host can't bias the measurements:

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort(data))
    .Add("array", () => Array.Sort(data))
    .WithBaseline("bubble")
    .WithIsolation()        // whole suite runs in one clean child process
    .RunAsync();
```

`WithIsolation(false)` is the default (in-process). The child rebuilds the suite from your own `Main`, so custom `IOutlierDetector` / `ISignificanceTest` instances and suite setup/teardown are preserved. See [Isolated Runs](../features/isolated-runs.md) for the full model.

## Multiple launches

Use `WithLaunchCount(n)` to run each benchmark in the suite N times as independent launches. See [Multiple launches](../features/multiple-launches.md) for the full guide.

## Parameterized benchmarks

Use `WithParameter` and typed `Add` overloads to run the same benchmark body across multiple input values. Each parameter combination produces a separate benchmark entry with a distinct name like `"sort(size=10)"`. See [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) for the full guide.

```csharp
var results = await new BenchmarkSuite("sorting")
    .WithParameter("size", 10, 100, 1000)
    .Add("sort", (int size) =>
    {
        var arr = Enumerable.Range(0, size).Reverse().ToArray();
        Array.Sort(arr);
    })
    .WithRunOrder(RunOrder.Declaration)
    .RunAsync();
```

## Run order

By default benchmarks run in a **random** order (Fisher-Yates shuffle). This guards against systematic bias where the first benchmark always benefits from a warm CPU cache.

```csharp
.WithRunOrder(RunOrder.Declaration)   // run in the order Add() was called
.WithRunOrder(RunOrder.Random)        // default
```

## Multiple reporters

You can attach any number of reporters. They all receive the same results:

```csharp
suite
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithReporter(new CsvReporter("results/"))
```

## Progress display

`ConsoleBenchmarkProgress` (from `NBenchmark.Reporters.Console`) shows warmup and measurement progress for each benchmark:

```csharp
.WithProgress(new ConsoleBenchmarkProgress())
```

Pass the same values you gave to `WithIterations` and `WithWarmup` so the progress display is accurate.

## Return value

`RunAsync()` returns `IReadOnlyList<BenchmarkResult>`. You can process the results programmatically after the run:

```csharp
var results = await suite.RunAsync();

foreach (var result in results.Where(r => !r.Errored))
    Console.WriteLine($"{result.Name}: {result.Median:F0} ns median");
```

Errored benchmarks have `result.Errored == true` and a message in `result.ErrorMessage`. They are included in the list so reporters can display them.

## Next steps

- [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) - run benchmarks across multiple input values
- [Multi-runtime comparison](../features/multi-runtime.md) - compare across .NET runtimes
- [Multiple launches](../features/multiple-launches.md) - measure run-to-run variance
- [Isolated runs](../features/isolated-runs.md) - run in a clean child process
- [Harness mode: BenchmarkHarness](./harness-mode.md) - attribute-based discovery and CLI control
- [Configuration](../reference/configuration.md) - full options reference
- [Reporters](../output/index.md) - all available reporters
