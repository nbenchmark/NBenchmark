---
title: Report Detail Levels
description: Understand the difference between Simple and Advanced detail modes, and how to control what reporters display.
order: 5
---

# Report Detail Levels

NBenchmark supports two report detail levels that control how much statistical information reporters display. **Simple** is the default; **Advanced** adds a per-benchmark stats block with the full distribution summary.

## Simple mode (default)

Simple mode shows a compact table:

| Column | Description |
|---|---|
| **Benchmark** | Benchmark name. |
| **Median** | Median timing. |
| **Mean** | Arithmetic mean. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). |
| **vs Baseline** | Visual bar plus ratio relative to the baseline. |
| **Sig** | ✓ = significant, ✗ = not significant, - = not applicable. |
| **Magnitude** | Strategy-defined qualitative effect label. |
| **Alloc/op** | Mean bytes allocated per iteration, or - if not measured. |

## Advanced mode

Advanced mode shows the same 10-column table **plus** a per-benchmark stats block. The console reporter prints each stats block below its row; the Markdown reporter emits a dedicated details section after the table. The stats block includes:

- **Outliers:** count of removed samples and the trimming method.
- **Range:** Min to Max spread.
- **Quartiles:** Q1, Q3, and IQR.
- **Fences:** Lower and upper fences (only for `IqrFence` mode).
- **Iterations:** pre-trim and post-trim sample counts and warmup count.
- **Confidence interval:** full CI bounds and margin percent of mean.
- **CV:** coefficient of variation as a percentage.
- **Skewness and Kurtosis:** shape of the distribution.
- **MAD:** median absolute deviation (scaled).
- **N:** post-trim sample count.
- **Allocation breakdown** (when `MeasureAllocations = true`): median, P95, and max allocation per iteration.

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
suite.WithDetail(ReportDetail.Advanced)
     .WithReporter(new ConsoleReporter())
     .RunAsync();
```

### `--detail` flag (Host mode)

```bash
dotnet run -- --detail advanced
dotnet run -- --detail simple
```

| Value | Behaviour |
|---|---|
| `simple` | Compact table with the essential statistics. **(default)** |
| `advanced` | Same table plus a per-benchmark stats block with quartiles, fences, confidence interval, skewness, kurtosis, MAD, and allocation percentiles. |

The `--detail` flag affects all registered reporters. JSON always emits the full record regardless of detail level.

### Quick mode

Quick mode (`Benchmark.Run` / `Benchmark.RunAsync`) always uses `Simple` detail and does not support `WithDetail()`.

## Reporter behaviour

| Reporter | Simple | Advanced |
|---|---|---|
| **Console** | Table only | Table + per-benchmark stats block below each row |
| **Markdown** | Table only | Table + dedicated details section after the table |
| **CSV** | Core columns | Extended columns including quartiles, fences, and shape stats |
| **JSON** | Full record (always) | Full record (always) |

## See also

- [Reporters](../reporters/) - available reporters and how to attach them
- [CLI Reference: `--detail`](../reference/cli.md#--detail-level) - full flag documentation
- [Descriptive Statistics](../statistics/descriptive.md) - what each field measures
