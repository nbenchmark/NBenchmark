---
title: The Measurement Engine
description: The adaptive loop at engineering depth - the clock-resolution probe, jitter calibration and detector auto-switch, quantization, and the optional-stopping correction.
order: 2
---

# The Measurement Engine

The [Measurement](../statistics/measurement.md) page describes the adaptive loop and its phases. This page is the engineering underneath the numbers: how the clock is probed rather than trusted, how the host's jitter is measured and what the engine does about it, why quantization is worse than it looks, and how the reported confidence interval is corrected for the fact that the loop stopped when it looked good.

## The clock-resolution probe

`Engine/Detectors/ClockResolutionProbe` measures the **effective resolution** of the measurement clock - the smallest non-zero interval the engine can observe through `IClock.GetElapsedNanoseconds` - by spinning until the reported elapsed time first becomes non-zero and taking the minimum over 32 attempts. Measured once per process and cached (`Lazy<T>`, since workers can share a process); the result lands on `AutoTuneDiagnostic.ClockResolutionNs`.

Deliberately not read from `Stopwatch.Frequency`, which is an advertised conversion rate rather than an observed granularity and can be wrong by more than an order of magnitude. On Apple Silicon it reports 1 GHz - implying 1 ns steps - while the underlying `mach_absolute_time` timebase runs at 24 MHz, so the counter only ever advances in 41.667 ns units; spinning on it yields smallest deltas of 41, 42, 83, 84, 125, 166, 167, 208 ns and nothing between.

"Effective" rather than "hardware quantum" is deliberate: when a clock read costs more than one hardware step - routine, since a counter read is tens of nanoseconds - the smallest observable delta is bounded by the read, not the timebase. That bound is the right number here, being the finest interval a timed sample can actually distinguish.

Three consumers:

- `ResolveTargetSampleDurationNs` raises Phase A's sample-duration target to `resolution × MinQuantaPerSample`, so quantization lands under 0.2% of a sample on every host rather than varying a thousand-fold between them. Only ever raises.
- `QuantizationFraction` records one step as a fraction of one *achieved* sample (computed from the achieved per-op mean × final `K`, not the target, since `K` is a power of two and post-warmup recalibration may have moved it) on `AutoTuneDiagnostic.SampleQuantizationFraction`.
- `AdaptiveLoop.BuildClockResolutionWarning` warns when the measured CI half-width is more than `ClockResolutionWarningFactor` (2×) finer than the quantization floor.

**Only a real-time clock is probed.** `ResolutionNs` returns `0` ("unknown", which disables every derived adjustment) for anything but a `StopwatchClock` with `IsRealTime` true. Probing calls `GetTimestamp` in a loop, and an injected clock generally serves a finite scripted sequence - so probing one consumes the readings a test scheduled for the measurement itself. This is not hypothetical: it broke every injected-clock test in the suite on first wiring, with empty sample arrays. A fake's "resolution" would in any case be an artefact of the script. `Measure(IClock, int)` stays `internal` so unit tests can drive the probe against a purpose-built stub directly.

## Why quantization is worse than it looks

Quantization presents asymmetrically, and that asymmetry is what makes it dangerous rather than merely imprecise.

*Within* a run, consecutive samples of a stable body land on the **same** step. The spread between them collapses, so the standard deviation is small, and the margin of error - which divides that by √n - becomes very small indeed. *Between* runs, a shift far smaller than one step is enough to push every sample to the next step, taking the median with it in one discrete jump.

The result is a benchmark that looks precise to four significant figures and moves by a whole step when you run it again. Measured on this repository's own calibration sample: a 2.53 ns body at K = 4096 spanned ~10.4 µs, or ~250 steps of the 41.667 ns clock, and reported a margin of **±0.027%** - while eight isolated runs of the same benchmark produced medians of 10209, 10250, 10291, 10375, 10416, 10458, 10500 and 10542 ns. Every one of those is a whole step from its neighbor, and the true run-to-run spread was **21× the reported margin**.

NBenchmark reports the floor rather than leaving it implicit. `AutoTune.SampleQuantizationFraction` is one step as a fraction of one sample, and a warning fires when the measured interval is finer than the clock can resolve:

```text
⚠ The measured confidence interval (±0.209%) is finer than this host's clock can resolve:
  one timer step is 41.0 ns against a 0.2 µs sample, so quantization alone is ±24.203% of
  the measurement. Within a run every sample lands on the same step, which is why the
  margin collapses; between runs a shift far smaller than one step moves every sample to
  the next one and takes the median with it.
```

