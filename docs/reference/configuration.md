---
title: Configuration
description: All MeasurementOptions settings with their defaults and valid ranges.
order: 0
---

# Configuration

All measurement settings are controlled by `MeasurementOptions`. The defaults are sensible for most benchmarks - only change what you have a reason to change.

## Using MeasurementOptions

### With Benchmark (Quick mode)

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
};

var result = Benchmark.Run(() => MyMethod(), options: options);
```

### With BenchmarkSuite (Suite mode)

Use the fluent `With*` methods - they each update a single option:

```csharp
await new BenchmarkSuite("name")
    .WithIterations(500)
    .WithWarmup(50)
    .WithAllocations()
    .WithOutlierMode(OutlierMode.IqrFence)
    .WithConfidenceLevel(0.99)
    .RunAsync();
```

### With BenchmarkHost (Host mode)

Call `WithOptions` or use CLI flags. CLI flags always take priority over `WithOptions`:

```csharp
BenchmarkHost.Create(args)
    .WithOptions(new MeasurementOptions { Iterations = 500 })
    ...
```

```bash
dotnet run -- --iterations 500 --warmup 50
```

## Options reference

### Iterations

```csharp
Iterations = null   // default - auto-resolved from a CI-width target
```

The number of measured samples per benchmark, typed as `int?`:

| Value | Behaviour |
|---|---|
| `null` **(default)** | Auto-resolved. NBenchmark streams samples until the confidence interval on the mean is tight enough (the `AutoTune.CiTarget` half-width), bounded by `AutoTune.MinSamples` and `AutoTune.MaxSamples`. |
| `0` | Dry-run. The body is not invoked and no measurements are taken. See [CLI Reference: `--dry-run`](./cli.md#--dry-run). |
| `> 0` | Pins an exact measured-sample count, disabling auto-sampling. Valid range: `1` to `100 000`. |

Pinning an exact count makes a run deterministic in sample size - useful for reproducible CI gates. Leaving it `null` lets each benchmark collect exactly as many samples as it needs to hit the precision target and no more.

> [!TIP]
> In auto mode a large Error resolves itself: NBenchmark keeps sampling until the interval is tight. To demand tighter intervals, lower `AutoTune.CiTarget` (or use the `Thorough` preset). To cap a long run, lower `AutoTune.MaxSamples` or `AutoTune.MaxTuningTime`.

CLI flag: `--iterations <n>` (pins the count). The auto-mode bounds map to `--ci-target`, `--min-samples`, and `--max-samples`.

### WarmupIterations

```csharp
WarmupIterations = null   // default - auto-detected with a plateau rule
```

The number of warmup samples discarded before measurement begins, typed as `int?`:

| Value | Behaviour |
|---|---|
| `null` **(default)** | Auto-detected. NBenchmark watches the per-sample timings and stops warmup once they plateau (stop improving), bounded by `AutoTune.MinWarmup` and `AutoTune.MaxWarmup`. |
| `0` | Skips warmup entirely - the first measured sample includes any cold-start cost. |
| `> 0` | Pins an exact warmup count. Valid range: `1` to `10 000`. |

Warmup lets the JIT compiler optimise your code and brings data into CPU caches. The plateau rule spends just enough warmup to reach steady state instead of a fixed budget. See [Key Concepts: Warmup](../getting-started/key-concepts.md#warmup) for more detail.

CLI flag: `--warmup <n>` (pins the count). The auto-mode bounds map to `--min-warmup` and `--max-warmup`.

### OpsPerSample

```csharp
OpsPerSample = null   // default - auto-calibrated (K)
```

The number of back-to-back body invocations timed together as one sample, called **K**. Typed as `int?`:

| Value | Behaviour |
|---|---|
| `null` **(default)** | Auto-calibrated. NBenchmark doubles K until one sample spans roughly `AutoTune.TargetSampleDurationNs` (1 µs by default), so a single timer read covers enough work to be meaningful. Reported per-op timings divide the batch time by K. |
| `> 0` | Pins an exact K (always honoured). Valid range: `1` to `16 777 216`. |

Calibration matters for **fast bodies**: a method that runs in a few nanoseconds is dominated by the cost of reading the timer. Timing K invocations as a batch amortises that fixed overhead, then NBenchmark divides back down to a per-operation number.

Auto-calibration is skipped (K stays `1`) when per-iteration `IterationSetup`/`IterationTeardown` is configured, or when `ForceGcBeforeEachIteration` is on, since those make a batch unrepresentative of a single call. An explicit `OpsPerSample` is always honoured.

BenchmarkSuite/BenchmarkHost fluent method: `.WithOpsPerSample(64)`  
CLI flag: `--ops-per-sample <n>` (pins K). The calibration target is `AutoTune.TargetSampleDurationNs`.

Unlike `Iterations` and `WarmupIterations`, `OpsPerSample` cannot be pinned per method via `[Benchmark]` - it is set suite- or host-wide only (`.WithOpsPerSample(n)` or `--ops-per-sample n`).

### LaunchCount

```csharp
LaunchCount = 1   // default
```

The number of times to repeat each benchmark as a separate launch, typed as `int`:

| Value | Behaviour |
|---|---|
| `1` **(default)** | Run the benchmark once. No aggregation. |
| `> 1` | Repeat the full benchmark (warmup + measurement) N times. Cross-launch statistics (mean, stddev, median, CI across launch medians) are computed and stored in `BenchmarkResult.LaunchStatistics`. The primary result fields reflect the **best** launch (lowest median). Valid range: `2` to `100`. |

Use multiple launches when single-run noise is a concern and you want to see how much the median itself varies across independent measurements. Each launch includes its own warmup and GC cycle, so consecutive launches are independent measurements of the same body - not correlated samples.

**Dry-run interaction:** When `--dry-run` (Iterations=0, WarmupIterations=0) is combined with `LaunchCount > 1`, exactly one dry launch is performed. The extra launches would not add information since dry runs skip the body.

**Isolation interaction:** When the benchmark runs in a child process (host mode default, or `WithIsolation()` in suite mode), the parent spawns N children. The child process is unaware of the launch count.

**Attribute override:** In Host mode each `[Benchmark]` can override the launch count per-method:

```csharp
// This method runs 5 independent launches regardless of the host setting.
[Benchmark(LaunchCount = 5)]
public void MyNoisyBenchmark() => SlowOperation();
```

The CLI flag `--launch-count` always takes priority over both `WithOptions` and the per-method attribute.

BenchmarkSuite fluent method: `.WithLaunchCount(5)`  
CLI flag: `--launch-count <n>`

### AutoTune

```csharp
AutoTune = AutoTuneOptions.Default   // default
```

Bounds and steers the adaptive measurement loop - the warmup plateau rule, the CI-width sample-count rule, and ops-per-sample calibration. Three named presets trade measurement time for precision:

| Preset | MinWarmup | MinSamples | CiTarget | Use it for |
|---|---|---|---|---|
| `AutoTuneOptions.Quick` | 4 | 15 | 0.05 (±5%) | Fast inner-loop feedback. |
| `AutoTuneOptions.Default` | 8 | 30 | 0.025 (±2.5%) | The balanced default. |
| `AutoTuneOptions.Thorough` | 16 | 100 | 0.01 (±1%) | Publication-grade numbers. |

Pick a preset with `.WithAutoTune(AutoTunePreset.Thorough)` (suite/host) or `--auto-tune thorough` on the CLI, or build your own `AutoTuneOptions` record. The individual knobs:

| Knob | Default | Meaning |
|---|---|---|
| `MinWarmup` / `MaxWarmup` | `8` / `10 000` | Floor and ceiling for auto-detected warmup length. |
| `WarmupEpsilon` | `0.02` | Minimum relative improvement a warmup batch must show to count as "still warming up". |
| `PlateauPatience` | `3` | Consecutive non-improving batches that end warmup. |
| `MinSamples` / `MaxSamples` | `30` / `100 000` | Floor and ceiling for the auto-resolved measured-sample count. |
| `CiTarget` | `0.025` | Target relative half-width of the confidence interval; sampling stops once it is met. |
| `TargetSampleDurationNs` | `1 000` | Per-sample duration that ops-per-sample calibration aims for. |
| `MaxOpsPerSample` | `1 048 576` | Ceiling on auto-calibrated K. |
| `BatchSize` | `8` | Warmup batch size and the cadence on which the CI-width rule is evaluated. |
| `MaxTuningTime` | `20 s` | Per-benchmark safety cap on cumulative in-body sample time (calibration + warmup + measurement). Setup, teardown, and GC are excluded, so real wall-clock can exceed it. |
| `CapBehavior` | `Warn` | What happens when `MaxTuningTime` is reached before the CI target or warmup plateau is reached. `Warn` emits a warning; `Error` marks the benchmark as errored. |

The interval's confidence level is `ConfidenceLevel` (below) - the CI-width rule targets that same level, so it is not duplicated on `AutoTune`.

BenchmarkSuite/BenchmarkHost fluent method: `.WithAutoTune(AutoTunePreset.Quick)` or `.WithAutoTune(customOptions)`  
CLI flags: `--auto-tune <default|quick|thorough>`, plus `--ci-target`, `--min-samples`, `--max-samples`, `--min-warmup`, `--max-warmup`, `--max-tuning-time`, `--autotune-cap-behavior`.

### Profile

```csharp
Profile = MeasurementProfile.Realistic   // default
```

The measurement profile is the authoritative setting that controls three behaviours: per-iteration Gen0 GC, between-benchmark full GC, and allocation tracking. The resolved booleans (`ForceGcBeforeEachIteration`, `ForceGcBetweenBenchmarks`, `MeasureAllocations`) are computed from `Profile` unless explicitly overridden via the corresponding `*Override` field.

| Profile | ForceGcBeforeEachIteration | ForceGcBetweenBenchmarks | MeasureAllocations |
|---|---|---|---|
| `Realistic` (default) | `false` | `false` | `true` |
| `Independent` | `true` | `true` | `false` |

Each resolved boolean can be overridden individually:

```csharp
// Enable per-iteration GC under Realistic
options with { ForceGcBeforeEachIterationOverride = true }

