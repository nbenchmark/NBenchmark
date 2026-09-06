---
title: Allocation Measurement
description: What the Alloc/op column measures, how it is sampled, what it excludes, and how to handle non-zero results.
order: 2
---

# Allocation Measurement

The **Alloc/op** column reports the mean number of bytes your benchmark allocated on the managed heap per operation. This is collected on every run by default, regardless of the [measurement profile](./measurement.md#measurement-profiles).

Because the allocation counters are read outside the timed window, tracking has no impact on timing accuracy. Allocation is often more actionable than timing; while a timing difference tells you that code is slower, an allocation difference often explains *why* and points to a specific line of code.

## What the number means

`Alloc/op` represents **bytes allocated**, not bytes retained. It counts every allocation the operation makes, regardless of whether the object survives until the end of the operation. For example, a method that allocates a 1 KiB buffer and immediately drops it reports 1 KiB per operation.

This metric is critical for benchmarking because the allocation rate - rather than the live-set size - drives garbage collection frequency. Code with zero allocations in a hot path cannot trigger a GC, making its latency predictable.

## How it is sampled

Each measured sample reads a GC allocation counter before and after the body runs:

| Step | Action |
|---|---|
| 1 | Forced Gen0 collection (if the `Independent` profile is active) |
| 2 | `IterationSetup` (if configured) |
| 3 | **Allocation counter read (before)** |
| 4 | Timestamp read |
| 5 | The body runs K times |
| 6 | Elapsed time read |
| 7 | **Allocation counter read (after)** |
| 8 | `IterationTeardown` (if configured) |

Two important consequences follow from this sequence:
- **Reads are outside the timed window.** The timestamp is taken after the first read and the elapsed time is computed before the second read. Counter access never inflates a timing, which is why allocation tracking is enabled by default even under the `Independent` profile.
- **Setup and teardown are excluded.** `IterationSetup` and `IterationTeardown` run outside the bracketed reads, so their allocations are not attributed to your benchmark body.

Only the measurement phase is sampled; warmup allocations are not recorded.

## Per-operation division

The counters bracket the entire batch of **K** operations (see [ops-per-sample calibration](./measurement.md#phase-a---ops-per-sample-calibration-k)). NBenchmark calculates the per-sample value by dividing the batch delta by K using **integer division**.

If a body's true allocation is smaller than one byte per operation on average (for example, 100 bytes spread across a batch of K = 4096), the sample records `0` rather than a fraction.

> [!NOTE]
> `Alloc/op: 0 B` means "less than one byte per operation on average," not necessarily "provably zero allocations." For most bodies, this distinction is irrelevant because an allocating body usually allocates at least one whole object per call. If you need certainty for a body that allocates rarely, pin `OpsPerSample = 1` so each sample brackets a single operation.

## Thread-local vs. process-wide counters

NBenchmark prefers `GC.GetAllocatedBytesForCurrentThread`, which is thread-local and immune to allocations by unrelated threads in your process.

However, if a sample finishes on a **different managed thread** than it started on (common for `async` bodies where a continuation is resumed by a different thread-pool thread), the thread-local delta is meaningless. In these cases, NBenchmark falls back to a process-wide delta from `GC.GetTotalAllocatedBytes`.

The fallback is correct but noisier, as a process-wide counter includes allocations from any other thread running during the sample. On a busy host, `async` benchmarks may show slightly inflated and more variable allocation figures for this reason. Both deltas are clamped at zero.

## Framework overhead

Any allocation occurring between the two reads is counted, including allocations by the framework itself on that thread. In practice, this is negligible for most benchmarks - the code between the reads is a loop over a typed delegate, which does not allocate. However, you should read the figure as a comparison between benchmarks rather than an absolute audit of your method.

## Reported fields

Allocation statistics are computed from the **raw, untrimmed** sample set. Timing [outlier trimming](./outliers.md) removes timing samples but does not remove their allocation counterparts. Consequently, a GC pause trimmed out of the median still contributes its allocation to these figures.

| Field | Meaning | Location |
|---|---|---|
| `MeanAllocatedBytes` | Arithmetic mean per operation (truncated to a whole number of bytes) | `Alloc/op` column, all detail levels |
| `AllocMedian` | Median (P50) per operation | Advanced detail, CSV, JSON |
| `AllocP95` | 95th percentile per operation ([nearest-rank](./descriptive.md#percentiles)) | Advanced detail, CSV, JSON |
| `AllocMax` | Largest per-operation value recorded by any single sample | Advanced detail, CSV, JSON |

`MeanAllocatedBytes` is `null` if tracking is disabled or the benchmark errors; the other three are absent unless the run produced statistics.

A **median of 0 with a non-zero mean and a large max** typically indicates that most operations allocate nothing, but a minority allocate significantly. This is a common signature of growing buffers, cache-miss paths, or lazily initialized fields.

At Advanced detail, the breakdown appears under the benchmark:

```text
Allocations:
  Mean: 1.2 KiB
  P50:  0 B
  P95:  8.0 KiB
  Max:  32.0 KiB
```

### Across multiple launches

With [`LaunchCount > 1`](../features/multiple-launches.md), NBenchmark combines per-launch figures: the mean of the per-launch means, the median of the per-launch medians and P95s, and the maximum of the per-launch maxima.

## Acting on non-zero results

If `Alloc/op` is higher than expected, consider these common causes:

| Cause | Typical signature | Fix |
|---|---|---|
| **Boxing** | Small, fixed size per op (often 24 B) | Avoid `object` and non-generic interfaces on value types; check for value types reaching a `params object[]` or a non-generic comparer. |
| **LINQ** | Fixed cost per op, scaling with the number of operators | Replace hot chains with loops; LINQ allocates an enumerator per operator and a closure per capturing lambda. |
| **Closures** | Small fixed cost when a lambda captures a local | Make the lambda `static` or pass the state as an argument. |
| **String work** | Scales with output length | Use `string.Create`, a pooled `StringBuilder`, or interpolated string handlers. |
| **Arrays and buffers** | Large, often bimodal with a zero median | Use `ArrayPool<T>.Shared`, or `stackalloc` / `Span<T>` for small fixed sizes. |
| **`async` state machines** | Fixed cost on paths that go asynchronous | Use `ValueTask` for frequently-synchronous paths; avoid `async` on methods that rarely await. |

**Tips for troubleshooting:**
- **Compare, don't audit.** Put the before and after versions in one [suite](../usage-modes/suite-mode.md) and read the difference. This cancels out any framework overhead.
- **Watch the timing.** Removing allocations usually improves latency, but pooling adds bookkeeping that can be slower for small buffers. The `Alloc/op` column and the median move independently.

## Turning it off

Allocation tracking is enabled by default. To disable it, use the CLI:

```bash
dotnet run -- --no-allocations
```

Or in code:

```csharp
options with { MeasureAllocations = false }
```

## See also

- [Measurement](./measurement.md) - The sample loop these reads are bracketed into.
- [Diagnostics](./diagnostics.md) - GC collection counts, showing how the collector responds to these allocations.
- [Descriptive statistics](./descriptive.md) - The percentile convention used for `AllocP95`.
- [Reading your results](../getting-started/reading-your-results.md) - The `Alloc/op` column in context.
