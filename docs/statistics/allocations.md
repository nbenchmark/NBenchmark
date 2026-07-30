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

Nothing on the measured path.

A benchmark body is bound to a delegate carrying the method's own signature - `Func<int>` for `int Compute()`, not a uniform `Func<object>` - and its return value is stored in a sink closed over that same type. A value-returning benchmark that allocates nothing reports `0 B/op`; the harness does not box returns or add delegate hops during timing.
