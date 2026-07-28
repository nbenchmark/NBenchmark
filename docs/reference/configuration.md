---
title: Configuration
description: Task-based configuration guides and the full MeasurementOptions reference.
order: 0
---

# Configuration

## Guides

Task-based configuration guides for common benchmarking situations. Each guide shows the code and CLI equivalent, explains why the settings work together, and links to the property definitions below.

---

### Tuning for noisy CI environments

**When to use:** Your benchmark runs on a shared CI runner (GitHub Actions, Azure Pipelines, etc.) where CPU cycles, memory bandwidth, and scheduler time are shared with other containers. Results are noisy and comparisons are unreliable.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Environment.ProcessPriority = High` | Reduces preemption by unrelated OS work. The benchmark thread is less likely to be paused mid-sample. |
| `OutlierMode.MedianAbsoluteDeviation` | More robust than the default IQR fence when a heavy tail of preempted samples distorts the quartile-based fence. MAD has a 50% breakdown point. |
| `LaunchCount = 3` | Runs the benchmark 3 times as independent launches. The best (lowest-median) launch is reported, giving you a second layer of noise rejection. |
| `AutoTune.CapBehavior = Error` | If the wall-clock cap is hit before the CI target is met, the benchmark errors instead of silently reporting a wide interval. |

**Fluent API:**

```csharp
await new BenchmarkSuite("ci-suite")
    .Add("myBenchmark", () => MyMethod())
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithLaunchCount(3)
    .WithAutoTune(new AutoTuneOptions { CapBehavior = CapBehavior.Error })
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --priority high --outlier mad --launch-count 3 --autotune-cap-behavior error
```

**See also:**

- [Environment](#environment)
- [OutlierMode](#outliermode)
- [LaunchCount](#launchcount)
- [AutoTune](#autotune)
- [Environment Control](../features/environment-control.md)

---

### Fast feedback during development

**When to use:** You are iterating on code and need a quick signal - seconds, not minutes. Precision is less important than turnaround time.

**What to combine:**

| Setting | Why |
| --- | --- |
| `AutoTune = AutoTuneOptions.Quick` | Lowers the CI target to ±5%, reduces minimum samples to 15, and minimum warmup to 4. |
| `WarmupIterations = 4` | Pins a short warmup instead of auto-detecting. |
| `Iterations = 20` | Pins a small measured sample count. |
| `ConfidenceLevel = 0.90` | A 90% CI is narrower and requires fewer samples to satisfy. |

**Fluent API:**

```csharp
await new BenchmarkSuite("fast-feedback")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTunePreset.Quick)
    .WithWarmup(4)
    .WithIterations(20)
    .WithConfidenceLevel(0.90)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --auto-tune quick --warmup 4 --iterations 20 --confidence 0.90
```

**See also:**

- [AutoTune](#autotune)
- [WarmupIterations](#warmupiterations)
- [Iterations](#iterations)
- [ConfidenceLevel](#confidencelevel)

---

### Publication-grade precision

**When to use:** You are publishing benchmark results, comparing across commits in a blog post, or establishing a baseline that others will rely on. Accuracy matters more than run time.

**What to combine:**

| Setting | Why |
| --- | --- |
| `AutoTune = AutoTuneOptions.Thorough` | Raises the CI target to ±1%, minimum samples to 100, and minimum warmup to 16. |
| `ConfidenceLevel = 0.99` | A 99% CI is wider and more conservative. |
| `LaunchCount = 5` | Multiple independent launches let you report cross-launch statistics and the best representative run. |
| `EnableHistogram = true` | The latency histogram gives you the full distribution, not just summary statistics. |

**Fluent API:**

```csharp
await new BenchmarkSuite("publication")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTunePreset.Thorough)
    .WithConfidenceLevel(0.99)
    .WithLaunchCount(5)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --auto-tune thorough --confidence 0.99 --launch-count 5
