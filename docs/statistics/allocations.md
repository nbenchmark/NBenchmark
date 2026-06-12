---
title: Allocation Measurement
description: How NBenchmark samples per-iteration heap allocation using GC counters.
order: 5
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
