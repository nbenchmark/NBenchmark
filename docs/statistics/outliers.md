---
title: Outlier Trimming
description: How NBenchmark removes outliers before computing statistics.
order: 2
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

`IqrFence` is the default because it adapts to each benchmark's actual spread rather than always discarding a fixed quota: a clean run keeps almost every sample, while a noisy run trims more. When the slow samples it discards form a tight secondary cluster - low relative spread, rather than scattered scheduling noise - NBenchmark records a non-fatal **bimodal-distribution warning** on the result (surfaced in `BenchmarkResult.Warnings` and by the console reporter). That pattern usually signals a structural second execution profile worth investigating (GC pauses, lock contention, cache misses) rather than random jitter.

> [!NOTE] Quartile definition
> `IqrFence` computes Q1 and Q3 with the same **[nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method)** percentile used
> everywhere else in NBenchmark (equivalent to `numpy.percentile(method='inverted_cdf')`).
> This deliberately differs from R's default `type = 7` linear interpolation: for a
> 1..20 ramp NBenchmark gives Q1 = 5, Q3 = 15, whereas R type 7 gives Q1 = 5.75,
> Q3 = 15.25. The choice keeps every [quantile](https://en.wikipedia.org/wiki/Quantile) in the library consistent and is
> pinned by `OutlierModeCrossCheckTests`.

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

Register it through `MeasurementOptions.OutlierDetector`, the suite builder, or the host:

```csharp
// Suite mode
.WithOutlierDetector(new KeepFastestDetector(0.90))

// Quick / Host mode
new MeasurementOptions { OutlierDetector = new KeepFastestDetector(0.90) }
```

A custom `OutlierDetector` takes priority over `OutlierMode`. The contract:

- `sortedSamples` arrives **sorted ascending**; do not mutate it.
- Return `Kept` sorted ascending (filtering a sorted input preserves order).
- **Never discard every sample** - return `OutlierClassification.KeepAll(sortedSamples)` when your rule would empty the set, so the engine always has data to summarize.
- Set `LowerFence` / `UpperFence` only when your rule is fence-based; they are surfaced in reports.

The detector's `Name` appears in the report header (`Outliers: ...`).
