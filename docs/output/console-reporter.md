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

When there are two or more benchmarks, a bar chart of median timings is also displayed below the table.

When **three or more** benchmarks are compared, the per-row Sig column shows the post-hoc pairwise verdict (candidate versus baseline, Holm-Bonferroni corrected) and a single omnibus line is printed above the footer, summarising the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) verdict across all groups:

```
Omnibus Kruskal-Wallis across 3 groups: H(2) = 7.20, p = 0.027 → significant

Significance: Kruskal-Wallis (p < 0.05)
Outliers: MAD (3×)
Effect metric: Cliff's δ (Romano neg/small/med/large labels)
Profile: realistic (no per-iteration GC, no between-benchmark GC, alloc tracking on)
3 benchmark(s) · 0.0s total · CI 95%
```

After the Interpretation section, ConsoleReporter prints a grey `auto-tuned: …` line per benchmark summarising what the [adaptive measurement loop](../statistics/measurement.md#the-measurement-loop) resolved - the measured-sample count, ops-per-sample (K), warmup length, and the achieved CI half-width. Pinned runs still show the line, with the resolved counts you set.

After the comparison and precision tables, ConsoleReporter prints an **Interpretation** section with omnibus/significance context, outlier mode, effect-metric semantics, and the measurement profile. If warnings exist, they are shown in a separate **Warnings** section below the auto-tune lines. The final summary line shows benchmark count, total run time, and confidence interval.

## Columns

| Column | Description |
|---|---|
| **Benchmark** | Name of the benchmark. Colour-coded: green (≤ 5% slower than baseline), yellow (≤ 50% slower), red (> 50% slower). Baseline is shown in bold. |
| **Median** | Median timing. |
| **Mean** | Arithmetic mean. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). `-` for errored or dry-run results. |
| **Ratio** | Visual bar plus ratio relative to the baseline. Green for faster, yellow for moderately slower, red for significantly slower. The baseline cell shows `baseline`. |
| **Sig** | **✓** = difference from baseline is statistically significant; **✗** = not significant; **-** = not applicable (baseline or significance not tested). |
| **Mag** | Strategy-defined qualitative effect label. With the built-in Mann-Whitney tests this is Cliff's delta classified by [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size): `neg` (abs(δ) < 0.147), `sml` (< 0.33), `med` (< 0.474), `lrg` (≥ 0.474). For `lrg`, the cell is bold-red when the candidate is slower and bold-green when faster. `-` for the baseline or when significance is not tested. See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| **Alloc/op** | Mean heap bytes per iteration (only visible when allocation tracking is enabled). |

An optional **Description** column appears if any benchmark has a `Description` set.

For [parameterized benchmarks](../features/parameterized-suite.md#reading-the-report), one column per parameter appears immediately after **Benchmark**, and the **Benchmark** cell shows the base method name. To save width, parametric tables label the comparison columns **Ratio**, **Sig** and **Mag**. When a single method is swept across parameter values, **Ratio** reports each point's scaling factor relative to the fastest point (the reference, shown as `baseline`); **Sig** and **Mag** stay `-`, because the engine does not test different workloads against one another. When a parameter group instead holds competing benchmarks, **Sig** and **Mag** carry the usual within-group significance and effect.

In Standard mode (`--detail standard` or `WithDetail(ReportDetail.Standard)`), the full multi-section output is shown: comparison table, Precision & Tail Latency, auto-tune, and Interpretation.

In Advanced mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`), each benchmark row is followed by an indented stats block.

## Adding progress display

`ConsoleBenchmarkProgress` displays warmup and measurement progress for each benchmark as it runs. It is independent of `ConsoleReporter` and can be used without it.

```csharp
using NBenchmark.Reporters.Console;

await new BenchmarkSuite("name")
    .WithWarmup(25)        // pin so the progress bar has an exact total
    .WithIterations(200)   // pin so the progress bar has an exact total
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

Pinning the warmup and sample counts gives the progress bar an exact total to track. With the default auto-resolved counts the bar fills toward the `MaxSamples` ceiling and the run usually stops earlier, once the confidence interval is tight enough.

Progress output is a live, updating line per benchmark:

```
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
await result.PrintAsync();
```

`PrintAsync` runs the single result through `ConsoleReporter` and renders a table.

## Printing markup from the summary line

The summary line at the bottom always shows the confidence level from the first successful result. If all benchmarks errored, only a list of error messages is shown.
