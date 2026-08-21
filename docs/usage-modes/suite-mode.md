---
title: "Suite mode: BenchmarkSuite"
description: Compare multiple implementations side-by-side using the fluent BenchmarkSuite API.
order: 2
---

# Suite mode: BenchmarkSuite

> [!TIP]
> For advanced features, see the Features section: [Parameterized benchmarks](../features/parameterized-suite.md), [categories](../features/categories.md), [isolated runs](../features/isolated-runs.md), [multi-runtime comparison](../features/multi-runtime.md), and [multiple launches](../features/multiple-launches.md).

`BenchmarkSuite` is a fluent builder for running and comparing several benchmarks in a single run. It automatically handles run ordering, significance testing, setup and teardown, and reporter output.

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

### Synchronous benchmarks

```csharp
suite.Add("name", () => DoWork());

// Return a value to prevent dead-code elimination
suite.Add("name", () => ComputeHash(data));
```

### Async benchmarks

```csharp
suite.Add("name", async () => await FetchDataAsync());

// Async with return value
suite.Add("name", async () => await ComputeAsync(input));
```

### Per-benchmark setup and teardown

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
> Setup and teardown time is **not** included in the measurement. NBenchmark only times the `action`.

### Using categories

Tag a benchmark with categories and filter the suite before running. For the full filtering model, see [Categories](../features/categories.md).

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

### Unique benchmark names

Every name within a suite must be distinct. The significance test keys raw samples by name; duplicate names corrupt the results and cause an `ArgumentException`.

```csharp
// This throws ArgumentException:
suite.Add("sort", SortA).Add("sort", SortB);
```

## Fluent configuration

Configuration methods return `this`, allowing you to chain them:

```csharp
await new BenchmarkSuite("name")
    .Add(...)
    .Add(...)
    .WithBaseline("name")           // Specifies the 1.00x reference benchmark
    .WithParameter("size", 10, 100) // Expands parameterized benchmarks across values
    .WithIterations(200)            // Sets measured samples (default: auto)
    .WithWarmup(25)                 // Sets warmup samples (default: auto)
    .WithLaunchCount(3)             // Repeats each benchmark 3 times as separate launches (default: 1)
    .WithAllocations()              // Enables allocation tracking
    .WithOutlierMode(OutlierMode.IqrFence)   // Default outlier mode
    .WithOutlierDetector(new MyDetector())   // Uses a custom IOutlierDetector (overrides WithOutlierMode)
    .WithConfidenceLevel(0.99)      // Default: 0.95
    .WithSignificanceLevel(0.05)    // Sets alpha for the significance test; default: 0.05
    .WithSignificance(false)        // Disables significance testing
    .WithSignificanceTest(new MyTest())   // Uses a custom ISignificanceTest
    .WithRunOrder(RunOrder.Declaration)   // Default: RunOrder.Random
    .WithSeed(1234)                 // Pins the shuffle seed for a reproducible order
    .WithSuiteSetup(() => { })      // Runs once before all benchmarks
    .WithSuiteTeardown(() => { })   // Runs once after all benchmarks
    .WithIsolation(false)           // Measures in the host process; the default is a worker
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

For more details on every option, see [Configuration](../reference/configuration.md).

## Custom statistics

The suite uses the same pluggable statistics as the rest of the engine. By default, it trims outliers with the IQR fence and tests significance with `DefaultSignificanceTest` - using Mann-Whitney U for two benchmarks, and the Kruskal-Wallis omnibus test (followed by post-hoc pairwise Mann-Whitney U with Holm-Bonferroni correction) for three or more.

You can override these when your data requires a different approach:

```csharp
using NBenchmark.Stats;

await new BenchmarkSuite("latency")
    .Add("a", RunA)
    .Add("b", RunB)
    .Add("c", RunC)
    .WithOutlierDetector(new KeepFastestDetector(0.90))   // Custom trimming
    .WithSignificanceTest(new MedianRatioSignificanceTest(25))   // Custom significance rule
    .RunAsync();
