---
title: Allocation Measurement
description: How NBenchmark samples per-iteration heap allocation using GC counters.
order: 2
---

# Allocation Measurement

When `MeasureAllocations = true`, each iteration records:

```
beforeThreadId    = CurrentManagedThreadId
beforeThreadBytes = GC.GetAllocatedBytesForCurrentThread()
beforeProcess     = GC.GetTotalAllocatedBytes()
// action runs
if CurrentManagedThreadId == beforeThreadId:
   allocations[i] = Max(0, GC.GetAllocatedBytesForCurrentThread() - beforeThreadBytes)
else:
   allocations[i] = Max(0, GC.GetTotalAllocatedBytes() - beforeProcess)
```

The reported `MeanAllocatedBytes` is the arithmetic mean across all iterations. This includes any allocations made by the benchmark framework itself that appear between the two reads - in practice, for simple benchmarks, this is usually negligible.

In synchronous benchmarks this is thread-local (`GC.GetAllocatedBytesForCurrentThread`) and does not include allocations from other threads. In async benchmarks, if the continuation hops threads, NBenchmark falls back to process-wide delta for that sample, which can include background allocation noise.

## What the harness itself contributes

Nothing on the measured path - and that took work to be true rather than being free.

Discovery used to reach a `[Benchmark]` method through a `Func<object, object?>`. One uniform delegate type is convenient, and it boxed the result of every value-returning benchmark method once per operation: the four bodies in `samples/Harness` are constant returns that allocate nothing, and each of them reported **24 B/op**. That is the harness's allocation, printed in the user's column.

A benchmark body is now bound to a delegate carrying the method's own signature - `Func<int>` for `int Compute()`, not `Func<object>` - and its return value is stored in a sink closed over that same type. A value-returning benchmark that allocates nothing now reports `0 B`.

**Numbers measured before this changed are not comparable with numbers measured after.** On the `samples/Harness` calibration set the per-operation allocation went from 24 B to 0 B and the median from ~9.3 ns to ~2.5 ns - none of that difference being the benchmarked code. Discard stored baselines that predate it.
