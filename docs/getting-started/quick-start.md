---
title: Quick Start
description: Write your first benchmark and understand the output.
order: 2
---

# Quick Start

## Your first benchmark

The simplest way to measure code is `Benchmark.Run`. Call it anywhere - no special project structure, no configuration.

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

Run with `dotnet run` and you'll see something like:

```
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 342.1 ns       Mean: 348.7 ns
  │  Ops/s:  2.87 Mops/s    Median ops/s: 2.92 Mops/s
  │  P95: 361.2 ns  P99: 378.5 ns  P99.9: 380.0 ns
  │  StdDev: 8.3 ns         CV:   2.38%
  │  Error:  ±3.1 ns (0.89% of Mean)
  │  CI:     [345.6 ns … 351.8 ns] (95%)
  │  Alloc/op: 0 B
  │
  └─────────────────────────────────────────────────
```

That's it. NBenchmark warmed up until the timings plateaued (to let the JIT compile your code), collected enough measured samples to tighten the confidence interval, trimmed outliers using the IQR fence rule, and printed a summary.

## Measuring async code

```csharp
var result = await Benchmark.RunAsync(async () =>
{
    await Task.Delay(1);
});

result.Print();
```

## Measuring a return value

If your benchmark returns a value, use the generic overload. This prevents the compiler from optimising the call away:

```csharp
var result = Benchmark.Run(() => int.Parse("12345"));
result.Print();
```

## Comparing two implementations

To compare two approaches side-by-side, use `BenchmarkSuite`:

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("sorting")
    .Add("Array.Sort", () => { var a = data.ToArray(); Array.Sort(a); })
    .Add("LINQ OrderBy", () => { _ = data.OrderBy(x => x).ToArray(); })
    .WithBaseline("Array.Sort")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

The console output will look like:
[![NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)

The **Ratio** column shows speed relative to the baseline. The **Sig** column shows **✓** when the difference is statistically significant and **✗** when it's not. No symbol means the benchmark is the baseline or significance wasn't tested.

## Saving results to a file

Chain file reporter methods on any `BenchmarkResult`:

```csharp
var result = Benchmark.Run(() => MyMethod());

await result.ToMarkdownAsync("results.md");
await result.ToCsvAsync("results.csv");
await result.ToJsonAsync("results/");   // directory
```

## What each number means

| Value | What it tells you |
|---|---|
| **Median** | The middle value - the most reliable single number. Ignores extreme outliers. |
| **Mean** | The average. Close to the median for stable code; further away when timings vary widely. |
| **Error** | How precisely the mean is estimated (±95% CI). A small Error means the mean is reliable. |
| **StdDev** | How spread out the measurements are. High StdDev = unpredictable timing. |
| **P95 / P99 / P99.9** | Tail-latency percentiles. P95: 95% of measurements completed within this time. Useful for latency budgets. Configurable via `MeasurementOptions.ReportedPercentiles`. |
| **Ratio** | Speed relative to the baseline. `0.75x` = 25% faster; `2.0x` = twice as slow. |
| **Sig** | **✓** = difference from baseline is statistically significant; **✗** = not significant ([p < 0.05](https://en.wikipedia.org/wiki/P-value)). |

See [Key Concepts](./key-concepts.md) for a deeper explanation of what these mean and how they are calculated.

## Next steps

- **[Key Concepts](./key-concepts.md)** - understand warmup, outlier trimming, and the statistics
- **[Usage modes](../usage-modes/)** - detailed coverage of all four usage modes
- **[Configuration](../reference/configuration.md)** - change defaults (iterations, warmup, confidence level, etc.)
