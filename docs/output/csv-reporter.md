---
title: CsvReporter
description: Save benchmark results to a CSV file for post-processing in Excel, Python, or other tools.
order: 3
---

# CsvReporter

`CsvReporter` writes results to a `.csv` file with all computed statistics, including the full confidence interval. It is part of the core `NBenchmark` package with no additional dependencies.

CSV output is well-suited for post-processing in spreadsheets, Python/pandas, R, or any tool that can read delimited data.

## Setup

```csharp
using NBenchmark.Reporters;

// Default - writes to the current directory with auto-naming
.WithReporter(new CsvReporter())

// Explicit directory
.WithReporter(new CsvReporter("results/"))

// Explicit directory and filename
.WithReporter(new CsvReporter("results/", "benchmarks.csv"))
```

### Constructor

```csharp
CsvReporter(string outputDirectory = ".", string? fileName = null)
```

- `outputDirectory` - The directory to write the file to. Created automatically if it does not exist. Must be under the current working directory.
- `fileName` - When `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. When specified, the exact filename is used (no counter or timestamp is appended).

### Auto-naming

When `fileName` is not provided, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```
benchmark-results-20260606-034000-001.csv
```

The counter increments each time `ReportAsync` is called within the same process.

### Explicit filename

Pass a `fileName` when you want a stable output path:

```csharp
new CsvReporter("results/", "benchmarks.csv")
```

When an explicit `fileName` is provided, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

```csv
ClassName,Name,Median,OpsPerSecond,Ratio,Significant,AllocPerOp,Detail,Profile
"SortingBenchmarks","Compute",300.0,3636363.6,0.75,"true",96,simple,realistic
"SortingBenchmarks","Baseline",400.0,2660985.4,1.00,"",120,simple,realistic

Percentile columns (P95, P99, etc.) are dynamic -- they appear only in Standard and Advanced modes when the corresponding percentiles are configured via `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. With the default set of `[0.50, 0.95, 0.99, 0.999, 1.0]`, columns P95 and P99 are emitted. P50 and Max (1.0) are excluded from percentile columns because they are shown separately as Median and Max. Values are in nanoseconds. Empty cells indicate the percentile was not in the configured set or the row is errored.
```

