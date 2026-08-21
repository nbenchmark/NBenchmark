---
title: The Measurement Engine
description: The adaptive loop at engineering depth - the clock-resolution probe, jitter calibration and detector auto-switch, quantization, and the optional-stopping correction.
order: 2
---

# The measurement engine

The [Measurement](../statistics/measurement.md) page describes the adaptive loop and its phases. This page describes the engineering internals: how the engine probes the clock rather than trusting it, how it measures the host's jitter, why quantization is dangerous, and how the reported confidence interval is corrected.

## The clock-resolution probe

`Engine/Detectors/ClockResolutionProbe` measures the **effective resolution** of the measurement clock - the smallest non-zero interval the engine can observe through `IClock.GetElapsedNanoseconds`. It does this by spinning until the reported elapsed time first becomes non-zero and taking the minimum over 32 attempts. The engine measures this once per process and caches the result (`Lazy<T>`, as workers can share a process); the result is stored in `AutoTuneDiagnostic.ClockResolutionNs`.

The engine does not read from `Stopwatch.Frequency` because that is an advertised conversion rate rather than an observed granularity and can be inaccurate by more than an order of magnitude. For example, on Apple Silicon, it reports 1 GHz (implying 1 ns steps), while the underlying `mach_absolute_time` timebase runs at 24 MHz. Consequently, the counter only advances in 41.667 ns units; spinning on it yields smallest deltas of 41, 42, 83, 84, 125, 166, 167, 208 ns and nothing between.

"Effective" resolution is used rather than "hardware quantum" because when a clock read costs more than one hardware step - which is common, as a counter read takes tens of nanoseconds - the smallest observable delta is bounded by the read, not the timebase. This bound is the correct value for the engine, as it is the finest interval a timed sample can actually distinguish.

The probe is used in three places:

- `ResolveTargetSampleDurationNs` raises the Phase A sample-duration target to `resolution × MinQuantaPerSample`. This ensures quantization remains under 0.2% of a sample on every host rather than varying between them. This operation only increases the duration.
- `QuantizationFraction` records one step as a fraction of one achieved sample on `AutoTuneDiagnostic.SampleQuantizationFraction`. This is computed from the achieved per-op mean multiplied by the final `K`, not the target, because `K` is a power of two and post-warmup recalibration may have shifted it.
- `AdaptiveLoop.BuildClockResolutionWarning` issues a warning when the measured confidence interval (CI) half-width is more than `ClockResolutionWarningFactor` (2×) finer than the quantization floor.

The engine only probes real-time clocks. `ResolutionNs` returns `0` (unknown, which disables all derived adjustments) for any clock other than a `StopwatchClock` with `IsRealTime` set to true. Probing calls `GetTimestamp` in a loop; since injected clocks generally serve a finite scripted sequence, probing one would consume the readings intended for the measurement itself. This is not hypothetical: it broke every injected-clock test in the suite on first wiring, with empty sample arrays. A fake's "resolution" would in any case be an artefact of the script. `Measure(IClock, int)` remains `internal` so unit tests can drive the probe against a purpose-built stub.

## Why quantization is dangerous

Quantization is asymmetric, which makes it dangerous rather than merely imprecise.

Within a run, consecutive samples of a stable body often land on the **same** step. This collapses the spread, resulting in a small standard deviation and a very small margin of error (since it divides by √n). Between runs, however, a shift far smaller than one step can push every sample to the next step, moving the median in one discrete jump.

This results in a benchmark that appears precise to four significant figures but shifts by a whole step between runs. For example, a 2.53 ns body at K = 4096 spanned ~10.4 µs (roughly 250 steps of a 41.667 ns clock) and reported a margin of **±0.027%**. However, eight isolated runs of the same benchmark produced medians ranging from 10209 to 10542 ns. Every result was a whole step from its neighbor, and the true run-to-run spread was **21× the reported margin**.

NBenchmark reports the floor rather than leaving it implicit. `AutoTune.SampleQuantizationFraction` represents one step as a fraction of one sample. A warning fires when the measured interval is finer than the clock can resolve:

```text
⚠ The measured confidence interval (±0.209%) is finer than this host's clock can resolve:
  one timer step is 41.0 ns against a 0.2 µs sample, so quantization alone is ±24.203% of
  the measurement. Within a run every sample lands on the same step, which is why the
  margin collapses; between runs a shift far smaller than one step moves every sample to
  the next one and takes the median with it.
```