// Disable allocation tracking under Realistic
options with { MeasureAllocationsOverride = false }
```

BenchmarkHost fluent method: `.WithMeasurementProfile(MeasurementProfile.Independent)`
BenchmarkSuite fluent method: `.WithMeasurementProfile(MeasurementProfile.Independent)`
CLI flag: `--profile independent`

### ForceGcBeforeEachIteration

```csharp
ForceGcBeforeEachIteration => ForceGcBeforeEachIterationOverride ?? (Profile == MeasurementProfile.Independent)
```

This is a **computed property** derived from `Profile` (or the `ForceGcBeforeEachIterationOverride` field when set). When `true`, a gen-0 GC collection is triggered before each measured iteration. This keeps allocation side-effects from previous iterations out of your measurement.

Under the `Realistic` profile (the default), this resolves to `false`. To enable per-iteration GC under `Realistic`, set `ForceGcBeforeEachIterationOverride = true` or use `--force-gc` on the CLI.

### ForceGcBetweenBenchmarks

```csharp
ForceGcBetweenBenchmarks => ForceGcBetweenBenchmarksOverride ?? (Profile == MeasurementProfile.Independent)
```

A **computed property** derived from `Profile` (or the `ForceGcBetweenBenchmarksOverride` field when set). When `true`, a full Gen2 GC (with finalizer wait) runs after warmup and before the measurement loop begins.

Under the `Realistic` profile (the default), this resolves to `false`. The benchmark body inherits whatever heap state the warmup left behind, matching production behaviour.

### MeasureAllocations

```csharp
MeasureAllocations => MeasureAllocationsOverride ?? (Profile == MeasurementProfile.Realistic)
```

A **computed property** derived from `Profile` (or the `MeasureAllocationsOverride` field when set). When `true`, NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` around each iteration and reports the mean bytes allocated per operation in the **Alloc/op** column (with a process-wide fallback for async thread hops).

