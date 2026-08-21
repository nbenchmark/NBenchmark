---
title: Descriptive Statistics
description: Mean, median, percentiles, confidence intervals, and the complete BenchmarkResult field reference.
order: 4
---

# Descriptive Statistics

NBenchmark computes statistics based on a sorted, trimmed array of `n` samples. Most statistics use the trimmed set, with two exceptions: tail metrics use the pre-trim set by default, and the standard error accounts for what trimming removed.

## Central tendency and dispersion

### Mean

The arithmetic average of the samples:

$$\bar{x} = \frac{1}{n} \sum_{i=1}^{n} x_i$$

### Median

NBenchmark uses the **mid-average** convention for the median:
- For odd `n`, the median is the middle value.
- For even `n`, the median is the mean of the two middle order statistics.

This matches `numpy.median` and the median NBenchmark uses for per-launch aggregation and jitter calibration. Consequently, the reported `Median` and the `P50` percentile agree.

### Percentiles

NBenchmark computes configurable percentile values using the [nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method) method: `i = ceil(p × n)`.

The median (`p = 0.50`) is the only exception, as it uses the mid-average method for even `n`. All other percentiles, including `Q1` and `Q3`, use nearest-rank.

You can control which percentiles are reported via `MeasurementOptions.ReportedPercentiles` (default: P50, P95, P99, P99.9, Max). Each entry is a `PercentileEntry` containing a `Percentile` (0-1) and a `Value` (nanoseconds). You can access a specific percentile using `result.GetPercentile(0.95)`.

**Tail metrics are computed from the full pre-trim distribution by default.** Because percentiles, `Min`, `Max`, and the histogram describe the shape of the distribution, NBenchmark computes them from the raw (pre-trim) sample set (`MeasurementOptions.TailMetricsBasis = Raw`). This ensures that the IQR/MAD fence does not remove the slow tail that P99/P99.9/Max are intended to describe.

Central-tendency and dispersion statistics - including the mean, standard deviation, CI, CV, skewness, kurtosis, MAD, and median - always use the **trimmed** set. This prevents fenced-out spikes from moving the mean or inflating the interval. To compute tail metrics from the inlier set instead, set `TailMetricsBasis = Trimmed` (or use `--tail-basis trimmed`).

NBenchmark records the basis used on the result as `BenchmarkResult.TailMetricsBasis` (and as `tailMetricsBasis` in JSON output), alongside the `OutlierDetector` that defined the boundary.

