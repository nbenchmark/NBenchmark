---
title: Measurement
description: How NBenchmark's measurement loop works, including timer resolution and per-iteration overhead.
order: 1
---

# Measurement

## The measurement loop

NBenchmark uses an **adaptive streaming loop**. Rather than running a fixed number of iterations, it resolves three dimensions at runtime - how many invocations to time per sample (**K**), how long to warm up, and how many measured samples to collect - and stops each as soon as it has enough. Every dimension can be pinned to an exact value (see [Configuration](../reference/configuration.md)); pinning all three reproduces a classic fixed-count run.

For each benchmark the loop runs in four phases:

### Phase 0 - Pre-flight jitter calibration

Before any real measurement, NBenchmark times a deterministic, allocation-free busy-weight loop and derives a robust jitter metric: the ratio of the median absolute deviation to the median (MAD / median) of its per-sample timings. This is a probe of the *host*, not the code under test: a quiet dedicated host reports well below 0.05, a shared-tenant CI runner typically reports 0.10-0.30. The metric is robust - both the median and MAD have a ~50% breakdown point, so a single JIT spike or one-off preemption cannot distort it the way stddev/mean can.

Why this matters: the default outlier detector (IQR fence) uses the interquartile range as its scale estimate, which has a low breakdown point - a heavy tail of scheduling-preempted samples distorts the fence and trims the wrong values. Median Absolute Deviation (MAD) has a ~50% breakdown point and is far more resilient to that tail. When the jitter metric exceeds `AutoTune.JitterAutoSwitchThreshold` (default 0.10) and the user has not pinned an outlier detector, the loop auto-switches the effective detector from IQR fence to MAD for that run. The switch is recorded on the `AutoTune` diagnostic (`OutlierDetectorSwitched`) and a warning is emitted explaining what happened and why.

The probe is on by default (`AutoTune.EnableJitterCalibration`). Pinning `OutlierMode` to a non-default value or supplying a custom `OutlierDetector` disables the auto-switch but not the probe - the metric is still reported for visibility. Set `AutoTune.JitterAutoSwitchThreshold` to 0 to disable the auto-switch while keeping the probe, or `AutoTune.EnableJitterCalibration` to false to skip the probe entirely.

### Phase 1 - Ops-per-sample calibration (K)

If `OpsPerSample` is `null` (the default) and the body is eligible, NBenchmark times a single invocation, then doubles K - timing 1, 2, 4, 8, … invocations as one batch - until a batch spans at least `AutoTune.TargetSampleDurationNs` (**10 µs** by default). The resolved K is reused for warmup and measurement, and every reported timing divides the batch time by K to give a per-operation number.

The 10 µs target keeps two per-sample error sources negligible: timer **quantization** (Windows QPC ticks at 100 ns, so a 10 µs sample resolves to ~0.1% rather than the ~±10% a 1 µs sample would suffer) and the fixed **timestamp-read overhead** (~10-30 ns, ~0.2% of 10 µs rather than ~1-3% of 1 µs). Both would otherwise leak into the ±2.5% CI target. Bodies already spanning ≥ 10 µs keep K = 1, so their per-op tail visibility is unchanged.

> [!NOTE] K > 1 batches change what percentiles mean
> When K > 1, each recorded sample is the mean of K back-to-back operations, so P95/P99/Max and the histogram describe **batch means**, not individual-operation tails - a slow individual op is averaged with its K-1 neighbours. For a sub-10 µs body the trade is deliberate (per-op timing at that scale is dominated by timer noise anyway); when you need per-op tail latency, pin `OpsPerSample = 1` and read the caveats in [Descriptive Statistics](./descriptive.md).

Calibration is skipped (K = 1) when `IterationSetup`/`IterationTeardown` is set, because a batch would no longer represent one isolated call. It is **not** skipped under the `Independent` profile: the forced Gen0 GC runs once per sample (the K-batch), before the timestamp and outside the timed window — the same semantics a pinned `OpsPerSample` gets — so nano-scale CPU bodies still amortise timer overhead. A pinned `OpsPerSample` is always honoured. Calibration runs against the body's **cold** (pre-warmup) speed; see [Post-warmup recalibration](#post-warmup-recalibration) below for how K is re-derived once the body is warm.

