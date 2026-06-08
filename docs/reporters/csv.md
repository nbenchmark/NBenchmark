---
title: CsvReporter
description: Save benchmark results to a CSV file for post-processing in Excel, Python, or other tools.
order: 4
---

# CsvReporter

`CsvReporter` writes results to a `.csv` file with all computed statistics, including the full confidence interval. It is part of the core `NBenchmark` package with no additional dependencies.

CSV output is well-suited for post-processing in spreadsheets, Python/pandas, R, or any tool that can read delimited data.

## Setup

```csharp
using NBenchmark.Reporters;

// Default path
.WithReporter(new CsvReporter())

// Explicit path
.WithReporter(new CsvReporter("results/benchmark.csv"))
```

The default path is `benchmark-results.csv` in the current working directory.

## Output format

```csv
Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,AllocPerOp
"Compute",300.0,275.3,85.9,6.1,16.2,259.1,291.5,0.95,0.3122,500.0,500.0,0.75,"true",null
"Baseline",400.0,375.8,114.3,8.1,21.6,354.2,397.4,0.95,0.3043,500.0,900.0,1.00,"",null
```

All timing values are in **nanoseconds**.

## Column reference

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

## Notes

- Results are sorted by median (fastest first).
- The output directory must already exist. `CsvReporter` does not create it.
- Names containing double-quotes are escaped by doubling the quote character (standard CSV escaping).

## Using with Benchmark (Quick mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToCsvAsync("results.csv");
```

## CLI usage (BenchmarkHost)

```bash
dotnet run -- --reporter csv
dotnet run -- --reporter csv --output ./results
```

When `--output` is specified, the file is written as `benchmark-results.csv` inside that directory.