All timing values are in **nanoseconds**. `EffectMetric` / `EffectValue` / `Magnitude` reflect the active significance strategy's effect output. With built-in Mann-Whitney tests, `EffectMetric` is `Cliff's δ`, `EffectValue` is signed (positive = candidate slower), and `Magnitude` is one of `neg`, `small`, `med`, `large` per the [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size) thresholds.

## Column reference

### Simple mode (9 columns)

| Column | Type | Description |
|---|---|---|
| `ClassName` | string | Benchmark class name (double-quote escaped). |
| `Name` | string | Benchmark name (double-quote escaped). |
| `Median` | float | Median timing in nanoseconds. |
| `OpsPerSecond` | float | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). Empty for errored or dry-run results. |
| `Ratio` | float or `null` | Speed relative to the baseline. `null` if no baseline or only one benchmark. |
| `Significant` | `"true"` / `"false"` / empty | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) significance result. Empty for the baseline or when significance testing is disabled. |
| `AllocPerOp` | integer or `null` | Mean heap bytes per iteration. `null` if allocation tracking is disabled. |
| `Detail` | string | Active detail level (`simple`, `standard`, or `advanced`). |
| `Profile` | string | Active measurement profile (`realistic` or `independent`). |

### Standard mode (dynamic columns - adds the following after the simple columns)

| Column | Type | Description |
|---|---|---|
| `Mean` | float | Arithmetic mean in nanoseconds. |
| `StdDev` | float | Sample standard deviation in nanoseconds. |
| `StdErr` | float | Standard error of the mean (`StdDev / √n`) in nanoseconds. |
| `MarginOfError` | float | Half-width of the confidence interval in nanoseconds. |
| `CiLower` | float | Lower bound of the confidence interval on the mean (`Mean - MarginOfError`). |
| `CiUpper` | float | Upper bound of the confidence interval on the mean (`Mean + MarginOfError`). |
| `ConfidenceLevel` | float | The confidence level used (e.g. `0.95`). |
| `CoefficientOfVariation` | float | `StdDev / Mean`. Dimensionless measure of relative variability. |
| `P{key}` | float | Dynamic percentile columns. One column per configured percentile value between P50 and Max (e.g. `P95`, `P99`, `P99.9`). Controlled by `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. Values in nanoseconds. |
| `EffectMetric` | string or empty | Strategy-defined effect metric name (for example `Cliff's δ`, `median-ratio`, `A12`). Empty for the baseline or when significance is not tested. |
| `EffectValue` | float or empty | Strategy-defined numeric effect value. For built-in Mann-Whitney tests this is **Cliff's delta** (positive = candidate slower than baseline, negative = candidate faster, range `[-1, 1]`). Empty for the baseline or when significance is not tested. See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| `Magnitude` | string or empty | Strategy-defined qualitative effect label. For built-in Mann-Whitney tests this is [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size) classification of `abs(Cliff's δ)`: `neg` < 0.147, `small` < 0.33, `med` < 0.474, `large` ≥ 0.474. Empty for the baseline or when significance is not tested. |
| `MarginPercent` | float | `MarginOfError / Mean * 100`. |
| `OutliersRemoved` | integer | Number of samples removed by outlier trimming. |

### Advanced mode (dynamic columns - all standard columns plus the following)

| Column | Type | Description |
|---|---|---|
| `Q1` | float | First quartile (P25) in nanoseconds. |
| `Q3` | float | Third quartile (P75) in nanoseconds. |
| `Iqr` | float | Q3 - Q1 in nanoseconds. |
| `LowerFence` | float or empty | Lower IQR fence. Empty when `OutlierMode` is not `IqrFence`. |
| `UpperFence` | float or empty | Upper IQR fence. Empty when `OutlierMode` is not `IqrFence`. |
| `Range` | float | Max - Min in nanoseconds. |
| `N` | integer | Post-trim sample count. |
| `Skewness` | float | Sample skewness. Zero for `n < 3`. |
| `Kurtosis` | float | Excess kurtosis. Zero for `n < 4`. |
| `Mad` | float | Median absolute deviation (scaled by 1.4826). |
| `AllocMedian` | integer or empty | Median allocation per iteration. Empty if allocation tracking is disabled. |
| `AllocP95` | integer or empty | P95 allocation per iteration. Empty if allocation tracking is disabled. |
| `AllocMax` | integer or empty | Max allocation per iteration. Empty if allocation tracking is disabled. |
| `StandardErrorPercent` | float | `StdErr / Mean * 100`. |
| `CoefficientOfVariationPercent` | float | `CoefficientOfVariation * 100`. |
| `WarmupIterations` | integer | Resolved warmup samples (excluded from stats). |
| `AutoTuneWarmup` | integer or empty | Resolved warmup length from the adaptive loop. Empty on dry-run/errored. |
| `AutoTuneSamples` | integer or empty | Resolved measured-sample count (pre-trim). Empty on dry-run/errored. |
| `AutoTuneOpsPerSample` | integer or empty | Resolved ops-per-sample (K). Empty on dry-run/errored. |
| `AutoTuneSampleStop` | string or empty | Why measurement stopped: `CiTargetMet`, `MaxCeiling`, `ExplicitCount`, or `WallClockCap`. Empty on dry-run/errored. |
| `AutoTuneCiWidth` | float or empty | Raw relative CI half-width achieved at stop. Empty on dry-run/errored. |
| `AutoTuneTuningMs` | float or empty | Wall-clock time spent in the adaptive loop, in milliseconds. Empty on dry-run/errored. |
| `Categories` | string or empty | Semicolon-separated category names. Empty if no categories. |

## Notes

- Results are sorted by median (fastest first).
- The output directory is created automatically if it does not exist.
- Names containing double-quotes are escaped by doubling the quote character (standard CSV escaping).
- Simple mode CSV has 9 fixed columns. Standard mode has 22 base columns plus one column per configured tail-latency percentile. Advanced mode adds 23 advanced fields on top of the standard columns and therefore also has a dynamic total column count.

## Using with Benchmark (Single mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToCsvAsync("results/");
await result.ToCsvAsync("results/", "benchmarks.csv");
```

## CLI usage (BenchmarkHarness)

```bash
dotnet run -- --reporter csv
dotnet run -- --reporter csv --output ./results
```

When `--output` is specified, files are written inside that directory.
