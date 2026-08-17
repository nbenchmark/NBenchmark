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

Before any real measurement, NBenchmark times a deterministic, allocation-free busy-weight loop and derives a robust jitter metric (MAD / median of its per-sample timings) as a probe of the *host*, not the code under test. A quiet dedicated host reports well below 0.05; a shared-tenant CI runner typically reports 0.10-0.30. When the metric exceeds `AutoTune.JitterAutoSwitchThreshold` (default 0.10) and the user has not pinned an outlier detector, the loop auto-switches the effective detector from IQR fence to MAD for that run - the switch is recorded on the `AutoTune` diagnostic (`OutlierDetectorSwitched`) and a warning is emitted. The probe is on by default (`AutoTune.EnableJitterCalibration`); see [The measurement engine: jitter calibration and the detector auto-switch](../deep-dives/measurement-engine.md#jitter-calibration-and-the-detector-auto-switch) for why the auto-switch exists and how to disable it.

### Phase A - Ops-per-sample calibration (K)

If `OpsPerSample` is `null` (the default) and the body is eligible, NBenchmark times a single invocation, then doubles K - timing 1, 2, 4, 8, … invocations as one batch - until a batch spans at least the resolved sample-duration target. The resolved K is reused for warmup and measurement, and every reported timing divides the batch time by K to give a per-operation number.

The target comes from two things. `AutoTune.TargetSampleDurationNs` (**10 µs** by default) covers the fixed **timestamp-read overhead** (~10-30 ns, ~0.2% of 10 µs rather than ~1-3% of 1 µs), which would otherwise leak into the ±2.5% CI target. `AutoTune.MinQuantaPerSample` (**512** by default) then covers timer **quantization**, which no fixed nanosecond figure can: 10 µs is ~100,000 steps of a 1 ns clock but only ~240 of Apple Silicon's 41.667 ns timebase and ~100 of Windows QPC's 100 ns tick. NBenchmark measures the clock's effective resolution once per process and raises the target to `resolution × MinQuantaPerSample` when the configured value falls short, so quantization lands under 0.2% of a sample on every host instead of varying a thousand-fold between them.

| Host | Measured resolution | Resolved target | Quantization per sample |
| --- | --- | --- | --- |
| TSC-backed Linux | ~1-5 ns | 10 µs (unchanged) | <0.05% |
| Apple Silicon | ~41.7 ns | ~21 µs | ~0.19% |
| Windows QPC | ~100 ns | ~51 µs | ~0.19% |

The target is only ever raised, so a preset asking for more (`Thorough` uses 50 µs) is left alone. Bodies already spanning the resolved target keep K = 1, so their per-op tail visibility is unchanged. See [Timer resolution](#timer-resolution) for why quantization is worth this much trouble.

> [!NOTE] K > 1 batches change what percentiles mean
> When K > 1, each recorded sample is the mean of K back-to-back operations, so P95/P99/Max and the histogram describe **batch means**, not individual-operation tails - a slow individual op is averaged with its K-1 neighbors. For a sub-10 µs body the trade is deliberate (per-op timing at that scale is dominated by timer noise anyway); when you need per-op tail latency, pin `OpsPerSample = 1` and read the caveats in [Descriptive Statistics](./descriptive.md).

Calibration is skipped (K = 1) when `IterationSetup`/`IterationTeardown` is set, because a batch would no longer represent one isolated call. It is **not** skipped under the `Independent` profile: the forced Gen0 GC runs once per sample (the K-batch), before the timestamp and outside the timed window - the same semantics a pinned `OpsPerSample` gets - so nano-scale CPU bodies still amortize timer overhead. A pinned `OpsPerSample` is always honored. Calibration runs against the body's **cold** (pre-warmup) speed; see [Post-warmup recalibration](#post-warmup-recalibration) below for how K is re-derived once the body is warm.

### Phase B - Warmup (plateau detection)

If `WarmupIterations` is `null`, NBenchmark collects warmup samples in batches of `AutoTune.BatchSize` and tracks the best (fastest) batch mean seen so far. Once `AutoTune.PlateauPatience` consecutive batches fail to improve on the best by at least `AutoTune.WarmupEpsilon`, the code is considered warm and warmup stops - never before `AutoTune.MinWarmup` samples, never after `AutoTune.MaxWarmup`. A pinned `WarmupIterations` runs exactly that many warmup samples.

The plateau rule alone measures warmup in *iterations*, but a fast body plateaus in microseconds of wall-clock - long before the background JIT delivers tier-1 (and dynamic-PGO) code. Warmup would then settle on the stable-but-slow tier-0 plateau and the tier-1 switch would land mid-measurement as a step change, the dominant source of run-to-run variance on very fast benchmarks. Two extra gates prevent that:

- **Warmup time floor** (`AutoTune.MinWarmupTime`, default 500 ms; 1 s under `Thorough`): auto-warmup will not settle until it has accumulated at least this much in-body time, giving tiered compilation time to land. Set to `0` to disable. In practice this floor, not `MinWarmup` or `PlateauPatience`, determines warmup length for almost every body.

  **`Quick` does not shorten this floor.** It is a correctness requirement, not a speed/accuracy trade-off: a short floor does not give you a rougher number, it gives you a *confidently wrong* one. `Quick` gets its speed from a looser `CiTarget`, a lower `MinSamples`, and a shorter `MaxTuningTime` instead.
- **JIT-quiescence gate** (`AutoTune.RequireJitQuiescence`, default on, with `AutoTune.JitQuietPeriod`, default 50 ms): NBenchmark tracks when the runtime last compiled a method and keeps warmup open until that has been quiet for `JitQuietPeriod`, so an in-flight tier-1 promotion extends warmup rather than landing in the measurement. Disabling the time floor (`MinWarmupTime = 0`) or setting `JitQuietPeriod = 0` also disables this gate.

Both gates only *delay* settling past the plateau; `MaxWarmup` and the calibration+warmup budget share (below) still bound warmup from above, so a genuinely slow body is not held open by them.

Because a fast body needs tens of thousands of samples to accumulate 500 ms, `AutoTune.MaxWarmup` defaults to **100,000**, not the 10,000 that bounds a *pinned* `WarmupIterations`. Hitting the ceiling below the time floor raises a prominent warning and `BenchmarkResult.AutoTune.WarmupTimeFloorMet` records it.

See [The measurement engine: the warmup gates](../deep-dives/measurement-engine.md#the-warmup-gates) for why each gate is shaped the way it is - the call-counting delay the floor is derived from, why a per-batch JIT delta reads zero, and the clamps that stop either gate holding warmup open forever.

For slow bodies the configured `BatchSize` is shrunk based on the per-sample estimate from calibration: a body that takes seconds per sample warms in batches of 1 so the plateau rule can settle after `PlateauPatience + 1` samples instead of `(PlateauPatience + 1) × BatchSize` (subject to the `MinWarmup` floor, which with the default `MinWarmup = 8` is then the binding constraint). Without this shrink a 2 s body with the default `BatchSize = 8` would need `(PlateauPatience + 1) × BatchSize = 32` samples - 64 s of warmup - just to clear the plateau requirement.

Calibration and warmup share a budget: together they may consume at most `AutoTune.WarmupBudgetFraction` of `AutoTune.MaxTuningTime` (default 0.4 = 40%), reserving the remainder for measurement. This keeps slow bodies from spending the whole cap on warmup and leaving measurement with a single sample. When the share is exhausted, warmup stops at the wall-clock cap and a warning names the share.

If `ForceGcBeforeMeasurement` is true (the `Independent` profile), a full gen-2 GC runs after warmup to establish a clean heap baseline. Under `Realistic` (the default) this is skipped and the benchmark inherits the warmup's heap state. (This is a distinct knob from `ForceGcBetweenBenchmarks`, which runs a full GC *between* benchmarks and is on for both profiles.)

### Post-warmup recalibration

Ops-per-sample calibration (Phase A) resolves K against the body's **cold** code. Once warmup has driven the body to its steady-state (tiered / PGO-optimized) speed, the same K may span well under the target duration - re-exposing the fixed timer overhead calibration existed to amortize - so after auto-warmup settles, the loop re-derives K from the warm per-op estimate and bumps it to the next power of two that reaches the target. Recalibration only applies when calibration ran, only ever increases K, and records the gap between the cold and warm K on `BenchmarkResult.AutoTune.InitialOpsPerSample`. See [The measurement engine: post-warmup recalibration](../deep-dives/measurement-engine.md#post-warmup-recalibration) for the full rule.

### Phase C - Measurement (CI-width target)

If `Iterations` is `null`, NBenchmark streams measured samples and, every `AutoTune.BatchSize` samples, recomputes the confidence interval on the mean. Sampling stops once the interval's relative half-width falls below `AutoTune.CiTarget` (±2.5% by default) - never before `AutoTune.MinSamples`, never after `AutoTune.MaxSamples`. A pinned `Iterations` collects exactly that many samples. A per-benchmark `AutoTune.MaxTuningTime` wall-clock cap bounds the whole loop so a pathological body can never run away.

Stopping at the first sample count that meets the target is *optional stopping*, and the reported interval is not corrected for it. Read the stop reason alongside the margin - see [The measurement engine: optional stopping](../deep-dives/measurement-engine.md#the-optional-stopping-correction).

Two gates sit on that stop rule, mirroring the warmup settle gates. The CI rule decides whether the interval is *narrow enough*; the gates decide whether it is *honest to stop*.

- **Measurement time floor** (`AutoTune.MinMeasurementTime`, default 100 ms; 50 ms under `Quick`, 500 ms under `Thorough`): measurement will not stop on the CI target until it has spanned this much in-body time. This is what makes the sample count scale with how cheap the body is. A flat `MinSamples` is blind to that - the same 30 samples cost 9 s on a 300 ms body and 0.5 ms on a 1 µs body, where thousands of samples are essentially free and buy meaningful percentiles, a usable histogram, and a significance test with real power. (At n ≈ 16 the reported P95, P99 and P99.9 all collapse onto the maximum.)

  The rule is simply: measurement spans at least this long, or reaches `AutoTune.MaxSamples` samples, whichever comes first. So worst-case added cost is `MinMeasurementTime` per benchmark, and it is **exactly zero** for any body already slower than `MinMeasurementTime / MinSamples` (≈3.3 ms by default), where `MinSamples` binds and nothing changes. Set to `0` to stop on `MinSamples` alone.
- **Steady-state (drift) gate** (`AutoTune.MeasurementDriftTolerance`, default 0.10): when the CI rule wants to stop, NBenchmark compares the mean of the first half of the collected samples against the mean of the second half, and refuses the stop if they disagree both *relatively* (by more than the tolerance, measured against the smaller half-mean) and *statistically* (by more than 4 standard errors of the difference).

  > [!CAUTION] If you hit a `driftUnresolved` stop
  > **Land the transition during warmup instead:** raise `--min-warmup-time <ms>` (default 500) so a JIT tier-up or dynamic-PGO re-optimization lands inside warmup, not measurement.
  > **Accept non-stationarity as the finding:** `--launch-count 5` measures the across-launch spread, which is the honest signal for a body that genuinely does not have a steady state.

  This guards the failure mode that is hardest to notice. A JIT tier-up - or a thermal ramp, or a filling cache - landing inside the measurement window produces a step change, and a CI-on-the-mean rule will happily report a tight interval straight across it. That is how a benchmark ends up 10× wrong with a ±0.9% error bar, looking more trustworthy than a correct result.

  Both conditions are required. A bare relative rule false-positives forever on a heavy-tailed body whose half-means differ by pure sampling noise; a bare significance rule flags sub-percent drift once *n* reaches the thousands. On a refusal the loop discards **all** samples collected so far and starts measurement over, up to `AutoTune.MeasurementRestartLimit` times (default 2 - one for tier-0→tier-1, one for instrumented→optimized under dynamic PGO). Restarts draw on the same `MaxTuningTime` budget as ordinary sampling, so they can never make a benchmark run longer. Exhausting the limit reports `SampleStopReason.DriftUnresolved` with a warning; set the tolerance to `0` to disable the gate. Either way `BenchmarkResult.AutoTune.SplitHalfDrift` records the gap, on every stop - including pinned-count and cap stops that never consult the gate - so a tight interval sitting next to a large drift is visible rather than silently trusted. See [The measurement engine: the drift gate and the cap](../deep-dives/measurement-engine.md#the-drift-gate-and-the-cap) for the implementation.

`AutoTune.MaxSamples` defaults to **5,000** (2,000 under `Quick`, 20,000 under `Thorough`). At 5,000 the CI rule still reaches ±2.5% for any body with a coefficient of variation up to roughly 90%. Past that the required count grows as `(t × CV / target)²` and runs away - a CV of 580% needs about 50,000 samples just to reach ±5% - but a body that noisy has variance that *is* the finding, and more samples only buy a tighter interval around an unstable centre. The ceiling warning therefore names the measured CV and the count convergence would actually take, and points at `--launch-count` as the more honest signal.

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

The CI-width stop rule evaluates the **raw** (untrimmed) sample stream as it arrives. After the loop ends, the collected per-op samples pass through [outlier trimming](./outliers.md), and the reported statistics - including the Error column - describe the **trimmed** set. So `AutoTune.AchievedRelativeCiWidth` is the raw stop value while `MarginOfError` is the reported interval on the trimmed mean.

They answer different questions, and the reported one is deliberately the narrower:

| Number | Describes | Read it when |
|---|---|---|
| `MarginOfError` | How precisely the **trimmed mean** is known - the [Winsorized interval](./descriptive.md#after-outlier-trimming-the-winsorized-standard-error), which accounts for the samples the fence removed rather than dropping their variance | You want an error bar on the reported number |
| `AutoTune.AchievedRelativeCiWidth` | How wide the interval on the **raw mean** got before the loop stopped | You want to know whether the machine was quiet enough for the measurement to converge |
| `Max`, `P99`, `P99.9` | The raw distribution's actual tail (they read the pre-trim set by default) | You want to know how slow the slow samples really were |

**The Winsorized interval closes part of the gap and is not meant to close all of it.** `MarginOfError` is the Winsorized interval on the trimmed mean, not a plain t-interval on the kept samples alone: it keeps every sample and clamps the trimmed ones, so it widens by the amount that was trimmed rather than describing a run that produced only the inliers. It does not widen by the outliers' *magnitude*, on purpose: an interval on the trimmed mean that moved when a discarded sample moved would not be an interval on the trimmed mean. So a body that reports `MarginOfError` at ±1.3% next to an `AchievedRelativeCiWidth` of `1.05` (±105%) still reports a far tighter margin than that, and the honest reading is unchanged:

> A tight Error column is trustworthy evidence that the *measurement converged* only when `SampleStop` is `CiTargetMet`. Read the stop reason before the margin, and the raw percentiles beside it.

If you want the reported number and its interval to describe the whole distribution, tail and all, set `OutlierMode.None` (`--outlier none`) - then nothing is trimmed, the Winsorized interval reduces to the plain one, and every statistic is on the same raw set.

### What the loop decided

Every measured result carries an `AutoTune` diagnostic (`BenchmarkResult.AutoTune`) recording the resolved K, warmup length, sample count, why each phase stopped, the achieved CI half-width and its convergence trace, the wall-clock time spent tuning, the pre-flight jitter metric, whether the outlier detector was auto-switched, the drift and restart counters, and the measured clock resolution with the sample duration and quantization floor derived from it. Reporters surface it as an `auto-tuned: …` line (console, Markdown), dedicated columns (CSV advanced), or an `autoTune` object (JSON). It is `null` on dry-run and errored results.

It also records what warmup observed about tiered compilation - see [the warmup curve](#the-warmup-curve).

Two of its fields exist specifically to catch a tight interval around a number that will not reproduce, which is the hardest failure in benchmarking to see from the interval alone. **`WarmupTimeFloorMet`** covers a body measured before tiered compilation finished; **`SampleQuantizationFraction`** covers a body measured more finely than the timer can resolve. Neither is visible in the margin of error, which is exactly why each is reported separately. A third cause - genuine between-process variance - is not a property of one measurement at all and needs [multiple launches](../features/multiple-launches.md#reading-the-reproducibility-warning) to see.

### The warmup curve

The [warmup gates](#phase-b---warmup-plateau-detection) decide *when* warmup may end. The diagnostic additionally retains what warmup *saw*, which is the only surviving record of the body tiering up: raw warmup timings are never persisted, and `RawSamples` covers the measurement phase only. `AutoTune.WarmupCurve` is the mean per-op time of each warmup batch, oldest first - a tier-0 → tier-1 promotion, and instrumented → optimized under dynamic PGO, each appear as a step down - alongside `JitLastChangeAtNs` (how far into warmup the compiled-method count last moved) and the `WarmupJit*` counters (`System.Runtime.JitInfo` deltas across warmup). The `WarmupJit*` counters are process-wide, not per-benchmark: in an in-process run the first benchmark to execute absorbs the bulk of startup compilation, and since [benchmarks run in random order by default](../faq.md#can-i-run-benchmarks-in-source-order-instead-of-random-order) that is a significant part of why the same benchmark's warmup differs between runs. `WarmupCurve` and `JitLastChangeAtNs` are per-benchmark - that benchmark's own warmup batches and its own quiet-period measurement. Use `--order declaration` (or `--seed` for a reproducible shuffle) if you need the JIT cost to fall in the same place every time.

For the full field-by-field reference, including the decimation rule and the `MethodLoadVerbose` caveat, see the [JSON reporter's `autoTune` object](../output/json-reporter.md#the-autotune-object).

## Measurement profiles

NBenchmark provides two measurement profiles that control how GC interacts with the measurement loop:

- **`Realistic`** (the default) - no per-iteration Gen0 GC, no pre-measurement full GC (the warmup heap is inherited). Numbers reflect what the same code does in production, including natural GC pauses and CPU cache effects.
- **`Independent`** (opt-in) - force Gen0 GC before every sample, run a full GC after warmup before measurement. Useful for pure-CPU measurements, cryptographic algorithms, numeric kernels, and other cases where iteration-to-iteration independence is more important than ecological validity.

Two behaviors are on for **both** profiles: the between-benchmark full GC (so one benchmark's leftover heap cannot bias the next) and allocation tracking (sampled outside the timed window, so it costs nothing and surfaces the "this pure-CPU body actually allocates" signal even under `Independent`). Disable them with `--no-gc-between-benchmarks` / `--no-allocations` if needed.

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

Each behavior can be overridden individually:

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

NBenchmark uses `System.Diagnostics.Stopwatch`, which wraps the platform's high-resolution performance counter. The **advertised** resolution is printed at the start of each `BenchmarkHarness` run:

```text
Timer resolution: 1,000,000,000 ticks/s (1.00 ns per tick)
```

That figure is `Stopwatch.Frequency`, and it can be badly wrong. It is what the runtime says the counter *converts to*, not the granularity at which the counter actually advances. On Apple Silicon `Stopwatch.Frequency` reports 1 GHz while the underlying `mach_absolute_time` timebase runs at 24 MHz, so the counter only ever moves in steps of **41.667 ns** - the printed "1.00 ns per tick" is out by more than forty-fold.

So NBenchmark does not trust the advertised rate. It **measures** the clock's effective resolution once per process (`Engine/Detectors/ClockResolutionProbe`) by spinning until the reported elapsed time first becomes non-zero, and takes the minimum over several attempts. The result lands on `AutoTune.ClockResolutionNs` and drives the sample-duration floor described in [Phase A](#phase-a---ops-per-sample-calibration-k).

Per-iteration timings are computed directly from raw `Stopwatch` ticks - deliberately **not** via `TimeSpan`, whose ticks are always 100 ns. That preserves whatever resolution the platform genuinely offers; round-tripping through `TimeSpan` would quantize every sample to a multiple of 100 ns and record sub-100 ns operations as zero.

Why quantization is worth this much trouble - the asymmetry that makes it dangerous, the measured 21× run-to-run spread behind a ±0.027% margin, and the warning that reports the floor - is covered in [The measurement engine: why quantization is worse than it looks](../deep-dives/measurement-engine.md#why-quantization-is-worse-than-it-looks).

> [!NOTE] Timer-call overhead
> Each sample includes the cost of one timestamp read (typically ~10-30 ns).
> Ops-per-sample calibration (Phase A above) amortizes this across K invocations
> for fast bodies, so the per-op number stays meaningful even in the low-nanosecond
> range. When K is pinned to 1 - or when setup/teardown forces it - the read cost is
> a fixed addend on every sample, so treat absolute values at that scale as upper
> bounds and compare against a baseline measured the same way.

## The host drift canary

Every drift check described above looks inside **one** benchmark's own sample stream. That is the
wrong shape for the noise that matters most across a table: a laptop warming up, a build starting
in another terminal, a co-tenant arriving on a CI runner. Those move every benchmark measured
afterwards and none measured before, so each row stays internally consistent - tight interval,
clean split-half, no restarts - while every comparison between them quietly stops meaning what it
says.

So NBenchmark measures the machine as well as the code. A fixed, deterministic control workload
runs at every benchmark boundary: once before the first benchmark, once after each one, once after
the last. The work never changes, so a change in how long it takes is a change in the host.

Each result carries the readings taken either side of it, on `BenchmarkResult.HostTimeline`:

| Field | What it is |
| --- | --- |
| `BeforeNs` / `AfterNs` | The readings bracketing this benchmark. Absolute nanoseconds of an arbitrary amount of work - only their ratios mean anything. |
| `RelativeToRunStart` | The bracketing mean as a multiple of the run's first reading. `1.07` = the same work took 7% longer here than at the start of the run. |
| `Position` | How many benchmarks had completed when this one started. |

`RelativeToRunStart` is the number to compare between rows, and comparing it is what the feature is
for. When the difference reported between a benchmark and the baseline is **smaller than the
distance the host moved between the two points at which they were measured**, the candidate's row
carries a warning:

```
host drift exceeds the difference being reported: the machine was 8% slower when 'Candidate' was
measured than when 'Baseline' was, and the 3% difference between them is smaller than that.
```

It warns and never downgrades the verdict. `MinimumPracticalEffect` and `MinimumRelativeShift`
change a verdict because they are statements about the comparison itself; the canary is a statement
about the machine, measured by a different workload at a different moment. That is indirect
evidence about your comparison, and it belongs in your hands rather than in the ✓ column.

Readings are taken between benchmarks, never inside a timed window, and one costs a fraction of a
millisecond - so a twenty-benchmark run pays for the canary in microseconds and it cannot affect
accuracy. It is on by default, skipped entirely on a dry-run, and switched off with
`--no-drift-canary` or `.WithDriftCanary(false)`. To keep it but change when it speaks up, set
`DriftCanaryOptions.MinimumReportableDrift` (default 1%) - see
[DriftCanary](../reference/configuration.md#driftcanary).

> [!NOTE]
> With more than one launch, each launch is a fresh worker with its own first reading, so the row
> reports the mean of its launches' `RelativeToRunStart`. Raising `--launch-count` is also the
> straightest remedy when the warning fires: it measures the two benchmarks near each other several
> times over, so the drift becomes something the replicates average over rather than something one
> ordering baked in.

## Reducing noise at the source

The adaptive loop, [outlier trimming](./outliers.md) (including the [bimodal warning](./outliers.md#bimodal-distribution-warning)), and [significance testing](./significance.md) all work around OS noise statistically - they discard or down-weight samples that look like interference, inferred from the timing value alone. [Evidence-based interference rejection](./outliers.md#evidence-based-interference-rejection) is the one exception: it reads the measuring thread's own CPU occupancy and discards a sample the OS is *known* to have preempted, as a fact rather than a guess, before the statistical rules ever see it. On by default; opt out with `--no-interference-filter`.

None of this removes noise that is baked into every sample, such as a benchmark thread migrating between cores (cold-cache stalls on every migration) or a normal-priority process on a busy host. NBenchmark provides **environment controls** - CPU affinity, process priority, thread placement, and dedicated-host guidance (thread control on by default; the rest opt-in) - that reduce this noise before the timer starts. See [Environment control](../features/environment-control.md) for the full model, platform notes, and isolated-process propagation.