```

**See also:**

- [AutoTune](#autotune)
- [ConfidenceLevel](#confidencelevel)
- [LaunchCount](#launchcount)
- [EnableHistogram](#enablehistogram)

---

### Pure CPU measurement

**When to use:** You want to measure CPU time only, excluding GC pressure, allocation overhead, and cache effects. Suitable for cryptographic algorithms, numeric kernels, and other CPU-bound work.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Profile = Independent` | Forces Gen0 GC before every iteration, full GC between benchmarks, and disables allocation tracking. |
| `OpsPerSample = 1` | Each sample is a single invocation. Calibration is skipped when per-iteration GC is on, so K stays 1 by default - pin it explicitly if you want a different value. |

**Fluent API:**

```csharp
await new BenchmarkSuite("cpu-only")
    .Add("myBenchmark", () => MyMethod())
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --profile independent
```

**See also:**

- [Profile](#profile)
- [ForceGcBeforeEachIteration](#forcegcbeforeeachiteration)
- [MeasureAllocations](#measureallocations)
- [Measurement Profiles](../statistics/measurement.md#measurement-profiles)

---

### Debugging unstable results

**When to use:** Your benchmark produces wildly different numbers across runs, the Error column is large, or you see a bimodal-distribution warning and want to understand why.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Diagnostics = DiagnosticsOptions.All` | Enables GC collection counts, heap info, exception tracking, and CPU time. Lets you correlate timing spikes with GC pauses or CPU throttling. |
| `OutlierMode = MedianAbsoluteDeviation` | More robust to heavy-tailed distributions. If the default IQR fence is being distorted by a long tail, MAD gives a clearer picture. |
| `Detail = Advanced` | Shows the auto-tune diagnostic line (K, warmup, samples, CI half-width, jitter metric) and the outlier fence values. |

**Fluent API:**

```csharp
await new BenchmarkSuite("debug")
    .Add("myBenchmark", () => MyMethod())
    .WithDiagnostics(DiagnosticsMode.All)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --diagnostics all --outlier mad --detail advanced
```

**What to look for:**

- High jitter metric (> 0.10) in the auto-tune diagnostic: the host is noisy. Consider [environment controls](../features/environment-control.md).
- GC collection counts that correlate with slow samples: GC pressure is affecting your timings. Try `--profile independent`.
- A bimodal-distribution warning: investigate the cause (lock contention, cache misses, GC pauses) rather than silencing it.

**See also:**

- [Diagnostics](#diagnostics)
- [OutlierMode](#outliermode)
- [Reading Your Results](../output/reading-your-results.md)
- [Troubleshooting](../troubleshooting.md)

---

## Reference

All measurement settings are controlled by `MeasurementOptions`. The defaults are sensible for most benchmarks - only change what you have a reason to change.

### Using MeasurementOptions

#### With Benchmark (Single mode)

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
};

var result = Benchmark.Run(() => MyMethod(), options: options);
```

#### With BenchmarkSuite (Suite mode)

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

#### With BenchmarkHarness (Harness mode)

Call `WithOptions` or use CLI flags. CLI flags always take priority over `WithOptions`:

```csharp
BenchmarkHarness.Create(args)
    .WithOptions(new MeasurementOptions { Iterations = 500 })
    ...
```

```bash
dotnet run -- --iterations 500 --warmup 50
```

### Options reference

### Iterations

```csharp
Iterations = null   // default - auto-resolved from a CI-width target
```

The number of measured samples per benchmark, typed as `int?`:

| Value | Behaviour |
| --- | --- |
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
| --- | --- |
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

Auto-calibration is skipped (K stays `1`) when per-iteration `IterationSetup`/`IterationTeardown` is configured, since that makes a K-batch unrepresentative of a single call. It is **not** skipped under the `Independent` profile: the forced Gen0 GC runs once per sample (K-batch), before the timestamp and outside the timed window — the same semantics a pinned `OpsPerSample` already gets — so a nano-scale CPU body still amortises timer overhead. (When `Independent` bodies allocate and `K > 1`, a warning notes a GC may land inside a timed batch; pin `--ops-per-sample 1` to avoid it.) An explicit `OpsPerSample` is always honoured.

BenchmarkSuite/BenchmarkHarness fluent method: `.WithOpsPerSample(64)`  
CLI flag: `--ops-per-sample <n>` (pins K). The calibration target is `AutoTune.TargetSampleDurationNs`.

Unlike `Iterations` and `WarmupIterations`, `OpsPerSample` cannot be pinned per method via `[Benchmark]` - it is set suite- or harness-wide only (`.WithOpsPerSample(n)` or `--ops-per-sample n`).

### LaunchCount

```csharp
LaunchCount = 1   // default
```

The number of times to repeat each benchmark as a separate launch, typed as `int`:

`MeasurementOptions` default is `1`; Harness mode applies `3` by default when launch count is not explicitly pinned via `WithLaunchCount`, `WithOptions`, `--launch-count`, or `[Benchmark(LaunchCount = ...)]`.

| Value | Behaviour |
|---|---|
| `1` **(default)** | Run the benchmark once. No aggregation. |
| `> 1` | Repeat the full benchmark (warmup + measurement) N times. Cross-launch statistics (mean, stddev, median, CI across launch medians) are computed and stored in `BenchmarkResult.LaunchStatistics`. The primary result fields reflect the **best** launch (lowest median). Valid range: `2` to `100`. |

Use multiple launches when single-run noise is a concern and you want to see how much the median itself varies across independent measurements. Each launch includes its own warmup and GC cycle, so consecutive launches are independent measurements of the same body - not correlated samples.

**Dry-run interaction:** When `--dry-run` (Iterations=0, WarmupIterations=0) is combined with `LaunchCount > 1`, exactly one dry launch is performed. The extra launches would not add information since dry runs skip the body.

**Isolation interaction:** When the benchmark runs in a child process (Harness mode default, or `WithIsolation()` in suite mode), the parent spawns N children. The child process is unaware of the launch count.

**Attribute override:** In Harness mode each `[Benchmark]` can override the launch count per-method:

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

| Preset | MinWarmup | MinSamples | MaxSamples | CiTarget | MinWarmupTime | MinMeasurementTime | Use it for |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `AutoTuneOptions.Quick` | 4 | 15 | 2 000 | 0.05 (±5%) | **500 ms** | 50 ms | Fast inner-loop feedback. |
| `AutoTuneOptions.Default` | 8 | 30 | 5 000 | 0.025 (±2.5%) | 500 ms | 100 ms | The balanced default. |
| `AutoTuneOptions.Thorough` | 16 | 100 | 20 000 | 0.01 (±1%) | 1 s | 500 ms | Publication-grade numbers. |

> `Quick` deliberately does **not** shorten `MinWarmupTime`. That floor is a measurement-correctness requirement, not a speed/accuracy trade-off: too short a warmup does not give you a rougher number, it gives you a confidently wrong one — a body measured on tier-0 code can report a median several times off with a ±1% error bar, and it will not reproduce between runs. `Quick` gets its speed from `CiTarget`, `MinSamples`, and `MaxTuningTime`.

Pick a preset with `.WithAutoTune(AutoTunePreset.Thorough)` (suite/harness) or `--auto-tune thorough` on the CLI, or build your own `AutoTuneOptions` record. The individual knobs:

| Knob | Default | Meaning |
| --- | --- | --- |
| `MinWarmup` / `MaxWarmup` | `8` / `100 000` | Floor and ceiling for auto-detected warmup length, as sample counts. `MaxWarmup` is deliberately far above what any body needs so that the *time* bounds bind instead: a fast body needs ~25 000 samples to accumulate `MinWarmupTime` at the 10 µs sample target, and a count ceiling binding first would silently defeat that floor. (The tighter `10 000` limit still applies to a *pinned* `WarmupIterations`.) |
| `WarmupEpsilon` | `0.02` | Minimum relative improvement a warmup batch must show to count as "still warming up". |
| `PlateauPatience` | `3` | Consecutive non-improving batches that end warmup. |
| `MinWarmupTime` | `500 ms` | Minimum in-body time auto-warmup must accumulate before it may settle, so background tiered JIT (tier-0 → tier-1 → dynamic PGO) lands before measurement rather than mid-run. 5× the runtime's `TieredCompilation.CallCountingDelayMs` (100 ms) — that delay restarts while tier-0 methods are still being first-called and tier-1 is only *queued* when it expires, so a floor at or below 100 ms reliably lands the tier-up inside measurement. In practice this is the binding constraint on warmup length for almost every body. Bounded above by the calibration+warmup budget share and `MaxWarmup`. `0` disables the floor (and the JIT-quiescence gate). `Thorough` uses 1 s; `Quick` inherits 500 ms. Chosen empirically: at 250 ms a `StringBuilder`-append loop still landed in either its tier-0 or its ~4.5× faster steady state depending on the run (4.8× run-to-run spread); 500 ms cost 55% more wall-clock and made it consistent, while 1 s cost a further 76% for one more benchmark. |
| `RequireJitQuiescence` | `true` | Whether auto-warmup also refuses to settle until the JIT has been quiet for `JitQuietPeriod`, read from `System.Runtime.JitInfo` at each batch boundary. Deactivates once warmup has run 4 × `MinWarmupTime` so a busy in-process host cannot block warmup forever; inactive when `MinWarmupTime = 0`. |
| `JitQuietPeriod` | `50 ms` | How long the JIT compiled-method count must stay unchanged before the quiescence gate opens. A *sustained* interval is required because a per-batch check cannot work: one batch of a fast body spans tens of microseconds, so a background compilation almost never lands inside it and a per-batch delta reads zero essentially always. Clamped down to `MinWarmupTime` so it never becomes the binding floor. `0` disables the gate. `Thorough` uses 100 ms. |
| `MinSamples` / `MaxSamples` | `30` / `5 000` | Floor and ceiling for the auto-resolved measured-sample count. `MinSamples` is the *validity* floor (below it the interval is untrustworthy however narrow) and is also what the `CapGraceFactor` grace budget chases. `MaxSamples` was formerly 100 000: at 5 000 the CI rule still reaches ±2.5% for a coefficient of variation up to ~90%, and past that the required count grows as `(t × CV / target)²` and runs away — a CV of 580% needs ~50 000 samples for ±5% — where the variance *is* the finding rather than something more samples fix. |
| `CiTarget` | `0.025` | Target relative half-width of the confidence interval; sampling stops once it is met. |
| `MinMeasurementTime` | `100 ms` | Minimum in-body time the measurement phase must span before it may stop on the CI target — the measurement analogue of `MinWarmupTime`, and what makes the sample count scale with body speed instead of being a flat number. A cheap body collects hundreds or thousands of samples for milliseconds of extra work, which is what makes its percentiles and significance test meaningful (at n ≈ 16 the reported P95/P99/P99.9 all collapse onto the maximum). The rule is: measurement spans at least this long, or reaches `MaxSamples` samples, whichever comes first — so worst-case added cost is `MinMeasurementTime` per benchmark and **zero** for any body already slower than `MinMeasurementTime / MinSamples` (≈3.3 ms by default). `0` disables the floor. `Quick` uses 50 ms, `Thorough` 500 ms. |
| `MeasurementDriftTolerance` | `0.10` | How far the first-half and second-half means of the measured samples may disagree (as a fraction of the smaller half-mean) before the CI stop is refused. Guards the failure mode that is hardest to spot: a JIT tier-up landing inside the measurement window is a step change, and the CI-on-the-mean rule will report a tight interval straight across it — a 10× wrong number with a ±0.9% error bar. The gap must also exceed 4 standard errors, so a heavy-tailed body whose half-means differ by pure noise is not flagged. `0` disables the gate; either way `AutoTune.SplitHalfDrift` records the gap. |
| `MeasurementRestartLimit` | `2` | How many times the drift gate may discard the collected samples and restart measurement — one for tier-0 → tier-1, one for instrumented → optimized under dynamic PGO. Restarts draw on the same `MaxTuningTime` budget as ordinary sampling, so they can never make a benchmark run longer. A body still drifting after the limit reports `SampleStopReason.DriftUnresolved` with a warning, which is a finding rather than something more restarts fix. `Thorough` uses 3. |
| `TargetSampleDurationNs` | `10 000` | Per-sample duration that ops-per-sample calibration aims for. 10 µs keeps timer quantization (~0.1% vs ~±10% at 1 µs on a 100 ns timer) and timestamp-read overhead (~0.2% vs ~1-3%) negligible against the CI target. Bodies ≥ 10 µs keep K = 1; sub-10 µs bodies are batched, so their percentiles describe batch means. `Thorough` preset uses 50 µs. |
| `MaxOpsPerSample` | `1 048 576` | Ceiling on auto-calibrated K. |
| `BatchSize` | `8` | Warmup batch size and the cadence on which the CI-width rule is evaluated. |
| `MaxTuningTime` | `20 s` | Per-benchmark safety cap on cumulative in-body sample time (calibration + warmup + measurement). Setup, teardown, and GC are excluded, so real wall-clock can exceed it. |
| `WarmupBudgetFraction` | `0.4` | Max share of `MaxTuningTime` that calibration (Phase A) and warmup (Phase B) may consume together. Once the share is exhausted, each phase stops at the wall-clock cap and the loop moves on, reserving the remainder for measurement. Must be in `(0, 1]`. |
| `CapGraceFactor` | `1.5` | Hard ceiling multiplier the measurement phase may reach while chasing `MinSamples` after the `MaxTuningTime` cap fires. When the cap fires before `MinSamples` is reached, the loop keeps sampling up to `MaxTuningTime × CapGraceFactor` so the reported statistics have enough samples to be meaningful (a one-sample result reports StdDev = 0 and MarginOfError = 0 - dangerously clean-looking). Must be at least 1; set to 1 to disable the grace path. `CapBehavior = Error` users are unaffected - the error fires at the base cap either way. |
| `CapBehavior` | `Warn` | What happens when `MaxTuningTime` is reached before the CI target or warmup plateau is reached. `Warn` emits a warning; `Error` marks the benchmark as errored. |
| `EnableJitterCalibration` | `true` | Whether the pre-flight jitter probe runs. When `false`, the jitter metric is `null` and the outlier detector is never auto-switched. |
| `JitterCalibrationSamples` | `32` | Number of timed samples the jitter probe collects. |
| `JitterCalibrationWorkPerSample` | `4096` | Number of deterministic arithmetic iterations each jitter sample performs. |
| `JitterAutoSwitchThreshold` | `0.10` | Jitter metric value above which the outlier detector auto-switches from IQR fence to MAD. Set to `0` to disable the auto-switch while keeping the probe. |

The interval's confidence level is `ConfidenceLevel` (below) - the CI-width rule targets that same level, so it is not duplicated on `AutoTune`.

BenchmarkSuite/BenchmarkHarness fluent method: `.WithAutoTune(AutoTunePreset.Quick)` or `.WithAutoTune(customOptions)`  
CLI flags: `--auto-tune <default|quick|thorough>`, plus `--ci-target`, `--min-samples`, `--max-samples`, `--min-warmup`, `--max-warmup`, `--max-tuning-time`, `--autotune-cap-behavior`, `--warmup-budget-fraction`, `--cap-grace-factor`, `--min-warmup-time`, `--no-jit-quiescence`, `--jit-quiet-period`, `--min-measurement-time`, `--drift-tolerance`, `--max-drift-restarts`.

### Profile

```csharp
Profile = MeasurementProfile.Realistic   // default
```

The measurement profile is the authoritative setting behind two GC behaviours: the per-iteration Gen0 GC and the pre-measurement full GC. The resolved booleans (`ForceGcBeforeEachIteration`, `ForceGcBeforeMeasurement`) are computed from `Profile` unless explicitly overridden via the corresponding `*Override` field. Two related behaviours are on for **both** profiles: `ForceGcBetweenBenchmarks` (so one benchmark cannot bias the next) and `MeasureAllocations` (measured outside the timed window, so it is free).

| Profile | ForceGcBeforeEachIteration | ForceGcBeforeMeasurement | ForceGcBetweenBenchmarks | MeasureAllocations |
| --- | --- | --- | --- | --- |
| `Realistic` (default) | `false` | `false` | `true` | `true` |
| `Independent` | `true` | `true` | `true` | `true` |

Each resolved boolean can be overridden individually:

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

### ForceGcBeforeEachIteration

```csharp
ForceGcBeforeEachIteration => ForceGcBeforeEachIterationOverride ?? (Profile == MeasurementProfile.Independent)
```

This is a **computed property** derived from `Profile` (or the `ForceGcBeforeEachIterationOverride` field when set). When `true`, a gen-0 GC collection is triggered before each measured sample (the K-batch), before the timestamp and outside the timed window. This keeps allocation side-effects from previous samples out of your measurement.

Under the `Realistic` profile (the default), this resolves to `false`. To enable per-iteration GC under `Realistic`, set `ForceGcBeforeEachIterationOverride = true` or use `--force-gc` on the CLI.

### ForceGcBeforeMeasurement

```csharp
ForceGcBeforeMeasurement => ForceGcBeforeMeasurementOverride ?? (Profile == MeasurementProfile.Independent)
```

A **computed property** derived from `Profile` (or the `ForceGcBeforeMeasurementOverride` field when set). When `true`, a full Gen2 GC (with finalizer wait) runs once after warmup and before the measurement loop begins, clearing the warmup heap so it cannot trigger a collection mid-measurement.

Under the `Realistic` profile (the default), this resolves to `false`: the benchmark body inherits whatever heap state the warmup left behind, matching production behaviour. It is distinct from `ForceGcBetweenBenchmarks` below, which runs *between* benchmarks rather than before measurement.

### ForceGcBetweenBenchmarks

```csharp
ForceGcBetweenBenchmarks => ForceGcBetweenBenchmarksOverride ?? true
```

A **computed property** (or the `ForceGcBetweenBenchmarksOverride` field when set). When `true`, a full Gen2 GC (with finalizer wait) runs between benchmarks so one benchmark's leftover heap cannot bias the next — which would make results order-dependent and undermine the significance test's independence assumption.

On by default for **both** profiles. Set `ForceGcBetweenBenchmarksOverride = false` or use `--no-gc-between-benchmarks` on the CLI when the inter-benchmark heap carry-over is intended.

### MeasureAllocations

```csharp
MeasureAllocations => MeasureAllocationsOverride ?? true
```

A **computed property** (or the `MeasureAllocationsOverride` field when set). When `true`, NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` around each iteration and reports the mean bytes allocated per operation in the **Alloc/op** column (with a process-wide fallback for async thread hops).

On by default for **both** profiles — the snapshot is taken outside the timed window, so it costs no timing purity and surfaces the "this 'pure-CPU' benchmark actually allocates" signal even under `Independent`. To disable allocation tracking, set `MeasureAllocationsOverride = false` or use `--no-allocations` on the CLI.

BenchmarkSuite fluent method: `.WithAllocations()`

> [!NOTE]
> Allocation tracking adds a small overhead to each iteration and may slightly affect timing measurements.

### Diagnostics

```csharp
Diagnostics = DiagnosticsOptions.Default   // default - GC collection counts on
```

Runtime diagnostics collected alongside timing and allocations. Typed as a `DiagnosticsOptions` record with four boolean toggles:

| Toggle | Default | What it collects |
| --- | --- | --- |
| `GcCollectionCounts` | `true` | Gen0, Gen1, Gen2 collection counts during the measurement phase (totals, not per-op). Cheap - two `GC.CollectionCount` reads per sample. |
| `GcHeapInfo` | `false` | Heap committed bytes and fragmented bytes delta across the measurement phase, via `GC.GetGCMemoryInfo`. |
| `Exceptions` | `false` | Total first-chance exceptions during the measurement phase, via an `AppDomain.FirstChanceException` subscription. Divided by total measurement ops to give exceptions per operation. |
| `CpuTime` | `false` | Process CPU time (TotalProcessorTime) delta per sample, divided by total measurement ops. Also reports the CPU/wall-clock ratio. |

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
> GC collection counts are on by default because they are cheap (`GC.CollectionCount` is a counter read, not a measurement). The other toggles add varying overhead: exception counting subscribes to `FirstChanceException` for the measurement phase, and CPU time reads `Process.TotalProcessorTime` per sample. Enable them only when you need the data.

See [Diagnostics](../statistics/diagnostics.md) for what each counter measures and how collection works.

### OutlierMode

```csharp
OutlierMode = OutlierMode.IqrFence   // default
```

Controls which samples are discarded before statistics are computed.

| Value | Behaviour |
| --- | --- |
| `OutlierMode.None` | No samples are removed. |
| `OutlierMode.RemoveTop5Percent` | The slowest 5% of samples are removed. |
| `OutlierMode.RemoveTopAndBottom5Percent` | The slowest and fastest 5% are removed. |
| `OutlierMode.IqrFence` | Samples beyond 1.5× the [IQR (inter-quartile range)](https://en.wikipedia.org/wiki/Interquartile_range) are removed. **(default)** |
| `OutlierMode.MedianAbsoluteDeviation` | Samples more than 3× the scaled [MAD](https://en.wikipedia.org/wiki/Median_absolute_deviation) from the median are removed - a robust alternative to `IqrFence` for heavily skewed data. |

`IqrFence` adapts to the actual spread of each benchmark instead of always discarding a fixed 5%: a clean run keeps nearly every sample, while a noisy run trims more. When the discarded slow samples form a tight secondary cluster (rather than scattered scheduling noise), NBenchmark adds a bimodal-distribution warning to the result so you can investigate the tail latency.

BenchmarkSuite fluent method: `.WithOutlierMode(mode)`  
CLI flag: `--outlier <none|top5|both5|iqr|mad>`

See [Outlier Trimming](../statistics/outliers.md) for the full algorithms.

### TailMetricsBasis

```csharp
TailMetricsBasis = TailMetricsBasis.Raw   // default
```

Which sample set the order statistics - percentiles, `Min`, `Max`, and the histogram - are computed from.

| Value | Behaviour |
| --- | --- |
| `TailMetricsBasis.Raw` | Full pre-trim distribution. Tail metrics describe the tail the outlier fence removed - so a GC pause the `Realistic` profile deliberately timed shows up in `Max`. **(default)** |
| `TailMetricsBasis.Trimmed` | Inlier (post-trim) set. Tail metrics describe only the central process. |

The resolved basis is recorded on the result (`BenchmarkResult.TailMetricsBasis`) so a consumer can label which sample set each statistic describes instead of inferring it. See [Descriptive statistics](../statistics/descriptive.md).

Central-tendency and dispersion statistics (mean, standard deviation, CI, CV, skewness, kurtosis, MAD, median, median CI) always stay on the trimmed set regardless of this setting.

CLI flag: `--tail-basis <raw|trimmed>`

See [Descriptive Statistics](../statistics/descriptive.md) for details.

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
| --- | --- |
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
| --- | --- |
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

### Environment

```csharp
Environment = null   // default - no hardware/OS controls applied
```

Opt-in hardware/OS controls applied for the duration of a run, typed as `EnvironmentOptions?`. When `null` (the default), the benchmark runs with whatever CPU affinity and process priority the host started it with - the zero-ceremony path. Set it to reduce measurement noise at its source (CPU migration, preemption, shared-host jitter) before the timer starts.

| Field | Type | Default | Effect |
| --- | --- | --- | --- |
| `CpuAffinity` | `IReadOnlyList<int>?` | `null` | Logical CPU core indices to pin the process to (e.g. `[2, 3]`). Restored on run exit. Linux/Windows only; ignored with a warning on macOS. |
| `ProcessPriority` | `ProcessPriorityClass?` | `null` | Process priority to request. `High` is recommended for dedicated hosts. Restored on run exit. A refused elevation is a warning, not an error. |
| `DedicatedHostGuidance` | `bool` | `false` | Emit a non-fatal pre-run warning when the host looks noisy (low core count, unraisable priority, or on macOS unobservable frequency scaling/thermal throttling). On a suitable host, actively suggests `--priority high`. |
| `SuppressBuildConfigurationWarning` | `bool` | `false` | Suppress the always-on warning that appears when the entry assembly is Debug-built or a debugger is attached. Use this only when measuring Debug behavior intentionally. |

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

BenchmarkSuite/BenchmarkHarness fluent methods: `.WithHardwareAffinity(2, 3)`, `.WithProcessPriority(ProcessPriorityClass.High)`, `.WithDedicatedHostGuidance()`  
CLI flags: `--cpu-affinity <list>`, `--priority <level>`, `--dedicated-host-guidance`  
Additional suppression knobs: `.WithSuppressBuildConfigurationWarning()` (suite/harness) or `NBENCHMARK_SUPPRESS_DEBUG_WARNING=1` (environment variable)

This is the proactive counterpart to the statistical noise handling in [Outlier Trimming](../statistics/outliers.md): trimming reacts to noise after the fact; environment control reduces it at the source. See [Environment control](../features/environment-control.md) for the full model, platform notes, and isolated-process propagation.

## Applying options per-method (Harness mode)

In Harness mode, the `[Benchmark]` attribute accepts per-method overrides that take priority over the host-level options:

```csharp
// This method uses 1000 iterations regardless of the host setting.
[Benchmark(Iterations = 1000, WarmupIterations = 100)]
public void MyExpensiveBenchmark() => SlowOperation();
```

> [!NOTE]
> Only `Iterations`, `WarmupIterations`, and `LaunchCount` are pinnable per method. `OpsPerSample` is not exposed on `[Benchmark]` - pin it host-wide with `.WithOpsPerSample(n)` or `--ops-per-sample n`.

## Categories

Categories are not part of `MeasurementOptions`; they are metadata declared with `[BenchmarkCategory]` and used for filtering. See the [Categories guide](../features/categories.md) for the full feature.

## Valid ranges summary

| Option | Type | Default | Valid range |
| --- | --- | --- | --- |
| `Iterations` | `int?` | `null` (auto) | `0` – `100 000` when set (`0` = dry-run) |
| `WarmupIterations` | `int?` | `null` (auto) | `0` – `10 000` when set |
| `OpsPerSample` | `int?` | `null` (auto) | `1` – `16 777 216` when set |
| `LaunchCount` | `int` | `1` | `1` – `100` |
| `AutoTune` | `AutoTuneOptions` | `AutoTuneOptions.Default` | See [AutoTune](#autotune) |
| `ConfidenceLevel` | `double` | `0.95` | `>0` and `<1` |
| `SignificanceLevel` | `double` | `0.05` | `>0` and `<1` |
| `Profile` | `enum` | `Realistic` | `Realistic` or `Independent` |
| `ForceGcBeforeEachIteration` | `bool` (computed) | `false` | Derives from `Profile`; override via `ForceGcBeforeEachIterationOverride` |
| `ForceGcBeforeMeasurement` | `bool` (computed) | `false` | Derives from `Profile` (`true` under `Independent`); override via `ForceGcBeforeMeasurementOverride` |
| `MeasureAllocations` | `bool` (computed) | `true` | On for both profiles; override via `MeasureAllocationsOverride` |
| `ForceGcBetweenBenchmarks` | `bool` (computed) | `true` | On for both profiles; override via `ForceGcBetweenBenchmarksOverride` |
| `MinimumPracticalEffect` | `double?` | `0.147` | `0` – `1` when set (`0` = p-value-only verdicts; `null` disables the gate) |
| `OutlierMode` | `enum` | `IqrFence` | See above |
| `OutlierDetector` | `IOutlierDetector?` | `null` | Overrides `OutlierMode` when set |
| `ReportedPercentiles` | `IReadOnlyList<double>` | `[0.50, 0.95, 0.99, 0.999, 1.0]` | Each value 0-1 |
| `EnableHistogram` | `bool` | `true` | - |
| `HistogramBucketCount` | `int` | `20` | `5` – `100` |
| `EnableSignificance` | `bool` | `true` | - |
| `SignificanceTest` | `ISignificanceTest?` | `null` | Defaults to `DefaultSignificanceTest` |
| `Environment` | `EnvironmentOptions?` | `null` | See [Environment](#environment) |
| `Diagnostics` | `DiagnosticsOptions` | `DiagnosticsOptions.Default` (GC counts on) | See [Diagnostics](#diagnostics) |

Values outside the valid range throw `ArgumentOutOfRangeException`.

---

**Still having issues?** See the [Troubleshooting guide](../troubleshooting.md) for symptom-to-configuration mappings for common measurement problems.
