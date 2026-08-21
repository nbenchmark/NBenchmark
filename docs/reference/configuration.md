---
title: Configuration
description: The full MeasurementOptions reference - every setting, its default, and its valid range.
order: 0
---

# Configuration

Every measurement setting is configured via `MeasurementOptions`. The defaults are suitable for most benchmarks; change only the settings you have a specific reason to modify.

> [!TIP]
> For common configuration scenarios, see [Tuning recipes](../guides/tuning-recipes.md). This guide covers noisy CI, fast feedback, publication-grade precision, pure CPU measurement, and debugging unstable results.

## Using MeasurementOptions

### In Single mode

Pass a `MeasurementOptions` instance to the `options` parameter of `Benchmark.Run`:

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
};

var result = Benchmark.Run(() => MyMethod(), options: options);
```

### In Suite mode

Use the fluent `With*` methods to update individual options:

```csharp
await new BenchmarkSuite("name")
    .WithIterations(500)
    .WithWarmup(50)
    .WithAllocations()
    .WithOutlierMode(OutlierMode.IqrFence)
    .WithConfidenceLevel(0.99)
    .RunAsync();
```

### In Harness mode

Call `WithOptions` or use CLI flags. CLI flags always take priority over `WithOptions`:

```csharp
BenchmarkHarness.Create(args)
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

The number of measured samples per benchmark (`int?`):