With the default `MinQuantaPerSample` floor, this warning is rare. It primarily occurs in configurations that bypass the floor, such as using a small pinned `OpsPerSample` on a fast body. Along with `AutoTune.WarmupTimeFloorMet`, this is a key diagnostic when a benchmark reports a tight margin but does not reproduce. The former indicates a body measured before tiered compilation finished, while the latter indicates a body measured finer than the timer can resolve.

## Jitter calibration and detector auto-switch

Phase 0 times a deterministic, allocation-free busy-weight loop to derive a robust jitter metric: the ratio of the median absolute deviation to the median (MAD / median) of its per-sample timings. This probes the host, not the code under test. A quiet dedicated host typically reports well below 0.05, while a shared-tenant CI runner typically reports 0.10-0.30. This metric is robust because both the median and MAD have a ~50% breakdown point, meaning a single JIT spike or preemption cannot distort it as they would for standard deviation or mean.

This metric is critical because the default outlier detector (IQR fence) uses the interquartile range as its scale estimate, which has a low breakdown point. A heavy tail of scheduling-preempted samples can distort the fence and cause the engine to trim the wrong values. Median Absolute Deviation (MAD) is far more resilient to such tails. When the jitter metric exceeds `AutoTune.JitterAutoSwitchThreshold` (default 0.10) and the user has not pinned an outlier detector, the engine automatically switches the detector from IQR fence to MAD for that run. The engine records this switch in the `AutoTune` diagnostic (`OutlierDetectorSwitched`) and emits a warning.

The probe is enabled by default (`AutoTune.EnableJitterCalibration`). Pinning `OutlierMode` to a non-default value or supplying a custom `OutlierDetector` disables the auto-switch but not the probe, as the metric is still reported for visibility. You can set `AutoTune.JitterAutoSwitchThreshold` to 0 to disable the auto-switch while keeping the probe, or set `AutoTune.EnableJitterCalibration` to false to skip the probe entirely.

When the engine auto-switches the detector, the runner creates an effective options record with the switched detector pinned. It passes this to `StatsPipeline` and `OutcomeBuilder` so that the trimmed stats and the result's `OutlierDetector` name reflect the switch.

## The optional-stopping correction

NBenchmark does not use an optional-stopping correction. This section explains the bias and why the engine does not implement a correction.

The CI rule evaluates the half-width on a cadence (`BatchSize` samples past `MinSamples`) and stops at the first crossing. Each evaluation is a "look" at the accumulating data. A CI computed at the nominal `ConfidenceLevel` at the stopping look no longer has its nominal coverage because the loop stopped precisely when the interval happened to be narrow. This is the classic optional-stopping bias, which results in an optimistic reported interval.

The textbook fix is a Bonferroni widening over the look count: the error rate is split across the looks, increasing the critical value. This was implemented and tested but found to be unsuitable for this loop. At 2,000 samples with a `BatchSize` of 8, the look count is roughly 250. This would increase the t critical value from **1.96 to approximately 3.7**, widening the reported interval by about 1.9x. Bonferroni assumes looks are independent tests, but in this loop, they are highly correlated. Correcting as if 250 independent chances had been taken over-corrects severely. A user requesting a ±2.5% CI target would see ±5% or worse, which is not more honest, only larger, and would cause regression gates to fail on runs that actually converged.

