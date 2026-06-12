---
title: Descriptive Statistics
description: Mean, median, percentiles, confidence intervals, and the complete BenchmarkResult field reference.
order: 3
---

# Descriptive Statistics

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

With the default 200 iterations (slightly fewer after outlier trimming), the t critical value at 95% is approximately **1.973** - very close to the normal 1.960, so the practical difference is small.

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

## Summary of all reported fields

### Core fields on BenchmarkResult

| Field | Formula / method | Description |
|---|---|---|
| `Median` | Nearest-rank P50 | Robust central tendency. |
| `Mean` | $\bar{x} = \frac{1}{n}\sum x_i$ | Arithmetic average. |
| `P95` | Nearest-rank P95 | 95th percentile. |
| `P99` | Nearest-rank P99 | 99th percentile. |
| `Min` | $x_1$ (sorted) | Fastest measured sample. |
| `Max` | $x_n$ (sorted) | Slowest measured sample. |
| `Q1` | Nearest-rank P25 | First quartile. |
| `Q3` | Nearest-rank P75 | Third quartile. |
| `InterquartileRange` | Q3 - Q1 | Spread of the middle 50% of samples. |
| `LowerFence` | Q1 - 1.5 $\times$ IQR | Lower outlier boundary (only when `OutlierMode == IqrFence`). |
| `UpperFence` | Q3 + 1.5 $\times$ IQR | Upper outlier boundary (only when `OutlierMode == IqrFence`). |
| `OutliersRemoved` | Count of discarded samples | Number of samples removed by outlier trimming. |
| `N` | Post-trim length | Sample count after outlier removal. |
| `StandardDeviation` | $s = \sqrt{\frac{1}{n-1}\sum(x_i-\bar{x})^2}$ | Spread of measurements (Bessel). |
| `StandardError` | $s/\sqrt{n}$ | Precision of the mean estimate. |
| `MarginOfError` | $t^{*} \times \text{SEM}$ | Half-width of CI on the mean. |
| `ConfidenceIntervalLower` | $\bar{x} - \text{MoE}$ | Lower CI bound. |
| `ConfidenceIntervalUpper` | $\bar{x} + \text{MoE}$ | Upper CI bound. |
| `CoefficientOfVariation` | $s / \bar{x}$ | Relative variability. |
| `Skewness` | $g_1 = \frac{n \sum (x_i - \bar{x})^3}{(n-1)(n-2) s^3}$ | Sample skewness. Zero for $n < 3$. |
| `Kurtosis` | $g_2 = \frac{n(n+1)\sum (x_i-\bar{x})^4}{(n-1)(n-2)(n-3)s^4} - \frac{3(n-1)^2}{(n-2)(n-3)}$ | Excess kurtosis. Zero for $n < 4$. |
| `Mad` | $\text{median}(\lvert x_i - \text{median}(x) \rvert) \times 1.4826$ | Median absolute deviation (scaled to $\sigma$). Zero for $n < 1$. |
| `PValue` | Mann-Whitney U | Two-tailed p-value vs. baseline. |
| `SignificanceVerdict` | $p < \alpha$ | Whether the difference is real (`Significant`, `NotSignificant`, or `NotTested`). |
| `MeanAllocatedBytes` | Mean of iteration deltas | Mean heap allocation per iteration. |
| `AllocMedian` | Nearest-rank P50 of iteration deltas | Median allocation per iteration (only when `MeasureAllocations = true`). |
| `AllocP95` | Nearest-rank P95 of iteration deltas | P95 allocation per iteration (only when `MeasureAllocations = true`). |
| `AllocMax` | Max of iteration deltas | Max allocation per iteration (only when `MeasureAllocations = true`). |

### Computed properties

| Property | Formula | Description |
|---|---|---|
| `Range` | Max - Min | Full spread of trimmed samples. |
| `StandardErrorPercent` | $\text{SEM} / \bar{x} \times 100$ | Standard error as a percentage of the mean. |
| `MarginPercent` | $\text{MoE} / \bar{x} \times 100$ | Margin of error as a percentage of the mean. |
| `CoefficientOfVariationPercent` | $\text{CV} \times 100$ | Coefficient of variation as a percentage. |
