---
title: Allocation Measurement
description: What the Alloc/op column measures, how it is sampled, what it excludes, and what to do about a non-zero result.
order: 2
---

# Allocation Measurement

The **Alloc/op** column reports the mean number of bytes your benchmark allocated on the managed
heap per operation. It is collected on every run by default, under both
[measurement profiles](./measurement.md#measurement-profiles), and costs nothing in timing accuracy -
the counters are read outside the timed window.

Allocation is often the more actionable of the two numbers a benchmark reports. A timing difference
tells you this code is slower; an allocation difference tells you *why*, and usually points at a
specific line.

## What the number means

`Alloc/op` is **bytes allocated**, not bytes retained. It counts every allocation the operation
made, whether or not the object survived to the end of it. A method that allocates a 1 KiB buffer
and drops it immediately reports 1 KiB per op.

That is the number you want for benchmarking, because allocation rate - not live-set size - is what
drives collection frequency. Zero allocations in a hot path means the code cannot trigger a GC, so
its latency is predictable.

## How it is sampled

Each measured sample reads a GC allocation counter before and after the body runs:

| Step | What happens |
| --- | --- |
| 1 | A forced Gen0 collection, if the `Independent` profile is active |
| 2 | `IterationSetup`, if configured |
| 3 | **Allocation counter read (before)** |
| 4 | Timestamp read |
| 5 | The body runs, K times |
| 6 | Elapsed time read |
| 7 | **Allocation counter read (after)** |
| 8 | `IterationTeardown`, if configured |

Two consequences follow from where steps 3 and 7 sit.

**The reads are outside the timed window.** The timestamp is taken after the "before" read and the
elapsed time is computed before the "after" read, so counter access can never inflate a timing. This
is why allocation tracking is on by default even under the `Independent` profile, whose whole point
is timing purity.

**Setup and teardown are excluded.** `IterationSetup` runs before the "before" read and
`IterationTeardown` after the "after" read, so whatever they allocate is not attributed to your body.

Only the measurement phase is sampled. Warmup allocations are not recorded.

## Per-operation division

The counters bracket the entire batch of **K** operations (see
[ops-per-sample calibration](./measurement.md#phase-a---ops-per-sample-calibration-k)), so the
recorded per-sample value is the batch delta divided by K.

That division is **integer division**. A body whose true allocation is smaller than one byte per
operation on average - for example 100 bytes spread across a K = 4096 batch - records `0` for that
sample rather than a fraction.

> [!NOTE]
> `Alloc/op: 0 B` therefore means "less than one byte per operation on average", not necessarily
> "provably zero allocations". For most bodies the distinction never matters, because an allocating
> body allocates at least a whole object per call. If you need certainty on a body that allocates
> rarely, pin `OpsPerSample = 1` so each sample brackets a single operation. The other reported
> fields do not help here - they are computed from the same already-divided per-sample values, so a
> batch that truncated to `0` is `0` in `AllocMax` too.

## Thread-local, with a process-wide fallback

The preferred counter is `GC.GetAllocatedBytesForCurrentThread`, which is thread-local and therefore
immune to allocation by unrelated threads in your process.

If the sample finishes on a **different managed thread** than it started on - the normal case for an
async body whose continuation is resumed by a different thread-pool thread - the thread-local delta
would be meaningless, so NBenchmark falls back to a process-wide delta from
`GC.GetTotalAllocatedBytes` for that sample.

The fallback is correct but noisier: a process-wide counter includes allocation from any other
thread that ran during the sample. On a busy host, async benchmarks can show slightly inflated and
more variable allocation figures for this reason. Both deltas are clamped at zero, so a counter that
appears to move backwards records `0` rather than a negative number.

## What is included that you did not write

Any allocation that lands between the two reads is counted, including allocation by the framework
itself on that thread. In practice this is negligible for ordinary benchmarks - the code between the
reads is a loop over a typed delegate, which does not allocate - but it is why the figure is best
read as a comparison between benchmarks rather than as an absolute audit of your method.

## Reported fields

Allocation statistics are computed from the **raw, untrimmed** sample set. Timing
[outlier trimming](./outliers.md) removes timing samples; it does not remove their allocation
counterparts, so a GC pause trimmed out of the median still contributes its allocation to these
figures.

| Field | Meaning | Where it appears |
| --- | --- | --- |
| `MeanAllocatedBytes` | Arithmetic mean per operation, truncated to a whole number of bytes | The `Alloc/op` column, every detail level |
| `AllocMedian` | Median (P50) per operation | Advanced detail, CSV, JSON |
| `AllocP95` | 95th percentile per operation, [nearest-rank](./descriptive.md#percentiles) | Advanced detail, CSV, JSON |
| `AllocMax` | Largest per-operation value recorded by any single sample | Advanced detail, CSV, JSON |

`MeanAllocatedBytes` is `null` when tracking is disabled or the benchmark errored; the other three
are additionally absent unless the run produced statistics.

A **median of 0 with a non-zero mean and a large max** is the signature worth knowing: most
operations allocate nothing and a minority allocate a lot. That is typically a growing buffer, a
cache-miss path, or a lazily-initialized field, and it is invisible in the mean alone.

At Advanced detail the breakdown is printed under the benchmark:

```text
Allocations:
  Mean: 1.2 KiB
  P50:  0 B
  P95:  8.0 KiB
  Max:  32.0 KiB
```

### Across multiple launches

With [`LaunchCount > 1`](../features/multiple-launches.md), the per-launch figures are combined as
you would expect: the mean of the per-launch means, the median of the per-launch medians and P95s,
and the maximum of the per-launch maxima.

## Acting on a non-zero result

When `Alloc/op` is higher than you expect, these are the usual causes:

| Cause | What it looks like | Fix |
| --- | --- | --- |
| **Boxing** | A small, fixed size per op - often 24 B | Avoid `object` and non-generic interfaces on value types; check for a value type reaching a `params object[]` or a non-generic comparer |
| **LINQ** | A fixed cost per op, scaling with the number of operators | Replace the hot chain with a loop; LINQ allocates an enumerator per operator plus a closure per capturing lambda |
| **Closures** | A small fixed cost, appearing when a lambda captures a local | Make the lambda `static`, or pass the state as an argument |
| **String work** | Scales with output length | `string.Create`, a pooled `StringBuilder`, or interpolated string handlers |
| **Arrays and buffers** | Large, often bimodal with a zero median | `ArrayPool<T>.Shared`, or `stackalloc` / `Span<T>` for small fixed sizes |
| **`async` state machines** | A fixed cost on a path that goes asynchronous | `ValueTask` for frequently-synchronous paths; avoid `async` on methods that rarely await |

Two measurement notes when you are chasing this:

- **Compare, don't audit.** Put the before and after in one [suite](../usage-modes/suite-mode.md) and
  read the difference. Absolute figures include whatever the framework contributed between the
  counter reads; a difference between two benchmarks measured the same way cancels that out.
- **Watch the timing too.** Removing allocation usually helps latency, but pooling adds bookkeeping
  and can be slower for small buffers. The `Alloc/op` column and the median move independently.

## Turning it off

Allocation tracking is on by default under both measurement profiles. It is cheap - two counter
reads per sample, outside the timed region - so there is rarely a reason to disable it. If you do:

```bash
dotnet run -- --no-allocations
```

```csharp
options with { MeasureAllocationsOverride = false }
```

## See also

- [Measurement](./measurement.md) - the sample loop these reads are bracketed into
- [Diagnostics](./diagnostics.md) - GC collection counts, which show how the collector *responded* to this allocation
- [Descriptive statistics](./descriptive.md) - the percentile convention used for `AllocP95`
- [Reading your results](../getting-started/reading-your-results.md) - the `Alloc/op` column in context