Under the `Realistic` profile (the default), this resolves to `true`. To disable allocation tracking under `Realistic`, set `MeasureAllocationsOverride = false` or use `--no-allocations` on the CLI.

BenchmarkSuite fluent method: `.WithAllocations()`

> [!NOTE]
> Allocation tracking adds a small overhead to each iteration and may slightly affect timing measurements.

### OutlierMode

```csharp
OutlierMode = OutlierMode.IqrFence   // default
```

Controls which samples are discarded before statistics are computed.

| Value | Behaviour |
|---|---|
| `OutlierMode.None` | No samples are removed. |
| `OutlierMode.RemoveTop5Percent` | The slowest 5% of samples are removed. |
| `OutlierMode.RemoveTopAndBottom5Percent` | The slowest and fastest 5% are removed. |
| `OutlierMode.IqrFence` | Samples beyond 1.5× the [IQR (inter-quartile range)](https://en.wikipedia.org/wiki/Interquartile_range) are removed. **(default)** |
| `OutlierMode.MedianAbsoluteDeviation` | Samples more than 3× the scaled [MAD](https://en.wikipedia.org/wiki/Median_absolute_deviation) from the median are removed - a robust alternative to `IqrFence` for heavily skewed data. |

`IqrFence` adapts to the actual spread of each benchmark instead of always discarding a fixed 5%: a clean run keeps nearly every sample, while a noisy run trims more. When the discarded slow samples form a tight secondary cluster (rather than scattered scheduling noise), NBenchmark adds a bimodal-distribution warning to the result so you can investigate the tail latency.

BenchmarkSuite fluent method: `.WithOutlierMode(mode)`  
CLI flag: `--outlier <none|top5|both5|iqr|mad>`

See [Outlier Trimming](../statistics/outliers.md) for the full algorithms.

### OutlierDetector

```csharp
OutlierDetector = null   // default - falls back to OutlierMode
```

A custom `IOutlierDetector` (from `NBenchmark.Stats`) that **takes priority over `OutlierMode`** when set. Use it to plug in a trimming rule that the built-in modes do not cover - a tail-preserving filter, a fixed physical threshold, and so on. `MeasurementOptions.ResolveOutlierDetector()` returns this detector when present, otherwise the detector mapped from `OutlierMode`.

```csharp
using NBenchmark.Stats;

new MeasurementOptions { OutlierDetector = new KeepFastestDetector(0.90) };
```

BenchmarkSuite fluent method: `.WithOutlierDetector(detector)`

> [!NOTE]
> The `--outlier` CLI flag always wins: passing it clears any programmatic `OutlierDetector` so the command line stays authoritative. See [Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors).

### ConfidenceLevel

```csharp
ConfidenceLevel = 0.95   // default
```

The confidence level for the margin of error reported in the Error column. Must be strictly between `0` and `1`.

| Value | Meaning |
|---|---|
| `0.90` | 90% confidence - narrower interval, less conservative |
| `0.95` | 95% confidence - the standard choice **(default)** |
| `0.99` | 99% confidence - wider interval, more conservative |

A higher confidence level produces a wider (larger) Error value. Use `0.99` when a result will be used to make an important decision and you want to be very conservative.

BenchmarkSuite fluent method: `.WithConfidenceLevel(0.99)`  
CLI flag: `--confidence 0.99`

### ReportedPercentiles

```csharp
ReportedPercentiles = [0.50, 0.95, 0.99, 0.999, 1.0]   // default
```

The set of percentile values to compute from the trimmed samples, typed as `IReadOnlyList<double>`. Each value must be between `0` and `1` inclusive. Values `> 0.50` and `< 1.0` appear as columns in reporter tail-latency tables (e.g. P95, P99, P99.9).

| Value | Behaviour |
|---|---|
| `[0.50, 0.95, 0.99, 0.999, 1.0]` **(default)** | Reports P50 (Median), P95, P99, P99.9, and Max. |
| Custom list | Only the specified percentile values are computed. P50 (`0.50`) does not produce a separate percentile column because it is already shown as Median. Max (`1.0`) is reported via the existing Max stat field. |
| `[0.90]` | Single custom percentile - P90 is computed and displayed. |

The computed values are stored in `BenchmarkResult.Percentiles` as `IReadOnlyList<PercentileEntry>`, where each entry has a `Percentile` (double, 0-1) and `Value` (double, in nanoseconds). Use `result.GetPercentile(0.95)` to retrieve a specific percentile value.

CLI flag: `--percentiles <list>` (comma-separated, e.g. `--percentiles 0.90,0.99,0.999`).

### EnableHistogram

```csharp
EnableHistogram = true   // default
```

When `true`, NBenchmark computes a latency histogram from the trimmed samples. The histogram is available on `BenchmarkResult.Histogram` as a `LatencyHistogram` record containing an ordered list of `HistogramBucket` values (each with `Lower`, `Upper`, and `Count`), plus `Min`, `Max`, and `SampleCount`.

Set to `false` to skip histogram computation and keep `BenchmarkResult.Histogram` as `null`. Useful when you do not need the histogram and want to save a small amount of computation.

CLI flag: `--no-histogram` (disables histogram).

### HistogramBucketCount

```csharp
HistogramBucketCount = 20   // default
```

The number of equal-width buckets in the latency histogram. Only used when `EnableHistogram` is `true`. Must be between `5` and `100`. More buckets provide finer granularity at the cost of fewer samples per bucket.

### EnableSignificance

```csharp
EnableSignificance = true   // default
```

When `true` and there are two or more benchmarks, NBenchmark tests whether the differences are statistically significant. With exactly two benchmarks it runs a [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (exact permutation p-value for small, tie-free samples; normal approximation with tie and continuity corrections otherwise). With **three or more** benchmarks it runs the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) omnibus test instead, reporting a single verdict across all groups.

Disable it if you don't need significance testing and want to reduce overhead:

```csharp
.WithSignificance(false)
```

### SignificanceLevel

```csharp
SignificanceLevel = 0.05   // default
```

The significance threshold (alpha) a result's p-value is compared against. A result is flagged significant when `p < SignificanceLevel`. Must be strictly between `0` and `1`. Lower it (e.g. `0.01`) to demand stronger evidence before calling a difference real.

CLI flag: `--alpha 0.01`

### SignificanceTest

```csharp
SignificanceTest = null   // default - DefaultSignificanceTest (group-count aware)
```

A custom `ISignificanceTest` (from `NBenchmark.Stats`) that replaces the built-in strategy. When `null`, `ResolveSignificanceTest()` returns `DefaultSignificanceTest`, which picks Mann-Whitney U for two groups and Kruskal-Wallis + post-hoc Mann-Whitney U (Holm-Bonferroni corrected) for three or more. Implement the interface to supply a bootstrap, Bayesian, post-hoc, or domain-specific rule:

```csharp
using NBenchmark.Stats;

new MeasurementOptions { SignificanceTest = new MedianRatioSignificanceTest(25) };
```

BenchmarkSuite fluent method: `.WithSignificanceTest(test)`

See [Custom significance tests](../statistics/significance.md#custom-significance-tests).

## Applying options per-method (Host mode)

In Host mode, the `[Benchmark]` attribute accepts per-method overrides that take priority over the host-level options:

```csharp
// This method uses 1000 iterations regardless of the host setting.
[Benchmark(Iterations = 1000, WarmupIterations = 100)]
public void MyExpensiveBenchmark() => SlowOperation();
```

> [!NOTE]
> Only `Iterations`, `WarmupIterations`, and `LaunchCount` are pinnable per method. `OpsPerSample` is not exposed on `[Benchmark]` - pin it host-wide with `.WithOpsPerSample(n)` or `--ops-per-sample n`.

## Categories

Categories are not part of `MeasurementOptions`; they are metadata declared with `[BenchmarkCategory]` and used for filtering. See the [Categories guide](../guides/categories.md) for the full feature.

## Valid ranges summary

| Option | Type | Default | Valid range |
|---|---|---|---|
| `Iterations` | `int?` | `null` (auto) | `0` – `100 000` when set (`0` = dry-run) |
| `WarmupIterations` | `int?` | `null` (auto) | `0` – `10 000` when set |
| `OpsPerSample` | `int?` | `null` (auto) | `1` – `16 777 216` when set |
| `LaunchCount` | `int` | `1` | `1` – `100` |
| `AutoTune` | `AutoTuneOptions` | `AutoTuneOptions.Default` | See [AutoTune](#autotune) |
| `ConfidenceLevel` | `double` | `0.95` | `>0` and `<1` |
| `SignificanceLevel` | `double` | `0.05` | `>0` and `<1` |
| `Profile` | `enum` | `Realistic` | `Realistic` or `Independent` |
| `ForceGcBeforeEachIteration` | `bool` (computed) | `false` | Derives from `Profile`; override via `ForceGcBeforeEachIterationOverride` |
| `MeasureAllocations` | `bool` (computed) | `true` | Derives from `Profile`; override via `MeasureAllocationsOverride` |
| `ForceGcBetweenBenchmarks` | `bool` (computed) | `false` | Derives from `Profile`; override via `ForceGcBetweenBenchmarksOverride` |
| `OutlierMode` | `enum` | `IqrFence` | See above |
| `OutlierDetector` | `IOutlierDetector?` | `null` | Overrides `OutlierMode` when set |
| `ReportedPercentiles` | `IReadOnlyList<double>` | `[0.50, 0.95, 0.99, 0.999, 1.0]` | Each value 0-1 |
| `EnableHistogram` | `bool` | `true` | - |
| `HistogramBucketCount` | `int` | `20` | `5` – `100` |
| `EnableSignificance` | `bool` | `true` | - |
| `SignificanceTest` | `ISignificanceTest?` | `null` | Defaults to `DefaultSignificanceTest` |

Values outside the valid range throw `ArgumentOutOfRangeException`.

---

**Still having issues?** See the [Troubleshooting guide](../troubleshooting.md) for symptom-to-configuration mappings for common measurement problems.
