---
title: ConsoleReporter
description: Rich terminal output with color-coded tables, significance indicators, and a bar chart.
order: 1
---

# ConsoleReporter

`ConsoleReporter` renders results to the terminal as a color-coded table using [Spectre.Console](https://spectreconsole.net/). It is part of the `NBenchmark.Reporters.Console` package.

## Setup

Add the console reporter package to your project:

```bash
dotnet add package NBenchmark.Reporters.Console
```

Then, attach the reporter to your benchmark suite:

```csharp
using NBenchmark.Reporters.Console;

// ...
.WithReporter(new ConsoleReporter())
```

### Using the CLI

When you reference the `NBenchmark.Reporters.Console` package, `ConsoleReporter` self-registers via `[ModuleInitializer]` and becomes available through the `--reporter console` CLI flag:

```bash
dotnet run -- --reporter console
```

You don't need to call `.WithReporter(new ConsoleReporter())` when using the CLI; the host discovers the reporter automatically through `ReporterRegistry`.

## Example output

```text
── BENCHMARK RESULTS  2026-06-06 03:40:00 UTC ──────────────────────────────────

  Benchmark              Median   Mean     Ops/s       Ratio                  Sig    Mag     Alloc/op
  Compute                300 ns   275 ns   3.64 Mops/s ████████ 0.75x         ✓      lrg     -
  Baseline (baseline)    400 ns   376 ns   2.66 Mops/s ████████████ baseline  -      -       -

── Precision & Tail Latency ────────────────────────────────────────────────────
... (error/stddev/cv/dynamic percentile columns)

── Interpretation ──────────────────────────────────────────────────────────────
Omnibus: not run (fewer than 3 comparable groups)
Significance: Mann-Whitney U (p < 0.05)
Outliers: IQR fence (1.5×)
Effect metric: Cliff's δ (Romano neg/small/med/large labels)
Profile: realistic (no per-iteration GC, no between-benchmark GC, alloc tracking on)
2 benchmark(s) · 0.0s total · CI 95%

Compute: auto-tuned: 190 samples × 1 ops, warmup 40, CI ±1.8%
Baseline: auto-tuned: 190 samples × 1 ops, warmup 40, CI ±1.9%

── Warnings ────────────────────────────────────────────────────────────────────
... (only shown when present)
```

NBenchmark displays a bar chart of median timings below the table when two or more benchmarks are compared.

When three or more benchmarks are compared, the **Sig** column shows the post-hoc pairwise verdict (candidate versus baseline, Holm-Bonferroni corrected). NBenchmark also prints a single omnibus line above the footer summarizing the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) verdict across all groups:

```text
Omnibus Kruskal-Wallis across 3 groups: H(2) = 7.20, p = 0.027 → significant

Significance: Kruskal-Wallis (p < 0.05)
Outliers: MAD (3×)
Effect metric: Cliff's δ (Romano neg/small/med/large labels)
Profile: realistic (no per-iteration GC, no between-benchmark GC, alloc tracking on)
3 benchmark(s) · 0.0s total · CI 95%
```

Following the **Interpretation** section, `ConsoleReporter` prints a grey `auto-tuned: …` line per benchmark. This line summarizes what the [adaptive measurement loop](../statistics/measurement.md#the-measurement-loop) resolved: the measured-sample count, operations-per-sample (K), warmup length, and the achieved CI half-width. Pinned runs still show this line with the counts you set.

The **Interpretation** section provides omnibus/significance context, the outlier mode, effect-metric semantics, and the measurement profile. If warnings exist, NBenchmark displays them in a separate **Warnings** section below the auto-tune lines. The final summary line shows the benchmark count, total run time, and confidence interval.

## Columns

| Column | Description |
| --- | --- |
| **Benchmark** | The benchmark name. Color-coding indicates performance relative to baseline: green (≤ 5% slower), yellow (≤ 50% slower), and red (> 50% slower). The baseline is bold. |
| **Median** | The median timing. |
| **Mean** | The arithmetic mean. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). A `-` indicates errored or dry-run results. |
| **Ratio** | A visual bar and ratio relative to the baseline. Colors indicate performance: green for faster, yellow for moderately slower, and red for significantly slower. The baseline cell shows `baseline`. |
| **Sig** | **✓** = difference from baseline is statistically significant; **✗** = not significant; **-** = not applicable (baseline or significance not tested). |
| **Mag** | A qualitative effect label. For built-in Mann-Whitney tests, this is Cliff's delta classified by [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size): `neg` (abs(δ) < 0.147), `sml` (< 0.33), `med` (< 0.474), and `lrg` (≥ 0.474). For `lrg`, the cell is bold-red when the candidate is slower and bold-green when faster. See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| **Alloc/op** | Mean heap bytes per iteration (visible only when allocation tracking is enabled). |

If any benchmark has a `Description` set, NBenchmark adds an optional **Description** column.

For [parameterized benchmarks](../features/parameterized-suite.md#reading-the-report), NBenchmark adds one column per parameter immediately after the **Benchmark** column. To save width, parametric tables label comparison columns **Ratio**, **Sig**, and **Mag**. 

- When a single method is swept across parameter values, **Ratio** reports each point's scaling factor relative to the fastest point (the reference, shown as `baseline`). **Sig** and **Mag** are `-` because NBenchmark does not test different workloads against one another. 
- When a parameter group holds competing benchmarks, **Sig** and **Mag** show the usual within-group significance and effect.

In Standard mode (`--detail standard` or `WithDetail(ReportDetail.Standard)`), NBenchmark shows the full multi-section output: the comparison table, Precision & Tail Latency, auto-tune, and Interpretation.

In Advanced mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`), NBenchmark follows each benchmark row with an indented stats block.

## Adding a progress display

`ConsoleBenchmarkProgress` displays warmup and measurement progress for each benchmark as it runs. You can use it with or without `ConsoleReporter`.

```csharp
using NBenchmark.Reporters.Console;

await new BenchmarkSuite("name")
    .WithWarmup(25)        // Pin to provide an exact total for the progress bar
    .WithIterations(200)   // Pin to provide an exact total for the progress bar
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

Pinning the warmup and sample counts gives the progress bar an exact total to track. With default auto-resolved counts, the bar fills toward the `MaxSamples` ceiling, and the run typically stops earlier once the confidence interval is tight enough.

Progress output is a live, updating line per benchmark:

```text
──────────────── Running 2 benchmark(s) ────────────────

  [1/2] Compute ████████████░░░░░░░░ 60% measuring (120/200) ETA 0.4s
  ✓ Compute 12.4 ns (0.8s)
  ✓ Baseline 41.9 ns (1.1s)

──────────────── Completed in 1.9s ────────────────
```

## Using with Benchmark (Single mode)

```csharp
using NBenchmark.Reporters.Console;

var result = Benchmark.Run(() => MyMethod());
await result.PrintTableAsync();
```

`PrintTableAsync` passes the single result through `ConsoleReporter` to render a table.

## Summary line markup

The summary line at the bottom shows the confidence level from the first successful result. If all benchmarks error, NBenchmark only shows a list of error messages.
