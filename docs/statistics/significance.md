---
title: Significance Testing
description: How NBenchmark uses the Mann-Whitney U test to determine whether benchmark differences are statistically real.
order: 4
---

# Significance Testing

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

1. Depending on the sample sizes:
   - **Small, tie-free samples** (combined `n₁ + n₂ ≤ 20` with no tied values): compute the **exact** two-sided [permutation](https://en.wikipedia.org/wiki/Permutation_test) p-value by enumerating the full distribution of U over all rank assignments (via a bounded-partition dynamic program). This matches `scipy.stats.mannwhitneyu(..., method='exact')`.
   - **Otherwise**: use the [normal approximation](https://en.wikipedia.org/wiki/Normal_distribution#Central_limit_theorem) with a **tie correction** and a **continuity correction** to compute a z-score, then derive a two-tailed [p-value](https://en.wikipedia.org/wiki/P-value).

A [p-value](https://en.wikipedia.org/wiki/P-value) below the configured significance level (alpha, default **0.05**) is considered significant (✓ in the Sig column). Set it with `MeasurementOptions.SignificanceLevel`, the `.WithSignificanceLevel(...)` fluent method, or the `--alpha` CLI flag.

Using the exact test for small samples removes the main source of error in the old normal-approximation-only approach, where the asymptotic p-value could differ from the exact permutation p-value by up to ≈ 0.05. For larger samples the continuity-corrected normal approximation is accurate and matches SciPy's asymptotic method closely; the exact and approximate paths are cross-checked against SciPy in [Validation & Accuracy](./validation.md).

> [!NOTE]
> NBenchmark uses the **pre-trim raw samples** (before outlier removal) for significance testing. This gives the test more data to work with. However it means that significance is assessed on the full distribution including extreme measurements.

### Minimum sample requirement

The test requires at least **2 samples in each group**. With fewer samples the U statistic is undefined and the test returns `null` (no significance indicator is shown).
