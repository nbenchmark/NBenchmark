---
title: CsvReporter
description: Save benchmark results to a CSV file for post-processing in Excel, Python, or other tools.
order: 3
---

# CsvReporter

`CsvReporter` writes results to a `.csv` file containing all computed statistics, including the full confidence interval. It is part of the core `NBenchmark` package and has no additional dependencies.

CSV output is ideal for post-processing in spreadsheets, Python (pandas), R, or any tool that supports delimited data.

## Setup

Attach the reporter to your benchmark suite:

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

- `outputDirectory`: The directory where the file is written. NBenchmark creates this directory automatically if it does not exist. The path must be under the current working directory.
- `fileName`: If `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. If specified, the reporter uses that exact filename without appending a counter or timestamp.

### Auto-naming

When you don't provide a `fileName`, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```text
benchmark-results-20260606-034000-001.csv
```

The counter increments each time `ReportAsync` is called within the same process.

### Explicit filenames

Pass a `fileName` when you need a stable output path:

```csharp
new CsvReporter("results/", "benchmarks.csv")
```

When you provide an explicit `fileName`, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

```csv
ClassName,Name,Median,OpsPerSecond,Ratio,Significant,AllocPerOp,Gen0,Gen1,Gen2,SchemaVersion,MeasurementEpoch,Detail,Profile,RuntimeProfile,RuntimeKnobs,ThreadControl,InterferenceFilter,Isolation

Each row contains a `MeasurementEpoch` of `7`:
"SortingBenchmarks","Compute",300.0,3636363.6,0.75,"true",96,12,3,0,1,7,simple,realistic,steady-state,"",true,true,"isolated"
"SortingBenchmarks","Baseline",400.0,2660985.4,1.00,"",120,11,2,0,1,7,simple,realistic,steady-state,"",true,true,"isolated"
```

NBenchmark records all timing values in **nanoseconds**.

Percentile columns (such as P95, P99, etc.) are dynamic. They appear only in Standard and Advanced modes when you configure the corresponding percentiles via `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. With the default set (`[0.50, 0.95, 0.99, 0.999, 1.0]`), NBenchmark emits columns P95 and P99. P50 and Max (1.0) are excluded from percentile columns because they appear separately as Median and Max. Empty cells indicate that the percentile was not configured or the row errored.

The `EffectMetric`, `EffectValue`, and `Magnitude` columns reflect the active significance strategy's output. For built-in Mann-Whitney tests:

- `EffectMetric` is `Cliff's δ`.
- `EffectValue` is signed (positive = candidate slower).
- `Magnitude` is one of `neg`, `small`, `med`, or `large` based on [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size) thresholds.

## Column reference

### Simple detail mode (19 columns)

