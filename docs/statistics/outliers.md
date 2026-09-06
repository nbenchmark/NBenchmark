---
title: Outlier Trimming
description: How NBenchmark removes outliers before computing statistics.
order: 3
---

# Outlier Trimming

After collection, NBenchmark removes [outliers](https://en.wikipedia.org/wiki/Outlier) based on the configured `OutlierMode`. The samples are first sorted in ascending order.

| Mode | Algorithm |
|---|---|
| `None` | No trimming occurs. |
| `RemoveTop5Percent` | Discards the top `ceil(n × 0.05)` samples. This is equivalent to keeping `floor(n × 0.95)`. |
| `RemoveTopAndBottom5Percent` | Discards the top and bottom `floor(n × 0.05)` samples from each end. |
| `IqrFence` | Computes Q1, Q3, and the [IQR](https://en.wikipedia.org/wiki/Interquartile_range) (Q3 − Q1). Discards any sample above `Q3 + 1.5 × IQR` or below `Q1 − 1.5 × IQR`. **(Default)** |
| `MedianAbsoluteDeviation` | Computes the median `m` and the scaled [MAD](https://en.wikipedia.org/wiki/Median_absolute_deviation) (`1.4826 × median(|xᵢ − m|)`). Discards any sample more than `3 × scaled MAD` from the median. |

Before this process runs, a separate pre-stage discards samples the OS is **known** to have preempted. For more information, see [Evidence-based interference rejection](#evidence-based-interference-rejection). `OutlierMode` and any custom `IOutlierDetector` only process samples that survive this interference filter.

The trimmed array is then passed to `StatsSummary.Compute`. The pre-trim raw array is stored separately for use in significance testing.

**Trimming does not shrink the error bar.** While the mean, standard deviation, and shape statistics are computed on the kept samples, the reported confidence interval is the [Winsorized (Yuen) one](./descriptive.md#winsorized-standard-error-for-trimmed-data). This interval is computed over the full pre-trim set by clamping trimmed samples to the nearest kept value rather than dropping them. Consequently, a discarded sample still counts as an observation and widens the interval, but its magnitude does not set the width. A run that trims no samples uses the plain `s/√n` interval.

`IqrFence` is the default because it adapts to the actual spread of each benchmark rather than discarding a fixed quota. In a clean run, almost every sample is kept; in a noisy run, more are trimmed. When the discarded slow samples form a tight secondary cluster (low relative spread rather than scattered noise), NBenchmark records a non-fatal **bimodal-distribution warning**. For more information, see [Bimodal-distribution warning](#bimodal-distribution-warning).

> [!NOTE] Quartile definition
> `IqrFence` computes Q1Ns and Q3Ns using the same **[nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method)** percentile used throughout NBenchmark (equivalent to `numpy.percentile(method='inverted_cdf')`).
>
> This differs from R's default `type = 7` linear interpolation. For a 1..20 ramp, NBenchmark gives Q1Ns = 5 and Q3Ns = 15, whereas R type 7 gives Q1Ns = 5.75 and Q3Ns = 15.25. This choice ensures consistency across all quantiles in the library.

## Bimodal-distribution warning

Outlier trimming discards the slow tail before statistics are computed. However, not every slow tail is random OS noise. Sometimes discarded samples form a **tight, repeatable secondary cluster**, representing a structural second execution profile that users will also encounter. Reporting only the fast cluster in these cases can hide latency bugs.

After trimming, NBenchmark inspects the discarded slow samples. If they look like a distinct second peak rather than scattered noise, NBenchmark emits a non-fatal **bimodal-distribution warning** in `BenchmarkResult.Warnings`.

### What the detector looks for

The detector runs on the boundary between trimming and statistics. It inspects the discarded samples that lie **above the kept median** (the slow tail) and checks for the following:

1. **Sufficient samples**: The slow cluster must contain at least 3 samples and at least 1% of the total run.
2. **Tight, repeatable extra cost**: The cluster's coefficient of variation (stddev / mean) must be $\le$ **0.15**. This means the discarded slow samples all took nearly the same amount of extra time. Random scheduling noise typically spreads delays across a wide range, whereas structural bottlenecks (such as a cache miss forcing a full memory read or a fixed-duration lock wait) concentrate them.

When both conditions are met, the warning specifies the cluster size and its center:

```text
⚠ MyBench.FastPath: 5 discarded outlier(s) form a distinct cluster near 502 ns rather than
  scattered noise - possible bimodal distribution; investigate this tail latency
  (e.g. GC pauses, lock contention, or cache misses).
```

### When you see this warning

A bimodal warning indicates that the slow samples were not random; they represent a repeatable second execution profile that fell outside the IQR fence. Common causes include:

| Cause | Typical signature |
|---|---|
| **Lock contention** | 90% of calls use the fast lock-free path; 10% collide and wait a fixed spin duration. |
| **Cache misses** | Most calls hit warm L1/L2; a minority miss to RAM and pay a ~100 ns penalty. |
| **GC pauses** | A Gen0 or Gen1 collection fires on a subset of samples, adding a fixed stall. |
| **Branch misprediction** | A data-dependent branch mispredicts on certain inputs, flushing the pipeline. |

The warning is **non-fatal**. The benchmark completes and reports statistics on the trimmed (fast-cluster) set. This tells you that the reported numbers describe the common case, while the worst case is reproducible and not random.

### How to resolve it

> [!CAUTION] Quick fix
> 1. **Check the body** for lock contention or cache misses. The cluster center in the warning identifies the extra cost of the slow path.
> 2. **If you suspect GC**: Run with `--gc per-sample-collect` to force per-sample Gen0 collection and make GC pauses deterministic.

1. **Do not silence the warning.** It reveals a real property of your code's performance distribution. The reported median describes the fast path, while the cluster center describes a latency real users will experience.
2. **Read the tail metrics.** By default, the [histogram](./descriptive.md) and the reported percentiles (P99, P99.9, max) are computed from the full pre-trim distribution (`TailMetricsBasis = Raw`). The trimmed cluster is still visible in these metrics, so you do not need to re-run with `OutlierMode.None`. If you have explicitly set `TailMetricsBasis = Trimmed`, re-run with `OutlierMode.None` to see the cluster.
3. **Investigate the cause.** Use a profiler or add instrumentation around suspected bottlenecks.
4. **Use `--gc per-sample-collect`** if you suspect GC.
5. **Reduce noise at the source** using [environment control](../features/environment-control.md) if OS scheduling contributed to the spread.

### Interaction with outlier mode

The bimodal detector runs **after** the active `OutlierMode` and inspects its discarded tail. It is most useful with the default `IqrFence`. 

- With `None`, no trimming occurs, so there is no discarded tail to inspect and the warning never fires.
- With `RemoveTop5Percent` or `RemoveTopAndBottom5Percent`, the discarded set is a fixed quota, but a tight cluster within it is still meaningful.
- With `MedianAbsoluteDeviation`, only slow samples above the kept median are considered.

The detector only adds a warning; it never changes which samples are kept.

### GC-correlated outliers

When GC collection counts are enabled (`DiagnosticsOptions.GcCollectionCounts`), NBenchmark records a per-sample GC delta. After trimming, it counts how many discarded samples coincided with a collection:

```text
⚠ 5 of 7 removed outlier(s) coincided with a garbage collection.
```

If a bimodal warning also fires, this information is folded into that warning. A high GC-correlation share suggests allocation pressure; consider using `--gc per-sample-collect` or reducing allocations in the body.

## Evidence-based interference rejection

Most outlier rules decide based on the **timing value alone**, which is an inference. A slow sample might be a genuinely expensive code path or a result of the OS preempting the measuring thread. Evidence-based interference rejection uses a **fact** to answer this.

NBenchmark reads the measuring thread's CPU time immediately outside the timed window for every sample to derive an **occupancy ratio**:

```text
r_i = cpuDelta_i / wallDelta_i
```

A sample that held the CPU for the entire window has a ratio close to the benchmark's typical value. A preempted sample has a visibly lower ratio. NBenchmark compares each ratio against the **benchmark's own median ratio** and rejects the sample if it falls below a threshold fraction of that median (`InterferenceOptions.RejectionThreshold`, default 0.5).

This is a **separate stage that runs before** outlier trimming. `OutlierMode` and custom `IOutlierDetector` instances only see samples that survive this filter.

### Reading the combined warning

When interference rejection finds evidence, the discard counts are reported in a single message:

```text
⚠ 42 sample(s) discarded - 31 confirmed preempted by the OS (CPU occupancy well below this
  benchmark's own median), 11 statistical outlier(s).
```

If the remaining statistical outliers form a tight cluster or coincide with GC, those details are also folded into this message.

A separate warning fires when the **rejected fraction** is high (`InterferenceOptions.HighRejectionWarningFraction`, default 20%), signaling that the host is too noisy to trust.

### Exclusions vs. rejections

An `await` may resume a benchmark body's continuation on a different thread. In this case, the thread-CPU-time delta is meaningless. NBenchmark reports the occupancy for such samples as **unknown**. These samples are excluded from the median and from the rejection logic; they are **never** rejected on this basis. If most samples hop threads, the filter disables itself for that benchmark.

### Graceful degradation

The filter is **enabled by default** and disables itself (with a reason in `AutoTuneDiagnostic.InterferenceDisabledReason`) under the following conditions:
- The thread-CPU clock is unavailable on the host platform.
- Two clock reads exceed `InterferenceOptions.ProbeCostBudgetFraction` (default 5%) of the resolved sample-duration target.
- Too few samples produced a known occupancy reading to trust a median.

You can disable the filter entirely with `--no-interference-filter`, `InterferenceOptions.Enabled = false`, or `.WithInterferenceFilter(false)`.

## Median absolute deviation (MAD)

`MedianAbsoluteDeviation` is a robust alternative to `IqrFence`. It measures spread using the **Median of absolute deviations from the median**, providing the highest possible [breakdown point](https://en.wikipedia.org/wiki/Robust_statistics#Breakdown_point) (50%). Up to half the samples can be contaminated before the estimate is distorted.

**The algorithm:**
1. Compute the median `m` of the sorted samples.
2. Compute each absolute deviation `|xᵢ − m|`.
3. Compute the **raw MAD** (the median of those deviations).
4. Scale it to be a consistent estimator of the standard deviation for normally distributed data: `scaledMad = 1.4826 × rawMad`.
5. Reject any sample where `|xᵢ − m| > 3 × scaledMad`.

If the scaled MAD is `0` (more than half the samples are identical) or there are fewer than three samples, every sample is kept.

Prefer `MedianAbsoluteDeviation` over `IqrFence` when your distribution is heavily contaminated or strongly skewed, as the symmetric MAD fence resists clusters of extreme values that would otherwise inflate the IQR.

> [!NOTE] Two different MADs
> The MAD described here is an **outlier detector**. NBenchmark also reports MAD as a **descriptive spread statistic** (see [Descriptive Statistics](./descriptive.md)). They use the same formula but serve different purposes.

## Custom outlier detectors

Every built-in mode maps to an `IOutlierDetector` in `NBenchmark.Stats.OutlierDetectors`. If a built-in rule does not fit your domain, you can supply your own.

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
            UpperFenceNs = sortedSamples[keep],
        };
    }
}
```

Register your detector through `MeasurementOptions.OutlierDetector`, the suite builder, or the harness:

```csharp
// Suite mode
.WithOutlierDetector(static () => new KeepFastestDetector(0.90))

// Single / Harness mode
new MeasurementOptions { OutlierDetector = static () => new KeepFastestDetector(0.90) }
```

A custom `OutlierDetector` takes priority over `OutlierMode`.

**The contract:**
- `sortedSamples` arrives **sorted ascending**; do not mutate it.
- Return `Kept` sorted ascending.
- **Never discard every sample.** Return `OutlierClassification.KeepAll(sortedSamples)` if your rule would empty the set.
- Set `LowerFenceNs` and `UpperFenceNs` only for fence-based rules.

The detector's `Name` appears in the report header.
