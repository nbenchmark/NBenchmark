---
title: Outlier Trimming
description: How NBenchmark removes outliers before computing statistics.
order: 3
---

# Outlier Trimming

After collection, [outliers](https://en.wikipedia.org/wiki/Outlier) are removed according to `OutlierMode`. The samples are first sorted ascending.

| Mode | Algorithm |
|---|---|
| `None` | No trimming. |
| `RemoveTop5Percent` | Discard the top `ceil(n × 0.05)` samples. Equivalent to keeping `floor(n × 0.95)`. |
| `RemoveTopAndBottom5Percent` | Discard the top and bottom `floor(n × 0.05)` samples from each end. |
| `IqrFence` | Compute Q1, Q3, and [IQR](https://en.wikipedia.org/wiki/Interquartile_range) = Q3 − Q1. Discard any sample above Q3 + 1.5 × IQR or below Q1 − 1.5 × IQR. **(default)** |
| `MedianAbsoluteDeviation` | Compute the median `m` and the scaled [MAD](https://en.wikipedia.org/wiki/Median_absolute_deviation) = 1.4826 × median(\|xᵢ − m\|). Discard any sample more than 3 × scaled MAD from the median. |

The trimmed array is passed to `StatsSummary.Compute`. The pre-trim raw array is stored separately for use in significance testing.

`IqrFence` is the default because it adapts to each benchmark's actual spread rather than always discarding a fixed quota: a clean run keeps almost every sample, while a noisy run trims more. When the slow samples it discards form a tight secondary cluster - low relative spread, rather than scattered scheduling noise - NBenchmark records a non-fatal **bimodal-distribution warning** on the result. See [Bimodal-distribution warning](#bimodal-distribution-warning) below for what the detector looks for, what to do, and how it interacts with each outlier mode.

> [!NOTE] Quartile definition
> `IqrFence` computes Q1 and Q3 with the same **[nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method)** percentile used
> everywhere else in NBenchmark (equivalent to `numpy.percentile(method='inverted_cdf')`).
> This deliberately differs from R's default `type = 7` linear interpolation: for a
> 1..20 ramp NBenchmark gives Q1 = 5, Q3 = 15, whereas R type 7 gives Q1 = 5.75,
> Q3 = 15.25. The choice keeps every [quantile](https://en.wikipedia.org/wiki/Quantile) in the library consistent and is
> pinned by `OutlierModeCrossCheckTests`.

## Bimodal-distribution warning

Outlier trimming discards the slow tail before statistics are computed - that is its job. But not every slow tail is random OS noise. Sometimes the discarded samples form a **tight, repeatable secondary cluster**: a structural second execution profile that a real user will also hit. Throwing those samples away and reporting only the fast cluster hides a latency bug.

After trimming, NBenchmark inspects the discarded slow samples and emits a non-fatal **bimodal-distribution warning** when they look like a distinct second peak rather than scattered scheduling noise. The warning is added to `BenchmarkResult.Warnings` and surfaced by the console and Markdown reporters.

### What the detector looks for

The detector runs on the boundary between trimming and statistics (`StatsPipeline` passes the kept and discarded arrays to `BimodalDetector`). It checks the discarded samples that lie **above the kept median** - the slow tail - and asks whether they cluster tightly:

1. **Enough samples to matter.** The slow cluster must contain at least 3 samples and at least 1% of the total run. A single stray sample is not a mode.
2. **A tight, repeatable extra cost.** The cluster's coefficient of variation (stddev / mean) must be at or below **0.15** - i.e. the discarded slow samples all took almost the same amount of extra time. Random scheduling noise spreads delays across a wide range; a structural bottleneck (a cache miss forcing a full memory read, a lock wait of fixed duration) concentrates them.

When both conditions hold, the warning names the cluster size and its centre:

```
⚠ MyBench.FastPath: 5 discarded outlier(s) form a distinct cluster near 502 ns rather than
  scattered noise - possible bimodal distribution; investigate this tail latency
  (e.g. GC pauses, lock contention, or cache misses).
```

### When you see it

A bimodal warning means the slow samples were **not** random - they were a repeatable second execution profile that happened to land outside the IQR fence. Common causes:

| Cause | Typical signature |
|---|---|
| **Lock contention** | 90% of calls take the fast lock-free path; 10% collide and wait a fixed spin duration. |
| **Cache misses** | Most calls hit warm L1/L2; a minority miss to RAM and pay a ~100 ns penalty. |
| **GC pauses** | A Gen0 or Gen1 collection fires on a subset of iterations, adding a fixed stall. |
| **Branch misprediction** | A data-dependent branch mispredicts on certain inputs, flushing the pipeline. |

The warning is **non-fatal**: the benchmark still completes and reports statistics on the trimmed (fast-cluster) set. The warning tells you that the reported numbers describe the common case, not the worst case - and that the worst case is reproducible, not random.

### What to do

1. **Do not silence it.** The warning is telling you something real about your code's performance distribution. The reported median describes the fast path; the cluster centre describes a latency a real user will also hit.
2. **Re-run with `OutlierMode.None`** to see the full distribution (the warning only fires when trimming is active, because it inspects the discarded tail). The [histogram](./descriptive.md) and the reported percentiles (P99, Max) will show the second peak.
3. **Investigate the cause.** Use a profiler or add instrumentation around the suspected bottleneck (lock, cache-hot path, GC notification). The cluster centre in the warning message is a hint about how much extra time the slow path costs.
4. **Consider `--profile independent`** if you suspect GC: it forces per-iteration Gen0 collection, which makes GC pauses deterministic rather than bimodal.
5. **Reduce noise at the source** with [environment control](../features/environment-control.md) if you suspect OS scheduling contributed to the spread.

### Interaction with outlier mode

The bimodal detector runs **after** whichever `OutlierMode` is active and inspects that mode's discarded tail. It is most useful with the default `IqrFence`, which discards a data-adaptive tail. With `None` (no trimming) there is no discarded tail to inspect, so the warning never fires. With `RemoveTop5Percent` or `RemoveTopAndBottom5Percent` the discarded set is a fixed quota, so a tight cluster in it is still meaningful. With `MedianAbsoluteDeviation` the symmetric fence can discard fast and slow samples; only the slow ones above the kept median are considered for the cluster.

The detector never changes which samples are kept - it only adds a warning. The trimmed statistics are computed exactly as the `OutlierMode` dictates.

## Median Absolute Deviation (MAD)

`MedianAbsoluteDeviation` is a robust alternative to `IqrFence`. It measures spread using the **median of absolute deviations from the median** rather than the quartiles, which gives it the highest possible [breakdown point](https://en.wikipedia.org/wiki/Robust_statistics#Breakdown_point) (50%): up to half the samples can be contaminated before the estimate is distorted.

The algorithm:

1. Compute the median `m` of the sorted samples.
2. Compute each absolute deviation `|xᵢ − m|`.
3. Compute the **raw MAD** - the median of those deviations.
4. Scale it to be a consistent estimator of the standard deviation for normally distributed data: `scaledMad = 1.4826 × rawMad`.
5. Reject any sample where `|xᵢ − m| > 3 × scaledMad`. The rejection fences are therefore `m ± 3 × scaledMad`.

If the scaled MAD is `0` (more than half the samples are identical) or there are fewer than three samples, every sample is kept - the detector never discards everything.

Prefer `MedianAbsoluteDeviation` over `IqrFence` when your distribution is heavily contaminated or strongly skewed: the symmetric MAD fence resists a cluster of extreme values that could otherwise inflate the IQR itself.

> [!NOTE] Two different MADs
> The MAD here is an **outlier detector**. NBenchmark also reports MAD as a **descriptive spread statistic** at the Advanced detail level (see [Descriptive Statistics](./descriptive.md)). They share the same formula but serve different purposes - one trims samples, the other summarizes spread.

## Custom outlier detectors

Every built-in mode maps onto an `IOutlierDetector` in `NBenchmark.Stats.OutlierDetectors`. When a built-in rule does not fit your domain, supply your own detector - for example a tail-preserving rule for latency SLOs, or a fixed physical threshold:

```csharp
using NBenchmark.Stats;

public sealed class KeepFastestDetector(double fraction) : IOutlierDetector
{
    public string Name => $"keep fastest {fraction * 100:0.#}%";

    public OutlierClassification Classify(double[] sortedSamples)
    {
        // Input is sorted ascending and must NOT be mutated.
        var keep = (int)Math.Floor(sortedSamples.Length * fraction);

        if (keep <= 0 || keep >= sortedSamples.Length)
            return OutlierClassification.KeepAll(sortedSamples);

        return new OutlierClassification
        {
            Kept = sortedSamples[..keep],
            Discarded = sortedSamples[keep..],
            UpperFence = sortedSamples[keep],
        };
    }
}
```

Register it through `MeasurementOptions.OutlierDetector`, the suite builder, or the harness:

```csharp
// Suite mode
.WithOutlierDetector(new KeepFastestDetector(0.90))

// Single / Harness mode
new MeasurementOptions { OutlierDetector = new KeepFastestDetector(0.90) }
```

A custom `OutlierDetector` takes priority over `OutlierMode`. The contract:

- `sortedSamples` arrives **sorted ascending**; do not mutate it.
- Return `Kept` sorted ascending (filtering a sorted input preserves order).
- **Never discard every sample** - return `OutlierClassification.KeepAll(sortedSamples)` when your rule would empty the set, so the engine always has data to summarize.
- Set `LowerFence` / `UpperFence` only when your rule is fence-based; they are surfaced in reports.

The detector's `Name` appears in the report header (`Outliers: ...`).
