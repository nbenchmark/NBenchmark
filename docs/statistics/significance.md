---
title: Significance Testing
description: How NBenchmark decides whether benchmark differences are statistically real.
order: 6
---

# Significance Testing

When two or more benchmarks are run, NBenchmark tests whether their differences are statistically real or merely measurement noise. The test used depends on the number of benchmarks being compared:

| Groups | Default test | Purpose |
|---|---|---|
| Exactly 2 | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (pairwise) | Determines if the candidate differs from the baseline. |
| 3 or more | [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) (omnibus) + post-hoc Mann-Whitney U with [Holm-Bonferroni](https://en.wikipedia.org/wiki/Holm%E2%80%93Bonferroni_method) correction | Determines if each candidate differs from the baseline (gated by the omnibus result). |

## Scope of significance

- **Suite mode** (`BenchmarkSuite`): Significance is computed across every benchmark in the suite, using a single baseline chosen from the whole suite.
- **Harness mode** (`BenchmarkHarness`): Significance is computed **per class** by default. Each class has its own baseline, and the `Sig` and `Magnitude` values are relative to that baseline.
- **Cross-class mode**: Use the `--cross-class` CLI flag or call `WithCrossClassSignificance()` in code to compute significance across all classes in a single comparison table. The baseline is chosen from the entire group, and a `Class` column is added to the report. Use this when comparing implementations in separate classes (e.g., a legacy version and a refactored version).

## Interpreting the output

### The Sig column

| Symbol | Meaning |
|---|---|
| **✓** | The difference from the baseline is statistically significant ($p < \alpha$, default 0.05). It is very unlikely to be noise. |
| **✗** | The difference is not statistically significant. You cannot confidently conclude that one implementation is faster than the other. |
| (blank) | The benchmark is the baseline, or significance was not tested (e.g., fewer than 2 samples in a group, or a non-significant omnibus result). |

**Guidelines for reading the Sig column:**
- A **✓** with a small Ratio (e.g., `1.01x`) indicates the difference is statistically real but may be practically meaningless. Check the **Magnitude** column.
- A **✗** with a large Ratio (e.g., `1.5x`) indicates the measurements are too noisy to draw a conclusion. Try reducing noise (see [Tuning for noisy CI](../guides/tuning-recipes.md#tuning-for-noisy-ci-environments)) or collecting more samples.
- A **✓** whose ratio interval spans `1.00x` (shown as `1.24x?` in the console) indicates a disagreement between the significance test and the ratio interval. In this case, trust the interval. See [Ratios](./ratios.md#when-sig-and-the-ratio-interval-disagree).

You can configure the significance threshold ($\alpha$) via `MeasurementOptions.SignificanceLevel`, the `.WithSignificanceLevel(...)` fluent method, or the `--significance-level` CLI flag.

### The Magnitude column

A p-value indicates whether a difference is unlikely under the null hypothesis, but not how large that difference is. NBenchmark reports the effect size in the **Magnitude** column (Negligible / Small / Medium / Large), classified using **Cliff's delta**.

| Magnitude | $|\delta|$ range | Meaning |
|---|---|---|
| Negligible | $[0, 0.147)$ | The two distributions overlap almost completely. The difference is tiny. |
| Small | $[0.147, 0.33)$ | A modest but detectable shift. |
| Medium | $[0.33, 0.474)$ | A clear, practically meaningful difference. |
| Large | $[0.474, 1.0]$ | The distributions barely overlap. A very strong difference. |

**Sign convention:** A positive delta means the candidate tends to be slower than the baseline (shown in red in the console); a negative delta means the candidate is faster (shown in green).

If a result is statistically significant (**✓**) but has a **Negligible** magnitude, the difference is real but likely too small to matter. Focus on results with Small, Medium, or Large magnitudes.

### Practical-significance gate

`MeasurementOptions.MinimumPracticalEffect` requires a minimum practical-effect score in $[0, 1]$ for a comparison to be meaningful. It defaults to `0.147` (the boundary between negligible and small effects). Consequently, a **✓** verdict by default means the difference is statistically real **and** at least a small effect.

- Comparisons with a practical effect below the threshold are reported as `Magnitude = MagnitudeLabel.Negligible`.
- The `Sig` verdict is downgraded from `Significant` to `NotSignificant` even if the p-value is below $\alpha$. A warning records this downgrade in the reports.
- Set the value to `0` (`--min-practical-effect 0`) to restore p-value-only verdicts, or set it to `null` in code to disable the gate.

The engine enforces the gate in `Significance.ApplyReport` after the test runs, so it works for any `ISignificanceTest` implementation - not just the built-in ones. Custom tests that return an `EffectSize` with `PracticalValue` are gated automatically; tests that do not return a practical value are unaffected.

```csharp
// Restore p-value-only verdicts (disable the default gate)
.WithMinimumPracticalEffect(0)

// Or demand a stronger effect: reject significance below |delta| = 0.33 (the "medium" threshold)
.WithMinimumPracticalEffect(0.33)
```

You can set this via `MeasurementOptions.MinimumPracticalEffect`, `BenchmarkSuite.WithMinimumPracticalEffect(...)`, `BenchmarkHarness.WithMinimumPracticalEffect(...)`, or the `--min-practical-effect <0-1>` CLI flag.

### The omnibus line (three or more groups)

When three or more benchmarks are compared, the console and Markdown reporters print an omnibus line below the table:

```text
Omnibus Kruskal-Wallis across 3 groups: H(2) = 7.20, p = 0.027 → significant
```

If the omnibus is **significant**, NBenchmark runs post-hoc pairwise comparisons and shows the Holm-Bonferroni-corrected verdict for each candidate. If the omnibus is **not significant**, no post-hoc comparisons run, and the `Sig` column remains blank.

### Minimum sample requirement

The significance tests require at least **2 samples in each group**. If a group has fewer samples, the U statistic is undefined, and the `Sig` column remains blank.

### Pre-trim raw samples

NBenchmark uses the **pre-trim raw samples** (before outlier removal) for significance testing. This provides more data for the test but means that significance is assessed on the full distribution, including extreme measurements.

---

## Technical detail: Mann-Whitney U test (two groups)

NBenchmark uses the **Mann-Whitney U test** (also called the Wilcoxon rank-sum test) to determine if the difference between two benchmarks' distributions is statistically significant.

### Why Mann-Whitney U?

Benchmark timings are typically right-skewed and do not follow a normal distribution. Parametric tests like the t-test assume normality. The Mann-Whitney U test is **[non-parametric](https://en.wikipedia.org/wiki/Nonparametric_statistics)**; it ranks combined values rather than computing moments, making no distributional assumptions.

### Algorithm

Given the **pre-trim raw samples** of two benchmarks A (length $n_1$) and B (length $n_2$):

1. Merge and sort all $n_1 + n_2$ values together, recording the source of each sample.
2. Assign **mid-ranks** to tied values: all tied observations share the average rank of the positions they occupy.
3. Compute the rank sum for group A: $R_1 = \sum \text{rank}(A_i)$.
4. Compute the U statistics:
   $$U_1 = R_1 - \frac{n_1(n_1+1)}{2}, \quad U_2 = n_1 n_2 - U_1, \quad U = \min(U_1, U_2)$$

**P-value calculation:**
- **Small, tie-free samples** (combined $n_1 + n_2 \le 20$ with no tied values): NBenchmark computes the **exact** two-sided [permutation](https://en.wikipedia.org/wiki/Permutation_test) p-value by enumerating the full distribution of U. This matches `scipy.stats.mannwhitneyu(..., method='exact')`.
- **Otherwise**: NBenchmark uses the [normal approximation](https://en.wikipedia.org/wiki/Normal_distribution#Central_limit_theorem) with a tie correction and a continuity correction to compute a z-score and the resulting two-tailed p-value.

A p-value below the configured significance level ($\alpha$, default **0.05**) is considered significant (**✓**).

## Technical detail: Kruskal-Wallis test (three or more groups)

To avoid inflating the false-positive rate when comparing multiple benchmarks (the [multiple-comparisons problem](https://en.wikipedia.org/wiki/Multiple_comparisons_problem)), NBenchmark first runs the **Kruskal-Wallis H test**. This rank-based generalization of one-way [ANOVA](https://en.wikipedia.org/wiki/Analysis_of_variance) reports a single **omnibus** verdict: *do any of these groups differ?*

### Algorithm

Given $k$ groups of **pre-trim raw samples** with total size $SampleCount = \Sigma n_i$:

1. Rank all $N$ values together, assigning mid-ranks to ties.
2. Sum the ranks within each group: $R_i$.
3. Compute the H statistic:
   $$H = \frac{12}{N(N+1)} \sum_{i=1}^{k} \frac{R_i^2}{n_i} - 3(N+1)$$
4. Apply a **tie correction** factor $C = 1 - \frac{\sum (t^3 - t)}{N^3 - N}$ and divide: $H \leftarrow H / C$.

Under the null hypothesis, $H$ follows a [chi-squared distribution](https://en.wikipedia.org/wiki/Chi-squared_distribution) with $k − 1$ degrees of freedom. The p-value is computed from the regularized upper incomplete gamma function.

### Post-hoc pairwise comparisons

If the Kruskal-Wallis omnibus is significant, NBenchmark performs a **pairwise Mann-Whitney U test** for each candidate versus the baseline. To control the family-wise error rate across $m$ comparisons, the raw p-values are adjusted using the **Holm-Bonferroni** step-down procedure:

1. Sort the $m$ raw p-values ascending: $p_{(1)} \le p_{(2)} \le \dots \le p_{(m)}$.
2. For each step $j$ (0-indexed), compute the adjusted p-value:
   $$p_{(j)}^{\text{adj}} = \max\left(\min\left((m - j) \cdot p_{(j)}, 1\right), p_{(j-1)}^{\text{adj}}\right)$$
   where $p_{(-1)}^{\text{adj}} = 0$.
3. A candidate is marked **significant** (**✓**) when its adjusted p-value is below $\alpha$.

The `PValue` field on `BenchmarkResult` stores the **raw** p-value, while `SignificanceVerdict` reflects the Holm-Bonferroni-corrected decision. Always use `SignificanceVerdict` as the authoritative signal.

## Technical detail: Cliff's delta

Cliff's delta is a non-parametric effect size that quantifies how often one sample's value exceeds the other's:

$$\delta = \frac{\#(b > a) - \#(b < a)}{n_1 \cdot n_2}$$

where $a$ represents baseline samples and $b$ represents candidate samples. It ranges from $-1$ to $1$:
- **+1**: Every candidate sample exceeds every baseline sample (candidate is uniformly slower).
- **0**: The two distributions overlap completely.
- **-1**: Every baseline sample exceeds every candidate sample (candidate is uniformly faster).

NBenchmark uses the [Romano et al. (2006)](https://en.wikipedia.org/wiki/Effect_size) thresholds to classify $|\delta|$ into Magnitude labels (Negligible, Small, Medium, Large). `EffectSize.Magnitude` carries that band as a `MagnitudeLabel`, or `null` when the strategy does not band its statistic; `MagnitudeLabel.ToShortString()` renders the abbreviations the reports show.

## Technical detail: Hodges-Lehmann shift

While Cliff's delta measures consistency, the **Hodges-Lehmann** estimate (`BenchmarkResult.MedianShift`) measures the magnitude of the shift in time units. It is the median of all pairwise candidate − baseline differences.

- **Point estimate**: $\text{median}(\{ b_j − a_i \})$.
- **Interval**: the k-th smallest to k-th largest pairwise difference, with `k = ⌊mn/2 − z·σ_U⌋` and the tie-corrected Mann-Whitney `σ_U` - the same construction R's `wilcox.test(conf.int = TRUE)` uses in its normal-approximation branch. The interval excludes zero exactly when the U test rejects at `α = 1 − confidenceLevel`.
- **Complexity**: Because the pairwise set is $O(n_1 \cdot n_2)$, NBenchmark materializes it in full on the same arrays the Mann-Whitney U test sees. Sharing $n$ is what makes the interval's zero-exclusion agree with the U test's rejection.

## Technical detail: i.i.d. sanity checks

The CI-width stop rule and the Mann-Whitney test assume independent, identically distributed (i.i.d.) samples. Drift (such as JIT tier-ups) or autocorrelation can shrink the computed interval, understating true uncertainty. When at least 50 samples are available, NBenchmark runs two post-hoc checks:

- **Drift**: A split-half Mann-Whitney U test between the first and second halves of the stream. A warning fires when $p < 0.001$.
- **Dependence**: A lag-1 autocorrelation $r$. A warning fires when $r > 0.5$.

These warnings are advisory; the result is still reported. They suggest that the reported interval may be too narrow and point toward longer warmup or host stability issues.

## Numerical accuracy of the asymptotic tail

The Mann-Whitney asymptotic branch reads the standard-normal CDF from an erfc accurate to ~1e-15 relative (W. J. Cody's rational Chebyshev approximation). This matters only for deep-tail exported p-values: at `α = 0.05` any reasonable approximation suffices, but p-values below ~1e-7 are meaningful rather than noise.

## Custom significance tests

The significance strategy is pluggable through `ISignificanceTest`. You can implement this interface to provide a bootstrap comparison, a Bayesian test, or a domain-specific rule.

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

            pairwise.Add(new PairwiseComparison(
                candidate.Name,
                PValue: null,
                Verdict: verdict,
                Effect: new EffectSize(
                    Metric: "median-ratio",
                    Value: deltaPercent,
                    Magnitude: deltaPercent switch
                    {
                        < 5 => MagnitudeLabel.Negligible,
                        < 15 => MagnitudeLabel.Small,
                        < 30 => MagnitudeLabel.Medium,
                        _ => MagnitudeLabel.Large,
                    },
                    Direction: EffectDirection.None,
                    PracticalValue: Math.Min(1.0, deltaPercent / 100.0))));
        }

        return new SignificanceReport { Pairwise = pairwise };
    }

    private static double Median(ReadOnlyMemory<double> samples) { /* implementation */ }
}
```

Register your test through `MeasurementOptions.SignificanceTest`, the suite builder, or the harness:

```csharp
// Suite mode
.WithSignificanceTest(static () => new MedianRatioSignificanceTest(thresholdPercent: 25))

// Single / Harness mode
new MeasurementOptions { SignificanceTest = static () => new MedianRatioSignificanceTest(25) }
```

The `Analyze` method receives a `SignificanceContext` and returns a `SignificanceReport` containing pairwise comparisons, an optional effect size, an optional location shift, and an optional omnibus verdict.
