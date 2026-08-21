---
title: Report Detail Levels
description: Understand the difference between Simple, Standard, and Advanced detail modes, and how to control what reporters display.
order: 5
---

# Report Detail Levels

NBenchmark supports three report detail levels that control how much statistical information reporters display. **Simple** is the default; **Standard** adds full multi-section output; and **Advanced** adds a per-benchmark stats block with a full distribution summary.

## Simple mode (default)

Simple mode shows a compact table with the essential information you need to determine if your code performs well or how it compares to other implementations:

| Column | Description |
| --- | --- |
| **Benchmark** | The benchmark name. |
| **Median** | The median timing. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). |
| **Ratio** | A visual bar and ratio relative to the baseline. |
| **Sig** | `✓` = significant, `✗` = not significant, `-` = not applicable. |
| **Alloc/op** | Mean bytes allocated per iteration, or `-` if not measured. |

A one-line footer shows the benchmark count, total duration, and confidence level. This mode avoids statistical jargon and auxiliary tables.

## Standard mode

Standard mode shows the comparison table with additional columns (Mean, Mag, Description) and several auxiliary sections:

- **Precision & Tail Latency table**: Displays Error (±CI), StdDev, CV, and upper-tail percentiles (such as P95, P99, etc.).
- **Diagnostics table**: Displays GC Gen0/Gen1/Gen2 collection counts, heap info, CPU/wall ratio, and exceptions per op when diagnostics are enabled. For more information, see [Diagnostics](../statistics/diagnostics.md).
- **Launch Aggregation table**: Displays cross-launch mean, stddev, median, and CI when `LaunchCount > 1`.
- **Interpretation block**: Includes the omnibus verdict, significance test name, outlier detector, effect metric summary, and measurement profile.
- **Auto-tune summary lines**: Displays resolved warmup, sample count, ops-per-sample, and achieved CI half-width.
- **Warnings**: Displays warnings when present.

Use this level if you need to understand variability and the statistical rigour behind your results.

## Advanced mode

Advanced mode shows everything in Standard mode plus a per-benchmark stats block. The `ConsoleReporter` prints each stats block below its row, while the `MarkdownReporter` emits a dedicated details section after the table. The stats block includes:

- **Outliers**: The count of removed samples and the trimming method.
- **Range**: The Min to Max spread.
- **Quartiles**: Q1, Q3, and IQR.
- **Fences**: Lower and upper fences (only for `IqrFence` mode).
- **Iterations**: Pre-trim and post-trim sample counts and the warmup count.
- **Confidence interval**: Full CI bounds and the margin percent of the mean.
- **CV**: The coefficient of variation as a percentage.
- **Skewness and Kurtosis**: The shape of the distribution.
- **MAD**: The scaled median absolute deviation.
- **Percentiles**: The full set of configured percentile values (such as P50, P95, P99, P99.9, and Max).
- **N**: The post-trim sample count.
- **Allocation breakdown**: Median, P95, and max allocation per iteration when `MeasureAllocations = true`.
- **Diagnostics breakdown**: GC collection counts, heap committed and fragmented bytes, CPU time, CPU/wall ratio, and exceptions per operation when diagnostics are enabled.

## Setting the detail level

### Using `WithDetail()` (Harness and Suite modes)

Both `BenchmarkHarness` and `BenchmarkSuite` expose a `WithDetail(ReportDetail)` method. This detail level is applied to all registered reporters. You can call `WithDetail` either before or after `WithReporter`.

```csharp
// Harness mode
var host = BenchmarkHarness.Create(args);
host.WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Suite mode
var suite = new BenchmarkSuite("MySuite");
suite.WithDetail(ReportDetail.Standard)
     .WithReporter(new ConsoleReporter())
     .RunAsync();
```

### Using the `--detail` flag (Harness mode)

You can set the detail level from the CLI:

```bash
dotnet run -- --detail advanced
dotnet run -- --detail standard
dotnet run -- --detail simple
```

| Value | Behavior |
| --- | --- |
| `simple` | Displays a compact table with essential statistics. **(default)** |
| `standard` | Displays the full comparison table plus Precision & Tail Latency, auto-tune, and Interpretation sections. |
| `advanced` | Displays everything in standard mode plus a per-benchmark stats block including quartiles, fences, confidence interval, skewness, kurtosis, MAD, configured percentiles, and allocation breakdown. |

The `--detail` flag affects all registered reporters. Note that JSON always emits the full record regardless of the detail level.

### Using Single mode

Single mode (`Benchmark.Run` / `Benchmark.RunAsync`) does not have a builder to call `WithDetail` on. Instead, pass the level directly to the `Print` method:

```csharp
var result = Benchmark.Run(() => MyMethod());

result.Print();                            // Simple (default) - Median and Ops/s
result.Print(ReportDetail.Standard);       // Adds Mean, percentiles, StdDev, Error, and CI
result.Print(ReportDetail.Advanced);       // Adds quartiles, fences, shape, and allocation breakdown
```

## Reporter behavior

| Reporter | Simple | Standard | Advanced |
| --- | --- | --- | --- |
| **Console** | 6-column table + counts footer | Full table + Precision & Tail Latency + Diagnostics + Interpretation + auto-tune | Standard + per-benchmark stats block (including diagnostics breakdown) |
| **Markdown** | 6-column table + counts footer | Full table + Precision & Tail Latency + Diagnostics + Interpretation | Standard + dedicated details section (including diagnostics breakdown) |
| **CSV** | 19 core columns (including GC counts) | 35 core columns (including GC counts) | 70 columns including quartiles, fences, shape stats, and full diagnostics |
| **JSON** | Full record (always) | Full record (always) | Full record (always) |

## See also

- [Reporters](./index.md) - Available reporters and how to attach them.
- [CLI Reference: `--detail`](../reference/cli.md#output) - Full flag documentation.
- [Descriptive Statistics](../statistics/descriptive.md) - Information on what each field measures.
