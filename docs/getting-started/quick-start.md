---
title: Quick Start
description: Write your first benchmark and understand the output.
order: 2
---

# Quick Start

## Your first benchmark

The simplest way to measure code is `Benchmark.Run`. Call it anywhere - no special project structure, no attributes, no configuration.

```csharp
using NBenchmark;
using NBenchmark.Console;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

Run with `dotnet run` and you'll see:

```
  Benchmark: 1.20 µs median
    Mean: 1.24 µs, P95: 2.00 µs
    StdDev: 360 ns
    95% CI: 1.19 µs … 1.29 µs (±50 ns)
```

That's it. NBenchmark ran 25 warmup iterations (to let the JIT compile your code), then 200 measured iterations, trimmed the noisiest 5%, and printed a summary.

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
using NBenchmark.Console;

var results = await new BenchmarkSuite("string concat")
    .Add("plus operator",  () => { var s = "hello" + " " + "world"; })
    .Add("interpolation",  () => { var s = $"hello {"world"}"; })
    .WithBaseline("plus operator")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

The console output will look like:

```
╭──────────────────┬────────┬────────┬────────┬────────┬────────┬────────┬───────┬──────────╮
│ Benchmark        │ Median │  Mean  │ Error  │ StdDev │  P95   │  P99   │ Ratio │ Alloc/op │
├──────────────────┼────────┼────────┼────────┼────────┼────────┼────────┼───────┼──────────┤
│ interpolation  ✓ │ 8.0 ns │ 8.1 ns │ ±1 ns  │ 6 ns   │ 10 ns  │ 11 ns  │ 0.95x │    -     │
│ plus operator    │ 8.5 ns │ 8.4 ns │ ±1 ns  │ 5 ns   │ 10 ns  │ 10 ns  │ 1.00x │    -     │
╰──────────────────┴────────┴────────┴────────┴────────┴────────┴────────┴───────┴──────────╯

Ran 2 benchmark(s) - Significance: Mann-Whitney U (p < 0.05) - Outliers: top 5%
Error = ±95% confidence interval half-width on the mean.
```

The **Ratio** column shows speed relative to the baseline. The **✓** next to a benchmark name means the difference is statistically significant - it's unlikely to be random noise.

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
| **P95 / P99** | Worst-case timing 95% / 99% of the time. Useful for latency budgets. |
| **Ratio** | Speed relative to the baseline. `0.75x` = 25% faster; `2.0x` = twice as slow. |
| **Sig ✓** | Difference from the baseline is statistically significant ([p < 0.05](https://en.wikipedia.org/wiki/P-value)). |

See [Key Concepts](./key-concepts.md) for a deeper explanation of what these mean and how they are calculated.

## Next steps

- **[Key Concepts](./key-concepts.md)** - understand warmup, outlier trimming, and the statistics
- **[Guides](../guides/)** - detailed coverage of all three usage modes
- **[Configuration](../configuration.md)** - change defaults (iterations, warmup, confidence level, etc.)
