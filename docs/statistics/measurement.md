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

- **Warmup time floor** (`AutoTune.MinWarmupTime`, default 500 ms; 1 s under `Thorough`): auto-warmup will not settle until it has accumulated at least this much in-body time, giving tiered compilation time to land. Set to `0` to disable.

  The default is 5× the runtime's `TieredCompilation.CallCountingDelayMs` (100 ms). That delay *restarts* whenever tier-0 methods are still being called for the first time, and tier-1 is only *queued* once it finally expires — then compiled on a background thread, with a second instrumented→optimized transition under dynamic PGO. A floor at or below 100 ms therefore reliably lands those transitions inside the measurement window rather than before it. In practice this floor, not `MinWarmup` or `PlateauPatience`, is what determines warmup length for almost every body.

  **`Quick` does not shorten this floor.** It is a measurement-correctness requirement, not a speed/accuracy trade-off: a short floor does not give you a rougher number, it gives you a *confidently wrong* one — a benchmark measured on tier-0 code can report a median several times off with a ±1% error bar, and it will not reproduce across runs. `Quick` gets its speed from a looser `CiTarget`, a lower `MinSamples`, and a shorter `MaxTuningTime` instead.
- **JIT-quiescence gate** (`AutoTune.RequireJitQuiescence`, default on, with `AutoTune.JitQuietPeriod`, default 50 ms): at each batch boundary NBenchmark reads `System.Runtime.JitInfo`'s compiled-method count and remembers where in warmup it last changed. Warmup continues until that change is `JitQuietPeriod` in the past, so an in-flight tier-1 promotion actually extends warmup.

  The *sustained interval* is the point. Asking only whether the JIT compiled anything during the most recent batch does not work: for a fast body one batch spans tens of microseconds, so a background compilation almost never lands inside that particular window and a per-batch delta reads zero essentially always. The quiet period is clamped down to `MinWarmupTime` so it can never become the binding floor, and the gate deactivates once warmup has run 4 × `MinWarmupTime` so a busy in-process host that JITs unrelated code cannot hold warmup open forever. Disabling the time floor (`MinWarmupTime = 0`) or setting `JitQuietPeriod = 0` also disables this gate.

Both gates only *delay* settling past the plateau; `MaxWarmup` and the calibration+warmup budget share (below) still bound warmup from above, so a genuinely slow body is not held open by them.

Because a fast body needs roughly 50,000 samples to accumulate 500 ms at the 10 µs sample target, `AutoTune.MaxWarmup` defaults to **100,000** — not the 10,000 that bounds a *pinned* `WarmupIterations`. A count ceiling that binds before the time floor would silently defeat it, so hitting the ceiling below the floor raises a prominent warning and `BenchmarkResult.AutoTune.WarmupTimeFloorMet` records it. A body that cannot reach the floor within the ceiling at all — typically `OpsPerSample` pinned to 1 on a nanosecond body — is told to raise `--ops-per-sample` so each sample spans more work.

For slow bodies the configured `BatchSize` is shrunk based on the per-sample estimate from calibration: a body that takes seconds per sample warms in batches of 1 so the plateau rule can settle after `PlateauPatience + 1` samples instead of `(PlateauPatience + 1) × BatchSize` (subject to the `MinWarmup` floor, which with the default `MinWarmup = 8` is then the binding constraint). Without this shrink a 2 s body with the default `BatchSize = 8` would need `(PlateauPatience + 1) × BatchSize = 32` samples — 64 s of warmup — just to clear the plateau requirement.

Calibration and warmup share a budget: together they may consume at most `AutoTune.WarmupBudgetFraction` of `AutoTune.MaxTuningTime` (default 0.4 = 40%), reserving the remainder for measurement. This keeps slow bodies from spending the whole cap on warmup and leaving measurement with a single sample. When the share is exhausted, warmup stops at the wall-clock cap and a warning names the share.

If `ForceGcBeforeMeasurement` is true (the `Independent` profile), a full gen-2 GC runs after warmup to establish a clean heap baseline. Under `Realistic` (the default) this is skipped and the benchmark inherits the warmup's heap state. (This is a distinct knob from `ForceGcBetweenBenchmarks`, which runs a full GC *between* benchmarks and is on for both profiles.)

### Post-warmup recalibration

Ops-per-sample calibration (Phase 1) resolves K against the body's **cold** code. Once warmup has driven the body to its steady-state (tiered / PGO-optimized) speed - often several times faster - the same K may span well under the target duration, re-exposing the fixed timer overhead calibration existed to amortise. So after auto-warmup settles, NBenchmark re-derives K from the warm per-op estimate the plateau detector measured (the last warmup batch mean): if the warm sample spans less than half the target, K is bumped to the next power of two that reaches the target, and one untimed sample runs to warm the larger batch's cache/branch state before measurement.

Recalibration only applies when calibration ran (not a pinned K, no setup/teardown) and only ever increases K. When it fires, `BenchmarkResult.AutoTune.InitialOpsPerSample` records the pre-recalibration (cold) K while `OpsPerSample` holds the final value; the gap shows how much faster the warm body ran than the cold code first timed. When no recalibration occurs, `InitialOpsPerSample` is `null`.

