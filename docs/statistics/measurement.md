---
title: Measurement
description: How NBenchmark's measurement loop works, including timer resolution and per-iteration overhead.
order: 1
---

# Measurement

## The measurement loop

NBenchmark uses an **adaptive streaming loop**. Rather than running a fixed number of iterations, it resolves three dimensions at runtime - how many invocations to time per sample (**K**), how long to warm up, and how many measured samples to collect - and stops each as soon as it has enough. Every dimension can be pinned to an exact value (see [Configuration](../reference/configuration.md)); pinning all three reproduces a classic fixed-count run.

For each benchmark the loop runs in three phases:

### Phase 1 - Ops-per-sample calibration (K)

If `OpsPerSample` is `null` (the default) and the body is eligible, NBenchmark times a single invocation, then doubles K - timing 1, 2, 4, 8, … invocations as one batch - until a batch spans at least `AutoTune.TargetSampleDurationNs` (1 µs by default). The resolved K is reused for warmup and measurement, and every reported timing divides the batch time by K to give a per-operation number.

Calibration is skipped (K = 1) when `IterationSetup`/`IterationTeardown` is set or `ForceGcBeforeEachIteration` is on, because a batch would no longer represent one isolated call. A pinned `OpsPerSample` is always honoured.

### Phase 2 - Warmup (plateau detection)

If `WarmupIterations` is `null`, NBenchmark collects warmup samples in batches of `AutoTune.BatchSize` and tracks the best (fastest) batch mean seen so far. Once `AutoTune.PlateauPatience` consecutive batches fail to improve on the best by at least `AutoTune.WarmupEpsilon`, the code is considered warm and warmup stops - never before `AutoTune.MinWarmup` samples, never after `AutoTune.MaxWarmup`. A pinned `WarmupIterations` runs exactly that many warmup samples.

If `ForceGcBetweenBenchmarks` is true (the `Independent` profile), a full gen-2 GC runs after warmup to establish a clean heap baseline. Under `Realistic` (the default) this is skipped and the benchmark inherits the warmup's heap state.

### Phase 3 - Measurement (CI-width target)

If `Iterations` is `null`, NBenchmark streams measured samples and, every `AutoTune.BatchSize` samples, recomputes the confidence interval on the mean. Sampling stops once the interval's relative half-width falls below `AutoTune.CiTarget` (±2.5% by default) - never before `AutoTune.MinSamples`, never after `AutoTune.MaxSamples`. A pinned `Iterations` collects exactly that many samples. A per-benchmark `AutoTune.MaxTuningTime` wall-clock cap bounds the whole loop so a pathological body can never run away.

Each measured sample does the following:

- If `ForceGcBeforeEachIteration` is true (the `Independent` profile), force a gen-0 collection.
- Call `IterationSetup` if provided.
- Record `Stopwatch.GetTimestamp()`.
- Invoke the benchmark action K times.
- Read the timestamp again and convert the raw tick delta to nanoseconds at the timer's **native resolution** (`delta × 10⁹ / Stopwatch.Frequency`), then divide by K.
- Record the allocation delta (divided by K) if `MeasureAllocations` is true (the `Realistic` profile).
- Call `IterationTeardown` if provided.

**Important:** the timer is read immediately after the K-batch returns, before teardown runs. Teardown time is not included in the measurement.

### Raw vs. trimmed statistics

The CI-width stop rule evaluates the **raw** (untrimmed) sample stream as it arrives. After the loop ends, the collected per-op samples pass through [outlier trimming](./outliers.md) and the reported statistics - including the Error column - are computed on the **trimmed** set. The two confidence intervals are close but need not be identical: the diagnostic's `AchievedRelativeCiWidth` reflects the raw stop value, while the reported interval reflects the trimmed result.

### What the loop decided

Every measured result carries an `AutoTune` diagnostic (`BenchmarkResult.AutoTune`) recording the resolved K, warmup length, sample count, why each phase stopped, the achieved CI half-width, and the wall-clock time spent tuning. Reporters surface it as an `auto-tuned: …` line (console, Markdown), dedicated columns (CSV advanced), or an `autoTune` object (JSON). It is `null` on dry-run and errored results.

## Measurement profiles

NBenchmark provides two measurement profiles that control how GC interacts with the measurement loop:

- **`Realistic`** (the default) - no per-iteration Gen0 GC, no between-benchmark full GC, allocation tracking on. Numbers reflect what the same code does in production, including natural GC pauses and CPU cache effects.
- **`Independent`** (opt-in) - force Gen0 GC before every iteration, force full GC between benchmarks, no allocation tracking. Useful for pure-CPU measurements, cryptographic algorithms, numeric kernels, and other cases where iteration-to-iteration independence is more important than ecological validity.

### Worked example

Consider a benchmark body that allocates 100 KB per call:

```csharp
BenchmarkSuite.Create("AllocPressure")
    .Add("alloc", () => _ = new byte[100_000])
    .RunAsync();
```

Under the **Realistic** profile (the default), the variance (CV%) is high and some iterations show Gen0-GC stalls. The `Alloc/op` column is populated and shows the allocation pressure. The numbers reflect what this code would do in production.

Under the **Independent** profile (`--profile independent`), the variance is low and the per-iteration numbers are tightly clustered. The `Alloc/op` column is empty by default. The numbers answer a narrower question: "how much CPU time does this take, ignoring GC and cache?"

### Setting the profile

```csharp
// In code (BenchmarkHost)
await BenchmarkHost.Create(args)
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

// Disable allocation tracking under Realistic
options with { MeasureAllocationsOverride = false }
```

CLI equivalents:

```bash
dotnet run -- --profile realistic --force-gc
dotnet run -- --profile realistic --no-allocations
```

### Timer resolution

NBenchmark uses `System.Diagnostics.Stopwatch`, which wraps the platform's high-resolution performance counter. The resolution is printed at the start of each `BenchmarkHost` run:

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