### Phase 2 - Warmup (plateau detection)

If `WarmupIterations` is `null`, NBenchmark collects warmup samples in batches of `AutoTune.BatchSize` and tracks the best (fastest) batch mean seen so far. Once `AutoTune.PlateauPatience` consecutive batches fail to improve on the best by at least `AutoTune.WarmupEpsilon`, the code is considered warm and warmup stops - never before `AutoTune.MinWarmup` samples, never after `AutoTune.MaxWarmup`. A pinned `WarmupIterations` runs exactly that many warmup samples.

The plateau rule alone measures warmup in *iterations*, but a fast body plateaus in microseconds of wall-clock - long before the background JIT delivers tier-1 (and dynamic-PGO) code. Warmup would then settle on the stable-but-slow tier-0 plateau and the tier-1 switch would land mid-measurement as a step change, the dominant source of run-to-run variance on very fast benchmarks. Two extra gates prevent that:

- **Warmup time floor** (`AutoTune.MinWarmupTime`, default 100 ms; 25 ms under `Quick`): auto-warmup will not settle until it has run at least this much wall-clock, giving tiered compilation time to land. Set to `0` to disable.
- **JIT-quiescence gate** (`AutoTune.RequireJitQuiescence`, default on): at each batch boundary NBenchmark reads `System.Runtime.JitInfo`'s compiled-method count; while it is still rising over a batch (tier-1 promotion in flight), warmup continues. To avoid blocking forever on a busy in-process host that JITs unrelated code, the gate deactivates once warmup has run 4 × `MinWarmupTime`. Disabling the time floor (`MinWarmupTime = 0`) also disables this gate.

Both gates only *delay* settling past the plateau; `MaxWarmup` and the calibration+warmup budget share (below) still bound warmup from above, so a genuinely slow body is not held open by them.

For slow bodies the configured `BatchSize` is shrunk based on the per-sample estimate from calibration: a body that takes seconds per sample warms in batches of 1 so the plateau rule can settle after `PlateauPatience + 1` samples instead of `(PlateauPatience + 1) × BatchSize` (subject to the `MinWarmup` floor, which with the default `MinWarmup = 8` is then the binding constraint). Without this shrink a 2 s body with the default `BatchSize = 8` would need `(PlateauPatience + 1) × BatchSize = 32` samples — 64 s of warmup — just to clear the plateau requirement.

Calibration and warmup share a budget: together they may consume at most `AutoTune.WarmupBudgetFraction` of `AutoTune.MaxTuningTime` (default 0.4 = 40%), reserving the remainder for measurement. This keeps slow bodies from spending the whole cap on warmup and leaving measurement with a single sample. When the share is exhausted, warmup stops at the wall-clock cap and a warning names the share.

If `ForceGcBeforeMeasurement` is true (the `Independent` profile), a full gen-2 GC runs after warmup to establish a clean heap baseline. Under `Realistic` (the default) this is skipped and the benchmark inherits the warmup's heap state. (This is a distinct knob from `ForceGcBetweenBenchmarks`, which runs a full GC *between* benchmarks and is on for both profiles.)

### Post-warmup recalibration

Ops-per-sample calibration (Phase 1) resolves K against the body's **cold** code. Once warmup has driven the body to its steady-state (tiered / PGO-optimized) speed - often several times faster - the same K may span well under the target duration, re-exposing the fixed timer overhead calibration existed to amortise. So after auto-warmup settles, NBenchmark re-derives K from the warm per-op estimate the plateau detector measured (the last warmup batch mean): if the warm sample spans less than half the target, K is bumped to the next power of two that reaches the target, and one untimed sample runs to warm the larger batch's cache/branch state before measurement.

Recalibration only applies when calibration ran (not a pinned K, no setup/teardown) and only ever increases K. When it fires, `BenchmarkResult.AutoTune.InitialOpsPerSample` records the pre-recalibration (cold) K while `OpsPerSample` holds the final value; the gap shows how much faster the warm body ran than the cold code first timed. When no recalibration occurs, `InitialOpsPerSample` is `null`.

