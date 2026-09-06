---
title: Quick Start
description: Write your first benchmark and understand the output.
order: 3
---

# Quick Start

## Your first benchmark

Use the `Benchmark.Run` method to measure a method. This requires no special project structure or configuration.

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

Run the project with `dotnet run` to see output similar to the following:

```text
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 342.1 ns       Ops/s: 2.87 Mops/s
  │  Alloc/op: 0 B
  │
  │  Measured in an isolated worker under 'steady-state'.
  │
  └─────────────────────────────────────────────────
```

The engine warms up your code until the timings settle, samples it until the result is sufficiently precise, trims outliers, and measures it in a fresh process to ensure that previous program state does not affect the results.

## View more detailed results

Pass a detail level to the `Print` method to view a full statistical picture:

```csharp
result.Print(ReportDetail.Standard);
```

The output is similar to the following:

```text
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
  │  Measured in an isolated worker under 'steady-state'.
  │
  └─────────────────────────────────────────────────
```

Use `ReportDetail.Advanced` to include quartiles, fences, and distribution shape. For more information, see [Report detail levels](../output/report-detail-levels.md).

## Measure async code

Use `Benchmark.RunAsync` to measure asynchronous code:

```csharp
var result = await Benchmark.RunAsync(async () =>
{
    await Task.Delay(1);
});

result.Print();
```

## Measure a return value

If your benchmark returns a value, use the generic overload to prevent the compiler from optimizing the call away:

```csharp
var result = Benchmark.Run(() => int.Parse("12345"));
result.Print();
```

## Compare two implementations

Use `BenchmarkSuite` to compare multiple approaches side-by-side:

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

[![NBenchmark console output showing a suite comparison table with ratio and significance columns](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)

The **Ratio** column shows speed relative to the baseline. The **Sig** column shows **✓** when the difference is statistically significant and **✗** when it is not.

## Save results to a file

You can export results to various file formats:

```csharp
var result = Benchmark.Run(() => MyMethod());

await result.SaveMarkdownAsync("results.md");
await result.SaveCsvAsync("results.csv");
await result.SaveJsonAsync("results/");   // specifies a directory
```

## Understand the numbers

- **Median**: The middle measurement, which is not affected by outliers. Use this number for quoting results.
- **Ops/s**: The measurement rate.
- **Error**: Indicates how precise the mean is.
- **Ratio**: Appears during comparisons. A value of `0.75x` means the implementation is 25% faster than the baseline.
- **Sig**: Appears during comparisons. A **✓** indicates the difference is statistically real rather than noise.

For a detailed explanation of every column, indicator, and warning, see [Reading your results](./reading-your-results.md).

> [!NOTE]
> By default, your benchmark runs in a separate process. This ensures reproducibility because JIT and GC settings can only be configured for a process before it starts. For more information, see [Isolated runs](../features/isolated-runs.md).

## Next steps

For more information, see the following pages:

1. [Reading your results](./reading-your-results.md) - Understand what the output is telling you.
2. [Key concepts](./key-concepts.md) - Learn about warmup, outliers, and confidence.
3. [Usage modes](../usage-modes/) - Explore suite mode, harness mode, and the global tool.
4. [Guides](../guides/) - Find complete recipes for CI gates, refactor comparisons, and more.
