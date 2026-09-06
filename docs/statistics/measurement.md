---
title: Measurement
description: How NBenchmark's measurement loop works, including timer resolution and per-sample overhead.
order: 1
---

# Measurement

## The measurement loop

NBenchmark uses an **adaptive streaming loop**. Instead of running a fixed number of samples, it resolves three dimensions at runtime: how many invocations to time per sample (**K**), how long to warm up, and how many measured samples to collect. The loop stops each phase as soon as it has sufficient data.

You can pin any of these dimensions to an exact value; pinning all three reproduces a classic fixed-count run. For more information, see [Configuration](../reference/configuration.md).

For each benchmark, the loop runs in four phases:

### Phase 0 - Pre-flight jitter calibration

Before any real measurement, NBenchmark times a deterministic, allocation-free busy-weight loop to derive a robust jitter metric (MAD / median of its per-sample timings). This serves as a probe of the *host*, not the code under test.

- A quiet, dedicated host typically reports well below 0.05.
- A shared-tenant CI runner typically reports 0.10-0.30.

When the metric exceeds `AutoTune.JitterAutoSwitchThreshold` (default 0.10) and you have not pinned an outlier detector, the loop auto-switches the effective detector from an IQR fence to MAD for that run. NBenchmark records this switch on the `AutoTune` diagnostic (`OutlierDetectorSwitched`) and emits a warning.