Instead, the engine reports the [Winsorized precision](../statistics/descriptive.md#winsorized-standard-error-for-trimmed-data) of the trimmed mean and surfaces the stopping behavior:

- `AutoTune.SampleStop` indicates why sampling stopped. A `CiTargetMet` stop is where optional stopping applies; `MaxCeiling` and `DriftUnresolved` stops do not stop early on a narrow interval.
- `AutoTune.CiWidthSeries` provides the half-width at every look, allowing the user to see the convergence trace and the look count.
- `AutoTune.AchievedRelativeCiWidth` provides the raw-stream width at the stopping look.

The correct approach is a group-sequential boundary (such as Pocock or O'Brien-Fleming) or an always-valid confidence sequence, which corrects for repeated looks based on their actual correlation. This is tracked as a separate statistics project.

## Warmup gates

The plateau rule alone measures warmup in iterations. However, a fast body can plateau in microseconds of wall-clock time, long before the background JIT delivers tier-1 (and dynamic-PGO) code. In such cases, warmup would settle on a stable but slow tier-0 plateau, and the tier-1 switch would occur mid-measurement as a step change, becoming the dominant source of run-to-run variance. Two gates prevent this.

### The 500 ms time floor

`AutoTune.MinWarmupTime` defaults to 5× the runtime's `TieredCompilation.CallCountingDelayMs` (100 ms). This delay restarts whenever tier-0 methods are called for the first time. Tier-1 is only queued once this delay expires and is then compiled on a background thread, with a second instrumented→optimized transition under dynamic PGO. A floor at or below 100 ms therefore reliably lands those transitions inside the measurement window rather than before it.

Because of this floor, `AutoTune.MaxWarmup` defaults to **100,000** rather than the 10,000 that bounds a pinned `WarmupIterations`. A fast body needs roughly 50,000 samples to accumulate 500 ms at a 10 µs sample, or ~24,000 at 21 µs. If the count ceiling is reached before the time floor, the engine raises a warning and records `AutoTune.WarmupTimeFloorMet`. If a body cannot reach the floor within the ceiling - typically when `OpsPerSample` is pinned to 1 on a nanosecond body - the engine suggests increasing `--ops-per-sample` so each sample spans more work.

### Quiescence as a sustained interval

The gate reads `System.Runtime.JitInfo`'s compiled-method count at each batch boundary and tracks the last change. It continues until that change occurred `JitQuietPeriod` in the past.

Checking only whether the JIT compiled anything during the most recent batch is insufficient. For a fast body, one batch spans tens of microseconds, so a background compilation rarely lands inside that specific window.

Two clamps prevent the gate from becoming a problem: the quiet period is clamped to `MinWarmupTime` so it cannot become the binding floor, and the gate deactivates once warmup has run for 4 × `MinWarmupTime`. This prevents a busy in-process host that JITs unrelated code from holding warmup open indefinitely.

## Post-warmup recalibration

Ops-per-sample calibration (Phase A) resolves K against the body's **cold** code. Once warmup drives the body to its steady-state (tiered/PGO-optimized) speed, the same K may span well under the target duration, re-exposing the fixed timer overhead that calibration was intended to amortize. After auto-warmup settles, the loop re-derives K from the warm per-op estimate measured by the plateau detector (the last warmup batch mean). If the warm sample spans less than half the target, the engine bumps K to the next power of two that reaches the target and runs one untimed sample to warm the larger batch's cache and branch state.

Recalibration only applies when calibration ran (not a pinned K, and no setup/teardown) and only increases K. When it fires, `BenchmarkResult.AutoTune.InitialOpsPerSample` records the pre-recalibration (cold) K, while `OpsPerSample` holds the final value. The gap indicates how much faster the warm body ran than the cold code. If no recalibration occurs, `InitialOpsPerSample` is `null`.

## The drift gate and the cap

The steady-state (drift) gate guards against a hard-to-notice failure mode: a JIT tier-up, thermal ramp, or filling cache that occurs inside the measurement window. This produces a step change that a CI-on-the-mean rule might report as a tight interval. This can lead to a result that is 10× wrong but has a ±0.9% error bar, appearing more trustworthy than a correct result.

Upon a refusal, the loop discards all samples collected so far - including timings, allocations, and diagnostics - and restarts. It does this up to `MeasurementRestartLimit` times (default 2 - one for tier-0 to tier-1, and one for instrumented to optimized under dynamic PGO). `accumulatedNs` is not reset, so restarts draw from the same tuning budget and cannot extend total runtime. Exhausting the limit reports `SampleStopReason.DriftUnresolved` with a warning. The split-half gap is recorded on `AutoTuneDiagnostic.SplitHalfDrift` at every stop, and half-means are tracked in O(1) per sample by `SplitHalfTracker`.

When the wall-clock cap fires before `MinSamples` is reached, the loop continues sampling up to `MaxTuningTime × CapGraceFactor` (default 1.5×) rather than stopping with an under-sampled result. A one-sample result reports a standard deviation and margin of error of 0, which looks deceptively precise. The grace path trades a longer run for enough samples to be meaningful. If the grace ceiling is reached before `MinSamples`, the engine flags the error margin as unreliable. Users with `CapBehavior = Error` are unaffected, as the error fires at the base cap.

## See also

For more information, see the following pages:

- [Measurement](../statistics/measurement.md) - The phases and user-facing knobs
- [Outlier Trimming](../statistics/outliers.md) - What the auto-switched detector does
- [Descriptive Statistics](../statistics/descriptive.md) - What the corrected interval is reported on