| Value | Behavior |
| --- | --- |
| `null` **(default)** | Auto-resolved. NBenchmark streams samples until the confidence interval on the mean meets the target half-width (`AutoTune.CiTarget`), bounded by `AutoTune.MinSamples` and `AutoTune.MaxSamples`. |
| `0` | Dry-run. NBenchmark does not invoke the body and takes no measurements. For more information, see [CLI Reference: `--dry-run`](./cli.md#measurement). |
| `> 0` | Pins an exact measured-sample count and disables auto-sampling. Valid range: 1 to 100,000. |

Pinning an exact count ensures a run is deterministic in sample size, which is useful for reproducible CI gates. Leaving this value `null` allows each benchmark to collect only as many samples as needed to hit the precision target.

> [!TIP]
> In auto mode, a large Error resolves itself because NBenchmark continues sampling until the interval is tight. To demand tighter intervals, lower `AutoTune.CiTarget` or use the `Thorough` preset. To cap a long run, lower `AutoTune.MaxSamples` or `AutoTune.MaxTuningTime`.

CLI flag: `--iterations <n>` (pins the count). The auto-mode bounds map to `--ci-target`, `--min-samples`, and `--max-samples`.

### WarmupIterations

```csharp
WarmupIterations = null   // default - auto-detected with a plateau rule
```

The number of warmup samples discarded before measurement begins (`int?`):

| Value | Behavior |
| --- | --- |
| `null` **(default)** | Auto-detected. NBenchmark monitors per-sample timings and stops warmup once they plateau (stop improving), bounded by `AutoTune.MinWarmup` and `AutoTune.MaxWarmup`. |
| `0` | Skips warmup entirely. The first measured sample includes any cold-start cost. |
| `> 0` | Pins an exact warmup count. Valid range: 1 to 10,000. |

Warmup allows the JIT compiler to optimize your code and brings data into CPU caches. The plateau rule spends only as much warmup as needed to reach a steady state instead of using a fixed budget. For more details, see [Key Concepts: Warmup](../getting-started/key-concepts.md#warmup).

CLI flag: `--warmup <n>` (pins the count). The auto-mode bounds map to `--min-warmup` and `--max-warmup`.

### OpsPerSample

```csharp
OpsPerSample = null   // default - auto-calibrated (K)
```

The number of back-to-back body invocations timed together as one sample, also called **K** (`int?`):

| Value | Behavior |
| --- | --- |
| `null` **(default)** | Auto-calibrated. NBenchmark doubles K until one sample spans roughly the target sample duration (`AutoTune.TargetSampleDurationNs`, 10 µs by default). NBenchmark also raises this target per host so the sample spans `AutoTune.MinQuantaPerSample` steps of the measured clock resolution, ensuring a single timer read covers enough work to be meaningful. Reported per-op timings divide the batch time by K. |
| `> 0` | Pins an exact K. Valid range: 1 to 16,777,216. |

Calibration is critical for **fast bodies**. A method that runs in a few nanoseconds is dominated by the cost of reading the timer. Timing K invocations as a batch amortizes that fixed overhead.

NBenchmark skips auto-calibration (K remains `1`) when per-iteration `IterationSetup` or `IterationTeardown` is configured, as a K-batch would be unrepresentative of a single call. However, auto-calibration is **not** skipped under the `Independent` profile. The forced Gen0 GC runs once per sample (K-batch) before the timestamp and outside the timed window, which is the same semantics as a pinned `OpsPerSample`. If an `Independent` body allocates and `K > 1`, NBenchmark issues a warning that a GC may occur inside a timed batch; pin `--ops-per-sample 1` to avoid this. An explicit `OpsPerSample` is always honored.

BenchmarkSuite/BenchmarkHarness fluent method: `.WithOpsPerSample(64)`
CLI flag: `--ops-per-sample <n>` (pins K). The calibration target is `AutoTune.TargetSampleDurationNs`, raised to clear `AutoTune.MinQuantaPerSample` clock-resolution steps.

> [!WARNING] Pinning a small K on a fast body can fall below the clock's resolution
> Pinning `OpsPerSample` skips calibration, including the clock-resolution floor. On hosts with a coarse clock - such as 41.667 ns on Apple Silicon or 100 ns on Windows QPC - a nanosecond-scale body at `K = 1` produces samples the clock cannot resolve. Most samples read zero and others read one whole step. NBenchmark warns when the measured interval is finer than one step. To fix this, pin a K large enough that a sample spans hundreds of steps, or leave K auto-calibrated. See [Timer resolution](../statistics/measurement.md#timer-resolution).

Unlike `Iterations` and `WarmupIterations`, `OpsPerSample` cannot be pinned per method via `[Benchmark]`. It is set suite- or harness-wide via `.WithOpsPerSample(n)` or `--ops-per-sample n`.

### LaunchCount

```csharp
.WithLaunchCount(1)   // default
```

The number of times to repeat each benchmark as a separate launch (`int`). You can set this through the fluent builders, attributes, or the CLI flag.

The default is `1`. In Harness mode, NBenchmark applies `5` by default if the launch count is not explicitly pinned. Pass `WithLaunchCount(1)` to opt out of the harness default.

Five launches are used because the between-launch interval is a Student-t half-width on `k - 1` degrees of freedom. The critical value falls steeply over the first few replicates: 12.71 at `k = 2`, 4.30 at 3, 3.18 at 4, and 2.78 at 5. Below five, the interval is often too wide for a real regression to be clear. Past five, replicates cost linearly but provide diminishing returns.

| Value | Behavior |
| --- | --- |
| `1` **(default)** | Runs the benchmark once. No aggregation is performed. |
| `> 1` | Repeats the full benchmark (warmup + measurement) N times, each in its own worker process. NBenchmark computes cross-launch statistics (mean, stddev, median, and CI across launch medians) and stores them in `BenchmarkResult.LaunchStatistics`. Primary result fields are the **average across launches**, and the reported interval comes from the spread between them. Valid range: 2 to 100. |

Use multiple launches when single-run noise is a concern and you want to see how much the median varies across independent measurements. Each launch includes its own warmup and GC cycle, ensuring consecutive launches are independent measurements of the same body.

**Dry-run interaction**: The `--dry-run` flag always performs exactly one launch, though an explicit `WithLaunchCount(n)` in code is still honored.

**Isolation interaction**: When the benchmark runs in a worker process (the default in every mode), NBenchmark spawns N workers, one per launch.

**Attribute override**: In Harness mode, each `[Benchmark]` can override the launch count per method:

```csharp
// This method runs 5 independent launches regardless of the host setting.
[Benchmark(LaunchCount = 5)]
public void MyNoisyBenchmark() => SlowOperation();
```

The CLI flag `--launch-count` always takes priority over `WithLaunchCount`. The per-method attribute is applied on top for that method. An isolated group uses the maximum launch count among its members so every benchmark in the group receives at least the launches it requested.

BenchmarkSuite fluent method: `.WithLaunchCount(5)`
CLI flag: `--launch-count <n>`

### AutoTune

```csharp
AutoTune = AutoTuneOptions.Default   // default
```

`AutoTune` bounds and steers the adaptive measurement loop, including the warmup plateau rule, the CI-width sample-count rule, and ops-per-sample calibration. Three named presets trade measurement time for precision:

| Preset | MinWarmup | MinSamples | MaxSamples | CiTarget | MinWarmupTime | MinMeasurementTime | Use case |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `AutoTuneOptions.Quick` | 4 | 15 | 2,000 | 0.05 (±5%) | 500 ms | 50 ms | Fast inner-loop feedback. |
| `AutoTuneOptions.Default` | 8 | 30 | 5,000 | 0.025 (±2.5%) | 500 ms | 100 ms | Balanced default. |
| `AutoTuneOptions.Thorough` | 16 | 100 | 20,000 | 0.01 (±1%) | 1 s | 500 ms | Publication-grade numbers. |

> `Quick` does **not** shorten `MinWarmupTime`. This floor is a measurement-correctness requirement, not a speed/accuracy trade-off. Too short a warmup can result in measurements on tier-0 code, which may report a median several times off with a ±1% error bar and will not reproduce between runs. `Quick` achieves its speed through `CiTarget`, `MinSamples`, and `MaxTuningTime`.

Select a preset using `.WithAutoTune(AutoTunePreset.Thorough)` (suite/harness) or `--auto-tune thorough` on the CLI, or build a custom `AutoTuneOptions` record. Individual knobs include:

| Knob | Default | Meaning |
| --- | --- | --- |
| `MinWarmup` / `MaxWarmup` | `8` / `100,000` | Floor and ceiling for auto-detected warmup length, as sample counts. `MaxWarmup` is set high so that the *time* bounds typically bind first. A fast body may need tens of thousands of samples to accumulate `MinWarmupTime`. (The tighter `10,000` limit applies to *pinned* `WarmupIterations`.) |
| `WarmupEpsilon` | `0.02` | The minimum relative improvement a warmup batch must show to be considered "still warming up". |
| `PlateauPatience` | `3` | The number of consecutive non-improving batches that end warmup. |
| `MinWarmupTime` | `500 ms` | The minimum in-body time auto-warmup must accumulate before it can settle. This ensures background tiered JIT (tier-0 $\rightarrow$ tier-1 $\rightarrow$ dynamic PGO) lands before measurement. This floor is 5× the runtime's `TieredCompilation.CallCountingDelayMs` (100 ms). `0` disables the floor and the JIT-quiescence gate. `Thorough` uses 1 s; `Quick` inherits 500 ms. |
| `RequireJitQuiescence` | `true` | Whether auto-warmup also waits until the JIT has been quiet for `JitQuietPeriod` (read from `System.Runtime.JitInfo` at each batch boundary). This deactivates once warmup has run 4 × `MinWarmupTime` to prevent a busy in-process host from blocking warmup indefinitely. Inactive when `MinWarmupTime = 0`. |
| `JitQuietPeriod` | `50 ms` | The duration the JIT compiled-method count must remain unchanged before the quiescence gate opens. A sustained interval is required because a per-batch check cannot work for fast bodies. Clamped down to `MinWarmupTime`. `0` disables the gate. `Thorough` uses 100 ms. |
| `MinSamples` / `MaxSamples` | `30` / `5,000` | Floor and ceiling for the auto-resolved measured-sample count. `MinSamples` is the validity floor; below it, the interval is untrustworthy. `MaxSamples` is 5,000 because the required count grows quadratically as the target narrows. |
| `CiTarget` | `0.025` | The target relative half-width of the confidence interval. Sampling stops once this is met. |
| `MinMeasurementTime` | `100 ms` | The minimum in-body time the measurement phase must span before it can stop on the CI target. This ensures that cheap bodies collect enough samples to make percentiles and significance tests meaningful. The loop stops at either this time or `MaxSamples`, whichever comes first. `0` disables the floor. `Quick` uses 50 ms; `Thorough` uses 500 ms. |
| `MeasurementDriftTolerance` | `0.10` | The maximum allowed difference (as a fraction of the smaller half-mean) between the first-half and second-half means of measured samples before the CI stop is refused. This guards against JIT tier-ups landing inside the measurement window. The gap must also exceed 4 standard errors to avoid flagging noise in heavy-tailed bodies. `0` disables the gate. |
| `MeasurementRestartLimit` | `2` | The number of times the drift gate may discard samples and restart measurement. Restarts use the same `MaxTuningTime` budget as ordinary sampling. A body still drifting after the limit reports `SampleStopReason.DriftUnresolved`. `Thorough` uses 3. |
| `TargetSampleDurationNs` | `10,000` | The per-sample duration target for ops-per-sample calibration. 10 µs keeps timestamp-read overhead negligible. Bodies $\ge$ the resolved target keep K = 1; faster bodies are batched. `Thorough` uses 50 µs. |
| `MinQuantaPerSample` | `512` | The number of clock-resolution steps one timed sample must span. NBenchmark measures the clock's effective resolution once per process and raises `TargetSampleDurationNs` to `resolution × MinQuantaPerSample` if needed. 512 steps keep quantization under 0.2% of a sample. `0` disables the floor. See [Timer resolution](../statistics/measurement.md#timer-resolution). |
| `MaxOpsPerSample` | `1,048,576` | The ceiling on auto-calibrated K. |
| `BatchSize` | `8` | The warmup batch size and the cadence for evaluating the CI-width rule. |
| `MaxTuningTime` | `20 s` | The per-benchmark safety cap on cumulative in-body sample time (calibration + warmup + measurement). Setup, teardown, and GC are excluded. |
| `WarmupBudgetFraction` | `0.4` | The maximum share of `MaxTuningTime` that calibration and warmup may consume together. Once exhausted, the loop moves to measurement. Must be in `(0, 1]`. |
| `CapGraceFactor` | `1.5` | A multiplier for the measurement phase when chasing `MinSamples` after the `MaxTuningTime` cap fires. This ensures that reported statistics have enough samples to be meaningful. Must be at least 1; set to 1 to disable. `CapBehavior = Error` users are unaffected. |
| `CapBehavior` | `Warn` | The action taken when `MaxTuningTime` is reached before the CI target or warmup plateau. `Warn` emits a warning; `Error` marks the benchmark as errored. |
| `EnableJitterCalibration` | `true` | Whether the pre-flight jitter probe runs. When `false`, the jitter metric is `null` and the outlier detector is never auto-switched. |
| `JitterCalibrationSamples` | `32` | The number of timed samples the jitter probe collects. |
| `JitterCalibrationWorkPerSample` | `4,096` | The number of deterministic arithmetic iterations each jitter sample performs. |
| `JitterAutoSwitchThreshold` | `0.10` | The jitter metric value above which the outlier detector auto-switches from IQR fence to MAD. Set to `0` to disable the auto-switch. |

The interval's confidence level is set by `ConfidenceLevel` (see below).

BenchmarkSuite/BenchmarkHarness fluent method: `.WithAutoTune(AutoTunePreset.Quick)` or `.WithAutoTune(customOptions)`
CLI flags: `--auto-tune <default|quick|thorough>`, plus `--ci-target`, `--min-samples`, `--max-samples`, `--min-warmup`, `--max-warmup`, `--max-tuning-time`, `--autotune-cap-behavior`, `--warmup-budget-fraction`, `--cap-grace-factor`, `--min-warmup-time`, `--no-jit-quiescence`, `--jit-quiet-period`, `--min-measurement-time`, `--drift-tolerance`, `--max-drift-restarts`.

### Profile

```csharp
Profile = MeasurementProfile.Realistic   // default
```

The measurement profile controls two GC behaviors: the per-iteration Gen0 GC and the pre-measurement full GC. Resolved booleans (`ForceGcBeforeEachIteration`, `ForceGcBeforeMeasurement`) are computed from `Profile` unless overridden. Two behaviors are enabled for **both** profiles: `ForceGcBetweenBenchmarks` (to prevent bias between benchmarks) and `MeasureAllocations` (measured outside the timed window).

| Profile | ForceGcBeforeEachIteration | ForceGcBeforeMeasurement | ForceGcBetweenBenchmarks | MeasureAllocations |
| --- | --- | --- | --- | --- |
| `Realistic` (default) | `false` | `false` | `true` | `true` |
| `Independent` | `true` | `true` | `true` | `true` |

You can override each resolved boolean individually:

```csharp
// Enable per-iteration GC under Realistic
options with { ForceGcBeforeEachIterationOverride = true }

// Inherit the warmup heap under Independent (skip the pre-measurement GC)
options with { ForceGcBeforeMeasurementOverride = false }

// Disable the between-benchmark GC (both profiles)
options with { ForceGcBetweenBenchmarksOverride = false }
```

BenchmarkHarness fluent method: `.WithMeasurementProfile(MeasurementProfile.Independent)`
BenchmarkSuite fluent method: `.WithMeasurementProfile(MeasurementProfile.Independent)`
CLI flag: `--profile independent`

### RuntimeProfile

```csharp
RuntimeProfile = RuntimeProfile.SteadyState   // default
```

The runtime-startup configuration used for measurement (JIT tiering, dynamic PGO, ReadyToRun, and GC flavor). This is distinct from `Profile`, which controls GC behavior *during* a run.

| Profile | Configuration | Use case |
| --- | --- | --- |
| `RuntimeProfile.SteadyState` | tiering off, PGO off, R2R off, background GC off | **(default)** Fully-optimized steady-state throughput. |
| `RuntimeProfile.Production` | tiering on, PGO on, R2R on | Reproducing shipping configurations; imprecise. |
| `RuntimeProfile.ServerGc` | `SteadyState` + server GC | Code destined for a server-GC host. |
| `RuntimeProfile.Host` | nothing set | Inherits the host's configuration. |

**Background GC**: `SteadyState` turns background GC off. Leaving it on introduces a second thread competing for the same core, which the sample stream cannot predict. Turning it off makes a Gen2 collection blocking, which the [outlier machinery](../statistics/outliers.md) handles well. If your benchmark measures collector behavior, use `Production` or a custom profile.

These settings can only be applied as a process starts. Therefore, they can only be honored for benchmarks that run in a worker process and not for those measured in the host process.

NBenchmark reports what was actually applied. Every result carries:
- `RuntimeProfileName`: The profile in effect, or `"host"` when inherited.
- `RuntimeKnobs`: The active knobs (e.g., `"tiered=off pgo=off r2r=off concurrentGc=off"`).

Results measured under different runtime profiles are **never placed in the same comparison group**. No significance test, effect size, ratio, or threshold gate spans different profiles.

Custom profiles are supported via `ExtraEnvironment`:

```csharp
var profile = RuntimeProfile.SteadyState with
{
    Name = "steady-state-big-gen0",
    ExtraEnvironment = new Dictionary<string, string> { ["DOTNET_GCgen0size"] = "1E00000" },
};
```

BenchmarkHarness fluent method: `.WithRuntimeProfile(RuntimeProfile.Production)`
BenchmarkSuite fluent method: `.WithRuntimeProfile(RuntimeProfile.Production)`
CLI flag: `--runtime-profile production`

For the measured impact and a full list of limitations, see [`--runtime-profile`](cli.md#measurement).

### ForceGcBeforeEachIteration

```csharp
ForceGcBeforeEachIteration => ForceGcBeforeEachIterationOverride ?? (Profile == MeasurementProfile.Independent)
```

This is a **computed property** derived from `Profile` (or `ForceGcBeforeEachIterationOverride`). When `true`, NBenchmark triggers a Gen0 GC collection before each measured sample (the K-batch), before the timestamp and outside the timed window. This prevents allocation side-effects from previous samples from affecting the measurement.

Under the `Realistic` profile (default), this resolves to `false`. To enable per-iteration GC under `Realistic`, set `ForceGcBeforeEachIterationOverride = true` or use `--force-gc` on the CLI.

### ForceGcBeforeMeasurement

```csharp
ForceGcBeforeMeasurement => ForceGcBeforeMeasurementOverride ?? (Profile == MeasurementProfile.Independent)
```

A **computed property** derived from `Profile` (or `ForceGcBeforeMeasurementOverride`). When `true`, a full Gen2 GC (with finalizer wait) runs once after warmup and before the measurement loop begins. This clears the warmup heap to prevent a collection from triggering mid-measurement.

Under the `Realistic` profile (default), this resolves to `false`: the benchmark body inherits the heap state left by warmup, matching production behavior. This is distinct from `ForceGcBetweenBenchmarks`, which runs *between* benchmarks.

### ForceGcBetweenBenchmarks

```csharp
ForceGcBetweenBenchmarks => ForceGcBetweenBenchmarksOverride ?? true
```

A **computed property** (or `ForceGcBetweenBenchmarksOverride`). When `true`, a full Gen2 GC (with finalizer wait) runs between benchmarks. This prevents one benchmark's leftover heap from biasing the next, which would make results order-dependent and undermine the significance test's independence assumption.

This is on by default for **both** profiles. Set `ForceGcBetweenBenchmarksOverride = false` or use `--no-gc-between-benchmarks` on the CLI if inter-benchmark heap carry-over is intended.

### MeasureAllocations

```csharp
MeasureAllocations => MeasureAllocationsOverride ?? true
```

A **computed property** (or `MeasureAllocationsOverride`). When `true`, NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` around each iteration and reports the mean bytes allocated per operation in the **Alloc/op** column.

This is on by default for **both** profiles. The snapshot is taken outside the timed window, so it does not affect timing purity. To disable allocation tracking, set `MeasureAllocationsOverride = false` or use `--no-allocations` on the CLI.

BenchmarkSuite fluent method: `.WithAllocations()`

> [!NOTE]
> The snapshot is taken outside the timed window, so allocation tracking does not affect timing purity.

### Diagnostics

```csharp
Diagnostics = DiagnosticsOptions.Default   // default - GC collection counts on
```

Runtime diagnostics are collected alongside timing and allocations. `DiagnosticsOptions` contains four boolean toggles:

| Toggle | Default | What it collects |
| --- | --- | --- |
| `GcCollectionCounts` | `true` | Gen0, Gen1, and Gen2 collection counts during the measurement phase. |
| `GcHeapInfo` | `false` | Heap committed and fragmented bytes delta across the measurement phase via `GC.GetGCMemoryInfo`. |
| `Exceptions` | `false` | Total first-chance exceptions during the measurement phase via an `AppDomain.FirstChanceException` subscription. |
| `CpuTime` | `false` | Process CPU time (`TotalProcessorTime`) delta per sample. Also reports the CPU/wall-clock ratio. |

Three named presets are available via `DiagnosticsOptions.FromMode(DiagnosticsMode)`:

| Mode | Toggles enabled |
| --- | --- |
| `DiagnosticsMode.None` | None |
| `DiagnosticsMode.Gc` | `GcCollectionCounts` |
| `DiagnosticsMode.GcAndCpu` | `GcCollectionCounts`, `CpuTime` |
| `DiagnosticsMode.All` | All four toggles |

```csharp
// Programmatic - enable everything
var options = new MeasurementOptions
{
    Diagnostics = DiagnosticsOptions.All,
};

// Or build a custom combination
var options = new MeasurementOptions
{
    Diagnostics = new DiagnosticsOptions { GcCollectionCounts = true, Exceptions = true },
};
```

BenchmarkSuite/BenchmarkHarness fluent methods: `.WithDiagnostics(DiagnosticsMode.All)` or `.WithDiagnostics(DiagnosticsOptions.All)`
CLI flag: `--diagnostics <none|gc|gcandcpu|all>`

> [!NOTE]
> GC collection counts are enabled by default because they are cheap. Other toggles add varying overhead: exception counting subscribes to `FirstChanceException`, and CPU time reads `Process.TotalProcessorTime` per sample. Enable them only when you need the data.

For more information on what each counter measures, see [Diagnostics](../statistics/diagnostics.md).

### DriftCanary

```csharp
DriftCanary = DriftCanaryOptions.Default   // default - on, 32 samples of 4 096 iterations
```

The host drift canary runs a fixed, deterministic control workload at every benchmark boundary. A change in how long that work takes indicates a change in the machine rather than your code. Other drift checks look inside one benchmark's own samples, which cannot detect a thermal ramp or a background process that started halfway through the run. Such events move every benchmark measured afterward, confounding comparisons between them.

`DriftCanaryOptions` properties:

| Property | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Whether readings are taken. |
| `Samples` | `32` | Timed samples per reading. Valid range: 4 to 1,024. |
| `WorkPerSample` | `4,096` | Busy-weight iterations per sample. Valid range: 64 to 1,048,576. |
| `MinimumReportableDrift` | `0.01` | The minimum host movement to report as a fraction. Valid range: 0 to 1. |

Each result carries readings taken on either side of it in `BenchmarkResult.HostTimeline`. The `RelativeToRunStart` value is the number to compare between rows: `1.07` means the fixed work took 7% longer at that point in the run than it did at the start.

A warning is issued when the difference between a benchmark and the baseline is smaller than the distance the host moved between the two measurement points:

```
host drift exceeds the difference being reported: the machine was 8% slower when 'Candidate' was
measured than when 'Baseline' was, and the 3% difference between them is smaller than that.
```

The canary warns and never changes the verdict. `MinimumPracticalEffect` and `MinimumRelativeShift` downgrade a result because they are statements about the comparison; the canary is a statement about the machine.

A reading at the defaults costs a fraction of a millisecond and is taken between benchmarks, never inside a timed window. It is skipped on dry-runs.

```csharp
// Leave the canary on but only mention drift past 5%
var options = new MeasurementOptions
{
    DriftCanary = DriftCanaryOptions.Default with { MinimumReportableDrift = 0.05 },
};

// Disable the canary
var options = new MeasurementOptions { DriftCanary = DriftCanaryOptions.Disabled };
```

BenchmarkSuite/BenchmarkHarness fluent methods: `.WithDriftCanary(false)` or `.WithDriftCanary(DriftCanaryOptions)`
CLI flag: `--no-drift-canary`

### Interference

```csharp
Interference = InterferenceOptions.Default   // default - on, reject below 50% of the median occupancy
```

Evidence-based interference rejection brackets every timed sample with a read of the measuring thread's own CPU time to determine a per-sample occupancy ratio (CPU time consumed / wall time elapsed). If a sample's ratio falls materially below the benchmark's median occupancy, NBenchmark discards it as having been preempted by the OS. This happens **before** `OutlierMode` or any custom `IOutlierDetector` sees the sample stream. For more information, see [Evidence-based interference rejection](../statistics/outliers.md#evidence-based-interference-rejection).

`InterferenceOptions` properties:

| Property | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Whether the filter runs. |
| `RejectionThreshold` | `0.5` | A sample below this fraction of the median occupancy ratio is rejected. Valid range: 0.01 to 1. |
| `ProbeCostBudgetFraction` | `0.05` | The probe disables itself if two clock reads cost more than this fraction of the sample-duration target. Valid range: 0.0001 to 1. |
| `KnownSampleFraction` | `0.5` | The minimum fraction of samples that must have a known occupancy reading before a median is trusted. Valid range: 0 to 1. |
| `HighRejectionWarningFraction` | `0.2` | Warns when the rejected fraction reaches this value ("this host is too noisy to trust"). Valid range: 0 to 1. |

```csharp
// Reject more aggressively (below 70% of the median occupancy)
var options = new MeasurementOptions
{
    Interference = InterferenceOptions.Default with { RejectionThreshold = 0.7 },
};

// Disable the filter
var options = new MeasurementOptions { Interference = InterferenceOptions.Disabled };
```

BenchmarkSuite/BenchmarkHarness fluent method: `.WithInterferenceFilter(false)`
CLI flag: `--no-interference-filter`

### OutlierMode

```csharp
OutlierMode = OutlierMode.IqrFence   // default
```

Controls which samples are discarded before statistics are computed:

| Value | Behavior |
| --- | --- |
| `OutlierMode.None` | No samples are removed. |
| `OutlierMode.RemoveTop5Percent` | Removes the slowest 5% of samples. |
| `OutlierMode.RemoveTopAndBottom5Percent` | Removes both the slowest and fastest 5%. |
| `OutlierMode.IqrFence` | Removes samples beyond 1.5× the [IQR (inter-quartile range)](https://en.wikipedia.org/wiki/Interquartile_range). **(default)** |
| `OutlierMode.MedianAbsoluteDeviation` | Removes samples more than 3× the scaled [MAD](https://en.wikipedia.org/wiki/Median_absolute_deviation) from the median. This is a robust alternative to `IqrFence` for heavily skewed data. |

`IqrFence` adapts to the actual spread of each benchmark. When discarded slow samples form a tight secondary cluster, NBenchmark adds a bimodal-distribution warning to the result.

BenchmarkSuite fluent method: `.WithOutlierMode(mode)`
CLI flag: `--outlier <none|top5|both5|iqr|mad>`

For more information, see [Outlier Trimming](../statistics/outliers.md).

### TailMetricsBasis

```csharp
TailMetricsBasis = TailMetricsBasis.Raw   // default
```

Determines which sample set is used for order statistics (percentiles, `Min`, `Max`, and the histogram).

| Value | Behavior |
| --- | --- |
| `TailMetricsBasis.Raw` | Uses the full pre-trim distribution. Tail metrics describe the tail the outlier fence removed. For example, a GC pause timed under the `Realistic` profile appears in `Max`. **(default)** |
| `TailMetricsBasis.Trimmed` | Uses the inlier (post-trim) set. Tail metrics describe only the central process. |

The resolved basis is recorded on the result (`BenchmarkResult.TailMetricsBasis`).

Central-tendency and dispersion statistics (mean, standard deviation, CV, skewness, kurtosis, MAD, median, and median CI) always use the trimmed set. The confidence interval on the mean is the only exception: it is the [Winsorized interval](../statistics/descriptive.md#winsorized-standard-error-for-trimmed-data) over the pre-trim set, so trimming never narrows the error bar.

CLI flag: `--tail-basis <raw|trimmed>`

For more details, see [Descriptive Statistics](../statistics/descriptive.md).

### OutlierDetector

```csharp
OutlierDetector = null   // default - falls back to OutlierMode
```

A custom `IOutlierDetector` (from `NBenchmark.Stats`) that **takes priority over `OutlierMode`** when set. Use it to implement trimming rules not covered by built-in modes, such as a tail-preserving filter or a fixed physical threshold.

```csharp
using NBenchmark.Stats;

new MeasurementOptions { OutlierDetector = new KeepFastestDetector(0.90) };
```

BenchmarkSuite fluent method: `.WithOutlierDetector(detector)`

> [!NOTE]
> The `--outlier` CLI flag always wins. Passing it clears any programmatic `OutlierDetector` so the command line remains authoritative. See [Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors).

### MaxTransferredStateBytes

```csharp
MaxTransferredStateBytes = 8 * 1024 * 1024   // 8 MiB default
```

The ceiling on the encoded size of the values a benchmark's closure sends to a measurement worker. A lambda that closes over data sends that data to the worker process, ensuring the benchmark is isolated without needing to rebuild the value.

Exceeding this limit results in a refusal that names the prepare delegate, rather than truncation. A truncated capture would measure a smaller input, which is incorrect.

The value must be between 1 and `MeasurementOptions.MaxTransferredStateCeiling` (32 MiB). Raising it beyond this limit is not recommended, as it may lead to transport failures that crash the entire group.

```csharp
// A benchmark over a genuinely large prepared input, kept isolated:
new MeasurementOptions { MaxTransferredStateBytes = 24 * 1024 * 1024 };

// Better: use a prepare delegate to build the value in the worker:
Benchmark.Run(prepare: () => BuildIndex(), body: index => index.Lookup("key"));
```

For more information, see [Isolated runs](../features/isolated-runs.md).

### ConfidenceLevel

```csharp
ConfidenceLevel = 0.95   // default
```

The confidence level for the margin of error reported in the Error column. This must be a value strictly between 0 and 1.

| Value | Meaning |
| --- | --- |
| `0.90` | 90% confidence - narrower interval, less conservative |
| `0.95` | 95% confidence - the standard choice **(default)** |
| `0.99` | 99% confidence - wider interval, more conservative |

A higher confidence level produces a larger Error value. Use `0.99` when you need to be very conservative before making a decision.

BenchmarkSuite fluent method: `.WithConfidenceLevel(0.99)`
CLI flag: `--confidence 0.99`

### ReportedPercentiles

```csharp
ReportedPercentiles = [0.50, 0.95, 0.99, 0.999, 1.0]   // default
```

The set of percentile values computed from the trimmed samples (`IReadOnlyList<double>`). Each value must be between 0 and 1 inclusive. Values between 0.50 and 1.0 appear as columns in reporter tail-latency tables.

| Value | Behavior |
| --- | --- |
| `[0.50, 0.95, 0.99, 0.999, 1.0]` **(default)** | Reports P50 (Median), P95, P99, P99.9, and Max. |
| Custom list | Only specified percentile values are computed. P50 (0.50) does not produce a separate column because it is already shown as Median. Max (1.0) is reported via the existing Max stat field. |
| `[0.90]` | Reports a single custom percentile (P90). |

Computed values are stored in `BenchmarkResult.Percentiles` as `IReadOnlyList<PercentileEntry>`. Use `result.GetPercentile(0.95)` to retrieve a specific value.

CLI flag: `--percentiles <list>` (comma-separated, e.g., `--percentiles 0.90,0.99,0.999`).

### EnableHistogram

```csharp
EnableHistogram = true   // default
```

When `true`, NBenchmark computes a latency histogram from the trimmed samples. The histogram is available on `BenchmarkResult.Histogram` as a `LatencyHistogram` record containing `Min`, `Max`, `SampleCount`, and an ordered list of `HistogramBucket` values.

Set to `false` to skip histogram computation and save a small amount of processing time.

CLI flag: `--no-histogram` (disables histogram).

### HistogramBucketCount

```csharp
HistogramBucketCount = 20   // default
```

The number of equal-width buckets in the latency histogram. This is only used when `EnableHistogram` is `true`. The value must be between 5 and 100. More buckets provide finer granularity but fewer samples per bucket.

### EnableSignificance

```csharp
EnableSignificance = true   // default
```

When `true` and two or more benchmarks are present, NBenchmark tests whether the differences are statistically significant. 

- With exactly two benchmarks: NBenchmark runs a [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test).
- With three or more benchmarks: NBenchmark runs the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) omnibus test.

Disable this to reduce overhead:

```csharp
.WithSignificance(false)
```

### SignificanceLevel

```csharp
SignificanceLevel = 0.05   // default
```

The significance threshold (alpha) used to compare a result's p-value. A result is flagged as significant when `p < SignificanceLevel`. This must be a value strictly between 0 and 1. Lower this (e.g., `0.01`) to demand stronger evidence before marking a difference as real.

CLI flag: `--alpha 0.01`

### SignificanceTest

```csharp
SignificanceTest = null   // default - DefaultSignificanceTest (group-count aware)
```

A custom `ISignificanceTest` (from `NBenchmark.Stats`) that replaces the built-in strategy. When `null`, `ResolveSignificanceTest()` returns `DefaultSignificanceTest`, which uses Mann-Whitney U for two groups and Kruskal-Wallis + post-hoc Mann-Whitney U (Holm-Bonferroni corrected) for three or more. Implement the interface to provide a bootstrap, Bayesian, post-hoc, or domain-specific rule:

```csharp
using NBenchmark.Stats;

new MeasurementOptions { SignificanceTest = new MedianRatioSignificanceTest(25) };
```

BenchmarkSuite fluent method: `.WithSignificanceTest(test)`

For more information, see [Custom significance tests](../statistics/significance.md#custom-significance-tests).

### Environment

```csharp
Environment = null   // default - no hardware/OS controls applied
```

Hardware and OS controls applied for the duration of a run (`EnvironmentOptions?`). When `null`, the benchmark runs with the CPU affinity and process priority of the host. Set this to reduce measurement noise (CPU migration, preemption, shared-host jitter) before the timer starts.

A `null` value is not inert. `ThreadControl` defaults to enabled and applies thread-level controls regardless of whether this record exists. The other three fields do nothing until set.

| Field | Type | Default | Effect |
| --- | --- | --- | --- |
| `CpuAffinity` | `IReadOnlyList<int>?` | `null` | Logical CPU core indices to pin the process **and the measuring thread** to (e.g., `[2, 3]`). Restored on run exit. Linux/Windows only; ignored on macOS. |
| `ProcessPriority` | `ProcessPriorityClass?` | `null` | Process priority to request, and (on Windows) the measuring thread's priority to match. `High` is recommended for dedicated hosts. Restored on run exit. |
| `ThreadControl` | `bool` | **`true`** | Applies thread-scoped controls: thread affinity, thread priority (Windows), and on macOS the `QOS_CLASS_USER_INTERACTIVE` class for Apple Silicon performance cores. Set `false` to measure under the host's default scheduling. |
| `DedicatedHostGuidance` | `bool` | `false` | Emits a non-fatal pre-run warning when the host looks noisy (low core count, unraisable priority, or macOS core split). On a suitable host, it suggests using `--priority high`. |
| `SuppressBuildConfigurationWarning` | `bool` | `false` | Suppresses the warning that appears when the entry assembly is Debug-built or a debugger is attached. Use this only when intentionally measuring Debug behavior. |

```csharp
var options = new MeasurementOptions
{
    Environment = new EnvironmentOptions
    {
        CpuAffinity = [2, 3],
        ProcessPriority = ProcessPriorityClass.High,
        DedicatedHostGuidance = true,
    },
};
```

BenchmarkSuite/BenchmarkHarness fluent methods: `.WithHardwareAffinity(2, 3)`, `.WithProcessPriority(ProcessPriorityClass.High)`, `.WithThreadControl(false)`, `.WithDedicatedHostGuidance()`
CLI flags: `--cpu-affinity <list>`, `--priority <level>`, `--no-thread-control`, `--dedicated-host-guidance`
Suppression: `.WithSuppressBuildConfigurationWarning()` (suite/harness) or `NBENCHMARK_SUPPRESS_DEBUG_WARNING=1` (environment variable).

This is the proactive counterpart to the statistical noise handling in [Outlier Trimming](../statistics/outliers.md): trimming reacts to noise after the fact; environment control reduces it at the source. See [Environment control](../features/environment-control.md) for the full model and platform notes.

### RequireIsolation

```csharp
RequireIsolation = true   // default
```

Whether an isolation **refusal** fails the run instead of falling back to a labeled host-process measurement.

A refusal occurs when NBenchmark cannot spawn a worker process. This happens if a capture's behavior is not determined by its contents, instances come from live code in the host process, the suite has no addressable entry point, or `nbworker` is not deployed. The exception names the benchmark, the reason, the remedy, and how to request the host process deliberately. In Harness mode, this is raised at discovery time.

This never gates a deliberate in-process run. `--dry-run`, `--in-process`, `[InProcess]`, `Benchmark.RunInProcess`, `WithIsolation(false)`, and `BenchmarkSuite.AddInProcess` all stamp `InProcessRequested`, which is not a refusal.

Set this to `false` to accept labeled fallbacks everywhere. This is the right setting for scratchpad use, where a labeled measurement is better than no measurement.

Fluent methods: `.WithRequireIsolation(bool)` on `BenchmarkSuite` and `BenchmarkHarness`.
CLI flag: `--strict-isolation` turns this on regardless of programmatic configuration and audits the results.

For more information, see [When isolation is refused](../features/isolated-runs.md#when-isolation-is-refused).

## Applying options per-method (Harness mode)

In Harness mode, the `[Benchmark]` attribute accepts per-method overrides that take priority over host-level options:

```csharp
// This method uses 1000 iterations regardless of the host setting.
[Benchmark(Iterations = 1000, WarmupIterations = 100)]
public void MyExpensiveBenchmark() => SlowOperation();
```

> [!NOTE]
> Only `Iterations`, `WarmupIterations`, and `LaunchCount` can be pinned per method. `OpsPerSample` must be pinned host-wide using `.WithOpsPerSample(n)` or `--ops-per-sample n`.

## Categories

Categories are metadata declared with `[BenchmarkCategory]` and used for filtering. They are not part of `MeasurementOptions`. See the [Categories guide](../features/categories.md) for more information.

## Valid ranges summary

| Option | Type | Default | Valid range |
| --- | --- | --- | --- |
| `Iterations` | `int?` | `null` (auto) | `0` - `100,000` when set (`0` = dry-run) |
| `WarmupIterations` | `int?` | `null` (auto) | `0` - `10,000` when set |
| `OpsPerSample` | `int?` | `null` (auto) | `1` - `16,777,216` when set |
| `LaunchCount` | `int` | `1` | `1` - `100` |
| `AutoTune` | `AutoTuneOptions` | `AutoTuneOptions.Default` | See [AutoTune](#autotune) |
| `ConfidenceLevel` | `double` | `0.95` | `>0` and `<1` |
| `SignificanceLevel` | `double` | `0.05` | `>0` and `<1` |
| `Profile` | `enum` | `Realistic` | `Realistic` or `Independent` |
| `ForceGcBeforeEachIteration` | `bool` (computed) | `false` | Derives from `Profile`; override via `ForceGcBeforeEachIterationOverride` |
| `ForceGcBeforeMeasurement` | `bool` (computed) | `false` | Derives from `Profile` (`true` under `Independent`); override via `ForceGcBeforeMeasurementOverride` |
| `MeasureAllocations` | `bool` (computed) | `true` | On for both profiles; override via `MeasureAllocationsOverride` |
| `ForceGcBetweenBenchmarks` | `bool` (computed) | `true` | On for both profiles; override via `ForceGcBetweenBenchmarksOverride` |
| `MinimumPracticalEffect` | `double?` | `0.147` | `0` - `1` when set (`0` = p-value-only verdicts; `null` disables the gate) |
| `OutlierMode` | `enum` | `IqrFence` | See above |
| `OutlierDetector` | `IOutlierDetector?` | `null` | Overrides `OutlierMode` when set |
| `ReportedPercentiles` | `IReadOnlyList<double>` | `[0.50, 0.95, 0.99, 0.999, 1.0]` | Each value 0-1 |
| `EnableHistogram` | `bool` | `true` | - |
| `HistogramBucketCount` | `int` | `20` | `5` - `100` |
| `EnableSignificance` | `bool` | `true` | - |
| `SignificanceTest` | `ISignificanceTest?` | `null` | Defaults to `DefaultSignificanceTest` |
| `Environment` | `EnvironmentOptions?` | `null` | See [Environment](#environment) |
| `Diagnostics` | `DiagnosticsOptions` | `DiagnosticsOptions.Default` (GC counts on) | See [Diagnostics](#diagnostics) |
