---
title: ConsoleReporter
description: Rich terminal output with colour-coded tables, significance indicators, and a bar chart.
order: 1
---

# ConsoleReporter

`ConsoleReporter` renders results to the terminal as a colour-coded table using [Spectre.Console](https://spectreconsole.net/). It is part of the `NBenchmark.Reporters.Console` package.

## Setup

```bash
dotnet add package NBenchmark.Reporters.Console
```

```csharp
using NBenchmark.Reporters.Console;

.WithReporter(new ConsoleReporter())
```

### CLI usage

When the `NBenchmark.Reporters.Console` package is referenced, `ConsoleReporter` self-registers via `[ModuleInitializer]` and becomes available through the `--reporter console` CLI flag:

```bash
dotnet run -- --reporter console
```

No explicit `.WithReporter(new ConsoleReporter())` call is needed when using the CLI - the host discovers it automatically through `ReporterRegistry`.

## Example output

```
Benchmark Results
Run at 2026-06-06 03:40:00 UTC - 25 warmup / 190 measured

╭──────────────────────┬─────────┬─────────┬────────┬─────────┬─────────┬─────────┬───────┬──────────╮
│ Benchmark            │ Median  │  Mean   │ Error  │ StdDev  │   P95   │   P99   │ Ratio │ Alloc/op │
├──────────────────────┼─────────┼─────────┼────────┼─────────┼─────────┼─────────┼───────┼──────────┤
│ Compute ✓            │ 300 ns  │ 275 ns  │ ±16 ns │  86 ns  │ 500 ns  │ 500 ns  │ 0.75x │    -     │
│ Baseline (baseline)  │ 400 ns  │ 376 ns  │ ±22 ns │ 114 ns  │ 500 ns  │ 900 ns  │ 1.00x │    -     │
╰──────────────────────┴─────────┴─────────┴────────┴─────────┴─────────┴─────────┴───────┴──────────╯

Ran 2 benchmark(s) in 0.0s - Significance: Mann-Whitney U (p < 0.05) - Outliers: IQR fence (1.5×)
Error = ±95% confidence interval half-width on the mean.
```

When there are two or more benchmarks, a bar chart of median timings is also displayed below the table.

## Columns

| Column | Description |
|---|---|
| **Benchmark** | Name of the benchmark. Colour-coded: green (≤ 5% slower than baseline), yellow (≤ 50% slower), red (> 50% slower). Baseline is shown in bold. |
| **Median** | Median timing. |
| **Mean** | Arithmetic mean. |
| **Error** | ±Margin of error on the mean at the configured confidence level. |
| **StdDev** | Sample standard deviation. |
| **P95** | 95th percentile. |
| **P99** | 99th percentile. |
| **Ratio** | Speed relative to the baseline. Green for faster, yellow for moderately slower, red for significantly slower. |
| **Alloc/op** | Mean heap bytes per iteration (only visible when allocation tracking is enabled). |

An optional **Description** column appears if any benchmark has a `Description` set.

A **✓** next to the name indicates the difference from the baseline is statistically significant (p < 0.05). A **✗** indicates it is not.

In Advanced mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`), each benchmark row is followed by an indented stats block.

## Adding progress display

`ConsoleBenchmarkProgress` displays warmup and measurement progress for each benchmark as it runs. It is independent of `ConsoleReporter` and can be used without it.

```csharp
using NBenchmark.Reporters.Console;

await new BenchmarkSuite("name")
    .WithIterations(200)
    .WithWarmup(25)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

Pass the same `measuredIterations` and `warmupIterations` values you gave to `WithIterations` and `WithWarmup` so the progress display shows accurate counts.

Progress output looks like:

```
Starting 2 benchmark(s)...
  [[Compute]] warming up (25 iterations)...
  [1/2] Compute - running (25 warmup / 200 measured)...
  [[Baseline]] warming up (25 iterations)...
  [2/2] Baseline - running (25 warmup / 200 measured)...
  Completed 2 benchmark(s).
```

## Using with Benchmark (Quick mode)

```csharp
using NBenchmark.Reporters.Console;

var result = Benchmark.Run(() => MyMethod());
await result.PrintAsync();
```

`PrintAsync` runs the single result through `ConsoleReporter` and renders a table.

## Printing markup from the summary line

The summary line at the bottom always shows the confidence level from the first successful result. If all benchmarks errored, only a list of error messages is shown.