> [!IMPORTANT] Percentiles describe samples, and a sample may be a batch
> When [ops-per-sample calibration](./measurement.md#phase-a---ops-per-sample-calibration-k) resolves `K > 1` (common for bodies under 10 µs), each **sample** is the mean of `K` back-to-back operations. Therefore, percentiles, `Min`, `Max`, and the histogram describe **batch means**, not individual operations. A single slow operation is averaged with its `K-1` neighbors, which understates true per-operation tail latency.
>
> This is a deliberate trade-off to amortize timer overhead. If you need genuine per-op tail latency, pin `OpsPerSample = 1`. Note that at this scale, reported values are dominated by timer resolution and read overhead; compare these results against a baseline measured the same way. Bodies that already span $\ge$ `AutoTune.TargetSampleDurationNs` (10 µs) keep `K = 1` and already provide per-operation percentiles.

### Min and Max

The `Min` and `Max` are the first (`samples[0]`) and last (`samples[n-1]`) values of the sorted tail source (the full pre-trim set by default).

### Sample standard deviation

NBenchmark uses [Bessel's correction](https://en.wikipedia.org/wiki/Bessel%27s_correction) to provide an unbiased estimator of the population standard deviation:

$$s = \sqrt{\frac{1}{n-1} \sum_{i=1}^{n}(x_i - \bar{x})^2}$$

If `n = 1`, the standard deviation is reported as `0`.

## Standard error of the mean

The [standard error of the mean (SEM)](https://en.wikipedia.org/wiki/Standard_error) measures how precisely the mean is estimated. For `n = 1`, SEM is `0`.

When no samples are trimmed, NBenchmark uses the textbook formula:

$$\text{SEM} = \frac{s}{\sqrt{n}}$$

### Winsorized standard error for trimmed data

Applying the standard formula to the trimmed set alone would ignore the variance of the removed samples, artificially tightening the margin of error.

When [outlier trimming](./outliers.md) removes samples, NBenchmark uses the **Winsorized** standard error (Yuen's estimator). With `n` pre-trim samples, `g_L` trimmed from the fast end, `g_U` from the slow end, and `h = n − g_L − g_U` kept:

1. **Winsorize** the sorted pre-trim set: clamp the `g_L` smallest values up to $x_{(g_L+1)}$ and the `g_U` largest values down to $x_{(n-g_U)}$. The sample retains all `n` values.
2. Compute the Winsorized standard deviation $s_w$ using the usual `n − 1` denominator.
3. Rescale the result onto the trimmed mean's sampling distribution:

$$\text{SEM} = \frac{s_w \sqrt{n}}{h}$$

This approach ensures that a trimmed sample still counts as an observation without allowing its magnitude to inflate the width. Moving a trimmed outlier from 100 ns to 100 ms does not change the interval.

**Consistency:** This formula reduces exactly. When nothing is trimmed (`h = n`), the Winsorized sample is identical to the original sample, and the formula collapses to $s/\sqrt{n}$.

**Limitations:** The Winsorized interval describes the **trimmed mean**. It does not restore the uncertainty of the *raw* mean. For information on the raw distribution, refer to the tail percentiles or `AutoTune.AchievedRelativeCiWidth` (see [Raw vs. trimmed statistics](./measurement.md#raw-vs-trimmed-statistics)).

## Confidence interval on the mean

The margin of error (MoE) is the half-width of the confidence interval:

$$\text{MoE} = t^{*}_{\alpha/2,\, \nu} \times \text{SEM}$$

Here, $t^{*}_{\alpha/2,\, \nu}$ is the two-tailed critical value of [Student's t-distribution](https://en.wikipedia.org/wiki/Student%27s_t-distribution) at the configured confidence level. The degrees of freedom $\nu$ are $n - 1$ when nothing is trimmed and $h - 1$ when trimming occurs.

The confidence interval is:

$$\bar{x} \pm \text{MoE} = [\bar{x} - \text{MoE},\; \bar{x} + \text{MoE}]$$

The interval is **not** corrected for the fact that the measurement loop stops at the first sample count that meets its CI target. For more information, see [the optional-stopping correction](../deep-dives/measurement-engine.md#the-optional-stopping-correction).

### Why Student's t instead of the normal distribution?

The [normal distribution](https://en.wikipedia.org/wiki/Normal_distribution) assumes the population standard deviation is known. Because NBenchmark estimates it from the sample, it uses Student's t to compensate with wider critical values for small sample sizes. As `n` grows, the t-distribution shrinks toward the normal distribution.

For typical auto-resolved sample counts (tens to low hundreds), the t critical value at 95% is approximately **1.97-1.98**, which is very close to the normal 1.960.

### Honest caveats

The CI is on the **mean** and relies on the [Central Limit Theorem](https://en.wikipedia.org/wiki/Central_limit_theorem), which assumes the sample mean is approximately normally distributed. This is generally safe for `n ≥ 30`. For very small sample counts, the approximation is weaker, but the t-distribution's heavier tails provide some protection.

### T-critical values in practice

| Confidence level | n = 10 (df=9) | n = 30 (df=29) | n = 200 (df=199) | Normal (df=∞) |
|---|---|---|---|---|
| 90% | 1.833 | 1.699 | 1.652 | 1.645 |
| 95% | 2.262 | 2.045 | 1.972 | 1.960 |
| 99% | 3.250 | 2.756 | 2.601 | 2.576 |

### Dependency-free implementation

NBenchmark computes t critical values without external libraries. It uses exact closed forms for df = 1 and df = 2, and the [Cornish-Fisher expansion](https://en.wikipedia.org/wiki/Cornish%E2%80%93Fisher_expansion) for df $\ge$ 3. The normal quantile uses Acklam's rational approximation (max error < $1.15 \times 10^{-9}$).

These approximations are cross-checked against SciPy on every build. The t critical value matches `scipy.stats.t.ppf` to machine precision for df = 1 and 2, and to **better than 1%** for df $\ge$ 3. See [Validation & Accuracy](./validation.md) for the full tolerance table.

## Confidence interval on the median

While the t-interval describes the mean, the median is the headline comparison metric for NBenchmark. `MedianCiLower` and `MedianCiUpper` report a **distribution-free** confidence interval on the median based on order statistics.

For `n < 50`, the rank bounds are exact, derived from the binomial(`n`, ½) distribution. The interval `[X(l), X(u)]` covers the median with probability $1 − 2·\text{CDF}(l−1)$, where `l` is the largest rank whose lower-tail mass does not exceed $\alpha/2$.

For `n \ge 50`, NBenchmark uses the normal approximation to the binomial: `l = ⌊(n − z√n)/2⌋` and `u = ⌈1 + (n + z√n)/2⌉`, where `z = Φ⁻¹((1+CL)/2)`.

The interval is computed on the same trimmed set as the `Median`. It is always present in JSON output and appears in the advanced-detail stats block.

## Coefficient of variation

$$\text{CV} = \frac{s}{\bar{x}}$$

The CV is a dimensionless relative measure of variability. For example, a CV of 0.05 means the standard deviation is 5% of the mean, indicating a stable benchmark. A CV of 0.5 or higher indicates high variability; treat these results with caution.

## Distribution shape

Three fields describe the shape of the sample distribution.

### Skewness

$$g_1 = \frac{n \sum (x_i - \bar{x})^3}{(n-1)(n-2) s^3}$$

- **Positive skew** (right-tailed): A few slow outliers pull the mean above the median. This is common in benchmarks where GC or scheduler preemption adds occasional spikes.
- **Negative skew** (left-tailed): Most samples are slow and a few are fast. This is unusual but can occur during compiler warmup.
- **Near zero**: The distribution is roughly symmetric.

Skewness is reported as `0` when `n < 3`.

### Kurtosis (excess)

$$g_2 = \frac{n(n+1)\sum (x_i-\bar{x})^4}{(n-1)(n-2)(n-3)s^4} - \frac{3(n-1)^2}{(n-2)(n-3)}$$

NBenchmark reports **excess kurtosis** (kurtosis minus 3), meaning the normal distribution benchmarks at `0`.

- **Positive excess kurtosis** (leptokurtic): Heavier tails than a normal distribution, indicating more extreme outliers.
- **Negative excess kurtosis** (platykurtic): Lighter tails and fewer extremes.
- **Near zero**: Tail weight is similar to a normal distribution.

Excess kurtosis is reported as `0` when `n < 4`.

### Median absolute deviation (MAD, scaled)

$$\text{MAD} = \text{median}(\lvert x_i - \text{median}(x) \rvert) \times 1.4826$$

MAD is a **robust** measure of spread. Because it uses the median rather than the mean, it is far less sensitive to outliers than the standard deviation. The scaling factor `1.4826` makes MAD consistent with the standard deviation $\sigma$ for normally distributed data. If MAD is noticeably smaller than the standard deviation, outliers are inflating the standard deviation more than the bulk of the data warrants.

MAD is reported as `0` when `n < 1`.

## Summary of all reported fields

### Core fields on BenchmarkResult

| Field | Formula / method | Description |
|---|---|---|
| `Median` | Mid-average P50 | [Robust central tendency](https://en.wikipedia.org/wiki/Median). |
| `Mean` | $\bar{x} = \frac{1}{n}\sum x_i$ | [Arithmetic average](https://en.wikipedia.org/wiki/Arithmetic_mean). |
| `Percentiles` | `IReadOnlyList<PercentileEntry>` | Configurable percentile values. Default set includes P50, P95, P99, P99.9, and Max. Controlled by `MeasurementOptions.ReportedPercentiles`. |
| `Histogram` | `LatencyHistogram?` | Latency histogram with bucket boundaries and sample counts. `null` if `EnableHistogram` is `false` or fewer than 2 samples exist. |
| `Min` | $x_1$ (sorted) | [Fastest measured sample](https://en.wikipedia.org/wiki/Sample_maximum_and_minimum). |
| `Max` | $x_n$ (sorted) | [Slowest measured sample](https://en.wikipedia.org/wiki/Sample_maximum_and_minimum). |
| `Q1` | Nearest-rank P25 | [First quartile](https://en.wikipedia.org/wiki/Quartile). |
| `Q3` | Nearest-rank P75 | [Third quartile](https://en.wikipedia.org/wiki/Quartile). |
| `InterquartileRange` | Q3 - Q1 | [Spread of the middle 50% of samples](https://en.wikipedia.org/wiki/Interquartile_range). |
| `LowerFence` | Detector-dependent | [Lower outlier boundary](https://en.wikipedia.org/wiki/Outlier#Tukey%27s_fences). Set only by fence-based detectors. `IqrFence`: $Q1 - k \times \text{IQR}$ (default $k = 1.5$). `MedianAbsoluteDeviation`: $m - t \times \text{scaledMAD}$ (default $t = 3$). `null` otherwise. |
| `UpperFence` | Detector-dependent | [Upper outlier boundary](https://en.wikipedia.org/wiki/Outlier#Tukey%27s_fences). Set only by fence-based detectors. `IqrFence`: $Q3 + k \times \text{IQR}$ (default $k = 1.5$). `MedianAbsoluteDeviation`: $m + t \times \text{scaledMAD}$ (default $t = 3$). `null` otherwise. |
| `OutliersRemoved` | Count of discarded samples | [Number of samples removed by outlier trimming](https://en.wikipedia.org/wiki/Outlier). |
| `N` | Post-trim length | Sample count after outlier removal. |
| `StandardDeviation` | $s = \sqrt{\frac{1}{n-1}\sum(x_i-\bar{x})^2}$ | Spread of measurements (Bessel). |
| `StandardError` | $s/\sqrt{n}$ (untrimmed); $s_w\sqrt{n}/h$ [Winsorized](#winsorized-standard-error-for-trimmed-data) | Precision of the mean estimate. |
| `MarginOfError` | $t^{*} \times \text{SEM}$, on $n-1$ df untrimmed and $h-1$ after trimming | Half-width of CI on the mean. |
| `ConfidenceIntervalLower` | $\bar{x} - \text{MoE}$ | Lower CI bound. |
| `ConfidenceIntervalUpper` | $\bar{x} + \text{MoE}$ | Upper CI bound. |
| `MedianCiLower` / `MedianCiUpper` | Order-statistic interval | Distribution-free confidence interval on the median. `null` when undefined ($n < 2$, dry-run, or errored). |
| `MedianShift` | Hodges-Lehmann + Lehmann CI | Location shift vs. baseline in ns/op. Positive means the candidate is slower. `null` for the baseline or when significance was not tested. |
| `CoefficientOfVariation` | $s / \bar{x}$ | Relative variability. |
| `Skewness` | $g_1 = \frac{n \sum (x_i - \bar{x})^3}{(n-1)(n-2) s^3}$ | [Sample skewness](https://en.wikipedia.org/wiki/Skewness). Zero for $n < 3$. |
| `Kurtosis` | $g_2$ (excess) | [Excess kurtosis](https://en.wikipedia.org/wiki/Kurtosis). Zero for $n < 4$. |
| `Mad` | Scaled MAD | [Median absolute deviation](https://en.wikipedia.org/wiki/Median_absolute_deviation). Zero for $n < 1$. |
| `PValue` | Mann-Whitney U | Two-tailed pairwise p-value vs. baseline. `null` for omnibus cases. |
| `SignificanceVerdict` | $p < \alpha$ | Whether the pairwise difference is real (`Significant`, `NotSignificant`, or `NotTested`). |
| `Omnibus` | Kruskal-Wallis | Across-all-groups verdict for three or more benchmarks. |
| `SignificanceTestName` | - | Display name of the pairwise significance test used. |
| `OutlierDetector` | - | Display name of the outlier detector applied. |
| `MeanAllocatedBytes` | Mean of iteration deltas | Mean heap allocation per iteration. |
| `AllocMedian` | Mid-average P50 | Median allocation per iteration. |
| `AllocP95` | Nearest-rank P95 | P95 allocation per iteration. |
| `AllocMax` | Max of iteration deltas | Max allocation per iteration. |

### Provenance fields

Provenance fields record the run configuration used for a result, allowing users to group, filter, or interpret results by settings. These are set by the measuring process.

| Field | Source | Description |
|---|---|---|
| `RuntimeProfileName` | Measuring process environment | The runtime-startup configuration actually used for measurement. `"host"` means the measurement ran in a process NBenchmark did not launch. |
| `RuntimeKnobs` | Measuring process environment | The runtime-startup knobs in effect (e.g., `"tiered=off pgo=off r2r=off"`). |
| `ThreadControlEnabled` | `MeasurementOptions.Environment` | Whether thread-level environment control (affinity, priority, etc.) was enabled. |
| `InterferenceFilterEnabled` | `MeasurementOptions.Interference` | Whether the evidence-based interference rejection filter was enabled. |

### Throughput fields

| Field | Formula | Description |
|---|---|---|
| `OperationsPerSecond` | `1e9 / Mean` | Mean operations per second. `NaN` for errored or dry-run results. |
| `MedianOperationsPerSecond` | `1e9 / Median` | Median operations per second. `NaN` for errored or dry-run results. |
| `NanosecondsPerOperation` | Alias for `Mean` | Convenience alias for the mean timing in nanoseconds per operation. |
| `TotalOperations` | `MeasuredIterations + WarmupIterations`, or `AutoTuneDiagnostic.TotalBodyInvocations` when auto-tuning | Total body invocations across warmup and measurement. |

### Computed properties

| Property | Formula | Description |
|---|---|---|
| `Range` | Max - Min | [Full spread of trimmed samples](https://en.wikipedia.org/wiki/Range_(statistics)). |
| `StandardErrorPercent` | $\text{SEM} / \bar{x} \times 100$ | Standard error as a percentage of the mean. |
| `MarginPercent` | $\text{MoE} / \bar{x} \times 100$ | Margin of error as a percentage of the mean. |
| `CoefficientOfVariationPercent` | $\text{CV} \times 100$ | Coefficient of variation as a percentage. |
