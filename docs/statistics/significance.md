---
title: Significance Testing
description: How NBenchmark decides whether benchmark differences are statistically real - the Mann-Whitney U test for two groups and the Kruskal-Wallis omnibus test (with post-hoc pairwise Mann-Whitney U and Holm-Bonferroni correction) for three or more. Plus Cliff's delta effect size and the opt-in MinimumPracticalEffect practical-significance gate.
order: 5
---

# Significance Testing

When two or more benchmarks have been run, NBenchmark tests whether their differences are statistically real rather than measurement noise. The test it picks depends on how many benchmarks you are comparing:

| Groups | Default test | What it answers |
|---|---|---|
| Exactly 2 | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (pairwise) | Does the candidate differ from the baseline? |
| 3 or more | [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) (omnibus) + post-hoc Mann-Whitney U with [Holm-Bonferroni](https://en.wikipedia.org/wiki/Holm%E2%80%93Bonferroni_method) correction | Does each candidate differ from the baseline? (gated on the omnibus) |

### Scope: suite mode versus Host mode

- In **suite mode** (`BenchmarkSuite`), significance is computed across every benchmark in that one suite. A single baseline is chosen from the whole suite.
- In **Host mode** (`BenchmarkHost`), significance is computed **per class** by default. Each discovered class gets its own baseline, and `Sig` / `Magnitude` are relative to that class's baseline. The console reporter renders one comparison table per class.
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

Set `MeasurementOptions.MinimumPracticalEffect` (or use `BenchmarkSuite.WithMinimumPracticalEffect(...)` / `BenchmarkHost.WithMinimumPracticalEffect(...)`) to require a minimum practical-effect score in `[0, 1]` for a comparison to count as meaningful. Built-in Mann-Whitney tests map this score to `|delta|`; custom tests can map any effect metric by returning `EffectSize.PracticalValue` in `PairwiseComparison`.

- Comparisons with practical effect below the threshold are reported with `Magnitude = neg` (so a sub-threshold result is never labelled `large`).
- The Sig verdict is downgraded from `Significant` to `NotSignificant` even when the p-value is below alpha.
- The configured value must be in the range `[0, 1]`.

The engine enforces the gate in `Significance.ApplyReport` after the test runs, so it works for any `ISignificanceTest` implementation - not just the built-in ones. Custom tests that return an `EffectSize` with `PracticalValue` are gated automatically; tests that do not return a practical value are unaffected.

```csharp
// Reject statistical significance below |delta| = 0.33 (the "small" threshold)
.WithMinimumPracticalEffect(0.33)
```

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

Register it through `MeasurementOptions.SignificanceTest`, the suite builder, or the host:

```csharp
// Suite mode
.WithSignificanceTest(new MedianRatioSignificanceTest(thresholdPercent: 25))

// Quick / Host mode
new MeasurementOptions { SignificanceTest = new MedianRatioSignificanceTest(25) }
```

`Analyze` receives a `SignificanceContext` (the comparable `Groups`, the `BaselineIndex`, the `Baseline` group, the non-baseline `Candidates`, and the `SignificanceLevel`) and returns a `SignificanceReport` containing:

- **`Pairwise`** - one `PairwiseComparison(name, pValue, verdict, effect)` per candidate. Use `PValue: null` for rules that do not produce a p-value.
- **`Effect`** (`EffectSize`) - optional strategy-defined effect metadata (`Metric`, numeric `Value`, string `Magnitude`, `Direction`, and optional normalized `PracticalValue` used by `MinimumPracticalEffect`).
- **`Omnibus`** - an optional single verdict across all groups. Set it for omnibus tests like Kruskal-Wallis; leave it `null` for purely pairwise tests.

The built-in strategies - `MannWhitneyUSignificanceTest`, `KruskalWallisSignificanceTest`, and the group-count-aware `DefaultSignificanceTest` - all implement this same interface, so you can also wrap or compose them.