With the default `MinQuantaPerSample` floor in place this warning is rare - it exists for configurations that bypass the floor, chiefly a small pinned `OpsPerSample` on a fast body. Alongside `AutoTune.WarmupTimeFloorMet`, it is the field to reach for when a benchmark reports a tight margin and will not reproduce: that one covers a body measured before tiered compilation finished, this one covers a body measured finer than the timer can see.

## Jitter calibration and the detector auto-switch

Phase 0 times a deterministic, allocation-free busy-weight loop and derives a robust jitter metric: the ratio of the median absolute deviation to the median (MAD / median) of its per-sample timings. This is a probe of the *host*, not the code under test: a quiet dedicated host reports well below 0.05, a shared-tenant CI runner typically reports 0.10-0.30. The metric is robust - both the median and MAD have a ~50% breakdown point, so a single JIT spike or one-off preemption cannot distort it the way stddev/mean can.

Why this matters: the default outlier detector (IQR fence) uses the interquartile range as its scale estimate, which has a low breakdown point - a heavy tail of scheduling-preempted samples distorts the fence and trims the wrong values. Median Absolute Deviation (MAD) has a ~50% breakdown point and is far more resilient to that tail. When the jitter metric exceeds `AutoTune.JitterAutoSwitchThreshold` (default 0.10) and the user has not pinned an outlier detector, the loop auto-switches the effective detector from IQR fence to MAD for that run. The switch is recorded on the `AutoTune` diagnostic (`OutlierDetectorSwitched`) and a warning is emitted explaining what happened and why.

The probe is on by default (`AutoTune.EnableJitterCalibration`). Pinning `OutlierMode` to a non-default value or supplying a custom `OutlierDetector` disables the auto-switch but not the probe - the metric is still reported for visibility. Set `AutoTune.JitterAutoSwitchThreshold` to 0 to disable the auto-switch while keeping the probe, or `AutoTune.EnableJitterCalibration` to false to skip the probe entirely.

When the loop auto-switched the detector, the runner builds an effective options record with the switched detector pinned and passes it to `StatsPipeline` and `OutcomeBuilder` so the trimmed stats and the result's `OutlierDetector` name reflect the switch.

## The optional-stopping correction

There isn't one. This section explains the bias, and why the obvious correction was implemented, measured, and withdrawn rather than shipped.

The CI rule evaluates the half-width on a cadence (`BatchSize` samples past `MinSamples`) and stops at the first crossing, so each evaluation is a "look" at the accumulating data. A CI computed at the nominal `ConfidenceLevel` at the stopping look no longer has its nominal coverage, because the loop stopped precisely when the interval happened to be narrow - the classic optional-stopping bias. The bias is real and its direction is known: the reported interval is optimistic.

The textbook fix is a Bonferroni widening over the look count: split the error rate across the looks, so the per-look α becomes α/looks and the critical value rises. That was built and it does not fit this loop. At 2,000 samples with the default `BatchSize` of 8, the look count is roughly 250 - so the effective confidence level becomes `1 − 0.05/250` and the t critical value lands near **3.7 against 1.96**, widening the reported interval about 1.9x on its own. Bonferroni assumes the looks are independent tests, and these are the opposite of independent: each look adds eight samples to a set already thousands strong, so consecutive half-widths are nearly the same number. Correcting as though 250 independent chances had been taken over-corrects severely for the bias that is actually present. A user who asked for a ±2.5% CI target would be shown ±5% or worse - not a more honest number, only a larger one, and one that would fail regression gates on runs that did converge.