### Phase 3 - Measurement (CI-width target)

If `Iterations` is `null`, NBenchmark streams measured samples and, every `AutoTune.BatchSize` samples, recomputes the confidence interval on the mean. Sampling stops once the interval's relative half-width falls below `AutoTune.CiTarget` (±2.5% by default) - never before `AutoTune.MinSamples`, never after `AutoTune.MaxSamples`. A pinned `Iterations` collects exactly that many samples. A per-benchmark `AutoTune.MaxTuningTime` wall-clock cap bounds the whole loop so a pathological body can never run away.

Two gates sit on that stop rule, mirroring the warmup settle gates. The CI rule decides whether the interval is *narrow enough*; the gates decide whether it is *honest to stop*.

- **Measurement time floor** (`AutoTune.MinMeasurementTime`, default 100 ms; 50 ms under `Quick`, 500 ms under `Thorough`): measurement will not stop on the CI target until it has spanned this much in-body time. This is what makes the sample count scale with how cheap the body is. A flat `MinSamples` is blind to that — the same 30 samples cost 9 s on a 300 ms body and 0.5 ms on a 1 µs body, where thousands of samples are essentially free and buy meaningful percentiles, a usable histogram, and a significance test with real power. (At n ≈ 16 the reported P95, P99 and P99.9 all collapse onto the maximum.)

  The rule is simply: measurement spans at least this long, or reaches `AutoTune.MaxSamples` samples, whichever comes first. So worst-case added cost is `MinMeasurementTime` per benchmark, and it is **exactly zero** for any body already slower than `MinMeasurementTime / MinSamples` (≈3.3 ms by default), where `MinSamples` binds and nothing changes. Set to `0` to stop on `MinSamples` alone.
- **Steady-state (drift) gate** (`AutoTune.MeasurementDriftTolerance`, default 0.10): when the CI rule wants to stop, NBenchmark compares the mean of the first half of the collected samples against the mean of the second half, and refuses the stop if they disagree both *relatively* (by more than the tolerance, measured against the smaller half-mean) and *statistically* (by more than 4 standard errors of the difference).

  > [!CAUTION] If you hit a `driftUnresolved` stop
  > **Land the transition during warmup instead:** raise `--min-warmup-time <ms>` (default 500) so a JIT tier-up or dynamic-PGO re-optimization lands inside warmup, not measurement.
  > **Accept non-stationarity as the finding:** `--launch-count 5` measures the across-launch spread, which is the honest signal for a body that genuinely does not have a steady state.

  This guards the failure mode that is hardest to notice. A JIT tier-up — or a thermal ramp, or a filling cache — landing inside the measurement window produces a step change, and a CI-on-the-mean rule will happily report a tight interval straight across it. That is how a benchmark ends up 10× wrong with a ±0.9% error bar, looking more trustworthy than a correct result.

  Both conditions are required. A bare relative rule false-positives forever on a heavy-tailed body whose half-means differ by pure sampling noise; a bare significance rule flags sub-percent drift once *n* reaches the thousands. On a refusal the loop discards **all** samples collected so far and starts measurement over, up to `AutoTune.MeasurementRestartLimit` times (default 2 — one for tier-0→tier-1, one for instrumented→optimized under dynamic PGO). Restarts draw on the same `MaxTuningTime` budget as ordinary sampling, so they can never make a benchmark run longer. Exhausting the limit reports `SampleStopReason.DriftUnresolved` with a warning; set the tolerance to `0` to disable the gate. Either way `BenchmarkResult.AutoTune.SplitHalfDrift` records the gap, on every stop — including pinned-count and cap stops that never consult the gate — so a tight interval sitting next to a large drift is visible rather than silently trusted.

`AutoTune.MaxSamples` defaults to **5,000** (2,000 under `Quick`, 20,000 under `Thorough`). At 5,000 the CI rule still reaches ±2.5% for any body with a coefficient of variation up to roughly 90%. Past that the required count grows as `(t × CV / target)²` and runs away — a CV of 580% needs about 50,000 samples just to reach ±5% — but a body that noisy has variance that *is* the finding, and more samples only buy a tighter interval around an unstable centre. The ceiling warning therefore names the measured CV and the count convergence would actually take, and points at `--launch-count` as the more honest signal.

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

The CI-width stop rule evaluates the **raw** (untrimmed) sample stream as it arrives. After the loop ends, the collected per-op samples pass through [outlier trimming](./outliers.md) and the reported statistics - including the Error column - are computed on the **trimmed** set. So the diagnostic's `AchievedRelativeCiWidth` reflects the raw stop value while the reported interval reflects the trimmed result.

The two are usually close, but they can diverge by two orders of magnitude, and the direction is always the same: **the reported interval is the narrower one.** When a benchmark's variance lives almost entirely in the outliers, trimming removes it and the reported margin tightens around what remains — one sample body reported `MarginOfError` at ±1.3% of its mean next to an `AchievedRelativeCiWidth` of `1.05` (±105%) and a `MaxCeiling` stop. Neither number is wrong; they describe different sample sets. But a tight Error column is only trustworthy evidence that the *measurement converged* when `SampleStop` is `CiTargetMet`. Read the stop reason before the margin.

