---
title: Key Concepts
description: What warmup, outlier trimming, confidence intervals, and statistical significance mean in a benchmarking context.
order: 3
---

# Key Concepts

You don't need to be a statistician to use NBenchmark, but understanding what it does under the hood will help you interpret results correctly and avoid common pitfalls.

## Warmup

Before any measurements are recorded, each benchmark runs for a number of **warmup iterations** (default: 25).

Warmup exists because the first few runs of .NET code are artificially slow:

- The **JIT compiler** hasn't compiled your method yet. The first call triggers [compilation](https://en.wikipedia.org/wiki/Just-in-time_compilation), which takes time.
- **CPU caches** are cold. Memory that your code accesses frequently won't be in the [L1/L2 cache](https://en.wikipedia.org/wiki/CPU_cache) yet.

If you skipped warmup, your first measurements would include JIT compilation time, which is not representative of steady-state performance. After warmup, subsequent runs use the compiled, cached version of your code.

> [!TIP]
> If your benchmark is a one-shot operation where you specifically want to measure cold-start time, set `WithWarmup(0)` or `WarmupIterations = 1`.

## Garbage collection

By default, NBenchmark forces a gen-0 GC collection (`GC.Collect(0)`) **before each iteration**. This ensures that heap allocations from the previous iteration don't influence the next one.

Without this, allocation patterns from earlier iterations can trigger GC mid-measurement, adding noise to your timings. Forcing GC before each iteration keeps measurements independent.

This behaviour is controlled by `ForceGcBeforeEachIteration` (default: `true`). You can disable it if your benchmark intentionally tests GC pressure.

## Outlier trimming

Even with warmup and forced GC, occasional **OS scheduling interrupts**, context switches, or thermal throttling can spike an individual measurement. These outliers are not representative of your code's performance - they reflect system noise.

By default, NBenchmark trims the **top 5%** of samples before computing statistics (`OutlierMode.RemoveTop5Percent`). With 200 iterations, this discards the 10 noisiest measurements.

Available modes:

| Mode | What is removed |
|---|---|
| `None` | Nothing. All samples are used. |
| `RemoveTop5Percent` | The slowest 5% of samples. **(default)** |
| `RemoveTopAndBottom5Percent` | The slowest and fastest 5%. |
| `IqrFence` | Any sample beyond 1.5× the [inter-quartile range](https://en.wikipedia.org/wiki/Interquartile_range). |

## Median vs. mean

The **median** is the middle value when all measurements are sorted. It is robust to outliers - a few very slow measurements do not pull it upward.

The **mean** is the arithmetic average. Even after outlier trimming, the mean is more sensitive to skewed distributions than the median.

For most purposes, the **median** is the most reliable single number to compare two benchmarks. The mean is useful when you also care about the confidence interval (see below).

## Standard deviation

**Standard deviation (StdDev)** measures how spread out your measurements are. A [standard deviation](https://en.wikipedia.org/wiki/Standard_deviation) high relative to the mean means your timings are inconsistent - the code runs in very different amounts of time from iteration to iteration. This can be caused by:

- GC pressure and pauses
- Cache misses due to working-set size
- OS scheduling
- Thermal throttling on laptops

A low StdDev means your benchmark is stable and the mean is trustworthy.

> [!TIP]
> If you see high StdDev or a large Error, see the [Troubleshooting guide](../troubleshooting.md) for configuration remedies.

## Confidence intervals and the Error column

The **Error** column shows the **[margin of error](https://en.wikipedia.org/wiki/Margin_of_error)** on the mean at a given confidence level (default: 95%).

Concretely: with 200 samples and a 95% confidence level, the true mean is likely within `Mean ± Error`. If the Error is `±50 ns` and the mean is `1.20 µs`, you can be 95% confident the true mean is somewhere between `1.15 µs` and `1.25 µs`.

**How it's calculated:** NBenchmark computes the [standard error of the mean](https://en.wikipedia.org/wiki/Standard_error) (`StdDev / √n`), then multiplies by the critical value of [Student's t-distribution](https://en.wikipedia.org/wiki/Student%27s_t-distribution) at the configured confidence level and `n-1` degrees of freedom. For large sample counts this converges to the familiar `z = 1.96` you might know from normal-distribution CIs.

**What a large Error means:** your measurements are highly variable, or you have too few iterations. Consider increasing `WithIterations(...)` or checking whether something external is interfering (e.g. background processes on your machine).

## Percentiles (P95, P99)

**P95** is the 95th percentile: 95% of individual measurements completed within this time. **P99** is the 99th percentile.

These are important for **latency-sensitive** code where you care about worst-case behaviour, not just the average. A method might have a low median but a high P99 if it occasionally triggers GC or hits a slow path.

## Statistical significance

When comparing two or more benchmarks, it's not enough to see that one has a lower median. The difference might be random noise.

NBenchmark uses the **[Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test)** to answer: "Is this difference statistically significant?" The test implementation (along with every other statistical primitive in the library) is dependency-free and cross-validated against SciPy and NumPy - see [Validation & Accuracy](../advanced/validation.md).

- A **✓** in the Sig column means the difference would occur by chance less than 5% of the time (p < 0.05). It is very unlikely to be noise.
- A **~** means the difference is not statistically significant - you cannot confidently conclude one is faster than the other.
- The test requires at least **5 samples** in each group; with fewer it returns no result.

The Mann-Whitney U test is **[non-parametric](https://en.wikipedia.org/wiki/Nonparametric_statistics)** - it makes no assumption that your timings follow a normal (bell-curve) distribution, which benchmark timings generally do not.

> [!NOTE]
> Statistical significance does not mean the difference is *large* or *important*. A tiny 0.1 ns difference can be statistically significant with many iterations. Always look at the Ratio column alongside significance.

## Allocation tracking

When `MeasureAllocations` is enabled, NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` before and after each iteration and reports the **mean bytes allocated per iteration** in the Alloc/op column. If an async iteration resumes on a different thread, it falls back to a process-wide allocation delta for that sample.

This is useful for detecting unexpected heap allocations - boxing of value types, LINQ overhead, string formatting, etc. Zero allocations in the hot path typically means less GC pressure and more predictable latency.

Allocation tracking is off by default because it adds a small measurement overhead.

## Next steps

- **[Guides](../guides/)** - see these concepts applied in real benchmarks
- **[Advanced: Statistics](../advanced/statistics.md)** - the full mathematical detail
- **[Configuration](../configuration.md)** - tune iterations, warmup, outlier mode, and confidence level