### Phase 3 - Measurement (CI-width target)

If `Iterations` is `null`, NBenchmark streams measured samples and, every `AutoTune.BatchSize` samples, recomputes the confidence interval on the mean. Sampling stops once the interval's relative half-width falls below `AutoTune.CiTarget` (±2.5% by default) - never before `AutoTune.MinSamples`, never after `AutoTune.MaxSamples`. A pinned `Iterations` collects exactly that many samples. A per-benchmark `AutoTune.MaxTuningTime` wall-clock cap bounds the whole loop so a pathological body can never run away.

When the cap fires before `AutoTune.MinSamples` is reached, the loop keeps sampling up to `AutoTune.MaxTuningTime × AutoTune.CapGraceFactor` (default 1.5×) rather than stop on a dangerously under-sampled result. A one-sample result reports StdDev = 0 and MarginOfError = 0 - dangerously clean-looking - so the grace path trades a longer run for enough samples to be meaningful. If the grace ceiling is still reached below `MinSamples`, a prominent warning flags the error margin as unreliable. Set `AutoTune.CapGraceFactor` to 1 to disable the grace path and stop at the base cap. `AutoTuneCapBehavior.Error` users are unaffected - the error fires at the base cap either way.

Each measured sample does the following:

- If `ForceGcBeforeEachIteration` is true (the `Independent` profile), force a gen-0 collection (once per sample, before the timestamp).
- Call `IterationSetup` if provided.
- Record `Stopwatch.GetTimestamp()`.
- Invoke the benchmark action K times.
- Read the timestamp again and convert the raw tick delta to nanoseconds at the timer's **native resolution** (`delta × 10⁹ / Stopwatch.Frequency`), then divide by K.
- Record the allocation delta (divided by K) if `MeasureAllocations` is true (on by default under both profiles; the snapshot is taken outside the timed window).
- Call `IterationTeardown` if provided.

**Important:** the timer is read immediately after the K-batch returns, before teardown runs. Teardown time is not included in the measurement.

### Raw vs. trimmed statistics

The CI-width stop rule evaluates the **raw** (untrimmed) sample stream as it arrives. After the loop ends, the collected per-op samples pass through [outlier trimming](./outliers.md) and the reported statistics - including the Error column - are computed on the **trimmed** set. The two confidence intervals are close but need not be identical: the diagnostic's `AchievedRelativeCiWidth` reflects the raw stop value, while the reported interval reflects the trimmed result.

### What the loop decided

Every measured result carries an `AutoTune` diagnostic (`BenchmarkResult.AutoTune`) recording the resolved K, warmup length, sample count, why each phase stopped, the achieved CI half-width, the wall-clock time spent tuning, the pre-flight jitter metric, and whether the outlier detector was auto-switched. Reporters surface it as an `auto-tuned: …` line (console, Markdown), dedicated columns (CSV advanced), or an `autoTune` object (JSON). It is `null` on dry-run and errored results.

## Measurement profiles

NBenchmark provides two measurement profiles that control how GC interacts with the measurement loop:

- **`Realistic`** (the default) - no per-iteration Gen0 GC, no pre-measurement full GC (the warmup heap is inherited). Numbers reflect what the same code does in production, including natural GC pauses and CPU cache effects.
- **`Independent`** (opt-in) - force Gen0 GC before every sample, run a full GC after warmup before measurement. Useful for pure-CPU measurements, cryptographic algorithms, numeric kernels, and other cases where iteration-to-iteration independence is more important than ecological validity.

