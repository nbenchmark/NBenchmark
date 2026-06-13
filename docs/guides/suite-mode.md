---
title: "Suite mode: BenchmarkSuite"
description: Compare multiple implementations side-by-side using the fluent BenchmarkSuite API.
order: 2
---

# Suite mode: BenchmarkSuite

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
    .WithIterations(200)            // default: 200
    .WithWarmup(25)                 // default: 25
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
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

See [Configuration](../reference/configuration.md) for details on every option.

## Custom statistics

The suite uses the same pluggable statistics as the rest of the engine. By default it trims outliers with the IQR fence and tests significance with `DefaultSignificanceTest` - Mann-Whitney U for two benchmarks, the Kruskal-Wallis omnibus test for three or more. Override either when your data needs it:

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

- [Host mode: BenchmarkHost](./host-mode.md) - attribute-based discovery and CLI control
- [Configuration](../reference/configuration.md) - full options reference
- [Reporters](../reporters/) - all available reporters
