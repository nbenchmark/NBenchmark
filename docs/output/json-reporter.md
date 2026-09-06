---
title: JsonReporter
description: Save benchmark results to a JSON file for programmatic consumption.
order: 4
---

# JsonReporter

`JsonReporter` writes results to a `.json` file as a structured object. It is part of the core `NBenchmark` package and uses `System.Text.Json` with no additional dependencies.

JSON output is ideal for CI dashboards, performance tracking over time, or any tooling that consumes structured data.

## Setup

Attach the reporter to your benchmark suite:

```csharp
using NBenchmark.Reporters;

// Default - writes to the current directory with auto-naming
.WithReporter(new JsonReporter())

// Explicit directory
.WithReporter(new JsonReporter("results/"))

// Explicit directory and filename
.WithReporter(new JsonReporter("results/", "benchmarks.json"))
```

### Constructor

```csharp
JsonReporter(string outputDirectory = ".", string? fileName = null)
```

- `outputDirectory`: The directory where the file is written. NBenchmark creates this directory automatically if it does not exist. The path must be under the current working directory.
- `fileName`: If `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. If specified, the reporter uses that exact filename without appending a counter or timestamp.

### Auto-naming

When you don't provide a `fileName`, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```text
benchmarks-20260606-034000-001.json
```

The counter increments each time `ReportAsync` is called within the same process, so multiple suite runs produce separate files instead of overwriting each other.

### Explicit filenames

Pass a `fileName` when you need a stable output path:

```csharp
new JsonReporter("results/", "benchmarks.json")
```

When you provide an explicit `fileName`, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

The envelope begins with `schemaVersion` and `measurementEpoch`. For more information, see [Report format versioning](./index.md#report-format-versioning) before diffing two files.

```json
{
  "schemaVersion": 1,
  "measurementEpoch": 7,
  "generatedAt": "2026-06-06T03:40:00.000Z",
  "detail": "simple",
  "profile": "realistic",
  "results": [
    {
      "name": "Compute",
      "description": null,
      "mean": 275.3,
      "median": 300.0,
      "percentiles": [
        { "percentile": 0.50, "value": 300.0 },
        { "percentile": 0.95, "value": 500.0 },
        { "percentile": 0.99, "value": 500.0 },
        { "percentile": 0.999, "value": 1000.0 },
        { "percentile": 1.0, "value": 1100.0 }
      ],
      "histogram": {
        "min": 200.0,
        "max": 1100.0,
        "sampleCount": 190,
        "buckets": [
          { "lower": 200.0, "upper": 245.0, "count": 10 },
          { "lower": 245.0, "upper": 290.0, "count": 45 }
        ]
      },
      "min": 200.0,
      "max": 1100.0,
      "standardDeviation": 85.9,
      "standardError": 6.1,
      "marginOfError": 16.2,
      "confidenceLevel": 0.95,
      "coefficientOfVariation": 0.3122,
      "confidenceIntervalLower": 259.1,
      "confidenceIntervalUpper": 291.5,
      "operationsPerSecond": 3636363.6,
      "medianOperationsPerSecond": 3333333.3,
      "nanosecondsPerOperation": 275.3,
      "totalOperations": 230,
      "meanAllocatedBytes": null,
      "pValue": 0.0012,
      "isSignificant": true,
      "errored": false,
      "errorMessage": null,
      "measuredIterations": 190,
      "warmupIterations": 40,
      "runAt": "2026-06-06T03:40:00.000Z",
      "totalDuration": "00:00:00.050",
      "measuredDuration": "00:00:00.040",
      "isBaseline": false,
      "outlierMode": "iqrFence",
      "outlierDetector": "IQR fence (1.5×)",
      "tailMetricsBasis": "raw",
      "autoTune": {
        "resolvedWarmup": 40,
        "resolvedSamples": 190,
        "opsPerSample": 1,
        "initialOpsPerSample": null,
        "totalBodyInvocations": 230,
        "warmupStop": "settled",
        "sampleStop": "ciTargetMet",
        "achievedRelativeCiWidth": 0.0184,
        "tuningWallClock": "00:00:00.050",
        "jitterMetric": 0.0271,
        "outlierDetectorSwitched": false,
        "ciWidthSeries": [0.212, 0.098, 0.041, 0.0184],
        "warmupTimeFloorMet": true,
        "warmupElapsedNs": 500049799.0,
        "warmupCurve": [412.0, 350.1, 288.4, 276.9, 275.4],
        "warmupSampleInterval": 8,
        "warmupJitCompiledMethods": 133,
        "warmupJitCompilationTime": "00:00:00.0205982",
        "warmupJitCompiledIlBytes": 7404,
        "jitLastChangeAtNs": 481309320.0,
        "jitQuiescenceAchieved": true,
        "measurementRestarts": 0,
        "splitHalfDrift": 0.0042,
        "clockResolutionNs": 41.0,
        "targetSampleDurationNs": 20992.0,
        "sampleDurationNs": 20777.2,
        "sampleQuantizationFraction": 0.00197
      }
    }
  ]
}
```

NBenchmark records all timing values in **nanoseconds**. Property names use `camelCase`.

Percentile values are provided in a `percentiles` array of `{ percentile, value }` objects. You can control the reported percentiles via `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. When `EnableHistogram` is `true` (the default), NBenchmark also includes a `histogram` object containing `min`, `max`, `sampleCount`, and `buckets` (an array of `{ lower, upper, count }`).

### Understanding sample sets

The result contains **two populations**, and the `tailMetricsBasis` field indicates which basis the tail metrics use. 