The probe is enabled by default (`AutoTune.EnableJitterCalibration`). For more information about why the auto-switch exists and how to disable it, see [The measurement engine: jitter calibration and detector auto-switch](../deep-dives/measurement-engine.md#jitter-calibration-and-detector-auto-switch).

### Phase A - Ops-per-sample calibration (K)

If `OpsPerSample` is `null` (the default) and the body is eligible, NBenchmark times a single invocation, then doubles K - timing 1, 2, 4, 8, ... invocations as one batch - until a batch spans at least the resolved sample-duration target. NBenchmark reuses the resolved K for warmup and measurement, and divides every reported timing by K to provide a per-operation number.

The target is derived from two factors:
- **Timestamp-read overhead**: `AutoTune.TargetSampleDurationNs` (10 µs by default) covers the fixed overhead (~10-30 ns). This ensures overhead is a small fraction of the sample rather than leaking into the ±2.5% CI target.
- **Timer quantization**: `AutoTune.MinQuantaPerSample` (512 by default) covers timer quantization. Because quantization varies by platform, NBenchmark measures the clock's effective resolution once per process and raises the target to `resolution × MinQuantaPerSample` when the configured value is insufficient.

| Host | Measured resolution | Resolved target | Quantization per sample |
| --- | --- | --- | --- |
| TSC-backed Linux | ~1-5 ns | 10 µs (unchanged) | <0.05% |
| Apple Silicon | ~41.7 ns | ~21 µs | ~0.19% |
| Windows QPC | ~100 ns | ~51 µs | ~0.19% |

NBenchmark only raises the target; if you provide a higher preset (such as the `Thorough` profile, which uses 50 µs), the preset is honored. Bodies that already span the resolved target keep K = 1. For more information, see [Timer resolution](#timer-resolution).

> [!NOTE] K > 1 batches change percentile meaning
> When K > 1, each recorded sample is the mean of K back-to-back operations. Therefore, P95/P99/max and the histogram describe **batch means**, not individual-operation tails. For bodies under 10 µs, this trade-off is deliberate because per-op timing at that scale is dominated by timer noise. If you need per-op tail latency, pin `OpsPerSample = 1` and see [Descriptive Statistics](./descriptive.md) for relevant caveats.

Calibration is skipped (K = 1) when `SampleSetup` or `SampleTeardown` is set, as a batch would no longer represent one isolated call. It is **not** skipped under the `PerSampleCollect` GC behavior; the forced Gen0 GC runs once per sample (the K-batch) before the timestamp and outside the timed window.

Calibration runs against the body's **cold** (pre-warmup) speed. See [Post-warmup recalibration](#post-warmup-recalibration) for how K is re-derived once the body is warm.

### Phase B - Warmup (plateau detection)

If `WarmupSamples` is `null`, NBenchmark collects warmup samples in batches of `AutoTune.BatchSize` and tracks the best (fastest) batch mean. Once `AutoTune.PlateauPatience` consecutive batches fail to improve on the best by at least `AutoTune.WarmupEpsilon`, the code is considered warm and warmup stops. This process occurs no sooner than `AutoTune.MinWarmupSamples` samples and no later than `AutoTune.MaxWarmupSamples`. If you pin `WarmupSamples`, NBenchmark runs exactly that many warmup samples.

The plateau rule alone measures warmup in *samples*, but fast bodies can plateau in microseconds of wall-clock time - often before the background JIT delivers tier-1 (and dynamic-PGO) code. To prevent a tier-1 switch from landing mid-measurement, NBenchmark uses two additional gates:

- **Warmup time floor**: `AutoTune.MinWarmupTime` (default 500 ms; 1 s under `Thorough`) ensures auto-warmup does not settle until it has accumulated this much in-body time. You can disable this by setting it to `0`.
  - **`Quick` does not shorten this floor.** This is a correctness requirement. `Quick` achieves speed through a looser `CiTarget`, a lower `MinSamples`, and a shorter `MaxTuningTime`.
- **JIT-quiescence gate**: If `AutoTune.RequireJitQuiescence` is enabled (default), NBenchmark tracks when the runtime last compiled a method and keeps warmup open until that has been quiet for `AutoTune.JitQuietPeriod` (default 50 ms).

Both gates only delay settling; `MaxWarmupSamples` and the combined calibration and warmup budget still bound warmup from above.

Because fast bodies require tens of thousands of samples to accumulate 500 ms, `AutoTune.MaxWarmupSamples` defaults to **100,000** (compared to the 10,000 that bounds a *pinned* `WarmupSamples`). If the ceiling is hit before the time floor, NBenchmark raises a warning and records it in `BenchmarkResult.AutoTune.WarmupTimeFloorMet`.

For more information, see [The measurement engine: warmup gates](../deep-dives/measurement-engine.md#warmup-gates).

For slow bodies, NBenchmark shrinks the configured `BatchSize` based on the per-sample estimate from calibration. This allows the plateau rule to settle after `PlateauPatience + 1` samples instead of `(PlateauPatience + 1) × BatchSize`.

Calibration and warmup share a budget: together they may consume at most `AutoTune.WarmupBudgetFraction` of `AutoTune.MaxTuningTime` (default 0.4 or 40%). If this share is exhausted, warmup stops at the wall-clock cap and a warning is emitted.

If `ForceGcBeforeMeasurement` is true (as in the `PerSampleCollect` GC behavior), a full gen-2 GC runs after warmup to establish a clean heap baseline. Under the `Natural` GC behavior (the default), the benchmark inherits the warmup's heap state.

### Post-warmup recalibration

Phase A resolves K against the body's **cold** code. Once warmup reaches a steady-state, the same K may span well under the target duration, re-exposing the timer overhead. After auto-warmup settles, the loop re-derives K from the warm per-op estimate and bumps it to the next power of two that reaches the target.

Recalibration only applies if calibration ran and only increases K. The gap between cold and warm K is recorded in `BenchmarkResult.AutoTune.InitialOpsPerSample`. See [The measurement engine: post-warmup recalibration](../deep-dives/measurement-engine.md#post-warmup-recalibration) for the full rule.

### Phase C - Measurement (CI-width target)

If `Samples` is `null`, NBenchmark streams measured samples and recomputes the confidence interval on the mean every `AutoTune.BatchSize` samples. Sampling stops once the interval's relative half-width falls below `AutoTune.CiTarget` (±2.5% by default). This process occurs no sooner than `AutoTune.MinSamples` and no later than `AutoTune.MaxSamples`. If you pin `Samples`, NBenchmark collects exactly that many samples.

A per-benchmark `AutoTune.MaxTuningTime` wall-clock cap bounds the whole loop to prevent pathological bodies from running indefinitely.

Stopping at the first sample count that meets the target is *optional stopping*, and the reported interval is not corrected for it. For more information, see [The measurement engine: optional stopping](../deep-dives/measurement-engine.md#the-optional-stopping-correction).

Two gates ensure the stop rule is honest:

- **Measurement time floor**: `AutoTune.MinMeasurementTime` (default 100 ms; 50 ms under `Quick`, 500 ms under `Thorough`) ensures measurement spans at least this much in-body time before stopping on the CI target. This ensures that very cheap bodies are sampled enough to provide meaningful percentiles and a usable histogram. If you set this to `0`, NBenchmark stops on `MinSamples` alone.
- **Steady-state (drift) gate**: When the CI rule triggers a stop, NBenchmark compares the mean of the first half of the collected samples against the mean of the second half. It refuses the stop if they disagree both *relatively* (by more than `AutoTune.MeasurementDriftTolerance`, default 0.10) and *statistically* (by more than 4 standard errors of the difference).

> [!CAUTION] Handling a `driftUnresolved` stop
> - **Land the transition during warmup:** Increase `--min-warmup-time <ms>` (default 500) so that a JIT tier-up or dynamic-PGO re-optimization occurs during warmup.
> - **Accept non-stationarity:** Use `--launch-count 5` to measure the across-launch spread, which is the honest signal for a body without a steady state.

This gate prevents a benchmark from appearing trustworthy (e.g., a tight error bar) when a step change - such as a JIT tier-up or thermal ramp - occurs mid-measurement. If the gate refuses a stop, the loop discards all samples collected so far and restarts measurement, up to `AutoTune.MeasurementRestartLimit` times (default 2). Restarts use the same `MaxTuningTime` budget. If the limit is exhausted, NBenchmark reports `SampleStopReason.DriftUnresolved` with a warning.

You can disable the gate by setting the tolerance to `0`. `BenchmarkResult.AutoTune.SplitHalfDrift` records the gap for every stop. See [The measurement engine: the drift gate and the cap](../deep-dives/measurement-engine.md#the-drift-gate-and-the-cap) for the implementation.

`AutoTune.MaxSamples` defaults to **5,000** (2,000 under `Quick`, 20,000 under `Thorough`). At 5,000 the CI rule still reaches ±2.5% for any body with a coefficient of variation up to roughly 90%. Past that the required count grows as `(t × CV / target)²` and runs away - a CV of 580% needs about 50,000 samples just to reach ±5% - but a body that noisy has variance that *is* the finding, and more samples only buy a tighter interval around an unstable centre. The ceiling warning therefore names the measured CV and the count convergence would actually take, and points at `--launch-count` as a more honest signal.

When the cap fires before `AutoTune.MinSamples` is reached, the loop keeps sampling up to `AutoTune.MaxTuningTime × AutoTune.CapGraceFactor` (default 1.5×). This trades a longer run for enough samples to be meaningful. If the grace ceiling is reached below `MinSamples`, a warning flags the error margin as unreliable.

Each measured sample performs the following:
1. If `ForceGcBeforeEachSample` is true (`PerSampleCollect` GC behavior), it forces a gen-0 collection before the timestamp.
2. Calls `SampleSetup` if provided.
3. Records `Stopwatch.GetTimestamp()`.
4. Invokes the benchmark action K times.
5. Reads the timestamp again, converts the raw tick delta to nanoseconds at the timer's **native resolution**, and divides by K.
6. Records the allocation delta (divided by K) if `MeasureAllocations` is true.
7. Calls `SampleTeardown` if provided.

**Important:** NBenchmark reads the timer immediately after the K-batch returns, before teardown runs. Teardown time is not included in the measurement.

### Raw vs. trimmed statistics

The CI-width stop rule evaluates the **raw** (untrimmed) sample stream. After the loop ends, the samples pass through [outlier trimming](./outliers.md), and the reported statistics - including the Error column - describe the **trimmed** set.

`AutoTune.AchievedRelativeCiWidth` is the raw stop value, while `MarginOfErrorNs` is the reported interval on the trimmed mean.

| Number | Describes | Use case |
| --- | --- | --- |
| `MarginOfErrorNs` | How precisely the **trimmed mean** is known (the [Winsorized interval](./descriptive.md#winsorized-standard-error-for-trimmed-data)) | When you want an error bar on the reported number |
| `AutoTune.AchievedRelativeCiWidth` | How wide the interval on the **raw mean** was before the loop stopped | When you want to know if the machine was quiet enough for convergence |
| `MaxNs`, `P99`, `P99.9` | The raw distribution's actual tail | When you want to know how slow the slow samples were |

The Winsorized interval accounts for samples the fence removed rather than dropping their variance. It does not widen based on the outliers' *magnitude*.

> A tight Error column is trustworthy evidence that the *measurement converged* only when `SampleStop` is `CiTargetMet`. Read the stop reason before the margin, and the raw percentiles beside it.

To have the reported number and its interval describe the whole distribution, set `OutlierMode.None` (`--outlier none`). This disables trimming, and the Winsorized interval reduces to a plain one.

### What the loop decided

Every measured result carries an `AutoTune` diagnostic (`BenchmarkResult.AutoTune`) recording:
- Resolved K, warmup length, and sample count.
- Stop reasons for each phase.
- Achieved CI half-width and convergence trace.
- Wall-clock time spent tuning.
- Pre-flight jitter metric and outlier detector auto-switch status.
- Drift and restart counters.
- Measured clock resolution, sample duration, and quantization floor.

Reporters display this as an `auto-tuned: ...` line (console, Markdown), dedicated columns (CSV advanced), or an `autoTune` object (JSON).

The diagnostic also records `WarmupTimeFloorMet` (if measured before tiered compilation finished) and `SampleQuantizationFraction` (if measured more finely than the timer can resolve). Neither is visible in the margin of error.

### The warmup curve

The [warmup gates](#phase-b---warmup-plateau-detection) decide *when* warmup ends. The `AutoTune.WarmupCurve` diagnostic records the mean per-op time of each warmup batch (oldest first), showing tier-0 $\rightarrow$ tier-1 promotions and instrumented $\rightarrow$ optimized transitions under dynamic PGO.

Related fields include `JitLastChangeAtNs` (when the compiled-method count last moved) and `WarmupJit*` counters. The `WarmupJit*` counters are process-wide. In an in-process run, the first benchmark typically absorbs the bulk of startup compilation. Because [benchmarks run in random order by default](../faq.md#can-i-run-benchmarks-in-source-order-instead-of-random-order), this causes warmup to differ between runs.

Use `--order declaration` (or `--seed` for a reproducible shuffle) to ensure JIT costs occur in the same place every time. For a full field reference, see the [JSON reporter's `autoTune` object](../output/json-reporter.md#the-autotune-object).

## GC behavior

NBenchmark provides two GC behaviors to control how GC interacts with the loop:

- **`Natural`** (the default): No per-sample Gen0 GC and no pre-measurement full GC. Numbers reflect production behavior, including natural GC pauses and CPU cache effects.
- **`PerSampleCollect`** (opt-in): Forces Gen0 GC before every sample and runs a full GC after warmup. This is useful for pure-CPU measurements, cryptographic algorithms, or numeric kernels.

Both profiles include a full GC between benchmarks and allocation tracking. You can disable these with `--no-gc-between-benchmarks` or `--no-allocations`.

### Worked example

Consider a benchmark body that allocates 100 KiB per call:

```csharp
BenchmarkSuite.Create("AllocPressure")
    .Add("alloc", () => _ = new byte[100_000])
    .RunAsync();
```

- Under the **Natural** GC behavior, variance (CV%) is high, and some samples show Gen0-GC stalls. The `Alloc/op` column shows the allocation pressure.
- Under the **PerSampleCollect** GC behavior (`--gc per-sample-collect`), variance is low and numbers are tightly clustered. The `Alloc/op` column still shows 100 KiB/op, as allocation tracking remains enabled.

### Setting the profile

**In code (BenchmarkHarness):**
```csharp
await BenchmarkHarness.Create(args)
    .WithGcBehavior(GcBehavior.PerSampleCollect)
    .RunAsync();
```

**In code (BenchmarkSuite):**
```csharp
new BenchmarkSuite("MySuite")
    .WithGcBehavior(GcBehavior.PerSampleCollect)
    .Add(...)
    .RunAsync();
```

**On the CLI:**
```bash
dotnet run -- --gc per-sample-collect
```

### Per-option overrides

You can override specific behaviors individually:

```csharp
// Enable per-sample GC under Natural
options with { ForceGcBeforeEachSample = true }

// Inherit the warmup heap under PerSampleCollect (skip the pre-measurement GC)
options with { ForceGcBeforeMeasurement = false }

// Disable allocation tracking (both profiles)
options with { MeasureAllocations = false }

// Disable the between-benchmark GC (both profiles)
options with { ForceGcBetweenBenchmarks = false }
```

CLI equivalents:
```bash
dotnet run -- --gc natural --force-gc
dotnet run -- --no-allocations
dotnet run -- --no-gc-between-benchmarks
```

### Timer resolution

NBenchmark uses `System.Diagnostics.Stopwatch`. The **advertised** resolution is printed at the start of each `BenchmarkHarness` run:

```text
Timer resolution: 1,000,000,000 ticks/s (1.00 ns per tick)
```

This figure (`Stopwatch.Frequency`) may be inaccurate. For example, on Apple Silicon, `Stopwatch.Frequency` reports 1 GHz, but the underlying `mach_absolute_time` timebase runs at 24 MHz, meaning the counter moves in steps of **41.667 ns**.

NBenchmark **measures** the clock's effective resolution once per process (`Engine/Detectors/ClockResolutionProbe`) by spinning until the reported elapsed time becomes non-zero. The result (`AutoTune.ClockResolutionNs`) drives the sample-duration floor in [Phase A](#phase-a---ops-per-sample-calibration-k).

Per-sample timings are computed from raw `Stopwatch` ticks - not via `TimeSpan` - to preserve the platform's genuine resolution. For more information, see [The measurement engine: why quantization is dangerous](../deep-dives/measurement-engine.md#why-quantization-is-dangerous).

> [!NOTE] Timer-call overhead
> Each sample includes the cost of one timestamp read (~10-30 ns). Phase A amortizes this across K invocations for fast bodies. When K is pinned to 1, the read cost is a fixed addend; treat absolute values at that scale as upper bounds and compare against a baseline measured the same way.

## The host drift canary

Standard drift checks look inside a single benchmark's sample stream. However, host-level noise (e.g., a laptop warming up or a CI co-tenant) can affect every subsequent benchmark, making comparisons between rows unreliable even if each row is internally consistent.

NBenchmark measures the machine by running a fixed, deterministic control workload at every benchmark boundary: once before the first benchmark, once after each one, and once after the last.

Each result carries readings in `BenchmarkResult.HostTimeline`:

| Field | Description |
| --- | --- |
| `BeforeNs` / `AfterNs` | Absolute nanoseconds of the bracketing work. Only their ratios are meaningful. |
| `RelativeToRunStart` | The bracketing mean as a multiple of the first reading (e.g., `1.07` means work took 7% longer). |
| `CompletedBenchmarks` | Number of completed benchmarks when this one started. |

Compare `RelativeToRunStart` between rows. If the difference between a benchmark and the baseline is **smaller than the distance the host moved between measurements**, NBenchmark emits a warning:

```
host drift exceeds the difference being reported: the machine was 8% slower when 'Candidate' was
measured than when 'Baseline' was, and the 3% difference between them is smaller than that.
```

This warning does not downgrade the verdict. `MinimumPracticalEffect` and `MinimumRelativeShift` are statements about the comparison; the canary is a statement about the machine.

The canary costs a fraction of a millisecond and does not affect accuracy. It is enabled by default, skipped on dry-runs, and disabled with `--no-drift-canary` or `.WithDriftCanary(false)`. To change the reporting threshold, set `DriftCanaryOptions.MinimumReportableDrift` (default 1%). See [DriftCanary](../reference/configuration.md#driftcanary).

> [!NOTE]
> With multiple launches, each launch is a fresh worker. The row reports the mean of its launches' `RelativeToRunStart`. Increasing `--launch-count` is the most effective remedy for drift warnings, as it averages the drift across replicates.

## Reducing noise at the source

The adaptive loop, [outlier trimming](./outliers.md), and [significance testing](./significance.md) manage OS noise statistically. [Evidence-based interference rejection](./outliers.md#evidence-based-interference-rejection) is the exception: it reads the thread's CPU occupancy and discards samples the OS is *known* to have preempted. This is enabled by default; disable it with `--no-interference-filter`.

To reduce noise baked into every sample (e.g., thread migration), use **environment controls** - CPU affinity, process priority, and thread placement. See [Environment control](../features/environment-control.md) for details.
