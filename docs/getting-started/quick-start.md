---
title: Quick Start
description: Write your first benchmark and understand the output.
order: 3
---

# Quick Start

## Your first benchmark

Call `Benchmark.Run` anywhere - no special project structure, no configuration.

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

Run it with `dotnet run` and you'll see:

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

That's it. Your code was warmed up until the timings settled, sampled until the result was precise
enough, trimmed of outliers, and measured in a fresh process so nothing your program did earlier
could affect it.

## Want more numbers?

Pass a detail level to `Print` for the full statistical picture:

```csharp
result.Print(ReportDetail.Standard);
```

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

`ReportDetail.Advanced` adds quartiles, fences, and distribution shape. See
[Report detail levels](../output/report-detail-levels.md).

## Measuring async code

```csharp
var result = await Benchmark.RunAsync(async () =>
{
    await Task.Delay(1);
});

result.Print();
```

## Measuring a return value

If your benchmark returns a value, use the generic overload. This stops the compiler optimizing the
call away:

```csharp
var result = Benchmark.Run(() => int.Parse("12345"));
result.Print();
```

## Comparing two implementations

To compare approaches side-by-side, use `BenchmarkSuite`:

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

The **Ratio** column shows speed relative to the baseline. The **Sig** column shows **✓** when the
difference is statistically real and **✗** when it isn't.

## Saving results to a file

```csharp
var result = Benchmark.Run(() => MyMethod());

await result.ToMarkdownAsync("results.md");
await result.ToCsvAsync("results.csv");
await result.ToJsonAsync("results/");   // directory
```

## What the numbers mean

**Median** is the number to quote - the middle measurement, unmoved by outliers. **Ops/s** is the
same thing as a rate. **Error** says how precise the mean is. **Ratio** and **Sig** appear when you
compare: `0.75x` means 25% faster, and **✓** means the difference is real rather than noise.

[Reading your results](./reading-your-results.md) covers every column, indicator, and warning.

> [!NOTE]
> Your benchmark ran in a separate process. That's the default in every mode, needs no
> configuration, and is what makes the numbers reproducible - JIT and GC settings can only be chosen
> for a process that hasn't started yet. See [Isolated runs](../features/isolated-runs.md).

## Next steps

1. **[Reading your results](./reading-your-results.md)** - what the output is telling you
2. **[Key concepts](./key-concepts.md)** - warmup, outliers, and confidence, in plain English
3. **[Usage modes](../usage-modes/)** - suite mode, harness mode, and the global tool
4. **[Guides](../guides/)** - complete recipes for CI gates, refactor comparisons, and more
