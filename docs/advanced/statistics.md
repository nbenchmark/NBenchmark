---
title: Statistics Deep Dive
description: A technical explanation of how NBenchmark measures, cleans, and analyses benchmark data.
order: 1
---

# Statistics Deep Dive

This page explains exactly how NBenchmark collects and analyses measurements. You don't need to understand all of this to use the library - the [Key Concepts](../getting-started/key-concepts.md) page covers the practical side. This is for readers who want the full mathematical picture.

## The measurement loop

For each benchmark, NBenchmark runs the following sequence:

1. **Warmup** - run the action `WarmupIterations` times (default: 25) without recording timings.
2. **Post-warmup GC** - force a full gen-2 GC collection to establish a clean heap baseline.
3. **Measurement loop** - for each of the `Iterations` (default: 200) measured runs:
   - If `ForceGcBeforeEachIteration` is true, force a gen-0 collection.
   - Call `iterationSetup` if provided.
   - Record `Stopwatch.GetTimestamp()`.
   - Invoke the benchmark action.
   - Calculate elapsed time with `Stopwatch.GetElapsedTime(timestamp)`.
   - Record allocation delta if `MeasureAllocations` is true.
   - Call `iterationTeardown` if provided.

**Important:** the timer is read immediately after the action returns, before teardown runs. Teardown time is not included in the measurement.

The raw timing data (in nanoseconds) is stored in a `double[]` of length `Iterations`.

### Timer resolution

NBenchmark uses `System.Diagnostics.Stopwatch`, which wraps the platform's high-resolution performance counter. The resolution is printed at the start of each `BenchmarkHost` run:

```
Timer resolution: 1,000,000,000 ticks/s (1.00 ns per tick)
```

On most modern hardware the resolution is 1 ns. On some virtual machines it may be coarser.

## Outlier trimming