Two behaviours are on for **both** profiles: the between-benchmark full GC (so one benchmark's leftover heap cannot bias the next) and allocation tracking (sampled outside the timed window, so it costs nothing and surfaces the "this pure-CPU body actually allocates" signal even under `Independent`). Disable them with `--no-gc-between-benchmarks` / `--no-allocations` if needed.

### Worked example

Consider a benchmark body that allocates 100 KB per call:

```csharp
BenchmarkSuite.Create("AllocPressure")
    .Add("alloc", () => _ = new byte[100_000])
    .RunAsync();
```

Under the **Realistic** profile (the default), the variance (CV%) is high and some iterations show Gen0-GC stalls. The `Alloc/op` column is populated and shows the allocation pressure. The numbers reflect what this code would do in production.

Under the **Independent** profile (`--profile independent`), the variance is low and the per-iteration numbers are tightly clustered. The `Alloc/op` column is still populated (allocation tracking is on for both profiles), so the 100 KB/op shows up even here. The numbers answer a narrower question: "how much CPU time does this take, ignoring GC and cache?"

### Setting the profile

```csharp
// In code (BenchmarkHarness)
await BenchmarkHarness.Create(args)
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .RunAsync();

// In code (BenchmarkSuite)
new BenchmarkSuite("MySuite")
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .Add(...)
    .RunAsync();

// On the CLI
dotnet run -- --profile independent
```

### Per-option overrides

Each behaviour can be overridden individually:

```csharp
// Enable per-iteration GC under Realistic
options with { ForceGcBeforeEachIterationOverride = true }

// Inherit the warmup heap under Independent (skip the pre-measurement GC)
options with { ForceGcBeforeMeasurementOverride = false }

// Disable allocation tracking (both profiles)
options with { MeasureAllocationsOverride = false }

// Disable the between-benchmark GC (both profiles)
options with { ForceGcBetweenBenchmarksOverride = false }
```

CLI equivalents:

```bash
dotnet run -- --profile realistic --force-gc
dotnet run -- --no-allocations
dotnet run -- --no-gc-between-benchmarks
```

### Timer resolution

NBenchmark uses `System.Diagnostics.Stopwatch`, which wraps the platform's high-resolution performance counter. The resolution is printed at the start of each `BenchmarkHarness` run:

```
Timer resolution: 1,000,000,000 ticks/s (1.00 ns per tick)
```

On most modern hardware the resolution is 1 ns. On some virtual machines it may be coarser; on Windows the counter typically runs at 10 MHz (100 ns per tick).

Per-iteration timings are computed directly from raw `Stopwatch` ticks - deliberately **not** via `TimeSpan`, whose ticks are always 100 ns. On a 1 GHz timer this preserves the full 1 ns sample resolution; round-tripping through `TimeSpan` would quantize every sample to a multiple of 100 ns and record sub-100 ns operations as zero.

> [!NOTE] Timer-call overhead
> Each sample includes the cost of one timestamp read (typically ~10-30 ns).
> Ops-per-sample calibration (Phase 1 above) amortises this across K invocations
> for fast bodies, so the per-op number stays meaningful even in the low-nanosecond
> range. When K is pinned to 1 - or when setup/teardown forces it - the read cost is
> a fixed addend on every sample, so treat absolute values at that scale as upper
> bounds and compare against a baseline measured the same way.

## Reducing noise at the source

The adaptive loop, [outlier trimming](./outliers.md) (including the [bimodal warning](./outliers.md#bimodal-distribution-warning)), and [significance testing](./significance.md) all work around OS noise statistically - they discard or down-weight samples that look like interference. But they cannot remove noise that is baked into every sample: a benchmark thread that migrates between cores suffers cold-cache stalls on every migration, and a normal-priority process on a busy host is preempted on a schedule that has nothing to do with your code.

NBenchmark provides opt-in **environment controls** that reduce this noise before the timer starts:

- **CPU affinity** - pin the benchmark process to specific cores to eliminate inter-core migration.
- **Process priority** - raise the process priority to reduce preemption by unrelated OS work.
- **Dedicated-host guidance** - a non-fatal probe that warns when the host looks noisy (low core count, unraisable priority, or on macOS unobservable frequency scaling/thermal throttling) and suggests `--priority high` on a suitable host.

All three default to off and are restored when the run completes. They are the proactive counterpart to the reactive statistical noise handling: trimming discards noisy samples after the fact; environment control reduces the noise at the source.

See [Environment control](../features/environment-control.md) for the full model, platform notes, and isolated-process propagation.
