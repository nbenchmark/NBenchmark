---
title: CsvReporter
description: Save benchmark results to a CSV file for post-processing in Excel, Python, or other tools.
order: 2
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
Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,AllocPerOp,MarginPercent,OutliersRemoved,Detail
"Compute",300.0,275.3,85.9,6.1,16.2,259.1,291.5,0.95,0.3122,500.0,500.0,0.75,"true",null,5.89,2,"simple"
"Baseline",400.0,375.8,114.3,8.1,21.6,354.2,397.4,0.95,0.3043,500.0,900.0,1.00,"",null,5.75,0,"simple"
```

All timing values are in **nanoseconds**.

## Column reference

### Simple mode (18 columns)

| Column | Type | Description |
|---|---|---|
| `Name` | string | Benchmark name (double-quote escaped). |
| `Median` | float | Median timing in nanoseconds. |
| `Mean` | float | Arithmetic mean in nanoseconds. |
| `StdDev` | float | Sample standard deviation in nanoseconds. |
| `StdErr` | float | Standard error of the mean (`StdDev / √n`) in nanoseconds. |
| `MarginOfError` | float | Half-width of the confidence interval in nanoseconds. |
| `CiLower` | float | Lower bound of the confidence interval on the mean (`Mean - MarginOfError`). |
| `CiUpper` | float | Upper bound of the confidence interval on the mean (`Mean + MarginOfError`). |
| `ConfidenceLevel` | float | The confidence level used (e.g. `0.95`). |
| `CoefficientOfVariation` | float | `StdDev / Mean`. Dimensionless measure of relative variability. |
| `P95` | float | 95th percentile in nanoseconds. |
| `P99` | float | 99th percentile in nanoseconds. |
| `Ratio` | float or `null` | Speed relative to the baseline. `null` if no baseline or only one benchmark. |
| `Significant` | `"true"` / `"false"` / empty | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) significance result. Empty for the baseline or when significance testing is disabled. |
| `AllocPerOp` | integer or `null` | Mean heap bytes per iteration. `null` if allocation tracking is disabled. |
| `MarginPercent` | float | `MarginOfError / Mean * 100`. |
| `OutliersRemoved` | integer | Number of samples removed by outlier trimming. |
| `Detail` | string | Active detail level (`simple` or `advanced`). |

### Advanced mode (34 columns - all simple columns plus the following)

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
| `WarmupIterations` | integer | Number of warmup iterations (excluded from stats). |

## Notes

- Results are sorted by median (fastest first).
- The output directory is created automatically if it does not exist.
- Names containing double-quotes are escaped by doubling the quote character (standard CSV escaping).
- Simple mode CSV has 18 columns. Advanced mode CSV has 34 columns and is selected via the `--detail advanced` flag or `WithDetail(ReportDetail.Advanced)`.

## Using with Benchmark (Quick mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToCsvAsync("results/");
await result.ToCsvAsync("results/", "benchmarks.csv");
```

## CLI usage (BenchmarkHost)

```bash
dotnet run -- --reporter csv
dotnet run -- --reporter csv --output ./results
```

When `--output` is specified, files are written inside that directory.
