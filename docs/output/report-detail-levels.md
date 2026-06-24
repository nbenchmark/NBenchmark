---
title: Report Detail Levels
description: Understand the difference between Simple, Standard, and Advanced detail modes, and how to control what reporters display.
order: 5
---

# Report Detail Levels

NBenchmark supports three report detail levels that control how much statistical information reporters display. **Simple** is the default; **Standard** adds the full multi-section output; **Advanced** adds a per-benchmark stats block with the full distribution summary.

## Simple mode (default)

Simple mode shows a compact table with the essential information an average developer needs to know whether their code performs well or how it compares to other implementations:

| Column | Description |
|---|---|
| **Benchmark** | Benchmark name. |
| **Median** | Median timing. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). |
| **Ratio** | Visual bar plus ratio relative to the baseline. |
| **Sig** | ✓ = significant, ✗ = not significant, - = not applicable. |
| **Alloc/op** | Mean bytes allocated per iteration, or - if not measured. |

A one-line footer shows the benchmark count, total duration, and confidence level. No statistical jargon, no auxiliary tables.

## Standard mode

Standard mode shows the same comparison table with additional columns (Mean, Mag, Description) plus several auxiliary sections:

- **Precision & Tail Latency** table: Error (±CI), StdDev, CV, and upper-tail percentiles (P95, P99, etc.).
- **Diagnostics** table (when diagnostics are enabled): GC Gen0/Gen1/Gen2 collection counts, heap info, CPU/wall ratio, and exceptions per op. See [Diagnostics](../statistics/diagnostics.md).
- **Launch Aggregation** table (when `LaunchCount > 1`): cross-launch mean, stddev, median, and CI.
- **Interpretation** block: omnibus verdict, significance test name, outlier detector, effect metric summary, and measurement profile.
- **Auto-tune summary** lines: resolved warmup, sample count, ops-per-sample, and achieved CI half-width.
- **Warnings** (when present).

This is the level for practitioners who want to understand variability and the statistical rigour behind the results.

## Advanced mode

Advanced mode shows everything in Standard **plus** a per-benchmark stats block. The console reporter prints each stats block below its row; the Markdown reporter emits a dedicated details section after the table. The stats block includes:

- **Outliers:** count of removed samples and the trimming method.
- **Range:** Min to Max spread.
- **Quartiles:** Q1, Q3, and IQR.
- **Fences:** Lower and upper fences (only for `IqrFence` mode).
- **Iterations:** pre-trim and post-trim sample counts and warmup count.
- **Confidence interval:** full CI bounds and margin percent of mean.
- **CV:** coefficient of variation as a percentage.
- **Skewness and Kurtosis:** shape of the distribution.
- **MAD:** median absolute deviation (scaled).
- **Percentiles:** the full set of configured percentile values (e.g. P50, P95, P99, P99.9, Max).
- **N:** post-trim sample count.
- **Allocation breakdown** (when `MeasureAllocations = true`): median, P95, and max allocation per iteration.
- **Diagnostics breakdown** (when diagnostics are enabled): GC collection counts, heap committed and fragmented bytes, CPU time and CPU/wall ratio, and exceptions per operation.

## Setting the detail level

### `WithDetail()` (Host and Suite modes)

Both `BenchmarkHost` and `BenchmarkSuite` expose a `WithDetail(ReportDetail)` method. The detail level is stamped onto all registered reporters, so calling `WithDetail` before or after `WithReporter` works in either order.

```csharp
// Host mode
var host = BenchmarkHost.Create(args);
host.WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Suite mode
var suite = new BenchmarkSuite("MySuite");
suite.WithDetail(ReportDetail.Standard)
     .WithReporter(new ConsoleReporter())
     .RunAsync();
```

### `--detail` flag (Host mode)

```bash
dotnet run -- --detail advanced
dotnet run -- --detail standard
dotnet run -- --detail simple
```

| Value | Behaviour |
|---|---|
| `simple` | Compact table with the essential statistics. **(default)** |
| `standard` | Full comparison table plus Precision & Tail Latency, auto-tune, and Interpretation sections. |
| `advanced` | Same as standard plus a per-benchmark stats block with quartiles, fences, confidence interval, skewness, kurtosis, MAD, configured percentiles, and allocation breakdown. |

The `--detail` flag affects all registered reporters. JSON always emits the full record regardless of detail level.

### Quick mode

Quick mode (`Benchmark.Run` / `Benchmark.RunAsync`) always uses `Simple` detail and does not support `WithDetail()`.

## Reporter behaviour

| Reporter | Simple | Standard | Advanced |
|---|---|---|---|
| **Console** | 6-column table + counts footer | Full table + Precision & Tail Latency + Diagnostics + Interpretation + auto-tune | Standard + per-benchmark stats block (incl. diagnostics breakdown) |
| **Markdown** | 6-column table + counts footer | Full table + Precision & Tail Latency + Diagnostics + Interpretation | Standard + dedicated details section (incl. diagnostics breakdown) |
| **CSV** | 12 core columns (incl. GC counts) | 25 core columns (incl. GC counts) | 51 columns including quartiles, fences, shape stats, and full diagnostics |
| **JSON** | Full record (always) | Full record (always) | Full record (always) |

## See also

- [Reporters](./index.md) - available reporters and how to attach them
- [CLI Reference: `--detail`](../reference/cli.md#--detail-level) - full flag documentation
- [Descriptive Statistics](../statistics/descriptive.md) - what each field measures