When `tailMetricsBasis` is `raw`:
- The order statistics (`min`, `max`, `percentiles`, and `histogram`) describe the **full pre-trim** sample set.
- The core statistics (`mean`, `median`, `standardDeviation`, `coefficientOfVariation`, and `measuredIterations`) describe the **trimmed (inlier)** set.
- The precision metrics (`standardError` and `marginOfError`) are hybrid. They describe the precision of the trimmed mean but are computed [Winsorized](../statistics/descriptive.md#winsorized-standard-error-for-trimmed-data) over the pre-trim set. This ensures that the samples removed by the outlier fence still count as observations.

This distinction is deliberate because the outlier fence removes exactly the slow tail that `P99` and `Max` are designed to describe. See [Descriptive statistics](../statistics/descriptive.md) for more information. When displaying both sets of metrics, you should label them clearly; the `outlierDetector` field names the detector that separated the two.

### The `autoTune` object

The `autoTune` object records the decisions made by the [adaptive measurement loop](../statistics/measurement.md#the-measurement-loop) for the benchmark. This object is `null` for dry-run and errored results.

| Group | Fields |
| --- | --- |
| **Resolution** | `resolvedWarmup`, `resolvedSamples`, `opsPerSample`, `initialOpsPerSample` (the pre-recalibration cold K, or `null`), `totalBodyInvocations` |
| **Stop reason** | `warmupStop` (`settled` / `maxCeiling` / `explicitCount` / `wallClockCap`), `sampleStop` (adds `ciTargetMet` and `driftUnresolved`) |
| **Convergence** | `achievedRelativeCiWidth` (on the **raw** stream), `ciWidthSeries` (the convergence trace), `tuningWallClock` |
| **Host stability** | `jitterMetric`, `outlierDetectorSwitched`, `measurementRestarts`, `splitHalfDrift` |
| **Warmup and JIT** | `warmupTimeFloorMet`, `warmupElapsedNs`, `warmupCurve`, `warmupSampleInterval`, `warmupJitCompiledMethods`, `warmupJitCompilationTime`, `warmupJitCompiledIlBytes`, `jitLastChangeAtNs`, `jitQuiescenceAchieved` |
| **Clock resolution** | `clockResolutionNs`, `targetSampleDurationNs`, `sampleDurationNs`, `sampleQuantizationFraction` |

> [!IMPORTANT]
> `achievedRelativeCiWidth` and `marginOfError` measure different things. The former is the CI half-width the loop achieved on the **raw** stream at the time of the stop decision. The latter is the interval on the **trimmed** mean. `marginOfError` accounts for how many samples the fence removed, but not how far out they were. When outliers carry most of the variance, these two values can diverge sharply. Treat the trimmed margin as optimistic whenever `sampleStop` is not `ciTargetMet`.

The `warmupCurve` records the mean per-op time of each warmup batch (oldest first), illustrating the shape of tiered compilation. Since a body promoted from tier-0 to tier-1 (and re-optimized under dynamic PGO) gets faster in steps, the curve shows these transitions. `warmupSampleInterval` provides the iterations between consecutive points for plotting. NBenchmark bounds this array at 512 points; longer warmups are decimated by a doubling stride to maintain shape. This array is empty for pinned `warmupIterations` or when `IncludeSamples` is off.

Note two limitations:
1. NBenchmark collects **aggregate decay**, not per-method tier attribution. To identify individual methods and their tiers, use the runtime's `MethodLoadVerbose` events via EventPipe or an in-process `EventListener`.
2. Ops-per-sample calibration runs before warmup and exercises the body. Therefore, some tier-up typically occurs before the first warmup batch is recorded. The curve shows remaining tiering, cache warming, and branch-predictor warming, rather than the full cold-start cliff.

The `clockResolutionNs` is the **measured** effective resolution of the timer, not `Stopwatch.Frequency`. For example, Apple Silicon may report 1 ns for frequency, but the counter steps in 41.667 ns units. `targetSampleDurationNs` is the duration target resolved against the measured resolution to span `AutoTune.MinQuantaPerSample` steps; `sampleDurationNs` is the actual duration of one sample.

The `sampleQuantizationFraction` is one clock step as a fraction of one sample. This represents the granularity floor of the measurement. If the `marginOfError` is well below this fraction, the result describes the clock's step grid rather than the code. A margin of ±0.03% next to a median that shifts 0.5% on re-run is a typical signature of this effect. See [Timer resolution](../statistics/measurement.md#timer-resolution).

The `jitLastChangeAtNs` field records how far into warmup the JIT last compiled a method. Under continuous load, this is typically the promotion of the benchmark's own hot path. Compare this against `warmupElapsedNs` to determine how much quiet time followed the last JIT event. The `warmupJit*` counters are process-wide `System.Runtime.JitInfo` deltas. In in-process runs, the first benchmark typically absorbs most startup compilation.

`totalDuration` is the end-to-end wall-clock time (warmup + pre-measure GC + measured loop), while `measuredDuration` is the measured loop only. The gap is primarily composed of warmup iterations and the pre-measure `GC.Collect`.

The `detail` and `profile` fields in the envelope report the active detail level (`simple`, `standard`, or `advanced`) and measurement profile. The result records always contain all available fields regardless of the detail level.

## Notes

- NBenchmark creates the output directory automatically if it does not exist.
- `BenchmarkResult` is serialized with all properties, including `ConfidenceIntervalLower` and `ConfidenceIntervalUpper` (computed from `Mean ± MarginOfError`).
- The `autoTune` object is `null` for dry-run and errored results. For pinned runs, the stop reasons are `explicitCount`.

## Using with Benchmark (Single mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.SaveJsonAsync("results/");
await result.SaveJsonAsync("results/", "benchmarks.json");
```

## CLI usage (BenchmarkHarness)

```bash
dotnet run -- --reporter json
dotnet run -- --reporter json --output ./results
```