| Column | Type | Description |
| --- | --- | --- |
| `ClassName` | string | Benchmark class name (double-quote escaped). |
| `Name` | string | Benchmark name (double-quote escaped). |
| `Median` | float | Median timing in nanoseconds. |
| `OpsPerSecond` | float | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). Empty for errored or dry-run results. |
| `Ratio` | float or `null` | Speed relative to the baseline. `null` if no baseline exists or only one benchmark was run. |
| `Significant` | `"true"` / `"false"` / empty | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) significance result. Empty for the baseline or when significance testing is disabled. |
| `AllocPerOp` | integer or `null` | Mean heap bytes per iteration. `null` if allocation tracking is disabled. |
| `Gen0`, `Gen1`, `Gen2` | integer or empty | Collection counts per generation. Empty when GC diagnostics are off. |
| `SchemaVersion` | integer | The report shape. For more information, see [Report format versioning](./index.md#report-format-versioning). |
| `MeasurementEpoch` | integer | Determines if numbers can be compared with another file. A different epoch means NBenchmark changed what it measures. |
| `Detail` | string | Active detail level (`simple`, `standard`, or `advanced`). |
| `Profile` | string | Active measurement profile (`realistic` or `independent`). |
| `RuntimeProfile` | string | The runtime profile the measuring process used (`steady-state`, `host`, etc.). |
| `RuntimeKnobs` | string | The environment variables the profile applied, or empty if inherited. |
| `ThreadControl` | `true` / `false` | Whether thread-level environment control was enabled (affinity, priority, macOS performance-core placement). |
| `InterferenceFilter` | `true` / `false` | Whether the evidence-based interference rejection filter was enabled. |
| `Isolation` | string | Isolation status for the row (`isolated`, `in-process`, or a refusal status). |

### Standard mode (dynamic columns)

Standard mode adds the following columns after the simple columns:

| Column | Type | Description |
| --- | --- | --- |
| `Mean` | float | Arithmetic mean in nanoseconds. |
| `StdDev` | float | Sample standard deviation in nanoseconds. |
| `StdErr` | float | Standard error of the mean (`StdDev / √n`) in nanoseconds. |
| `MarginOfError` | float | Half-width of the confidence interval in nanoseconds. |
| `CiLower` | float | Lower bound of the confidence interval on the mean (`Mean - MarginOfError`). |
| `CiUpper` | float | Upper bound of the confidence interval on the mean (`Mean + MarginOfError`). |
| `ConfidenceLevel` | float | The confidence level used (such as `0.95`). |
| `CoefficientOfVariation` | float | `StdDev / Mean`. A dimensionless measure of relative variability. |
| `RatioCiLower` | float or empty | Lower bound of the paired per-launch ratio interval. Empty if the run had a single launch. |
| `RatioCiUpper` | float or empty | Upper bound of the paired per-launch ratio interval. An interval spanning `1.0` means the run cannot distinguish this benchmark from the baseline. |
| `RatioReplicates` | integer or empty | The number of launches paired to produce the interval. |
| `P{key}` | float | Dynamic percentile columns (e.g., `P95`, `P99`). Values are in nanoseconds. |
| `EffectMetric` | string or empty | Strategy-defined effect metric name (such as `Cliff's δ`). Empty for the baseline or when significance is not tested. |
| `EffectValue` | float or empty | Strategy-defined numeric effect value. For Mann-Whitney tests, this is **Cliff's delta** (positive = candidate slower than baseline). See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| `Magnitude` | string or empty | Strategy-defined qualitative effect label. For Mann-Whitney tests, this is the [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size) classification of `abs(Cliff's δ)`. |
| `MarginPercent` | float | `MarginOfError / Mean * 100`. |
| `OutliersRemoved` | integer | Number of samples removed by outlier trimming. |

### Advanced mode (dynamic columns)

Advanced mode adds the following columns to the standard set:

| Column | Type | Description |
| --- | --- | --- |
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
| `AllocMedian` | integer or empty | Median allocation per iteration. |
| `AllocP95` | integer or empty | P95 allocation per iteration. |
| `AllocMax` | integer or empty | Max allocation per iteration. |
| `StandardErrorPercent` | float | `StdErr / Mean * 100`. |
| `CoefficientOfVariationPercent` | float | `CoefficientOfVariation * 100`. |
| `WarmupIterations` | integer | Resolved warmup samples (excluded from stats). |
| `AutoTuneWarmup` | integer or empty | Resolved warmup length from the adaptive loop. |
| `AutoTuneSamples` | integer or empty | Resolved measured-sample count (pre-trim). |
| `AutoTuneOpsPerSample` | integer or empty | Resolved operations-per-sample (K). |
| `AutoTuneSampleStop` | string or empty | Stop reason: `CiTargetMet`, `MaxCeiling`, `ExplicitCount`, or `WallClockCap`. |
| `AutoTuneCiWidth` | float or empty | Raw relative CI half-width achieved at stop. |
| `AutoTuneTuningMs` | float or empty | Wall-clock time spent in the adaptive loop, in milliseconds. |
| `AutoTuneJitterMetric` | float or empty | Pre-flight jitter metric (MAD / median). |
| `AutoTuneDetectorSwitched` | `"true"` or empty | Set when the outlier detector auto-switched from IQR fence to MAD. |
| `AutoTuneSplitHalfDrift` | float or empty | Split-half drift measured on the final stop. |
| `AutoTuneRestarts` | integer or empty | Measurement restarts from the drift gate. |
| `AutoTuneWarmupTimeFloorMet` | `"true"` / `"false"` or empty | Whether the warmup time floor was reached. |
| `AutoTuneWarmupJitMethods` | integer or empty | Compiled-method count delta across warmup. |
| `HeapCommitted` | integer or empty | Heap committed bytes delta. |
| `HeapFragmented` | integer or empty | Heap fragmented bytes delta. |
| `ExceptionPerOp` | float or empty | First-chance exceptions per operation. |
| `CpuTimeNsPerOp` | float or empty | CPU time per operation. |
| `CpuWallRatio` | float or empty | CPU/wall time ratio. |
| `DiagnosticsMode` | string or empty | Active diagnostics mode (`none`, `gc`, `gcandcpu`, `all`). |
| `Categories` | string or empty | Semicolon-separated category names. |

## Notes

- NBenchmark sorts results by median (fastest first).
- NBenchmark creates the output directory automatically if it does not exist.
- For names containing double-quotes, NBenchmark uses standard CSV escaping by doubling the quote character.
- Simple detail CSV has 19 fixed columns. Standard mode has 35 non-percentile columns plus one per configured tail-latency percentile. Advanced mode adds 35 further fields.

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

When you specify `--output`, NBenchmark writes the files inside that directory.
