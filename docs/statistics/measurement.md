---
title: Measurement
description: How NBenchmark's measurement loop works, including timer resolution and per-iteration overhead.
order: 1
---

# Measurement

## The measurement loop

For each benchmark, NBenchmark runs the following sequence:

1. **Warmup** - run the action `WarmupIterations` times (default: 25) without recording timings.
2. **Post-warmup GC** - if `ForceGcBetweenBenchmarks` is true (the `Independent` profile), force a full gen-2 GC collection to establish a clean heap baseline. Under the `Realistic` profile (the default), this step is skipped and the benchmark inherits the warmup's heap state.
3. **Measurement loop** - for each of the `Iterations` (default: 200) measured runs:
   - If `ForceGcBeforeEachIteration` is true (the `Independent` profile), force a gen-0 collection.
   - Call `iterationSetup` if provided.
   - Record `Stopwatch.GetTimestamp()`.
   - Invoke the benchmark action.
   - Read the timestamp again and convert the raw tick delta to nanoseconds at the timer's **native resolution** (`delta × 10⁹ / Stopwatch.Frequency`).
   - Record allocation delta if `MeasureAllocations` is true (the `Realistic` profile).
   - Call `iterationTeardown` if provided.

**Important:** the timer is read immediately after the action returns, before teardown runs. Teardown time is not included in the measurement.

The raw timing data (in nanoseconds) is stored in a `double[]` of length `Iterations`.

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
> For bodies in the low-nanosecond range this overhead is a meaningful fraction
> of the measurement, so treat absolute values at that scale as upper bounds and
> prefer comparing benchmarks against a baseline measured the same way.
