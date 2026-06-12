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
| `IqrFence` | Compute Q1, Q3, and [IQR](https://en.wikipedia.org/wiki/Interquartile_range) = Q3 − Q1. Discard any sample above Q3 + 1.5 × IQR or below Q1 − 1.5 × IQR. |

The trimmed array is passed to `StatsSummary.Compute`. The pre-trim raw array is stored separately for use in significance testing.

`IqrFence` is the default because it adapts to each benchmark's actual spread rather than always discarding a fixed quota: a clean run keeps almost every sample, while a noisy run trims more. When the slow samples it discards form a tight secondary cluster - low relative spread, rather than scattered scheduling noise - NBenchmark records a non-fatal **bimodal-distribution warning** on the result (surfaced in `BenchmarkResult.Warnings` and by the console reporter). That pattern usually signals a structural second execution profile worth investigating (GC pauses, lock contention, cache misses) rather than random jitter.

> [!NOTE] Quartile definition
> `IqrFence` computes Q1 and Q3 with the same **[nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method)** percentile used
> everywhere else in NBenchmark (equivalent to `numpy.percentile(method='inverted_cdf')`).
> This deliberately differs from R's default `type = 7` linear interpolation: for a
> 1..20 ramp NBenchmark gives Q1 = 5, Q3 = 15, whereas R type 7 gives Q1 = 5.75,
> Q3 = 15.25. The choice keeps every [quantile](https://en.wikipedia.org/wiki/Quantile) in the library consistent and is
> pinned by `OutlierModeCrossCheckTests`.