### What the loop decided

Every measured result carries an `AutoTune` diagnostic (`BenchmarkResult.AutoTune`) recording the resolved K, warmup length, sample count, why each phase stopped, the achieved CI half-width and its convergence trace, the wall-clock time spent tuning, the pre-flight jitter metric, whether the outlier detector was auto-switched, and the drift and restart counters. Reporters surface it as an `auto-tuned: …` line (console, Markdown), dedicated columns (CSV advanced), or an `autoTune` object (JSON). It is `null` on dry-run and errored results.

It also records what warmup observed about tiered compilation - see [the warmup curve](#the-warmup-curve).

### The warmup curve

The [warmup gates](#phase-2---warmup-plateau-detection) decide *when* warmup may end. The diagnostic additionally retains what warmup *saw*, which is the only surviving record of the body tiering up: raw warmup timings are never persisted, and `RawSamples` covers the measurement phase only.

- **`WarmupCurve`** - the mean per-op time of each warmup batch, oldest first. The plateau rule already computes a batch mean, so retaining them costs nothing, and the averaging keeps a two-or-three-step decay from being buried in per-sample jitter. Tier-0 → tier-1 promotion, and instrumented → optimized under dynamic PGO, each appear as a step down. **`WarmupSampleInterval`** gives the warmup iterations between consecutive points, so the array plots against a real iteration axis. Bounded at 512 points - a longer warmup is decimated by a doubling stride, keeping points evenly spaced and the shape intact at coarser resolution. Empty for a pinned `WarmupIterations`, which runs no plateau detection.
- **`JitLastChangeAtNs`** - how far into warmup the compiled-method count last moved, with **`WarmupElapsedNs`** as the total extent. Under continuous load the final compilation is usually the promotion of the body's own hot path, which makes this the closest thing to a tier-up marker to draw on the curve. **`JitQuiescenceAchieved`** reports whether the quiet period genuinely elapsed rather than the gate being bypassed by its deactivation threshold.
- **`WarmupJitCompiledMethods`**, **`WarmupJitCompilationTime`**, **`WarmupJitCompiledIlBytes`** - `System.Runtime.JitInfo` deltas across warmup, sampled at batch boundaries. Compilation *time* is the most directly useful: it is denominated in the same units as the benchmark, so it answers "what did tiering cost here?" rather than "how many methods were involved?".

All three counters are **process-wide**, not per-benchmark. In an in-process run the first benchmark to execute absorbs the bulk of startup compilation and later ones see almost none - which is real, and since [benchmarks run in random order by default](../faq.md#can-i-run-benchmarks-in-source-order-instead-of-random-order) it is a significant part of why the same benchmark's warmup differs between runs. Use `--order declaration` (or `--seed` for a reproducible shuffle) if you need the JIT cost to fall in the same place every time.

Two limits worth knowing:

- **This is aggregate decay, not per-method tier attribution.** Naming individual methods and their tiers (`QuickJitted`, `OptimizedTier1`, OSR, instrumented) requires the runtime's `MethodLoadVerbose` events via EventPipe or an in-process `EventListener`, which NBenchmark does not collect.
- **Ops-per-sample calibration runs before warmup** and already exercises the body, so some tier-up has typically happened before the first warmup batch is recorded. The curve shows what remains of tiering plus cache and branch-predictor warming, not the full cold-start cliff - expect a few percent for an already-fast body and several times for an allocation-heavy one.

## Measurement profiles

NBenchmark provides two measurement profiles that control how GC interacts with the measurement loop:

- **`Realistic`** (the default) - no per-iteration Gen0 GC, no pre-measurement full GC (the warmup heap is inherited). Numbers reflect what the same code does in production, including natural GC pauses and CPU cache effects.
- **`Independent`** (opt-in) - force Gen0 GC before every sample, run a full GC after warmup before measurement. Useful for pure-CPU measurements, cryptographic algorithms, numeric kernels, and other cases where iteration-to-iteration independence is more important than ecological validity.

Two behaviours are on for **both** profiles: the between-benchmark full GC (so one benchmark's leftover heap cannot bias the next) and allocation tracking (sampled outside the timed window, so it costs nothing and surfaces the "this pure-CPU body actually allocates" signal even under `Independent`). Disable them with `--no-gc-between-benchmarks` / `--no-allocations` if needed.

### Worked example

Consider a benchmark body that allocates 100 KiB per call:

```csharp
BenchmarkSuite.Create("AllocPressure")
    .Add("alloc", () => _ = new byte[100_000])
    .RunAsync();
```

Under the **Realistic** profile (the default), the variance (CV%) is high and some iterations show Gen0-GC stalls. The `Alloc/op` column is populated and shows the allocation pressure. The numbers reflect what this code would do in production.

Under the **Independent** profile (`--profile independent`), the variance is low and the per-iteration numbers are tightly clustered. The `Alloc/op` column is still populated (allocation tracking is on for both profiles), so the 100 KiB/op shows up even here. The numbers answer a narrower question: "how much CPU time does this take, ignoring GC and cache?"

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
