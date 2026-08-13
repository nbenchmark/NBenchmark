---
title: JsonReporter
description: Save benchmark results to a JSON file for programmatic consumption.
order: 4
---

# JsonReporter

`JsonReporter` writes results to a `.json` file as a structured object. It is part of the core `NBenchmark` package (uses `System.Text.Json` with no additional dependencies).

JSON output is suitable for CI dashboards, performance tracking over time, or any tooling that consumes structured data.

## Setup

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

- `outputDirectory` - The directory to write the file to. Created automatically if it does not exist. Must be under the current working directory.
- `fileName` - When `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. When specified, the exact filename is used (no counter or timestamp is appended).

### Auto-naming

When `fileName` is not provided, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```
benchmarks-20260606-034000-001.json
```

The counter increments each time `ReportAsync` is called within the same process, so multiple suite runs produce separate files instead of overwriting each other.

### Explicit filename

Pass a `fileName` when you want a stable output path:

```csharp
new JsonReporter("results/", "benchmarks.json")
```

When an explicit `fileName` is provided, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

The envelope opens with `schemaVersion` and `measurementEpoch` - see
[Report format versioning](./index.md#report-format-versioning) before diffing two files.

```json
{
  "schemaVersion": 1,
  "measurementEpoch": 4,
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

All timing values are in **nanoseconds**. Property names use camelCase.

Percentile values are emitted in a `percentiles` array of `{ percentile, value }` objects. The set of reported percentiles is controlled by `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. When `EnableHistogram` is `true` (default), a `histogram` object with `min`, `max`, `sampleCount`, and `buckets` (array of `{ lower, upper, count }`) is also included.

### Which sample set each statistic describes

The result carries **two populations**, and `tailMetricsBasis` says which basis the tail metrics used. Under the default `raw`, the order statistics — `min`, `max`, `percentiles` and `histogram` — describe the **full pre-trim** sample set, while `mean`, `median`, `standardDeviation`, `standardError`, `marginOfError`, `coefficientOfVariation`, the confidence intervals and `measuredIterations` always describe the **trimmed (inlier)** set. That is deliberate: the outlier fence removes exactly the slow tail P99/Max exist to describe (see [Descriptive statistics](../statistics/descriptive.md)). But it means the two groups are not directly comparable — a consumer that displays both should label which is which, and `outlierDetector` names the detector that drew the line.

### The `autoTune` object

`autoTune` records what the [adaptive measurement loop](../statistics/measurement.md#the-measurement-loop) decided for this benchmark. It is `null` on dry-run and errored results.

| Group | Fields |
| --- | --- |
| **What it resolved** | `resolvedWarmup`, `resolvedSamples`, `opsPerSample`, `initialOpsPerSample` (the pre-recalibration cold K, or `null`), `totalBodyInvocations` |
| **Why it stopped** | `warmupStop` (`settled` / `maxCeiling` / `explicitCount` / `wallClockCap`), `sampleStop` (adds `ciTargetMet` and `driftUnresolved`) |
| **How well it converged** | `achievedRelativeCiWidth` (on the **raw** stream — see the caveat below), `ciWidthSeries` (the convergence trace, one entry per cadence check), `tuningWallClock` |
| **Host and stability** | `jitterMetric`, `outlierDetectorSwitched`, `measurementRestarts`, `splitHalfDrift` |
| **Warmup and tiered compilation** | `warmupTimeFloorMet`, `warmupElapsedNs`, `warmupCurve`, `warmupSampleInterval`, `warmupJitCompiledMethods`, `warmupJitCompilationTime`, `warmupJitCompiledIlBytes`, `jitLastChangeAtNs`, `jitQuiescenceAchieved` |
| **Clock resolution** | `clockResolutionNs`, `targetSampleDurationNs`, `sampleDurationNs`, `sampleQuantizationFraction` |

> **`achievedRelativeCiWidth` and `marginOfError` measure different things.** The former is the CI half-width the loop achieved on the **raw** stream at its stop decision; the latter is recomputed on the **trimmed** set. When the outliers carry most of the variance the two diverge sharply — a benchmark can report `marginOfError` at ±1% of the mean next to an `achievedRelativeCiWidth` of `1.05`. That is not a contradiction, but treat the trimmed margin as optimistic whenever `sampleStop` is not `ciTargetMet`.

`warmupCurve` is the mean per-op time of each warmup batch, oldest first — the shape of tiered compilation landing, since a body promoted from tier-0 to tier-1 (and re-optimized again under dynamic PGO) gets faster in steps. `warmupSampleInterval` gives the warmup iterations between consecutive points, so the array can be plotted against a real iteration axis. The array is bounded at 512 points: longer warmups are decimated by a doubling stride, keeping the points evenly spaced and the shape intact at coarser resolution. It is empty for pinned `warmupIterations` (which runs no plateau detection) and when `IncludeSamples` is off.

Two limits worth knowing. This is **aggregate decay, not per-method tier attribution** — naming individual methods and their tiers (`QuickJitted`, `OptimizedTier1`, OSR, instrumented) requires the runtime's `MethodLoadVerbose` events via EventPipe or an in-process `EventListener`, which NBenchmark does not collect. And **ops-per-sample calibration runs before warmup** and already exercises the body, so some tier-up has typically happened before the first warmup batch is recorded — the curve shows what remains of tiering plus cache and branch-predictor warming, not the full cold-start cliff.

`clockResolutionNs` is the **measured** effective resolution of the timer, not `Stopwatch.Frequency` — that figure is an advertised conversion rate and reports 1 ns on Apple Silicon, where the counter actually steps in 41.667 ns units. `targetSampleDurationNs` is the sample-duration target calibration resolved against, after the measured resolution raised it to span `AutoTune.MinQuantaPerSample` steps; `sampleDurationNs` is what one sample really spanned (K is a power of two, so it overshoots).

`sampleQuantizationFraction` is one clock step as a fraction of one sample — the granularity floor on how finely this measurement could be resolved, whatever `marginOfError` says. **Read the two together.** A margin well below this fraction is describing the clock's step grid rather than the code: within a run consecutive samples of a stable body land on the same step so the spread looks tiny, while between runs a shift far smaller than one step moves every sample to the next step and the median with it. A margin of ±0.03% next to a median that moves 0.5% on re-run is that signature, and it is indistinguishable from a genuine result without this field. See [Timer resolution](../statistics/measurement.md#timer-resolution).

`jitLastChangeAtNs` is how far into warmup the JIT last compiled anything. With the body under continuous load that is typically the promotion of its own hot path, which makes it the closest thing to a tier-up marker to draw on the curve; compare it against `warmupElapsedNs` to see how much quiet time followed. The three `warmupJit*` counters are process-wide `System.Runtime.JitInfo` deltas, so in an in-process run the first benchmark to execute absorbs most of the startup compilation and later ones see almost none — that is real, and since benchmark order is randomised it is a large part of why the same benchmark's warmup differs between runs.

`totalDuration` is end-to-end wall-clock (warmup + pre-measure GC + measured loop); `measuredDuration` is the measured loop only. `measuredDuration <= totalDuration` always; the gap is dominated by warmup iterations and the pre-measure `GC.Collect`.

The `detail` and `profile` fields in the envelope report the active detail level (`simple`, `standard`, or `advanced`) and measurement profile. The result records always contain all available fields regardless of detail level.

## Notes

- The output directory is created automatically if it does not exist.
- `BenchmarkResult` is serialised with all properties, including `ConfidenceIntervalLower` and `ConfidenceIntervalUpper` (computed from `Mean ± MarginOfError`).
- The `autoTune` object is `null` for dry-run and errored results; for pinned runs the stop reasons are `explicitCount`.

## Using with Benchmark (Single mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToJsonAsync("results/");
await result.ToJsonAsync("results/", "benchmarks.json");
```

## CLI usage (BenchmarkHarness)

```bash
dotnet run -- --reporter json
dotnet run -- --reporter json --output ./results
```
