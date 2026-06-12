---
title: Measurement
description: How NBenchmark's measurement loop works, including timer resolution and per-iteration overhead.
order: 1
---

# Measurement

## The measurement loop

For each benchmark, NBenchmark runs the following sequence:

1. **Warmup** - run the action `WarmupIterations` times (default: 25) without recording timings.
2. **Post-warmup GC** - force a full gen-2 GC collection to establish a clean heap baseline.
3. **Measurement loop** - for each of the `Iterations` (default: 200) measured runs:
   - If `ForceGcBeforeEachIteration` is true, force a gen-0 collection.
   - Call `iterationSetup` if provided.
   - Record `Stopwatch.GetTimestamp()`.
   - Invoke the benchmark action.
   - Read the timestamp again and convert the raw tick delta to nanoseconds at the timer's **native resolution** (`delta × 10⁹ / Stopwatch.Frequency`).
   - Record allocation delta if `MeasureAllocations` is true.
   - Call `iterationTeardown` if provided.

**Important:** the timer is read immediately after the action returns, before teardown runs. Teardown time is not included in the measurement.

The raw timing data (in nanoseconds) is stored in a `double[]` of length `Iterations`.

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