So the interval reports the [Winsorized precision](../statistics/descriptive.md#after-outlier-trimming-the-winsorized-standard-error) of the trimmed mean and nothing more, and the loop's stopping behavior is surfaced instead of silently priced in:

- `AutoTune.SampleStop` names why sampling stopped. A `CiTargetMet` stop is the case where optional stopping applies; `MaxCeiling` and `DriftUnresolved` stops did not stop early on a narrow interval at all.
- `AutoTune.CiWidthSeries` is the half-width at every look, so the convergence trace - and its length, the look count - is in the output.
- `AutoTune.AchievedRelativeCiWidth` is the raw-stream width at the stopping look.

The right instrument is a group-sequential boundary (Pocock, O'Brien-Fleming) or an always-valid confidence sequence, which correct for repeated looks at their actual correlation rather than at the worst case. That is a statistics project of its own rather than a rider on the trimming correction, and it is tracked as one.

## The warmup gates

The plateau rule alone measures warmup in *iterations*, but a fast body plateaus in microseconds of
wall-clock - long before the background JIT delivers tier-1 (and dynamic-PGO) code. Warmup would
then settle on the stable-but-slow tier-0 plateau and the tier-1 switch would land mid-measurement
as a step change: the dominant source of run-to-run variance on very fast benchmarks. Two gates
prevent that.

### Why the time floor is 500 ms

`AutoTune.MinWarmupTime` defaults to 5× the runtime's `TieredCompilation.CallCountingDelayMs`
(100 ms). That delay *restarts* whenever tier-0 methods are still being called for the first time,
and tier-1 is only *queued* once it finally expires - then compiled on a background thread, with a
second instrumented→optimized transition under dynamic PGO. A floor at or below 100 ms therefore
reliably lands those transitions inside the measurement window rather than before it.

The floor is also why `AutoTune.MaxWarmup` defaults to **100,000** rather than the 10,000 that
bounds a pinned `WarmupIterations`: a fast body needs roughly 50,000 samples to accumulate 500 ms at
a 10 µs sample, or ~24,000 at the ~21 µs a coarse-clocked host resolves to. A count ceiling that
binds before the time floor would silently defeat it, so hitting the ceiling below the floor raises
a prominent warning and records `AutoTune.WarmupTimeFloorMet`. A body that cannot reach the floor
within the ceiling at all - typically `OpsPerSample` pinned to 1 on a nanosecond body - is told to
raise `--ops-per-sample` so each sample spans more work.

### Why quiescence is a sustained interval, not a per-batch delta

The gate reads `System.Runtime.JitInfo`'s compiled-method count at each batch boundary and remembers
where in warmup it last changed, continuing until that change is `JitQuietPeriod` in the past.

Asking only whether the JIT compiled anything *during the most recent batch* does not work: for a
fast body one batch spans tens of microseconds, so a background compilation almost never lands
inside that particular window and a per-batch delta reads zero essentially always.

Two clamps keep the gate from becoming the problem it solves. The quiet period is clamped down to
`MinWarmupTime` so it can never become the binding floor, and the gate deactivates once warmup has
run 4 × `MinWarmupTime`, so a busy in-process host that JITs unrelated code cannot hold warmup open
forever.

## Post-warmup recalibration

Ops-per-sample calibration (Phase A) resolves K against the body's **cold** code. Once warmup has driven the body to its steady-state (tiered / PGO-optimized) speed - often several times faster - the same K may span well under the target duration, re-exposing the fixed timer overhead calibration existed to amortize. So after auto-warmup settles, the loop re-derives K from the warm per-op estimate the plateau detector measured (the last warmup batch mean): if the warm sample spans less than half the target, K is bumped to the next power of two that reaches the target, and one untimed sample runs to warm the larger batch's cache/branch state before measurement.

Recalibration only applies when calibration ran (not a pinned K, no setup/teardown) and only ever increases K. When it fires, `BenchmarkResult.AutoTune.InitialOpsPerSample` records the pre-recalibration (cold) K while `OpsPerSample` holds the final value; the gap shows how much faster the warm body ran than the cold code first timed. When no recalibration occurs, `InitialOpsPerSample` is `null`.

## The drift gate and the cap

The steady-state (drift) gate guards the failure mode that is hardest to notice: a JIT tier-up - or a thermal ramp, or a filling cache - landing inside the measurement window produces a step change, and a CI-on-the-mean rule will happily report a tight interval straight across it. That is how a benchmark ends up 10× wrong with a ±0.9% error bar, looking more trustworthy than a correct result.

On a refusal the loop discards **all** samples collected so far - `timings`, `allocations`, and `diagnostics` together, since they are read by shared ordinal downstream - and restarts, up to `MeasurementRestartLimit` times (default 2 - one for tier-0→tier-1, one for instrumented→optimized under dynamic PGO). `accumulatedNs` is deliberately *not* reset, so restarts draw on the same tuning budget and can never extend total runtime. Exhausting the limit reports `SampleStopReason.DriftUnresolved` with a warning. The split-half gap is recorded on `AutoTuneDiagnostic.SplitHalfDrift` on *every* stop, including pinned-count and cap stops that never consult the gate, and the half-means are tracked in O(1) per sample by `SplitHalfTracker`.

When the wall-clock cap fires before `MinSamples` is reached, the loop keeps sampling up to `MaxTuningTime × CapGraceFactor` (default 1.5×) rather than stop on a dangerously under-sampled result. A one-sample result reports StdDev = 0 and MarginOfError = 0 - dangerously clean-looking - so the grace path trades a longer run for enough samples to be meaningful. If the grace ceiling is still reached below `MinSamples`, a prominent warning flags the error margin as unreliable. `CapBehavior = Error` users are unaffected - the error fires at the base cap either way.

## See also

- [Measurement](../statistics/measurement.md) - the phases and the user-facing knobs
- [Outlier Trimming](../statistics/outliers.md) - what the auto-switched detector does
- [Descriptive Statistics](../statistics/descriptive.md) - what the corrected interval is reported on
