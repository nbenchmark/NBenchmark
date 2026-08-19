---
title: Descriptive Statistics
description: Mean, median, percentiles, confidence intervals, and the complete BenchmarkResult field reference.
order: 4
---

# Descriptive Statistics

## Descriptive statistics

Given a sorted, trimmed array of `n` samples (the two exceptions are noted where they arise: tail metrics read the pre-trim set by default, and the standard error accounts for what trimming removed):

### Mean

$$\bar{x} = \frac{1}{n} \sum_{i=1}^{n} x_i$$

### Median

The **mid-average** convention: the middle value for odd `n`, and the mean of the two middle order statistics for even `n`. This matches `numpy.median` and the median NBenchmark uses elsewhere (per-launch aggregation, jitter calibration), so the reported `Median` and the `P50` percentile agree. Every other percentile uses the nearest-rank method (see below); the median is the sole exception, because the mid-average removes the small systematic downward bias nearest-rank has on even `n`.

### Percentiles

Configurable percentile values computed via the [nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method) method: `i = ceil(p × n)`. The median (`p = 0.50`) is the one exception - it mid-averages the two middles on even `n` (see [Median](#median) above); `Q1`/`Q3` and every other percentile stay nearest-rank. Controlled by `MeasurementOptions.ReportedPercentiles` (default: P50, P95, P99, P99.9, Max). Each entry is a `PercentileEntry` with a `Percentile` (0-1) and `Value` (nanoseconds). Access a specific percentile with `result.GetPercentile(0.95)`.

**Tail metrics are computed from the full pre-trim distribution by default.** Percentiles, `Min`, `Max`, and the histogram describe the shape of the distribution, so they are computed from the raw (pre-trim) sample set - `MeasurementOptions.TailMetricsBasis = Raw`, the default. This keeps them honest: the IQR/MAD fence removes exactly the slow tail that P99/P99.9/Max exist to describe, so a GC pause the `Realistic` profile deliberately timed appears in `Max` rather than being trimmed out of it. Central-tendency and dispersion statistics (mean, standard deviation, CI, CV, skewness, kurtosis, MAD, median, median CI) always stay on the **trimmed** set, so a fenced-out spike never moves the mean or inflates the interval. Set `TailMetricsBasis = Trimmed` (or `--tail-basis trimmed`) to compute tail metrics from the inlier set instead.

Because the split is not obvious from the numbers themselves, the basis that was actually used is recorded on the result as `BenchmarkResult.TailMetricsBasis` (and emitted as `tailMetricsBasis` by the JSON reporter), alongside `OutlierDetector` naming the detector that drew the line. Anything rendering both groups - a table, a dashboard, a chart axis - should say which basis each number came from rather than leaving a reader to assume one distribution. `Max` sitting hundreds of times above `Median` is normal under the default basis and does not mean the mean is wrong; it means they describe different sample sets.

> [!IMPORTANT] Percentiles describe samples, and a sample may be a batch
> When [ops-per-sample calibration](./measurement.md#phase-a---ops-per-sample-calibration-k) resolves `K > 1` (the norm for sub-10 µs bodies), each **sample** is the mean of `K` back-to-back operations. Percentiles, Min, Max, and the histogram are therefore over **batch means**, not individual operations - a single slow op is averaged with its `K-1` neighbors, so the tail percentiles understate true per-operation tail latency. This is the deliberate cost of amortizing timer overhead on fast bodies. When you need genuine per-op tail latency, pin `OpsPerSample = 1` (accepting that at that scale the reported values are dominated by timer resolution and read overhead - compare against a baseline measured the same way). Bodies that already span ≥ `AutoTune.TargetSampleDurationNs` (10 µs) keep `K = 1`, so their percentiles are already per-operation.

### Min and Max

`samples[0]` and `samples[n-1]` of the sorted tail source (the full pre-trim set by default; see the tail-metrics note above).

### Sample standard deviation ([Bessel's correction](https://en.wikipedia.org/wiki/Bessel%27s_correction))

$$s = \sqrt{\frac{1}{n-1} \sum_{i=1}^{n}(x_i - \bar{x})^2}$$

The `n-1` denominator (Bessel's correction) makes `s` an unbiased estimator of the population standard deviation. For `n = 1`, the [standard deviation](https://en.wikipedia.org/wiki/Standard_deviation) is reported as `0`.

## [Standard error of the mean](https://en.wikipedia.org/wiki/Standard_error)

When nothing was trimmed, SEM is the textbook formula:

$$\text{SEM} = \frac{s}{\sqrt{n}}$$

SEM measures how precisely the mean is estimated. For `n = 1`, SEM is `0`.

### After outlier trimming: the Winsorized standard error

Applying that formula to the trimmed set alone answers a question nobody asked - "how precisely would this mean be known if the run had produced exactly these `h` samples and no others?" The variance the fence removed just disappears, so the margin tightens by however much was trimmed, always in the same direction.

So when [outlier trimming](./outliers.md) removed samples, the standard error is the **Winsorized** one (Yuen's estimator). With `n` pre-trim samples, `g_L` trimmed from the fast end, `g_U` from the slow end, and `h = n − g_L − g_U` kept:

1. **Winsorize** the sorted pre-trim set: clamp the `g_L` smallest up to $x_{(g_L+1)}$ and the `g_U` largest down to $x_{(n-g_U)}$. Nothing is dropped; the sample keeps all `n` values.
2. Take the Winsorized standard deviation $s_w$ with the usual `n − 1` denominator.
3. Rescale onto the trimmed mean's own sampling distribution:

$$\text{SEM} = \frac{s_w \sqrt{n}}{h}$$

A trimmed sample therefore counts as an observation - the run did produce a reading out past the fence - without its magnitude setting the width. Move a trimmed outlier from 100 ns to 100 ms and the interval does not change at all. That is the property that makes it robust rather than merely wider.

**It reduces exactly.** With nothing trimmed, `h = n`, the Winsorized sample is the original sample, and this collapses to $s/\sqrt{n}$ on `n − 1` degrees of freedom - the formula above, unchanged. A clean run's numbers do not move.

**What it does not do.** It makes the interval correct for what it describes: the **trimmed mean**. It does not restore the uncertainty of the *raw* mean, because clamping deliberately discards the outliers' magnitude. A body whose raw distribution is far wider than its trimmed one is described by the tail percentiles (computed on the raw set by default) and by `AutoTune.AchievedRelativeCiWidth` - see [Raw vs. trimmed statistics](./measurement.md#raw-vs-trimmed-statistics).

## [Confidence interval](https://en.wikipedia.org/wiki/Confidence_interval) on the mean

The margin of error is the half-width of the confidence interval:

$$\text{MoE} = t^{*}_{\alpha/2,\, \nu} \times \text{SEM}$$

where $t^{*}_{\alpha/2,\, \nu}$ is the two-tailed critical value of [Student's t-distribution](https://en.wikipedia.org/wiki/Student%27s_t-distribution) at the configured confidence level, on $\nu = n - 1$ degrees of freedom when nothing was trimmed and $\nu = h - 1$ when it was (matching the standard error above).

The confidence interval is:

$$\bar{x} \pm \text{MoE} = [\bar{x} - \text{MoE},\; \bar{x} + \text{MoE}]$$

The interval is **not** corrected for the fact that the measurement loop stops at the first sample count that meets its CI target - see [the optional-stopping correction](../deep-dives/measurement-engine.md#the-optional-stopping-correction) for why, and what to read instead.

### Why Student's t and not the normal distribution?

The [normal distribution](https://en.wikipedia.org/wiki/Normal_distribution)'s critical value (e.g. 1.96 for 95%) assumes the population standard deviation is known. In benchmarking it is not - we estimate it from the sample. Student's t compensates by using wider critical values for small sample sizes, shrinking towards the normal as `n` grows.

With a typical auto-resolved sample count (tens to low hundreds), the t critical value at 95% sits around **1.97-1.98** - very close to the normal 1.960, so the practical difference is small.

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

## Confidence interval on the median

The t-interval above is on the **mean**, but the median is NBenchmark's headline comparison metric (ratios and the Mann-Whitney semantics both key off it). `MedianCiLower`/`MedianCiUpper` report a **distribution-free** confidence interval on the median built from order statistics - no normality assumption.

For `n < 50` the rank bounds are exact, from the binomial(`n`, ½) distribution: the interval `[X(l), X(u)]` (1-based order statistics) covers the median with probability `1 − 2·CDF(l−1)`, and `l` is the largest rank whose lower-tail mass does not exceed `α/2`. This can only be conservative (coverage ≥ the requested level). For `n ≥ 50` the normal approximation to the binomial gives `l = ⌊(n − z√n)/2⌋`, `u = ⌈1 + (n + z√n)/2⌉` with `z = Φ⁻¹((1+CL)/2)`. Ranks are clamped into `[1, n]`; when even the widest interval cannot reach the requested level (tiny `n`, high `CL`) the full range is returned.

The interval is computed on the same (trimmed) set as `Median`. It appears in the advanced-detail stats block and is always present in JSON.

## [Coefficient of variation](https://en.wikipedia.org/wiki/Coefficient_of_variation)

$$\text{CV} = \frac{s}{\bar{x}}$$

A dimensionless relative measure of variability. A CV of 0.05 means the standard deviation is 5% of the mean - the benchmark is fairly stable. A CV of 0.5 or higher indicates high variability and the results should be treated with caution.

## Distribution shape

Three fields describe the *shape* of the sample distribution, not just its central tendency or spread.

### [Skewness](https://en.wikipedia.org/wiki/Skewness)

$$g_1 = \frac{n \sum (x_i - \bar{x})^3}{(n-1)(n-2) s^3}$$

- **Positive skew** (right-tailed): a few slow outliers pull the mean above the median. Common in benchmarks where scheduler preemption or GC adds occasional spikes.
- **Negative skew** (left-tailed): most samples are slow and a few are fast - unusual in benchmarking, but can appear after compiler warmup where early iterations are slower.
- **Near zero**: roughly symmetric distribution.

Skewness is reported as `0` when `n < 3`.

### [Kurtosis](https://en.wikipedia.org/wiki/Kurtosis) (excess)

$$g_2 = \frac{n(n+1)\sum (x_i-\bar{x})^4}{(n-1)(n-2)(n-3)s^4} - \frac{3(n-1)^2}{(n-2)(n-3)}$$

This is **excess kurtosis** (kurtosis minus 3), so the normal distribution benchmarks at `0`.

- **Positive excess kurtosis** (leptokurtic): heavier tails than a normal distribution. More extreme outliers than expected under normality. A benchmark with occasional GC pauses or page faults often shows this.
- **Negative excess kurtosis** (platykurtic): lighter tails, fewer extremes. Rare in benchmarking; can occur when samples are tightly clamped by hardware limits.
- **Near zero**: tail weight similar to a normal distribution.

Excess kurtosis is reported as `0` when `n < 4`.

### [Median absolute deviation](https://en.wikipedia.org/wiki/Median_absolute_deviation) (MAD, scaled)

$$\text{MAD} = \text{median}(\lvert x_i - \text{median}(x) \rvert) \times 1.4826$$

MAD is a **robust** measure of spread - it uses the median rather than the mean, so it is far less sensitive to outliers than the standard deviation. The scaling factor `1.4826` makes MAD consistent with the standard deviation $\sigma$ for normally distributed data, which means the two can be compared directly: if MAD is noticeably smaller than the standard deviation, outliers are inflating the standard deviation more than the bulk of the data warrant.

Reported as `0` when `n < 1`.

## Summary of all reported fields

### Core fields on BenchmarkResult

| Field | Formula / method | Description |
|---|---|---|
| `Median` | Mid-average P50 (mean of the two middles on even `n`) | [Robust central tendency](https://en.wikipedia.org/wiki/Median). |
| `Mean` | $\bar{x} = \frac{1}{n}\sum x_i$ | [Arithmetic average](https://en.wikipedia.org/wiki/Arithmetic_mean). |
| `Percentiles` | `IReadOnlyList<PercentileEntry>` | Configurable percentile values. Default set includes P50 (0.50), P95 (0.95), P99 (0.99), P99.9 (0.999), Max (1.0). Controlled by `MeasurementOptions.ReportedPercentiles`. Access via `GetPercentile(p)`. |
| `Histogram` | `LatencyHistogram?` | Latency histogram with bucket boundaries and sample counts. `null` when `EnableHistogram` is `false` or fewer than 2 samples. |
| `Min` | $x_1$ (sorted) | [Fastest measured sample](https://en.wikipedia.org/wiki/Sample_maximum_and_minimum). |
| `Max` | $x_n$ (sorted) | [Slowest measured sample](https://en.wikipedia.org/wiki/Sample_maximum_and_minimum). |
| `Q1` | Nearest-rank P25 | [First quartile](https://en.wikipedia.org/wiki/Quartile). |
| `Q3` | Nearest-rank P75 | [Third quartile](https://en.wikipedia.org/wiki/Quartile). |
| `InterquartileRange` | Q3 - Q1 | [Spread of the middle 50% of samples](https://en.wikipedia.org/wiki/Interquartile_range). |
| `LowerFence` | Detector-dependent | [Lower outlier boundary](https://en.wikipedia.org/wiki/Outlier#Tukey%27s_fences); set only by fence-based detectors. `IqrFence`: $Q1 - k \times \text{IQR}$ (default $k = 1.5$). `MedianAbsoluteDeviation`: $m - t \times \text{scaledMAD}$ (default $t = 3$). `null` otherwise. |
| `UpperFence` | Detector-dependent | [Upper outlier boundary](https://en.wikipedia.org/wiki/Outlier#Tukey%27s_fences); set only by fence-based detectors. `IqrFence`: $Q3 + k \times \text{IQR}$ (default $k = 1.5$). `MedianAbsoluteDeviation`: $m + t \times \text{scaledMAD}$ (default $t = 3$). `null` otherwise. |
| `OutliersRemoved` | Count of discarded samples | [Number of samples removed by outlier trimming](https://en.wikipedia.org/wiki/Outlier). |
| `N` | Post-trim length | Sample count after outlier removal. |
| `StandardDeviation` | $s = \sqrt{\frac{1}{n-1}\sum(x_i-\bar{x})^2}$ | Spread of measurements (Bessel). |
| `StandardError` | $s/\sqrt{n}$ untrimmed; $s_w\sqrt{n}/h$ [Winsorized](#after-outlier-trimming-the-winsorized-standard-error) after trimming | Precision of the mean estimate. |
| `MarginOfError` | $t^{*} \times \text{SEM}$, on $n-1$ df untrimmed and $h-1$ after trimming | Half-width of CI on the mean. |
| `ConfidenceIntervalLower` | $\bar{x} - \text{MoE}$ | Lower CI bound. |
| `ConfidenceIntervalUpper` | $\bar{x} + \text{MoE}$ | Upper CI bound. |
| `MedianCiLower` / `MedianCiUpper` | Order-statistic interval | Distribution-free confidence interval on the median (exact binomial for $n < 50$, normal approximation above). `null` when undefined ($n < 2$, dry-run, errored). |
| `MedianShift` | Hodges-Lehmann + Lehmann CI | Location shift vs. baseline (median of pairwise candidate − baseline differences) with a rank-based interval, in ns/op. Positive = candidate slower. `null` for the baseline or when significance did not run. |
| `CoefficientOfVariation` | $s / \bar{x}$ | Relative variability. |
| `Skewness` | $g_1 = \frac{n \sum (x_i - \bar{x})^3}{(n-1)(n-2) s^3}$ | [Sample skewness](https://en.wikipedia.org/wiki/Skewness). Zero for $n < 3$. |
| `Kurtosis` | $g_2 = \frac{n(n+1)\sum (x_i-\bar{x})^4}{(n-1)(n-2)(n-3)s^4} - \frac{3(n-1)^2}{(n-2)(n-3)}$ | [Excess kurtosis](https://en.wikipedia.org/wiki/Kurtosis). Zero for $n < 4$. |
| `Mad` | $\text{median}(\lvert x_i - \text{median}(x) \rvert) \times 1.4826$ | [Median absolute deviation](https://en.wikipedia.org/wiki/Median_absolute_deviation) (scaled to $\sigma$). Zero for $n < 1$. |
| `PValue` | Mann-Whitney U | Two-tailed pairwise p-value vs. baseline. `null` for the omnibus case (three or more groups - see `Omnibus`). |
| `SignificanceVerdict` | $p < \alpha$ | Whether the pairwise difference is real (`Significant`, `NotSignificant`, or `NotTested`). |
| `Omnibus` | Kruskal-Wallis | The across-all-groups verdict when three or more benchmarks are compared; `null` otherwise. Holds `TestName`, `Statistic`, `DegreesOfFreedom`, `GroupCount`, `PValue`, and `Verdict`. |
| `SignificanceTestName` | - | Display name of the pairwise significance test used (e.g. `"Mann-Whitney U"`). |
| `OutlierDetector` | - | Display name of the outlier detector applied (e.g. `"IQR fence (1.5×)"` or `"MAD (3×)"`). |
| `MeanAllocatedBytes` | Mean of iteration deltas | Mean heap allocation per iteration. |
| `AllocMedian` | Mid-average P50 of iteration deltas | Median allocation per iteration (only when `MeasureAllocations = true`). |
| `AllocP95` | Nearest-rank P95 of iteration deltas | P95 allocation per iteration (only when `MeasureAllocations = true`). |
| `AllocMax` | Max of iteration deltas | Max allocation per iteration (only when `MeasureAllocations = true`). |

### Provenance fields

These record the run configuration a result was measured under, so a consumer can group, filter, or interpret results by the settings that produced them. They are set once by the measuring process and ride through every aggregation unchanged.

| Field | Source | Description |
|---|---|---|
| `RuntimeProfileName` | Measuring process environment | The runtime-startup configuration this result was *actually* measured under - not the one requested. `"host"` means the measurement ran in a process NBenchmark did not launch. |
| `RuntimeKnobs` | Measuring process environment | The runtime-startup knobs in effect, e.g. `"tiered=off pgo=off r2r=off"`. Read from the measuring process's own environment so a hand-set knob is reported as faithfully as one NBenchmark applied. |
| `ThreadControlEnabled` | `MeasurementOptions.Environment` | Whether thread-level environment control was enabled: thread affinity, thread priority, and (on macOS) the `QOS_CLASS_USER_INTERACTIVE` elevation that keeps the measuring thread on an Apple Silicon performance core. On by default; `false` when disabled via `--no-thread-control` / `.WithThreadControl(false)`. Records the configured setting, not the application outcome - on macOS the elevation is refused on thread-pool threads (the default worker path), and that refusal is reported via the console warning rather than this field. |
| `InterferenceFilterEnabled` | `MeasurementOptions.Interference` | Whether the evidence-based interference rejection filter was enabled - the pre-stage that discards samples the OS is known to have preempted before outlier trimming runs. On by default; `false` when disabled via `--no-interference-filter` / `.WithInterferenceFilter(false)`. Distinct from `AutoTuneDiagnostic.InterferenceDisabledReason`, which records *involuntary* disablement by the engine (unsupported platform, probe too costly, too few known-occupancy samples); this field records the user's configured setting. |

### Throughput fields

| Field | Formula | Description |
|---|---|---|
| `OperationsPerSecond` | `1e9 / Mean` when timing is in nanoseconds | Mean operations per second. `NaN` for errored or dry-run results. |
| `MedianOperationsPerSecond` | `1e9 / Median` when timing is in nanoseconds | Median operations per second. `NaN` for errored or dry-run results. |
| `NanosecondsPerOperation` | Alias for `Mean` | Convenience alias that expresses the mean timing as nanoseconds per operation. |
| `TotalOperations` | `MeasuredIterations + WarmupIterations`, or `AutoTuneDiagnostic.TotalBodyInvocations` when auto-tuning | Total body invocations executed across warmup and measurement. |

### Computed properties

| Property | Formula | Description |
|---|---|---|
| `Range` | Max - Min | [Full spread of trimmed samples](https://en.wikipedia.org/wiki/Range_(statistics)). |
| `StandardErrorPercent` | $\text{SEM} / \bar{x} \times 100$ | Standard error as a percentage of the mean. |
| `MarginPercent` | $\text{MoE} / \bar{x} \times 100$ | Margin of error as a percentage of the mean. |
| `CoefficientOfVariationPercent` | $\text{CV} \times 100$ | Coefficient of variation as a percentage. |
