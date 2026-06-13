---
title: Significance Testing
description: How NBenchmark decides whether benchmark differences are statistically real - the Mann-Whitney U test for two groups and the Kruskal-Wallis test for three or more.
order: 4
---

# Significance Testing

When two or more benchmarks have been run, NBenchmark tests whether their differences are statistically real rather than measurement noise. The test it picks depends on how many benchmarks you are comparing:

| Groups | Default test | What it answers |
|---|---|---|
| Exactly 2 | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (pairwise) | Does the candidate differ from the baseline? |
| 3 or more | [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) (omnibus) | Do *any* of the groups differ? |

Both are **[non-parametric](https://en.wikipedia.org/wiki/Nonparametric_statistics)** rank-based tests, chosen because benchmark timings are right-skewed and rarely normal. The Mann-Whitney U test compares exactly two samples, so for three or more groups NBenchmark runs the Kruskal-Wallis **omnibus** test - a generalization of Mann-Whitney U to *k* groups that reports a single verdict for the whole comparison. Both default choices are made by `DefaultSignificanceTest`; you can override the strategy entirely (see [Custom significance tests](#custom-significance-tests)).

## Mann-Whitney U test (two groups)

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

A [p-value](https://en.wikipedia.org/wiki/P-value) below the configured significance level (alpha, default **0.05**) is considered significant (✓ in the Sig column). Set it with `MeasurementOptions.SignificanceLevel`, the `.WithSignificanceLevel(...)` fluent method, or the `--alpha` CLI flag.

Using the exact test for small samples removes the main source of error in the old normal-approximation-only approach, where the asymptotic p-value could differ from the exact permutation p-value by up to ≈ 0.05. For larger samples the continuity-corrected normal approximation is accurate and matches SciPy's asymptotic method closely; the exact and approximate paths are cross-checked against SciPy in [Validation & Accuracy](./validation.md).

> [!NOTE]
> NBenchmark uses the **pre-trim raw samples** (before outlier removal) for significance testing. This gives the test more data to work with. However it means that significance is assessed on the full distribution including extreme measurements.

### Minimum sample requirement

The test requires at least **2 samples in each group**. With fewer samples the U statistic is undefined and the test returns `null` (no significance indicator is shown).

## Kruskal-Wallis test (three or more groups)

When three or more benchmarks are compared, running a series of pairwise Mann-Whitney U tests would inflate the false-positive rate (the [multiple-comparisons problem](https://en.wikipedia.org/wiki/Multiple_comparisons_problem)). Instead NBenchmark runs the **Kruskal-Wallis H test** once across all groups - the rank-based generalization of one-way [ANOVA](https://en.wikipedia.org/wiki/Analysis_of_variance) - and reports a single **omnibus** verdict: *are any of these groups drawn from different distributions?*

### Algorithm

Given `k` groups of **pre-trim raw samples** with total size `N = Σnᵢ`:

1. Rank all `N` values together, assigning **mid-ranks** to ties.
2. Sum the ranks within each group: `Rᵢ`.
3. Compute the H statistic:

$$H = \frac{12}{N(N+1)} \sum_{i=1}^{k} \frac{R_i^2}{n_i} - 3(N+1)$$

1. Apply the **tie correction** factor $C = 1 - \frac{\sum (t^3 - t)}{N^3 - N}$ (summed over each set of `t` tied values) and divide: `H ← H / C`.
2. Under the null hypothesis, `H` follows a [chi-squared distribution](https://en.wikipedia.org/wiki/Chi-squared_distribution) with `k − 1` degrees of freedom. The p-value is its [survival function](https://en.wikipedia.org/wiki/Survival_function) $P(\chi^2_{k-1} \ge H)$, computed from the regularized upper incomplete gamma function.

A p-value below alpha means **at least one** group differs - the omnibus test does not say *which*. The console and Markdown reporters print the omnibus line below the table, for example:

```
Omnibus Kruskal-Wallis across 3 groups: H(2) = 7.20, p = 0.027 → significant
```

(For three groups `{1,2,3}`, `{4,5,6}`, `{7,8,9}` the statistic is `H = 7.2` on `2` degrees of freedom, `p ≈ 0.027`.) When every value is identical (`H = 0`, `p = 1`) or fewer than two groups have data, the test reports "not tested".

> [!NOTE]
> The omnibus verdict is attached to every row's `BenchmarkResult.Omnibus`. The per-row pairwise `PValue` / `SignificanceVerdict` are left untested in the 3+ group case, because a significant omnibus result does not localize the difference to any single pair. If you need pairwise verdicts across many groups, supply a custom post-hoc test (next section).

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
            pairwise.Add(new PairwiseComparison(candidate.Name, PValue: null, verdict));
        }

        return new SignificanceReport { Pairwise = pairwise };
    }

    private static double Median(double[] samples) { /* sort a copy, take the middle */ }
}
```

Register it through `MeasurementOptions.SignificanceTest`, the suite builder, or the host:

```csharp
// Suite mode
.WithSignificanceTest(new MedianRatioSignificanceTest(thresholdPercent: 25))

// Quick / Host mode
new MeasurementOptions { SignificanceTest = new MedianRatioSignificanceTest(25) }
```

`Analyze` receives a `SignificanceContext` (the comparable `Groups`, the `BaselineIndex`, the `Baseline` group, the non-baseline `Candidates`, and the `SignificanceLevel`) and returns a `SignificanceReport` containing:

- **`Pairwise`** - one `PairwiseComparison(name, pValue, verdict)` per candidate. Use `PValue: null` for rules that do not produce a p-value.
- **`Omnibus`** - an optional single verdict across all groups. Set it for omnibus tests like Kruskal-Wallis; leave it `null` for purely pairwise tests.

The built-in strategies - `MannWhitneyUSignificanceTest`, `KruskalWallisSignificanceTest`, and the group-count-aware `DefaultSignificanceTest` - all implement this same interface, so you can also wrap or compose them.