After collection, [outliers](https://en.wikipedia.org/wiki/Outlier) are removed according to `OutlierMode`. The samples are first sorted ascending.

| Mode | Algorithm |
|---|---|
| `None` | No trimming. |
| `RemoveTop5Percent` | Discard the top `ceil(n × 0.05)` samples. Equivalent to keeping `floor(n × 0.95)`. |
| `RemoveTopAndBottom5Percent` | Discard the top and bottom `floor(n × 0.05)` samples from each end. |
| `IqrFence` | Compute Q1, Q3, and [IQR](https://en.wikipedia.org/wiki/Interquartile_range) = Q3 − Q1. Discard any sample above Q3 + 1.5 × IQR or below Q1 − 1.5 × IQR. |

The trimmed array is passed to `StatsSummary.Compute`. The pre-trim raw array is stored separately for use in significance testing.

::: info Quartile definition
`IqrFence` computes Q1 and Q3 with the same **[nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method)** percentile used
everywhere else in NBenchmark (equivalent to `numpy.percentile(method='inverted_cdf')`).
This deliberately differs from R's default `type = 7` linear interpolation: for a
1..20 ramp NBenchmark gives Q1 = 5, Q3 = 15, whereas R type 7 gives Q1 = 5.75,
Q3 = 15.25. The choice keeps every [quantile](https://en.wikipedia.org/wiki/Quantile) in the library consistent and is
pinned by `OutlierModeCrossCheckTests`.
:::

## Descriptive statistics

Given a sorted, trimmed array of `n` samples:

### Mean

$$\bar{x} = \frac{1}{n} \sum_{i=1}^{n} x_i$$

### Median

The [nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method) method. For sorted sample index `i = ceil(0.5 × n)` (1-based). Equivalent to the middle value for odd `n`, and the lower-middle for even `n`.

### Percentiles (P95, P99)

Also [nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method): `i = ceil(p × n)`.

### Min and Max

`samples[0]` and `samples[n-1]` of the sorted, trimmed array.

### Sample standard deviation ([Bessel's correction](https://en.wikipedia.org/wiki/Bessel%27s_correction))

$$s = \sqrt{\frac{1}{n-1} \sum_{i=1}^{n}(x_i - \bar{x})^2}$$

The `n-1` denominator (Bessel's correction) makes `s` an unbiased estimator of the population standard deviation. For `n = 1`, the [standard deviation](https://en.wikipedia.org/wiki/Standard_deviation) is reported as `0`.

## [Standard error of the mean](https://en.wikipedia.org/wiki/Standard_error)

$$\text{SEM} = \frac{s}{\sqrt{n}}$$

SEM measures how precisely the mean is estimated. For `n = 1`, SEM is `0`.

## [Confidence interval](https://en.wikipedia.org/wiki/Confidence_interval) on the mean

The margin of error is the half-width of the confidence interval:

$$\text{MoE} = t^{*}_{\alpha/2,\, n-1} \times \text{SEM}$$

where $t^{*}_{\alpha/2,\, n-1}$ is the two-tailed critical value of [Student's t-distribution](https://en.wikipedia.org/wiki/Student%27s_t-distribution) at the configured confidence level and `n − 1` degrees of freedom.

The confidence interval is:

$$\bar{x} \pm \text{MoE} = [\bar{x} - \text{MoE},\; \bar{x} + \text{MoE}]$$

### Why Student's t and not the normal distribution?

The [normal distribution](https://en.wikipedia.org/wiki/Normal_distribution)'s critical value (e.g. 1.96 for 95%) assumes the population standard deviation is known. In benchmarking it is not - we estimate it from the sample. Student's t compensates by using wider critical values for small sample sizes, shrinking towards the normal as `n` grows.

With the default 200 iterations (190 after 5% trimming), the t critical value at 95% is approximately **1.973** - very close to the normal 1.960, so the practical difference is small.

### Honest caveats

The CI is on the **mean** and relies on the [Central Limit Theorem](https://en.wikipedia.org/wiki/Central_limit_theorem) - the assumption that the sample mean is approximately normally distributed. For `n ≥ 30` this is generally safe even when the underlying distribution is not normal. For very small sample counts (e.g. a parameterised benchmark with 10 iterations) the approximation is weaker, but the t-distribution's heavier tails at low degrees of freedom provide some protection.

### t-critical values in practice

| Confidence level | n = 10 (df=9) | n = 30 (df=29) | n = 200 (df=199) | Normal (df=∞) |
|---|---|---|---|---|
| 90% | 1.833 | 1.699 | 1.652 | 1.645 |
| 95% | 2.262 | 2.045 | 1.972 | 1.960 |
| 99% | 3.250 | 2.756 | 2.601 | 2.576 |

### Dependency-free implementation

NBenchmark computes the t critical value without any external libraries using exact closed forms for df = 1 and df = 2, and the [Cornish-Fisher expansion](https://en.wikipedia.org/wiki/Cornish%E2%80%93Fisher_expansion) (Abramowitz & Stegun §26.7.5) for df ≥ 3. The normal quantile uses Acklam's rational approximation (max error < 1.15 × 10⁻⁹).

These approximations are cross-checked against SciPy on every build: the t
critical value matches `scipy.stats.t.ppf` to machine precision for df = 1, 2 and
to **better than 1%** for df ≥ 3 (worst case ≈ 0.79% at df = 3, 99%). See
[Validation & Accuracy](./validation.md) for the full tolerance table.

## [Coefficient of variation](https://en.wikipedia.org/wiki/Coefficient_of_variation)

$$\text{CV} = \frac{s}{\bar{x}}$$

A dimensionless relative measure of variability. A CV of 0.05 means the standard deviation is 5% of the mean - the benchmark is fairly stable. A CV of 0.5 or higher indicates high variability and the results should be treated with caution.

## Allocation measurement

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

## Statistical significance: [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test)

When two or more benchmarks have been run, NBenchmark tests whether the difference in their distributions is statistically significant using the **Mann-Whitney U test** (also called the Wilcoxon rank-sum test).

### Why Mann-Whitney U?

Benchmark timings are typically right-skewed (a few slow outliers) and do not follow a normal distribution. Parametric tests like the t-test assume normality. The Mann-Whitney U test is **[non-parametric](https://en.wikipedia.org/wiki/Nonparametric_statistics)** - it ranks combined values rather than computing moments, and makes no distributional assumptions.

### Algorithm

Given the **pre-trim raw samples** of two benchmarks A (length n₁) and B (length n₂):

1. Merge and sort all `n₁ + n₂` values together, recording which sample each came from.
2. Assign **mid-ranks** to tied values: all tied observations share the average rank of the positions they occupy.
3. Compute the rank sum for group A: $R_1 = \sum \text{rank}(A_i)$.
4. Compute the U statistics:

$$U_1 = R_1 - \frac{n_1(n_1+1)}{2}, \quad U_2 = n_1 n_2 - U_1, \quad U = \min(U_1, U_2)$$

1. For large samples (n₁ ≥ 5 and n₂ ≥ 5), use the [normal approximation](https://en.wikipedia.org/wiki/Normal_distribution#Central_limit_theorem) with a tie correction to compute a z-score, then derive a two-tailed [p-value](https://en.wikipedia.org/wiki/P-value).

A [p-value](https://en.wikipedia.org/wiki/P-value) below **0.05** is considered significant (✓ in the Sig column). This threshold is fixed and is not configurable.

The normal approximation uses **no continuity correction**, so it corresponds to
`scipy.stats.mannwhitneyu(..., method='asymptotic', use_continuity=False)` - which
NBenchmark matches to better than 1e-6. On small samples this approximation can
differ from the exact [permutation](https://en.wikipedia.org/wiki/Permutation_test) p-value by up to ≈ 0.05; that gap is pinned and
documented in [Validation & Accuracy](./validation.md).

::: info
NBenchmark uses the **pre-trim raw samples** (before outlier removal) for significance testing. This gives the test more data to work with. However it means that significance is assessed on the full distribution including extreme measurements.
:::

### Minimum sample requirement

The test requires at least **5 samples in each group**. With fewer samples the normal approximation is unreliable and the test returns `null` (no significance indicator is shown).

## Summary of all reported statistics

| Field | Formula | Description |
|---|---|---|
| `Median` | Nearest-rank P50 | Robust central tendency. |
| `Mean` | $\bar{x} = \frac{1}{n}\sum x_i$ | Arithmetic average. |
| `P95` | Nearest-rank P95 | 95th percentile. |
| `P99` | Nearest-rank P99 | 99th percentile. |
| `Min` | $x_1$ (sorted) | Fastest measured sample. |
| `Max` | $x_n$ (sorted) | Slowest measured sample. |
| `StandardDeviation` | $s = \sqrt{\frac{1}{n-1}\sum(x_i-\bar{x})^2}$ | Spread of measurements (Bessel). |
| `StandardError` | $s/\sqrt{n}$ | Precision of the mean estimate. |
| `MarginOfError` | $t^{*} \times \text{SEM}$ | Half-width of CI on the mean. |
| `ConfidenceIntervalLower` | $\bar{x} - \text{MoE}$ | Lower CI bound. |
| `ConfidenceIntervalUpper` | $\bar{x} + \text{MoE}$ | Upper CI bound. |
| `CoefficientOfVariation` | $s / \bar{x}$ | Relative variability. |
| `PValue` | Mann-Whitney U | Two-tailed p-value vs. baseline. |
| `SignificanceVerdict` | $p < 0.05$ | Whether the difference is real (`Significant`, `NotSignificant`, or `NotTested`). |
| `MeanAllocatedBytes` | Mean of iteration deltas | Mean heap allocation per iteration. |