```

`WithOutlierDetector` takes priority over `WithOutlierMode`. For the interfaces and contracts, see [Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors) and [Custom significance tests](../statistics/significance.md#custom-significance-tests).

## Setting a baseline

Call `WithBaseline("name")` to designate one benchmark as the reference point. The **Ratio** column in the output shows the speed of each other benchmark relative to the baseline, and NBenchmark tests significance against this reference.

If you do not set a baseline, NBenchmark uses the benchmark with the lowest median as the implicit baseline for ratio calculations.

## Suite setup and teardown

`WithSuiteSetup` and `WithSuiteTeardown` run once around the entire suite. Use these for starting a server, opening a connection, or initializing shared state:

```csharp
await new BenchmarkSuite("http")
    .WithSuiteSetup(() => server.Start())
    .WithSuiteTeardown(() => server.Stop())
    .Add("get", async () => await httpClient.GetStringAsync("/"))
    .Add("post", async () => await httpClient.PostAsync("/", content))
    .RunAsync();
```

Once suite setup succeeds, NBenchmark guarantees that suite teardown runs - even if the run is canceled through a `CancellationToken`. This ensures that resources opened in setup are always released.

## Multi-runtime comparison

Use `WithRuntimes` to run the same benchmarks across multiple .NET runtimes and compare the results side-by-side. For the full guide, see [Multi-runtime comparison](../features/multi-runtime.md).

## Process isolation

Suites are measured in a dedicated worker process by default. This requires no configuration and no changes to how you write your benchmarks:

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort())
    .Add("array", () => ArraySort())
    .WithBaseline("bubble")
    .RunAsync();
```

The whole suite shares one worker, which keeps every ratio between its benchmarks a paired, within-process comparison. Use the following options for common variations:

- **`BenchmarkSuite.Over("name", () => BuildData())`**: The worker builds the state itself. Use this for prepared data that is large, live (such as a `Stream` or a `DbConnection`), or otherwise cannot be copied. This method also types each body's parameter:

  ```csharp
  await BenchmarkSuite.Over("sorting", () => BuildData())   // Built once per benchmark, in the worker
      .Add("array", d => Array.Sort(d))
      .Add("linq",  d => d.OrderBy(x => x).ToArray())
      .RunAsync();
  ```

- **`AddInProcess(name, body)`**: Measures one benchmark in the host process while the rest of the suite stays in a worker. Use this when a single body holds an object that cannot cross process boundaries, such as a live handle or a warm cache.

- **`WithIsolation(false)`**: Opts the entire suite into the host process.

If a benchmark requires a worker but cannot have one, NBenchmark fails the run rather than quietly falling back to a host-process measurement. For a list of what cannot cross process boundaries and the corresponding remedies, see [Isolated runs](../features/isolated-runs.md#when-isolation-is-refused).

## Multiple launches

Use `WithLaunchCount(n)` to run each benchmark in the suite $N$ times as independent launches. For the full guide, see [Multiple launches](../features/multiple-launches.md).

## Parameterized benchmarks

Use `WithParameter` and typed `Add` overloads to run the same benchmark body across multiple input values. Each parameter combination produces a separate benchmark entry with a distinct name, such as `"sort(size=10)"`. For the full guide, see [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md).

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

By default, benchmarks run in a **random** order using a Fisher-Yates shuffle. This prevents systematic bias where the first benchmark always benefits from a warm CPU cache.

```csharp
.WithRunOrder(RunOrder.Declaration)   // Run in the order Add() was called
.WithRunOrder(RunOrder.Random)        // Default
```

## Multiple reporters

You can attach any number of reporters. Each reporter receives the same results:

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

Pass the same values you provide to `WithIterations` and `WithWarmup` to ensure the progress display is accurate.

## Return value

`RunAsync()` returns `IReadOnlyList<BenchmarkResult>`. You can process the results programmatically after the run:

```csharp
var results = await suite.RunAsync();

foreach (var result in results.Where(r => !r.Errored))
    Console.WriteLine($"{result.Name}: {result.Median:F0} ns median");
```

Errored benchmarks have `result.Errored == true` and a message in `result.ErrorMessage`. NBenchmark includes them in the list so reporters can display them.

## Next steps

- [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) - Run benchmarks across multiple input values
- [Multi-runtime comparison](../features/multi-runtime.md) - Compare across .NET runtimes
- [Multiple launches](../features/multiple-launches.md) - Measure run-to-run variance
- [Isolated runs](../features/isolated-runs.md) - Run in a clean worker
- [Harness mode: BenchmarkHarness](./harness-mode.md) - Attribute-based discovery and CLI control
- [Configuration](../reference/configuration.md) - Full options reference
- [Reporters](../output/index.md) - All available reporters
