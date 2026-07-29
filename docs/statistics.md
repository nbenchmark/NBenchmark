# significance.md

---
title: Significance Testing
description: How NBenchmark decides whether benchmark differences are statistically real - the Mann-Whitney U test for two groups and the Kruskal-Wallis omnibus test (with post-hoc pairwise Mann-Whitney U and Holm-Bonferroni correction) for three or more. Plus Cliff's delta effect size and the MinimumPracticalEffect practical-significance gate (on by default at 0.147).
order: 5
---

# Significance Testing

When two or more benchmarks have been run, NBenchmark tests whether their differences are statistically real rather than measurement noise. The test it picks depends on how many benchmarks you are comparing:

| Groups | Default test | What it answers |
|---|---|---|
| Exactly 2 | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (pairwise) | Does the candidate differ from the baseline? |
| 3 or more | [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) (omnibus) + post-hoc Mann-Whitney U with [Holm-Bonferroni](https://en.wikipedia.org/wiki/Holm%E2%80%93Bonferroni_method) correction | Does each candidate differ from the baseline? (gated on the omnibus) |

### Scope: suite mode versus Harness mode

- In **suite mode** (`BenchmarkSuite`), significance is computed across every benchmark in that one suite. A single baseline is chosen from the whole suite.
- In **Harness mode** (`BenchmarkHarness`), significance is computed **per class** by default. Each discovered class gets its own baseline, and `Sig` / `Magnitude` are relative to that class's baseline. The console reporter renders one comparison table per class.
- Pass `--cross-class` on the CLI or call `WithCrossClassSignificance()` in code to compute significance across all classes in a single comparison table. The baseline is chosen from the whole group, and the reporter adds a `Class` column so rows can be distinguished. Use this when comparing implementations that live in separate classes (e.g. a legacy version and a refactored version). Cross-class mode is opt-in because mixing unrelated benchmark classes into one significance table produces a baseline that may be semantically meaningless.

## Interpreting the output

### The Sig column

| Symbol | Meaning |
|---|---|
| **✓** | The difference from the baseline is statistically significant (p < alpha, default 0.05). It is very unlikely to be noise. |
| **✗** | The difference is not statistically significant. You cannot confidently conclude one is faster than the other. |
| (blank) | The benchmark is the baseline, or significance was not tested (fewer than 2 samples in a group, or the omnibus was not significant). |

**What to do:**
- A ✓ with a small Ratio (e.g. `1.01x`) means the difference is statistically real but may be too small to matter in practice. Check the Magnitude column.
- A ✗ with a large Ratio (e.g. `1.5x`) means the measurements are too noisy to tell. Try reducing noise (see [Tuning for noisy CI](../reference/configuration.md#tuning-for-noisy-ci-environments)) or collecting more samples.

The significance threshold (alpha) is configurable via `MeasurementOptions.SignificanceLevel`, the `.WithSignificanceLevel(...)` fluent method, or the `--alpha` CLI flag. Lower it (e.g. `0.01`) to demand stronger evidence before calling a difference real.

### The Magnitude column

A p-value tells you whether a difference is unlikely under the null, but not *how large* the difference is. With many iterations a tiny 0.1 ns shift can be "statistically significant" while being practically meaningless. NBenchmark reports the effect size alongside the p-value as a **Magnitude** column (Negligible / Small / Medium / Large) classified from **Cliff's delta**.

| Magnitude | `\|delta\|` range | What it means |
|---|---|---|
| Negligible | `[0, 0.147)` | The two distributions overlap almost completely. The difference is tiny. |
| Small | `[0.147, 0.33)` | A modest but detectable shift. |
| Medium | `[0.33, 0.474)` | A clear, practically meaningful difference. |
| Large | `[0.474, 1.0]` | The distributions barely overlap. A very strong difference. |

The sign convention is: **positive delta = candidate tends to be slower than baseline** (shown in red in the console reporter); negative = candidate is faster (shown in green).

**What to do:** A statistically significant result (✓) with a Negligible magnitude means the difference is real but too small to care about. Focus on results with Small, Medium, or Large magnitudes.

### Practical-significance gate

`MeasurementOptions.MinimumPracticalEffect` requires a minimum practical-effect score in `[0, 1]` for a comparison to count as meaningful. **It defaults to `0.147`** — the Romano negligible/small boundary (the same cutoff the Magnitude column uses) — so out of the box a ✓ verdict means "statistically real **and** at least a small effect", not merely "p < alpha". Built-in Mann-Whitney tests map this score to `|delta|`; custom tests can map any effect metric by returning `EffectSize.PracticalValue` in `PairwiseComparison`.

- Comparisons with practical effect below the threshold are reported with `Magnitude = neg` (so a sub-threshold result is never labelled `large`).
- The Sig verdict is downgraded from `Significant` to `NotSignificant` even when the p-value is below alpha, and a **warning records the downgrade** (visible in the reporters' warnings section) so the change is discoverable rather than silent.
- The configured value must be in the range `[0, 1]`. Set it to **`0`** (`--min-practical-effect 0`) to restore p-value-only verdicts, or to `null` in code to disable the gate entirely.

The engine enforces the gate in `Significance.ApplyReport` after the test runs, so it works for any `ISignificanceTest` implementation - not just the built-in ones. Custom tests that return an `EffectSize` with `PracticalValue` are gated automatically; tests that do not return a practical value are unaffected.

```csharp
// Restore p-value-only verdicts (disable the default gate)
.WithMinimumPracticalEffect(0)

// Or demand a stronger effect: reject significance below |delta| = 0.33 (the "medium" threshold)
.WithMinimumPracticalEffect(0.33)
```

Set it via `MeasurementOptions.MinimumPracticalEffect`, `BenchmarkSuite.WithMinimumPracticalEffect(...)` / `BenchmarkHarness.WithMinimumPracticalEffect(...)`, or `--min-practical-effect <0-1>` on the CLI.

Leave it `null` (the default) to keep p-value-only Sig semantics, in which case the Magnitude column is purely informational.

### The omnibus line (three or more groups)

When three or more benchmarks are compared, the console and Markdown reporters print an omnibus line below the table:

```
Omnibus Kruskal-Wallis across 3 groups: H(2) = 7.20, p = 0.027 → significant
```

If the omnibus is **significant** (at least one group differs), the per-row Sig column shows the Holm-Bonferroni-corrected verdict for each candidate versus the baseline. If the omnibus is **not significant**, no post-hoc comparisons run and the per-row Sig column stays blank.

### Minimum sample requirement

The test requires at least **2 samples in each group**. With fewer samples the U statistic is undefined and the test returns no result (the Sig column stays blank).

### Pre-trim raw samples

NBenchmark uses the **pre-trim raw samples** (before outlier removal) for significance testing. This gives the test more data to work with. However it means that significance is assessed on the full distribution including extreme measurements.

---

## Technical detail: Mann-Whitney U test (two groups)

NBenchmark tests whether the difference in two benchmarks' distributions is statistically significant using the **Mann-Whitney U test** (also called the Wilcoxon rank-sum test).

### Why Mann-Whitney U?

Benchmark timings are typically right-skewed (a few slow outliers) and do not follow a normal distribution. Parametric tests like the t-test assume normality. The Mann-Whitney U test is **[non-parametric](https://en.wikipedia.org/wiki/Nonparametric_statistics)** - it ranks combined values rather than computing moments, and makes no distributional assumptions.

### Algorithm

Given the **pre-trim raw samples** of two benchmarks A (length n₁) and B (length n₂):

1. Merge and sort all `n₁ + n₂` values together, recording which sample each came from.
2. Assign **mid-ranks** to tied values: all tied observations share the average rank of the positions they occupy.
3. Compute the rank sum for group A: $R_1 = \sum \text{rank}(A_i)$.
4. Compute the U statistics:

$$U_1 = R_1 - \frac{n_1(n_1+1)}{2}, \quad U_2 = n_1 n_2 - U_1, \quad U = \min(U_1, U_2)$$

1. Depending on the sample sizes:
   - **Small, tie-free samples** (combined `n₁ + n₂ ≤ 20` with no tied values): compute the **exact** two-sided [permutation](https://en.wikipedia.org/wiki/Permutation_test) p-value by enumerating the full distribution of U over all rank assignments (via a bounded-partition dynamic program). This matches `scipy.stats.mannwhitneyu(..., method='exact')`.
   - **Otherwise**: use the [normal approximation](https://en.wikipedia.org/wiki/Normal_distribution#Central_limit_theorem) with a **tie correction** and a **continuity correction** to compute a z-score, then derive a two-tailed [p-value](https://en.wikipedia.org/wiki/P-value).

A [p-value](https://en.wikipedia.org/wiki/P-value) below the configured significance level (alpha, default **0.05**) is considered significant (✓ in the Sig column).

For small samples, using the exact test avoids approximation error: the asymptotic p-value can differ from the exact permutation p-value by up to ≈ 0.05. For larger samples the continuity-corrected normal approximation is accurate and matches SciPy's asymptotic method closely; the exact and approximate paths are cross-checked against SciPy in [Validation & Accuracy](./validation.md).

> [!NOTE]
> NBenchmark uses the **pre-trim raw samples** (before outlier removal) for significance testing. This gives the test more data to work with. However it means that significance is assessed on the full distribution including extreme measurements.

## Technical detail: Kruskal-Wallis test (three or more groups)

When three or more benchmarks are compared, running a series of pairwise Mann-Whitney U tests would inflate the false-positive rate (the [multiple-comparisons problem](https://en.wikipedia.org/wiki/Multiple_comparisons_problem)). Instead NBenchmark first runs the **Kruskal-Wallis H test** once across all groups - the rank-based generalization of one-way [ANOVA](https://en.wikipedia.org/wiki/Analysis_of_variance) - and reports a single **omnibus** verdict: *are any of these groups drawn from different distributions?*

### Algorithm

Given `k` groups of **pre-trim raw samples** with total size `N = Σnᵢ`:

1. Rank all `N` values together, assigning **mid-ranks** to ties.
2. Sum the ranks within each group: `Rᵢ`.
3. Compute the H statistic:

$$H = \frac{12}{N(N+1)} \sum_{i=1}^{k} \frac{R_i^2}{n_i} - 3(N+1)$$

1. Apply the **tie correction** factor $C = 1 - \frac{\sum (t^3 - t)}{N^3 - N}$ (summed over each set of `t` tied values) and divide: `H ← H / C`.
2. Under the null hypothesis, `H` follows a [chi-squared distribution](https://en.wikipedia.org/wiki/Chi-squared_distribution) with `k − 1` degrees of freedom. The p-value is its [survival function](https://en.wikipedia.org/wiki/Survival_function) $P(\chi^2_{k-1} \ge H)$, computed from the regularized upper incomplete gamma function.

A p-value below alpha means **at least one** group differs - the omnibus test does not say *which*.

(For three groups `{1,2,3}`, `{4,5,6}`, `{7,8,9}` the statistic is `H = 7.2` on `2` degrees of freedom, `p ≈ 0.027`.) When every value is identical (`H = 0`, `p = 1`) or fewer than two groups have data, the test reports "not tested".

### Post-hoc pairwise comparisons

If the Kruskal-Wallis omnibus is significant, NBenchmark follows up with a **pairwise Mann-Whitney U test** for each candidate versus the baseline. To control the family-wise error rate across the `m` tested candidate comparisons (finite p-values), the raw p-values are adjusted with the **Holm-Bonferroni** step-down procedure:

1. Sort the `m` raw p-values ascending: $p_{(1)} \le p_{(2)} \le \dots \le p_{(m)}$.
2. For each step `j` (0-indexed), compute the adjusted p-value:
   $$p_{(j)}^{\text{adj}} = \max\left(\min\left((m - j) \cdot p_{(j)}, 1\right), p_{(j-1)}^{\text{adj}}\right)$$
   where $p_{(-1)}^{\text{adj}} = 0$.
3. A candidate is marked **significant** (✓) when its adjusted p-value is below the configured significance level (alpha).

Candidates whose pairwise test cannot be computed (for example, fewer than 2 samples in either group) keep `PValue = null` and `SignificanceVerdict = NotTested`, and are excluded from `m`.

The per-row `PValue` field on `BenchmarkResult` stores the **raw** Mann-Whitney U p-value (not the adjusted one), so you can inspect the original test statistic. The verdict in `SignificanceVerdict` reflects the Holm-Bonferroni-corrected decision and is the authoritative signal for significance - always read `SignificanceVerdict` rather than comparing `PValue` to alpha yourself, since the raw p-value and the corrected verdict can disagree when the Holm adjustment flips a candidate across the threshold.

If the omnibus is **not** significant, no post-hoc comparisons run. The per-row `PValue` and `SignificanceVerdict` stay at their defaults (`null` and `NotTested`), and the omnibus verdict is attached to every result's `Omnibus` field.

> [!NOTE]
> The post-hoc step only runs when the omnibus is significant. This two-stage procedure (omnibus gate then pairwise correction) preserves the family-wise error rate while giving you per-benchmark significance indicators in the table.

## Technical detail: Cliff's delta

Cliff's delta is a non-parametric effect size that quantifies how often one sample's value exceeds the other's:

$$\delta = \frac{\#(b > a) - \#(b < a)}{n_1 \cdot n_2}$$

with `a` = baseline and `b` = candidate samples. It ranges over `[-1, 1]`:

| delta | Interpretation |
|---|---|
| `+1` | Every candidate sample exceeds every baseline sample (candidate is uniformly slower). |
| `0` | The two distributions overlap completely (no shift). |
| `-1` | Every baseline sample exceeds every candidate sample (candidate is uniformly faster). |

The sign convention is: **positive delta = candidate tends to be slower than baseline**. The console reporter color-codes the cell to make this readable at a glance - red when the candidate is slower, green when faster.

### Romano magnitude thresholds

The **Magnitude** column classifies `|delta|` using the [Romano et al. (2006)](https://en.wikipedia.org/wiki/Effect_size) thresholds:

| `\|delta\|` range | Magnitude label |
|---|---|
| `[0, 0.147)` | Negligible |
| `[0.147, 0.33)` | Small |
| `[0.33, 0.474)` | Medium |
| `[0.474, 1.0]` | Large |

These are the same thresholds used in the educational-assessment literature. They are guidelines, not laws - your domain may call for stricter or looser cutoffs (see [Practical-significance gate](#practical-significance-gate) above).

## Technical detail: Hodges-Lehmann shift

Cliff's delta says how *consistently* the candidate differs from the baseline; it does not say *by how much*. The **Hodges-Lehmann** estimate, `BenchmarkResult.MedianShift`, closes that gap in time units: it is the median of all pairwise candidate − baseline differences, with a rank-based (Lehmann) confidence interval.

- **Point estimate:** `median({ bⱼ − aᵢ })`. Positive = candidate slower.
- **Interval:** the k-th smallest to k-th largest pairwise difference, with `k = ⌊mn/2 − z·σ_U⌋` and the tie-corrected Mann-Whitney `σ_U` - the same construction R's `wilcox.test(conf.int = TRUE)` uses in its normal-approximation branch. The interval excludes zero exactly when the U test rejects at `α = 1 − confidenceLevel`.
- **Cost:** the pairwise set is O(n₁·n₂), so each group is deterministically stride-subsampled to at most 512 values before pairing. The estimate stays representative and the result is reproducible run to run.

It appears in the advanced-detail stats block (e.g. `Median shift (Hodges-Lehmann): +12.3 ns [8.1 ns, 16.9 ns] (95%)`) and is always present in JSON.

## Technical detail: i.i.d. sanity checks

Both the CI-width stop rule and the Mann-Whitney test assume independent, identically distributed samples. Drift (a JIT tier-up or DPGO step landing mid-measurement, a thermal ramp, periodic GC) and autocorrelation both shrink the computed interval faster than the truth warrants. When at least 50 measured samples are available, NBenchmark runs two cheap post-hoc checks over the arrival-order stream and adds a warning when either trips:

- **Drift:** a split-half Mann-Whitney U between the first and second halves of the stream. A warning fires when `p < 0.001` - the distribution moved during measurement.
- **Dependence:** the lag-1 autocorrelation `r`. A warning fires when `r > 0.5`, noting the deflated effective sample size `≈ n·(1 − r)/(1 + r)`.

These are advisory: the result is still reported. They tell you the reported interval may understate the true uncertainty and point at longer warmup (`--min-warmup-time`) or host thermal/load state.

## Numerical accuracy of the asymptotic tail

The Mann-Whitney asymptotic branch reads the standard-normal CDF from an erfc accurate to ~1e-15 relative (W. J. Cody's rational Chebyshev approximation). This matters only for deep-tail exported p-values: at `α = 0.05` any reasonable approximation suffices, but p-values below ~1e-7 are meaningful rather than noise.

## Custom significance tests

The whole strategy is pluggable through `ISignificanceTest`. Implement it to swap in a bootstrap comparison, a Bayesian test, a post-hoc procedure, or a domain-specific rule:

```csharp
using NBenchmark.Stats;

public sealed class MedianRatioSignificanceTest(double thresholdPercent) : ISignificanceTest
{
    public string Name => $"median ratio (>{thresholdPercent:0.#}%)";

    public SignificanceReport Analyze(SignificanceContext context)
    {
        var baseline = Median(context.Baseline.Samples);
        var pairwise = new List<PairwiseComparison>();

        foreach (var candidate in context.Candidates)
        {
            var deltaPercent = Math.Abs(Median(candidate.Samples) / baseline - 1.0) * 100.0;
            var verdict = deltaPercent > thresholdPercent
                ? SignificanceVerdict.Significant
                : SignificanceVerdict.NotSignificant;

            // No p-value for this rule, so report null.
            pairwise.Add(new PairwiseComparison(
                candidate.Name,
                PValue: null,
                Verdict: verdict,
                Effect: new EffectSize(
                    Metric: "median-ratio",
                    Value: deltaPercent,
                    Magnitude: deltaPercent switch
                    {
                        < 5 => "neg",
                        < 15 => "small",
                        < 30 => "med",
                        _ => "large",
                    },
                    Direction: EffectDirection.None,
                    PracticalValue: Math.Min(1.0, deltaPercent / 100.0))));
        }

        return new SignificanceReport { Pairwise = pairwise };
    }

    private static double Median(double[] samples) { /* sort a copy, take the middle */ }
}
```

Register it through `MeasurementOptions.SignificanceTest`, the suite builder, or the harness:

```csharp
// Suite mode
.WithSignificanceTest(new MedianRatioSignificanceTest(thresholdPercent: 25))

// Single / Harness mode
new MeasurementOptions { SignificanceTest = new MedianRatioSignificanceTest(25) }
```

`Analyze` receives a `SignificanceContext` (the comparable `Groups`, the `BaselineIndex`, the `Baseline` group, the non-baseline `Candidates`, and the `SignificanceLevel`) and returns a `SignificanceReport` containing:

- **`Pairwise`** - one `PairwiseComparison(name, pValue, verdict, effect, shift)` per candidate. Use `PValue: null` for rules that do not produce a p-value.
- **`Effect`** (`EffectSize`) - optional strategy-defined effect metadata (`Metric`, numeric `Value`, string `Magnitude`, `Direction`, and optional normalized `PracticalValue` used by `MinimumPracticalEffect`).
- **`Shift`** (`ShiftEstimate`) - optional location-shift estimate in time units with a confidence interval (the built-in strategies populate the Hodges-Lehmann shift). Copied to `BenchmarkResult.MedianShift`.
- **`Omnibus`** - an optional single verdict across all groups. Set it for omnibus tests like Kruskal-Wallis; leave it `null` for purely pairwise tests.

The built-in strategies - `MannWhitneyUSignificanceTest`, `KruskalWallisSignificanceTest`, and the group-count-aware `DefaultSignificanceTest` - all implement this same interface, so you can also wrap or compose them.


---

# descriptive.md

---
title: Descriptive Statistics
description: Mean, median, percentiles, confidence intervals, and the complete BenchmarkResult field reference.
order: 4
---

# Descriptive Statistics

## Descriptive statistics

Given a sorted, trimmed array of `n` samples:

### Mean

$$\bar{x} = \frac{1}{n} \sum_{i=1}^{n} x_i$$

### Median

The **mid-average** convention: the middle value for odd `n`, and the mean of the two middle order statistics for even `n`. This matches `numpy.median` and the median NBenchmark uses elsewhere (per-launch aggregation, jitter calibration), so the reported `Median` and the `P50` percentile agree. Every other percentile uses the nearest-rank method (see below); the median is the sole exception, because the mid-average removes the small systematic downward bias nearest-rank has on even `n`.

### Percentiles

Configurable percentile values computed via the [nearest-rank](https://en.wikipedia.org/wiki/Percentile#The_nearest-rank_method) method: `i = ceil(p × n)`. The median (`p = 0.50`) is the one exception - it mid-averages the two middles on even `n` (see [Median](#median) above); `Q1`/`Q3` and every other percentile stay nearest-rank. Controlled by `MeasurementOptions.ReportedPercentiles` (default: P50, P95, P99, P99.9, Max). Each entry is a `PercentileEntry` with a `Percentile` (0-1) and `Value` (nanoseconds). Access a specific percentile with `result.GetPercentile(0.95)`.

**Tail metrics are computed from the full pre-trim distribution by default.** Percentiles, `Min`, `Max`, and the histogram describe the shape of the distribution, so they are computed from the raw (pre-trim) sample set - `MeasurementOptions.TailMetricsBasis = Raw`, the default. This keeps them honest: the IQR/MAD fence removes exactly the slow tail that P99/P99.9/Max exist to describe, so a GC pause the `Realistic` profile deliberately timed appears in `Max` rather than being trimmed out of it. Central-tendency and dispersion statistics (mean, standard deviation, CI, CV, skewness, kurtosis, MAD, median, median CI) always stay on the **trimmed** set, so a fenced-out spike never moves the mean or inflates the interval. Set `TailMetricsBasis = Trimmed` (or `--tail-basis trimmed`) to compute tail metrics from the inlier set instead.

Because the split is not obvious from the numbers themselves, the basis that was actually used is recorded on the result as `BenchmarkResult.TailMetricsBasis` (and emitted as `tailMetricsBasis` by the JSON reporter), alongside `OutlierDetector` naming the detector that drew the line. Anything rendering both groups - a table, a dashboard, a chart axis - should say which basis each number came from rather than leaving a reader to assume one distribution. `Max` sitting hundreds of times above `Median` is normal under the default basis and does not mean the mean is wrong; it means they describe different sample sets.

> [!IMPORTANT] Percentiles describe samples, and a sample may be a batch
> When [ops-per-sample calibration](./measurement.md#phase-1---ops-per-sample-calibration-k) resolves `K > 1` (the norm for sub-10 µs bodies), each **sample** is the mean of `K` back-to-back operations. Percentiles, Min, Max, and the histogram are therefore over **batch means**, not individual operations - a single slow op is averaged with its `K-1` neighbours, so the tail percentiles understate true per-operation tail latency. This is the deliberate cost of amortising timer overhead on fast bodies. When you need genuine per-op tail latency, pin `OpsPerSample = 1` (accepting that at that scale the reported values are dominated by timer resolution and read overhead - compare against a baseline measured the same way). Bodies that already span ≥ `AutoTune.TargetSampleDurationNs` (10 µs) keep `K = 1`, so their percentiles are already per-operation.

### Min and Max

`samples[0]` and `samples[n-1]` of the sorted tail source (the full pre-trim set by default; see the tail-metrics note above).

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

Reported as `0` when `n < 1$.

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
| `StandardError` | $s/\sqrt{n}$ | Precision of the mean estimate. |
| `MarginOfError` | $t^{*} \times \text{SEM}$ | Half-width of CI on the mean. |
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


---

# outliers.md

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

> [!CAUTION] Quick fix
> 1. **Check the body** for **lock contention** or **cache misses** - the cluster centre in the warning names the extra cost the slow path pays.
> 2. **If you suspect GC:** `dotnet run -- --profile independent` (forces per-iteration Gen0 collection, making GC pauses deterministic rather than bimodal).

1. **Do not silence it.** The warning is telling you something real about your code's performance distribution. The reported median describes the fast path; the cluster centre describes a latency a real user will also hit.
2. **Read the tail metrics as-is - they already show the second peak.** By default the [histogram](./descriptive.md) and the reported percentiles (P99, P99.9, Max) are computed from the full pre-trim distribution (`TailMetricsBasis = Raw`), so the trimmed cluster is still visible in them; you do not need to re-run with `OutlierMode.None`. Trimming affects only the central statistics (mean, standard deviation, CI). If you have explicitly set `TailMetricsBasis = Trimmed`, re-run with `OutlierMode.None` to see the cluster in the tail metrics.
3. **Investigate the cause.** Use a profiler or add instrumentation around the suspected bottleneck (lock, cache-hot path, GC notification). The cluster centre in the warning message is a hint about how much extra time the slow path costs.
4. **Consider `--profile independent`** if you suspect GC: it forces per-iteration Gen0 collection, which makes GC pauses deterministic rather than bimodal.
5. **Reduce noise at the source** with [environment control](../features/environment-control.md) if you suspect OS scheduling contributed to the spread.

### Interaction with outlier mode

The bimodal detector runs **after** whichever `OutlierMode` is active and inspects that mode's discarded tail. It is most useful with the default `IqrFence`, which discards a data-adaptive tail. With `None` (no trimming) there is no discarded tail to inspect, so the warning never fires. With `RemoveTop5Percent` or `RemoveTopAndBottom5Percent` the discarded set is a fixed quota, so a tight cluster in it is still meaningful. With `MedianAbsoluteDeviation` the symmetric fence can discard fast and slow samples; only the slow ones above the kept median are considered for the cluster.

The detector never changes which samples are kept - it only adds a warning. The trimmed statistics are computed exactly as the `OutlierMode` dictates.

### GC-correlated outliers

When GC collection counts are collected (`DiagnosticsOptions.GcCollectionCounts`, on by default), NBenchmark records a per-sample GC delta alongside each timing. After trimming, it counts how many of the discarded samples coincided with a collection and annotates the result - answering the first question an outlier tail raises, *"was that a GC?"*, without a re-run:

```
⚠ 5 of 7 removed outlier(s) coincided with a garbage collection.
```

When a bimodal warning also fired, the same fact is folded into that warning ("… (3 of the discarded outliers coincided with a garbage collection.)") so the two signals are not reported twice. A high GC-correlation share points at allocation pressure; consider `--profile independent` (forces per-iteration Gen0 collection, making GC deterministic) or reducing allocations in the body.

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


---

# validation.md

---
title: Validation & Accuracy
description: How NBenchmark's statistical results are verified against reference implementations.
order: 7
---

# Validation & Accuracy

NBenchmark's numerical core is dependency-free - it ships its own implementations
of the [Student's t quantile](https://en.wikipedia.org/wiki/Student%27s_t-distribution), the normal quantile, percentiles, the
[Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test), and the [Kruskal-Wallis test](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test). This page documents how those implementations are verified,
and to what tolerance, so you can trust the numbers in the output.

The verification lives in the test suite (`tests/NBenchmark.Tests`) and runs on
every build. It has three layers.

## 1. Property / brute-force recomputation

`StatsRecomputationTests` generates many random samples (sizes from 2 to 500
across ten seeds) and, for each one, **recomputes every reported quantity from
first principles inside the test**:

| Quantity | Independent recomputation | Tolerance |
|---|---|---|
| Mean | $\sum x_i / n$ | 1e-9 relative |
| Standard deviation | $\sqrt{\sum(x_i-\bar{x})^2/(n-1)}$ | 1e-9 relative |
| Standard error | $s/\sqrt{n}$ | 1e-9 relative |
| Margin of error | $t^{*} \times \text{SEM}$ | 1e-9 relative |
| Coefficient of variation | $s/\bar{x}$ | 1e-9 relative |
| Percentiles (P1–P99) | Nearest-rank `ceil(p·n)−1` | exact |

Because this covers arbitrary inputs rather than a handful of hand-picked
arrays, it is the strongest guard against a regression in the descriptive
statistics.

## 2. External cross-checks (SciPy / NumPy)

`StatsCrossCheckTests` and `MannWhitneyCrossCheckTests` pin NBenchmark's output
against values pre-computed with **SciPy 1.17.1** and **NumPy 2.4.6**. The
reference values are embedded as constants in the tests; the generators are
listed below so they can be regenerated.

| NBenchmark | Reference | Agreement |
|---|---|---|
| `StatsSummary` mean / stddev / SEM | `numpy.mean`, `numpy.std(ddof=1)` | ≤ 1e-9 relative |
| `Percentile.Compute` | `numpy.percentile(method='inverted_cdf')` | exact |
| `StudentT.CriticalValue` (df = 1, 2) | `scipy.stats.t.ppf` | ≤ 1e-9 relative |
| `StudentT.CriticalValue` (df ≥ 3) | `scipy.stats.t.ppf` | < 1% (worst ≈ 0.79% at df = 3, 99%) |
| `StudentT.NormalQuantile` | `scipy.stats.norm.ppf` | ≤ 1.15e-8 absolute |
| `MannWhitneyU.Test` (small, tie-free, combined n ≤ 20) | `scipy.stats.mannwhitneyu(method='exact')` | ≤ 1e-9 relative |
| `MannWhitneyU.Test` (otherwise) | `scipy.stats.mannwhitneyu(method='asymptotic', use_continuity=True)` | < 1e-6 absolute |
| `ChiSquared.SurvivalFunction` | Closed forms (df = 2: $e^{-x/2}$; df = 4) and `scipy.stats.chi2.sf` | \u2264 1e-9 on closed forms; \u2264 1e-4 on spot values |
| `KruskalWallis.Test` (H, p) | `scipy.stats.kruskal` (with tie correction) | H \u2264 1e-9; p \u2264 1e-6 |

Reference values were generated with:

```python
import numpy as np
from scipy import stats

np.mean(x)                                   # mean
np.std(x, ddof=1)                            # sample standard deviation
np.percentile(x, q, method='inverted_cdf')  # nearest-rank percentile
stats.t.ppf((1 + cl) / 2, df)                # two-tailed [t critical value](https://en.wikipedia.org/wiki/Student%27s_t-distribution)
stats.norm.ppf(p)                            # [normal quantile](https://en.wikipedia.org/wiki/Normal_distribution)
stats.mannwhitneyu(a, b, alternative='two-sided',
                   method='exact')                          # exact p-value (small samples)
stats.mannwhitneyu(a, b, alternative='two-sided',
                   method='asymptotic', use_continuity=True)  # [p-value](https://en.wikipedia.org/wiki/P-value) (large samples)
stats.chi2.sf(x, df)                         # chi-squared survival function
stats.kruskal(*groups)                       # Kruskal-Wallis H and p-value
```

### Exact vs. approximate Mann-Whitney U

For small, tie-free samples (combined `n ≤ 20`) NBenchmark computes the **exact**
[permutation](https://en.wikipedia.org/wiki/Permutation_test) p-value, matching
`scipy.stats.mannwhitneyu(method='exact')` to 1e-9. `MannWhitneyCrossCheckTests`
verifies this against both SciPy and an independent in-process rank-assignment
enumerator. For larger samples NBenchmark falls back to the tie- and
continuity-corrected normal approximation, which matches SciPy's asymptotic
method (`use_continuity=True`) to better than 1e-6:

> Using the exact test on small samples removes the up-to-**≈ 0.05** gap that a
> normal approximation alone would have versus the exact permutation p-value.
> For larger samples the approximation is accurate and the difference is
> negligible.

The significance test requires at least two samples per group.

## 3. End-to-end measurement loop sanity

`TimingSanityTests.Engine_MinimumSample_Is_Near_Known_BusyWait_Floor` runs the
full measurement engine against a CPU-bound busy-wait of known duration and
asserts that the **minimum** sample lands near the target (within 0.9–3.0×).

Unlike mean-based assertions (which absorb all scheduler preemption spikes), the
minimum is stable:

- A CPU-bound busy-wait has a hard floor - the minimum cannot be materially
  *below* the target.
- Preemption only ever adds time, pushing the mean around but barely affecting
  the minimum.
- This catches a class of bugs the deterministic statistical tests cannot detect:
  unit errors (ns vs ms), a broken measurement loop, or the timer wired up wrong.

## What is *not* asserted to ground truth

- **Allocation tracking** is a smoke test (a 64 KiB allocation reports ≥ 1 KiB),
  not an exact byte comparison, because framework allocations can appear between
  the before/after allocation counter reads.
- **Absolute timing accuracy** depends on the platform's `Stopwatch` resolution
  and scheduler; the timing tests bound it coarsely rather than precisely.


---

# allocations.md

---
title: Allocation Measurement
description: How NBenchmark samples per-iteration heap allocation using GC counters.
order: 2
---

# Allocation Measurement

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

## What the harness itself contributes

Nothing on the measured path - and that took work to be true rather than being free.

Discovery used to reach a `[Benchmark]` method through a `Func<object, object?>`. One uniform delegate type is convenient, and it boxed the result of every value-returning benchmark method once per operation: the four bodies in `samples/Harness` are constant returns that allocate nothing, and each of them reported **24 B/op**. That is the harness's allocation, printed in the user's column.

A benchmark body is now bound to a delegate carrying the method's own signature - `Func<int>` for `int Compute()`, not `Func<object>` - and its return value is stored in a sink closed over that same type. A value-returning benchmark that allocates nothing now reports `0 B`.

**Numbers measured before this changed are not comparable with numbers measured after.** On the `samples/Harness` calibration set the per-operation allocation went from 24 B to 0 B and the median from ~9.3 ns to ~2.5 ns - none of that difference being the benchmarked code. Discard stored baselines that predate it.


---

# index.md

---
title: Statistics
description: How NBenchmark measures, analyses, and reports benchmark data.
order: 6
---

# Statistics

This section explains how NBenchmark collects and analyses measurements. The [Key Concepts](../getting-started/key-concepts.md) page covers the practical side. For a practical guide to interpreting the output you see on screen, see [Reading Your Results](../output/reading-your-results.md). These pages are for readers who want the full mathematical picture.

## In this section

- **[Measurement](./measurement.md)** - the measurement loop, timer resolution, and warmup sequence.
- **[Allocation Measurement](./allocations.md)** - how per-iteration heap allocation is sampled.
- **[Outlier Trimming](./outliers.md)** - IQR fence, MAD, fixed-quota modes, custom detectors, and the bimodal-distribution warning.
- **[Descriptive Statistics](./descriptive.md)** - mean, median, percentiles, standard deviation, confidence intervals, CV, distribution shape (skewness, kurtosis, MAD), and the complete `BenchmarkResult` field reference.
- **[Significance Testing](./significance.md)** - the Mann-Whitney U test for two groups and the Kruskal-Wallis omnibus test (with post-hoc pairwise Mann-Whitney U and Holm-Bonferroni correction) for three or more: why non-parametric, the algorithms, p-value interpretation, **Cliff's delta effect size and Magnitude column**, the `MinimumPracticalEffect` practical-significance gate, and custom tests.
- **[Diagnostics](./diagnostics.md)** - runtime counters for GC collection counts, heap state, exceptions, and CPU time.
- **[Validation & Accuracy](./validation.md)** - how the numerical implementations are verified against SciPy and NumPy.


---

# measurement.md

---
title: Measurement
description: How NBenchmark's measurement loop works, including timer resolution and per-iteration overhead.
order: 1
---

# Measurement

## The measurement loop

NBenchmark uses an **adaptive streaming loop**. Rather than running a fixed number of iterations, it resolves three dimensions at runtime - how many invocations to time per sample (**K**), how long to warm up, and how many measured samples to collect - and stops each as soon as it has enough. Every dimension can be pinned to an exact value (see [Configuration](../reference/configuration.md)); pinning all three reproduces a classic fixed-count run.

For each benchmark the loop runs in four phases:

### Phase 0 - Pre-flight jitter calibration

Before any real measurement, NBenchmark times a deterministic, allocation-free busy-weight loop and derives a robust jitter metric: the ratio of the median absolute deviation to the median (MAD / median) of its per-sample timings. This is a probe of the *host*, not the code under test: a quiet dedicated host reports well below 0.05, a shared-tenant CI runner typically reports 0.10-0.30. The metric is robust - both the median and MAD have a ~50% breakdown point, so a single JIT spike or one-off preemption cannot distort it the way stddev/mean can.

Why this matters: the default outlier detector (IQR fence) uses the interquartile range as its scale estimate, which has a low breakdown point - a heavy tail of scheduling-preempted samples distorts the fence and trims the wrong values. Median Absolute Deviation (MAD) has a ~50% breakdown point and is far more resilient to that tail. When the jitter metric exceeds `AutoTune.JitterAutoSwitchThreshold` (default 0.10) and the user has not pinned an outlier detector, the loop auto-switches the effective detector from IQR fence to MAD for that run. The switch is recorded on the `AutoTune` diagnostic (`OutlierDetectorSwitched`) and a warning is emitted explaining what happened and why.

The probe is on by default (`AutoTune.EnableJitterCalibration`). Pinning `OutlierMode` to a non-default value or supplying a custom `OutlierDetector` disables the auto-switch but not the probe - the metric is still reported for visibility. Set `AutoTune.JitterAutoSwitchThreshold` to 0 to disable the auto-switch while keeping the probe, or `AutoTune.EnableJitterCalibration` to false to skip the probe entirely.

### Phase 1 - Ops-per-sample calibration (K)

If `OpsPerSample` is `null` (the default) and the body is eligible, NBenchmark times a single invocation, then doubles K - timing 1, 2, 4, 8, … invocations as one batch - until a batch spans at least `AutoTune.TargetSampleDurationNs` (**10 µs** by default). The resolved K is reused for warmup and measurement, and every reported timing divides the batch time by K to give a per-operation number.

The 10 µs target keeps two per-sample error sources negligible: timer **quantization** (Windows QPC ticks at 100 ns, so a 10 µs sample resolves to ~0.1% rather than the ~±10% a 1 µs sample would suffer) and the fixed **timestamp-read overhead** (~10-30 ns, ~0.2% of 10 µs rather than ~1-3% of 1 µs). Both would otherwise leak into the ±2.5% CI target. Bodies already spanning ≥ 10 µs keep K = 1, so their per-op tail visibility is unchanged.

> [!NOTE] K > 1 batches change what percentiles mean
> When K > 1, each recorded sample is the mean of K back-to-back operations, so P95/P99/Max and the histogram describe **batch means**, not individual-operation tails - a slow individual op is averaged with its K-1 neighbours. For a sub-10 µs body the trade is deliberate (per-op timing at that scale is dominated by timer noise anyway); when you need per-op tail latency, pin `OpsPerSample = 1` and read the caveats in [Descriptive Statistics](./descriptive.md).

Calibration is skipped (K = 1) when `IterationSetup`/`IterationTeardown` is set, because a batch would no longer represent one isolated call. It is **not** skipped under the `Independent` profile: the forced Gen0 GC runs once per sample (the K-batch), before the timestamp and outside the timed window — the same semantics a pinned `OpsPerSample` gets — so nano-scale CPU bodies still amortise timer overhead. A pinned `OpsPerSample` is always honoured. Calibration runs against the body's **cold** (pre-warmup) speed; see [Post-warmup recalibration](#post-warmup-recalibration) below for how K is re-derived once the body is warm.

### Phase 2 - Warmup (plateau detection)

If `WarmupIterations` is `null`, NBenchmark collects warmup samples in batches of `AutoTune.BatchSize` and tracks the best (fastest) batch mean seen so far. Once `AutoTune.PlateauPatience` consecutive batches fail to improve on the best by at least `AutoTune.WarmupEpsilon`, the code is considered warm and warmup stops - never before `AutoTune.MinWarmup` samples, never after `AutoTune.MaxWarmup`. A pinned `WarmupIterations` runs exactly that many warmup samples.

The plateau rule alone measures warmup in *iterations*, but a fast body plateaus in microseconds of wall-clock - long before the background JIT delivers tier-1 (and dynamic-PGO) code. Warmup would then settle on the stable-but-slow tier-0 plateau and the tier-1 switch would land mid-measurement as a step change, the dominant source of run-to-run variance on very fast benchmarks. Two extra gates prevent that:

- **Warmup time floor** (`AutoTune.MinWarmupTime`, default 500 ms; 1 s under `Thorough`): auto-warmup will not settle until it has accumulated at least this much in-body time, giving tiered compilation time to land. Set to `0` to disable.

  The default is 5× the runtime's `TieredCompilation.CallCountingDelayMs` (100 ms). That delay *restarts* whenever tier-0 methods are still being called for the first time, and tier-1 is only *queued* once it finally expires — then compiled on a background thread, with a second instrumented→optimized transition under dynamic PGO. A floor at or below 100 ms therefore reliably lands those transitions inside the measurement window rather than before it. In practice this floor, not `MinWarmup` or `PlateauPatience`, is what determines warmup length for almost every body.

  **`Quick` does not shorten this floor.** It is a measurement-correctness requirement, not a speed/accuracy trade-off: a short floor does not give you a rougher number, it gives you a *confidently wrong* one — a benchmark measured on tier-0 code can report a median several times off with a ±1% error bar, and it will not reproduce across runs. `Quick` gets its speed from a looser `CiTarget`, a lower `MinSamples`, and a shorter `MaxTuningTime` instead.
- **JIT-quiescence gate** (`AutoTune.RequireJitQuiescence`, default on, with `AutoTune.JitQuietPeriod`, default 50 ms): at each batch boundary NBenchmark reads `System.Runtime.JitInfo`'s compiled-method count and remembers where in warmup it last changed. Warmup continues until that change is `JitQuietPeriod` in the past, so an in-flight tier-1 promotion actually extends warmup.

  The *sustained interval* is the point. Asking only whether the JIT compiled anything during the most recent batch does not work: for a fast body one batch spans tens of microseconds, so a background compilation almost never lands inside that particular window and a per-batch delta reads zero essentially always. The quiet period is clamped down to `MinWarmupTime` so it can never become the binding floor, and the gate deactivates once warmup has run 4 × `MinWarmupTime` so a busy in-process host that JITs unrelated code cannot hold warmup open forever. Disabling the time floor (`MinWarmupTime = 0`) or setting `JitQuietPeriod = 0` also disables this gate.

Both gates only *delay* settling past the plateau; `MaxWarmup` and the calibration+warmup budget share (below) still bound warmup from above, so a genuinely slow body is not held open by them.

Because a fast body needs roughly 50,000 samples to accumulate 500 ms at the 10 µs sample target, `AutoTune.MaxWarmup` defaults to **100,000** — not the 10,000 that bounds a *pinned* `WarmupIterations`. A count ceiling that binds before the time floor would silently defeat it, so hitting the ceiling below the floor raises a prominent warning and `BenchmarkResult.AutoTune.WarmupTimeFloorMet` records it. A body that cannot reach the floor within the ceiling at all — typically `OpsPerSample` pinned to 1 on a nanosecond body — is told to raise `--ops-per-sample` so each sample spans more work.

For slow bodies the configured `BatchSize` is shrunk based on the per-sample estimate from calibration: a body that takes seconds per sample warms in batches of 1 so the plateau rule can settle after `PlateauPatience + 1` samples instead of `(PlateauPatience + 1) × BatchSize` (subject to the `MinWarmup` floor, which with the default `MinWarmup = 8` is then the binding constraint). Without this shrink a 2 s body with the default `BatchSize = 8` would need `(PlateauPatience + 1) × BatchSize = 32` samples — 64 s of warmup — just to clear the plateau requirement.

Calibration and warmup share a budget: together they may consume at most `AutoTune.WarmupBudgetFraction` of `AutoTune.MaxTuningTime` (default 0.4 = 40%), reserving the remainder for measurement. This keeps slow bodies from spending the whole cap on warmup and leaving measurement with a single sample. When the share is exhausted, warmup stops at the wall-clock cap and a warning names the share.

If `ForceGcBeforeMeasurement` is true (the `Independent` profile), a full gen-2 GC runs after warmup to establish a clean heap baseline. Under `Realistic` (the default) this is skipped and the benchmark inherits the warmup's heap state. (This is a distinct knob from `ForceGcBetweenBenchmarks`, which runs a full GC *between* benchmarks and is on for both profiles.)

### Post-warmup recalibration

Ops-per-sample calibration (Phase 1) resolves K against the body's **cold** code. Once warmup has driven the body to its steady-state (tiered / PGO-optimized) speed - often several times faster - the same K may span well under the target duration, re-exposing the fixed timer overhead calibration existed to amortise. So after auto-warmup settles, NBenchmark re-derives K from the warm per-op estimate the plateau detector measured (the last warmup batch mean): if the warm sample spans less than half the target, K is bumped to the next power of two that reaches the target, and one untimed sample runs to warm the larger batch's cache/branch state before measurement.

Recalibration only applies when calibration ran (not a pinned K, no setup/teardown) and only ever increases K. When it fires, `BenchmarkResult.AutoTune.InitialOpsPerSample` records the pre-recalibration (cold) K while `OpsPerSample` holds the final value; the gap shows how much faster the warm body ran than the cold code first timed. When no recalibration occurs, `InitialOpsPerSample` is `null`.

### Phase 3 - Measurement (CI-width target)

If `Iterations` is `null`, NBenchmark streams measured samples and, every `AutoTune.BatchSize` samples, recomputes the confidence interval on the mean. Sampling stops once the interval's relative half-width falls below `AutoTune.CiTarget` (±2.5% by default) - never before `AutoTune.MinSamples`, never after `AutoTune.MaxSamples`. A pinned `Iterations` collects exactly that many samples. A per-benchmark `AutoTune.MaxTuningTime` wall-clock cap bounds the whole loop so a pathological body can never run away.

Two gates sit on that stop rule, mirroring the warmup settle gates. The CI rule decides whether the interval is *narrow enough*; the gates decide whether it is *honest to stop*.

- **Measurement time floor** (`AutoTune.MinMeasurementTime`, default 100 ms; 50 ms under `Quick`, 500 ms under `Thorough`): measurement will not stop on the CI target until it has spanned this much in-body time. This is what makes the sample count scale with how cheap the body is. A flat `MinSamples` is blind to that — the same 30 samples cost 9 s on a 300 ms body and 0.5 ms on a 1 µs body, where thousands of samples are essentially free and buy meaningful percentiles, a usable histogram, and a significance test with real power. (At n ≈ 16 the reported P95, P99 and P99.9 all collapse onto the maximum.)

  The rule is simply: measurement spans at least this long, or reaches `AutoTune.MaxSamples` samples, whichever comes first. So worst-case added cost is `MinMeasurementTime` per benchmark, and it is **exactly zero** for any body already slower than `MinMeasurementTime / MinSamples` (≈3.3 ms by default), where `MinSamples` binds and nothing changes. Set to `0` to stop on `MinSamples` alone.
- **Steady-state (drift) gate** (`AutoTune.MeasurementDriftTolerance`, default 0.10): when the CI rule wants to stop, NBenchmark compares the mean of the first half of the collected samples against the mean of the second half, and refuses the stop if they disagree both *relatively* (by more than the tolerance, measured against the smaller half-mean) and *statistically* (by more than 4 standard errors of the difference).

  > [!CAUTION] If you hit a `driftUnresolved` stop
  > **Land the transition during warmup instead:** raise `--min-warmup-time <ms>` (default 500) so a JIT tier-up or dynamic-PGO re-optimization lands inside warmup, not measurement.
  > **Accept non-stationarity as the finding:** `--launch-count 5` measures the across-launch spread, which is the honest signal for a body that genuinely does not have a steady state.

  This guards the failure mode that is hardest to notice. A JIT tier-up — or a thermal ramp, or a filling cache — landing inside the measurement window produces a step change, and a CI-on-the-mean rule will happily report a tight interval straight across it. That is how a benchmark ends up 10× wrong with a ±0.9% error bar, looking more trustworthy than a correct result.

  Both conditions are required. A bare relative rule false-positives forever on a heavy-tailed body whose half-means differ by pure sampling noise; a bare significance rule flags sub-percent drift once *n* reaches the thousands. On a refusal the loop discards **all** samples collected so far and starts measurement over, up to `AutoTune.MeasurementRestartLimit` times (default 2 — one for tier-0→tier-1, one for instrumented→optimized under dynamic PGO). Restarts draw on the same `MaxTuningTime` budget as ordinary sampling, so they can never make a benchmark run longer. Exhausting the limit reports `SampleStopReason.DriftUnresolved` with a warning; set the tolerance to `0` to disable the gate. Either way `BenchmarkResult.AutoTune.SplitHalfDrift` records the gap, on every stop — including pinned-count and cap stops that never consult the gate — so a tight interval sitting next to a large drift is visible rather than silently trusted.

`AutoTune.MaxSamples` defaults to **5,000** (2,000 under `Quick`, 20,000 under `Thorough`). At 5,000 the CI rule still reaches ±2.5% for any body with a coefficient of variation up to roughly 90%. Past that the required count grows as `(t × CV / target)²` and runs away — a CV of 580% needs about 50,000 samples just to reach ±5% — but a body that noisy has variance that *is* the finding, and more samples only buy a tighter interval around an unstable centre. The ceiling warning therefore names the measured CV and the count convergence would actually take, and points at `--launch-count` as the more honest signal.

When the cap fires before `AutoTune.MinSamples` is reached, the loop keeps sampling up to `AutoTune.MaxTuningTime × AutoTune.CapGraceFactor` (default 1.5×) rather than stop on a dangerously under-sampled result. A one-sample result reports StdDev = 0 and MarginOfError = 0 - dangerously clean-looking - so the grace path trades a longer run for enough samples to be meaningful. If the grace ceiling is still reached below `MinSamples`, a prominent warning flags the error margin as unreliable. Set `AutoTune.CapGraceFactor` to 1 to disable the grace path and stop at the base cap. `AutoTuneCapBehavior.Error` users are unaffected - the error fires at the base cap either way.

Each measured sample does the following:

- If `ForceGcBeforeEachIteration` is true (the `Independent` profile), force a gen-0 collection (once per sample, before the timestamp).
- Call `IterationSetup` if provided.
- Record `Stopwatch.GetTimestamp()`.
- Invoke the benchmark action K times.
- Read the timestamp again and convert the raw tick delta to nanoseconds at the timer's **native resolution** (`delta × 10⁹ / Stopwatch.Frequency`), then divide by K.
- Record the allocation delta (divided by K) if `MeasureAllocations` is true (on by default under both profiles; the snapshot is taken outside the timed window).
- Call `IterationTeardown` if provided.

**Important:** the timer is read immediately after the K-batch returns, before teardown runs. Teardown time is not included in the measurement.

### Raw vs. trimmed statistics

The CI-width stop rule evaluates the **raw** (untrimmed) sample stream as it arrives. After the loop ends, the collected per-op samples pass through [outlier trimming](./outliers.md) and the reported statistics - including the Error column - are computed on the **trimmed** set. So the diagnostic's `AchievedRelativeCiWidth` reflects the raw stop value while the reported interval reflects the trimmed result.

The two are usually close, but they can diverge by two orders of magnitude, and the direction is always the same: **the reported interval is the narrower one.** When a benchmark's variance lives almost entirely in the outliers, trimming removes it and the reported margin tightens around what remains — one sample body reported `MarginOfError` at ±1.3% of its mean next to an `AchievedRelativeCiWidth` of `1.05` (±105%) and a `MaxCeiling` stop. Neither number is wrong; they describe different sample sets. But a tight Error column is only trustworthy evidence that the *measurement converged* when `SampleStop` is `CiTargetMet`. Read the stop reason before the margin.

### What the loop decided

Every measured result carries an `AutoTune` diagnostic (`BenchmarkResult.AutoTune`) recording the resolved K, warmup length, sample count, why each phase stopped, the achieved CI half-width and its convergence trace, the wall-clock time spent tuning, the pre-flight jitter metric, whether the outlier detector was auto-switched, and the drift and restart counters. Reporters surface it as an `auto-tuned: …` line (console, Markdown), dedicated columns (CSV advanced), or an `autoTune` object (JSON). It is `null` on dry-run and errored results.

It also records what warmup observed about tiered compilation - see [the warmup curve](#the-warmup-curve).

### The warmup curve

The [warmup gates](#phase-2---warmup-plateau-detection) decide *when* warmup may end. The diagnostic additionally retains what warmup *saw*, which is the only surviving record of the body tiering up: raw warmup timings are never persisted, and `RawSamples` covers the measurement phase only.

- **`WarmupCurve`** - the mean per-op time of each warmup batch, oldest first. The plateau rule already computes a batch mean, so retaining them costs nothing, and the averaging keeps a two-or-three-step decay from being buried in per-sample jitter. Tier-0 → tier-1 promotion, and instrumented → optimized under dynamic PGO, each appear as a step down. **`WarmupSampleInterval`** gives the warmup iterations between consecutive points, so the array plots against a real iteration axis. Bounded at 512 points - a longer warmup is decimated by a doubling stride, keeping points evenly spaced and the shape intact at coarser resolution. Empty for a pinned `WarmupIterations`, which runs no plateau detection.
- **`JitLastChangeAtNs`** - how far into warmup the compiled-method count last moved, with **`WarmupElapsedNs`** as the total extent. Under continuous load the final compilation is usually the promotion of the body's own hot path, which makes this the closest thing to a tier-up marker to draw on the curve. **`JitQuiescenceAchieved`** reports whether the quiet period genuinely elapsed rather than the gate being bypassed by its deactivation threshold.
- **`WarmupJitCompiledMethods`**, **`WarmupJitCompilationTime`**, **`WarmupJitCompiledIlBytes`** - `System.Runtime.JitInfo` deltas across warmup, sampled at batch boundaries. Compilation *time* is the most directly useful: it is denominated in the same units as the benchmark, so it answers "what did tiering cost here?" rather than "how many methods were involved?".

All three counters are **process-wide**, not per-benchmark. In an in-process run the first benchmark to execute absorbs the bulk of startup compilation and later ones see almost none - which is real, and since [benchmarks run in random order by default](../faq.md#can-i-run-benchmarks-in-source-order-instead-of-random-order) it is a significant part of why the same benchmark's warmup differs between runs. Use `--order declaration` (or `--seed` for a reproducible shuffle) if you need the JIT cost to fall in the same place every time.

Two limits worth knowing:

- **This is aggregate decay, not per-method tier attribution.** Naming individual methods and their tiers (`QuickJitted`, `OptimizedTier1`, OSR, instrumented) requires the runtime's `MethodLoadVerbose` events via EventPipe or an in-process `EventListener`, which NBenchmark does not collect.
- **Ops-per-sample calibration runs before warmup** and already exercises the body, so some tier-up has typically happened before the first warmup batch is recorded. The curve shows what remains of tiering plus cache and branch-predictor warming, not the full cold-start cliff - expect a few percent for an already-fast body and several times for an allocation-heavy one.

## Measurement profiles

NBenchmark provides two measurement profiles that control how GC interacts with the measurement loop:

- **`Realistic`** (the default) - no per-iteration Gen0 GC, no pre-measurement full GC (the warmup heap is inherited). Numbers reflect what the same code does in production, including natural GC pauses and CPU cache effects.
- **`Independent`** (opt-in) - force Gen0 GC before every sample, run a full GC after warmup before measurement. Useful for pure-CPU measurements, cryptographic algorithms, numeric kernels, and other cases where iteration-to-iteration independence is more important than ecological validity.

Two behaviours are on for **both** profiles: the between-benchmark full GC (so one benchmark's leftover heap cannot bias the next) and allocation tracking (sampled outside the timed window, so it costs nothing and surfaces the "this pure-CPU body actually allocates" signal even under `Independent`). Disable them with `--no-gc-between-benchmarks` / `--no-allocations` if needed.

### Worked example

Consider a benchmark body that allocates 100 KB per call:

```csharp
BenchmarkSuite.Create("AllocPressure")
    .Add("alloc", () => _ = new byte[100_000])
    .RunAsync();
```

Under the **Realistic** profile (the default), the variance (CV%) is high and some iterations show Gen0-GC stalls. The `Alloc/op` column is populated and shows the allocation pressure. The numbers reflect what this code would do in production.

Under the **Independent** profile (`--profile independent`), the variance is low and the per-iteration numbers are tightly clustered. The `Alloc/op` column is still populated (allocation tracking is on for both profiles), so the 100 KB/op shows up even here. The numbers answer a narrower question: "how much CPU time does this take, ignoring GC and cache?"

### Setting the profile

```csharp
// In code (BenchmarkHarness)
await BenchmarkHarness.Create(args)
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .RunAsync();

// In code (BenchmarkSuite)
new BenchmarkSuite("MySuite")
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .Add(...)
    .RunAsync();

// On the CLI
dotnet run -- --profile independent
```

### Per-option overrides

Each behaviour can be overridden individually:

```csharp
// Enable per-iteration GC under Realistic
options with { ForceGcBeforeEachIterationOverride = true }

// Inherit the warmup heap under Independent (skip the pre-measurement GC)
options with { ForceGcBeforeMeasurementOverride = false }

// Disable allocation tracking (both profiles)
options with { MeasureAllocationsOverride = false }

// Disable the between-benchmark GC (both profiles)
options with { ForceGcBetweenBenchmarksOverride = false }
```

CLI equivalents:

```bash
dotnet run -- --profile realistic --force-gc
dotnet run -- --no-allocations
dotnet run -- --no-gc-between-benchmarks
```

### Timer resolution

NBenchmark uses `System.Diagnostics.Stopwatch`, which wraps the platform's high-resolution performance counter. The resolution is printed at the start of each `BenchmarkHarness` run:

```
Timer resolution: 1,000,000,000 ticks/s (1.00 ns per tick)
```

On most modern hardware the resolution is 1 ns. On some virtual machines it may be coarser; on Windows the counter typically runs at 10 MHz (100 ns per tick).

Per-iteration timings are computed directly from raw `Stopwatch` ticks - deliberately **not** via `TimeSpan`, whose ticks are always 100 ns. On a 1 GHz timer this preserves the full 1 ns sample resolution; round-tripping through `TimeSpan` would quantize every sample to a multiple of 100 ns and record sub-100 ns operations as zero.

> [!NOTE] Timer-call overhead
> Each sample includes the cost of one timestamp read (typically ~10-30 ns).
> Ops-per-sample calibration (Phase 1 above) amortises this across K invocations
> for fast bodies, so the per-op number stays meaningful even in the low-nanosecond
> range. When K is pinned to 1 - or when setup/teardown forces it - the read cost is
> a fixed addend on every sample, so treat absolute values at that scale as upper
> bounds and compare against a baseline measured the same way.

## Reducing noise at the source

The adaptive loop, [outlier trimming](./outliers.md) (including the [bimodal warning](./outliers.md#bimodal-distribution-warning)), and [significance testing](./significance.md) all work around OS noise statistically - they discard or down-weight samples that look like interference. But they cannot remove noise that is baked into every sample: a benchmark thread that migrates between cores suffers cold-cache stalls on every migration, and a normal-priority process on a busy host is preempted on a schedule that has nothing to do with your code.

NBenchmark provides opt-in **environment controls** that reduce this noise before the timer starts:

- **CPU affinity** - pin the benchmark process to specific cores to eliminate inter-core migration.
- **Process priority** - raise the process priority to reduce preemption by unrelated OS work.
- **Dedicated-host guidance** - a non-fatal probe that warns when the host looks noisy (low core count, unraisable priority, or on macOS unobservable frequency scaling/thermal throttling) and suggests `--priority high` on a suitable host.

All three default to off and are restored when the run completes. They are the proactive counterpart to the reactive statistical noise handling: trimming discards noisy samples after the fact; environment control reduces the noise at the source.

See [Environment control](../features/environment-control.md) for the full model, platform notes, and isolated-process propagation.


---

# diagnostics.md

---
title: Diagnostics
description: Runtime counters for GC pressure, heap state, exceptions, and CPU usage.
order: 6
---

# Diagnostics

Allocation bytes tell you how much memory a benchmark allocated, but they do not explain *why* it is slow or *how* the garbage collector responded. Diagnostics add runtime counters alongside the timing and allocation data so you can distinguish steady-state code from allocation-heavy code, CPU-bound work from IO-bound work, and normal control flow from exception-driven control flow.

## What is collected

Four counters are available, each independently toggleable via `DiagnosticsOptions`:

### GC collection counts

Gen0, Gen1, and Gen2 collection counts during the measurement phase, reported as totals (not per-operation rates). Collected via `GC.CollectionCount(n)` bracketed around each sample.

**Why it matters:** Allocation bytes are a proxy for memory pressure. Collection counts show *actual* GC pressure. A benchmark that allocates heavily but stays in Gen0 has different characteristics from one that triggers Gen1 or Gen2 collections. The totals let you compare: a body that causes 0 Gen2 collections across the measurement phase is steady-state; one that causes many is allocation-heavy.

**Overhead:** negligible. `GC.CollectionCount` is a counter read, not a measurement operation.

**Default:** on (`GcCollectionCounts = true`). This is cheap enough to always collect.

### GC heap info

Heap committed bytes and fragmented bytes, reported as a delta across the measurement phase via `GC.GetGCMemoryInfo()`. A snapshot is taken before measurement begins and after it ends; the difference is reported.

**Why it matters:** Shows how the benchmark affected the managed heap. A growing committed footprint or rising fragmentation signals that the body is not releasing memory efficiently, even if per-iteration allocations look small.

**Overhead:** low. Two `GetGCMemoryInfo` calls per benchmark (one before measurement, one after).

**Default:** off. Enable with `GcHeapInfo = true` or `--diagnostics all`.

### Exception count

Total first-chance exceptions thrown during the measurement phase, divided by total measurement operations to give exceptions per operation. Collected via an `AppDomain.CurrentDomain.FirstChanceException` subscription scoped to the measurement loop.

**Why it matters:** Exception-driven control flow is a common hidden cost. `TryParse` failures, regex matching against non-matching input, and serialization fallbacks all throw first-chance exceptions that do not appear in any other metric. A high exceptions-per-op value explains latency that allocation and timing data cannot.

**Overhead:** moderate. The `FirstChanceException` event fires on every exception in the process during the subscription window, not just benchmark exceptions. NBenchmark subscribes only for the measurement phase and unsubscribes in a `finally` block so the handler cannot leak.

**Default:** off. Enable with `Exceptions = true` or `--diagnostics all`.

### CPU time

Process CPU time (via `Process.GetCurrentProcess().TotalProcessorTime`) bracketed around each sample, divided by total measurement operations to give CPU nanoseconds per operation. Also reports the CPU/wall-clock ratio (`CpuTime / MeasuredDuration`).

**Why it matters:** The CPU/wall-clock ratio distinguishes CPU-bound benchmarks (ratio near the core count) from IO-bound or wait-bound benchmarks (ratio well below the core count). A ratio of `1.0` on a single-core run means the body is purely CPU-bound; a ratio of `0.25` on a 4-core machine means 75% of the wall-clock time was spent waiting.

**Overhead:** low. `TotalProcessorTime` is a counter read, not a measurement operation.

**Default:** off. Enable with `CpuTime = true`, `--diagnostics gcandcpu`, or `--diagnostics all`.

> [!NOTE]
> The CPU/wall-clock ratio is process-wide and can exceed `1.0` on multi-core machines. A multi-threaded benchmark on a 4-core machine can show up to `4.0` (400%). In console output, CPU% is colour-coded from the raw ratio: green at 85%+, yellow at 50-85%, red below 50%.

## How collection works

Diagnostics are collected during Phase C (measurement) only. Calibration and warmup phases do not collect diagnostics, mirroring the allocation-tracking pattern.

For each sample, `DiagnosticMeter.Capture()` reads the current counter values before the body loop runs, and `DiagnosticMeter.Delta()` computes the difference after. The per-sample deltas are stored in a `DiagnosticDelta` array on `AdaptiveResult`.

Exception counting uses a separate mechanism: `ExceptionCounter.Subscribe()` attaches a `FirstChanceException` handler before the measurement loop, and `ExceptionCounter.Unsubscribe()` detaches it in a `finally` block. The handler increments a thread-safe counter via `Interlocked.Increment`. The total count is read after the loop and divided by total measurement operations.

Heap info is a per-benchmark snapshot, not per-sample: `GC.GetGCMemoryInfo()` is called once before the measurement loop and once after. The delta (committed and fragmented bytes) is reported directly.

## The DiagnosticsResult record

All collected counters are available on `BenchmarkResult.Diagnostics` as a `DiagnosticsResult?`:

| Field | Type | Meaning |
|---|---|---|
| `Gen0Collections` | `long?` | Total Gen0 collections during measurement. |
| `Gen1Collections` | `long?` | Total Gen1 collections during measurement. |
| `Gen2Collections` | `long?` | Total Gen2 collections during measurement. |
| `HeapCommittedBytes` | `long?` | Heap committed bytes delta. |
| `HeapFragmentedBytes` | `long?` | Heap fragmented bytes delta. |
| `ExceptionCountPerOp` | `double?` | Exceptions per operation (total exceptions / measurement ops). |
| `CpuTimeNsPerOp` | `double?` | CPU time per operation in nanoseconds. |
| `CpuWallRatio` | `double?` | CPU time / wall-clock time (process-wide, can exceed 1.0 on multi-core). |
| `Mode` | `DiagnosticsMode` | Which counters were collected. |

All fields are `null` when the corresponding toggle was off, when diagnostics are disabled (`DiagnosticsOptions.None`), or when the run errored.

## Configuring diagnostics

### Programmatic (any mode)

```csharp
// Single mode
var result = Benchmark.Run(() => MyMethod(), options: new MeasurementOptions
{
    Diagnostics = DiagnosticsOptions.All,
});

// Suite mode
await new BenchmarkSuite("MySuite")
    .WithDiagnostics(DiagnosticsMode.All)
    .Add("MethodA", () => MethodA())
    .RunAsync();

// Harness mode
BenchmarkHarness.Create(args)
    .WithDiagnostics(DiagnosticsMode.GcAndCpu)
    .RunAsync();
```

### Custom combinations

`DiagnosticsOptions` is a record, so you can enable any combination of toggles:

```csharp
// GC counts + exceptions, no CPU time or heap info
var options = new MeasurementOptions
{
    Diagnostics = new DiagnosticsOptions
    {
        GcCollectionCounts = true,
        Exceptions = true,
    },
};
```

### CLI (Harness mode)

```bash
# Default - GC counts only
dotnet run -- --diagnostics gc

# GC counts + CPU time
dotnet run -- --diagnostics gcandcpu

# Everything
dotnet run -- --diagnostics all

# Disable all diagnostics
dotnet run -- --diagnostics none
```

The `--diagnostics` flag overrides any programmatic `Diagnostics` setting, mirroring how `--no-allocations` overrides `MeasureAllocations`.

## Reporting

### Diagnostics table

At `standard` and `advanced` detail levels, a separate **Diagnostics** table appears below the Precision & Tail Latency table. The table only renders when at least one benchmark has diagnostics data. Columns are dynamic - only columns with data appear:

| Column | Source | When it appears |
|---|---|---|
| Benchmark | Benchmark name | Always (when table renders) |
| Runtime | Runtime moniker | When multi-runtime results are present |
| Gen0, Gen1, Gen2 | Collection count totals | When `GcCollectionCounts` is on |
| Heap | Committed bytes (formatted) | When `GcHeapInfo` is on |
| CPU% | CPU/wall ratio as a percentage | When `CpuTime` is on |
| Exc/op | Exceptions per operation | When `Exceptions` is on |

### Advanced stats block

At `advanced` detail, the per-benchmark stats block (shown below each console row, or in the Markdown details section) includes a **Diagnostics** sub-block with the same fields in a vertical layout:

```
Diagnostics:
  Gen0: 12   Gen1: 0   Gen2: 0
  Heap: 1.2 MB (fragmented 80 KB)
  CPU: 98% (1.2 µs/op)
  Exc/op: 0.0033
```

### CSV columns

The CSV reporter adds diagnostics columns at all detail levels:

| Detail level | Columns added |
|---|---|
| Simple | `Gen0`, `Gen1`, `Gen2` |
| Standard | `Gen0`, `Gen1`, `Gen2` |
| Advanced | `Gen0`, `Gen1`, `Gen2`, `HeapCommitted`, `HeapFragmented`, `ExceptionPerOp`, `CpuTimeNsPerOp`, `CpuWallRatio`, `DiagnosticsMode` |

Null values render as empty fields, consistent with other optional columns.

### JSON

The JSON reporter serializes the full `BenchmarkResult` record, so `Diagnostics` appears automatically as a `diagnostics` object with all non-null fields. No configuration needed.

### Live progress

Both the default and Spectre.Console progress displays append a GC summary to the `OnBenchmarkCompleted` line when GC collection counts are available:

```
  ✓ MyBenchmark  42.3 ns · 12/0/0 GC  (1.2s)
```

The three numbers are Gen0/Gen1/Gen2 collection totals.

## Platform compatibility

All four counters use BCL APIs available on every .NET runtime NBenchmark targets (net8.0, net9.0, net10.0) and on all operating systems (Linux, macOS, Windows). No platform-specific code or elevated privileges are required.

Hardware performance counters (instructions retired, cache misses, branch mispredictions) are not included. Those require platform-specific APIs (`perf_event_open` on Linux, kernel drivers on Windows, unavailable on macOS) and are deferred to a potential future `NBenchmark.Diagnostics.HardwareCounters` package.

## See also

- [Configuration: Diagnostics](../reference/configuration.md#diagnostics) - the `DiagnosticsOptions` surface
- [CLI Reference: `--diagnostics`](../reference/cli.md#--diagnostics-mode) - the CLI flag
- [Allocation Measurement](./allocations.md) - how per-iteration heap allocation is sampled
- [Report Detail Levels](../output/report-detail-levels.md) - how the Diagnostics table fits into each detail tier

---

