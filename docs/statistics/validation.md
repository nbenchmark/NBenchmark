---
title: Validation & Accuracy
description: How NBenchmark's statistical results are verified against reference implementations.
order: 8
---

# Validation & Accuracy

NBenchmark's numerical core is dependency-free. It provides its own implementations of the [Student's t quantile](https://en.wikipedia.org/wiki/Student%27s_t-distribution), the normal quantile, percentiles, the [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test), and the [Kruskal-Wallis test](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test).

To ensure these implementations are reliable, NBenchmark verifies them in the test suite (`tests/NBenchmark.Tests`) using a three-layer approach.

## 1. Property and brute-force recomputation

`StatsRecomputationTests` generates numerous random samples (sizes ranging from 2 to 500 across ten seeds). For each set, it recomputes every reported quantity from first principles within the test to verify accuracy:

| Quantity | Independent recomputation | Tolerance |
|---|---|---|
| Mean | $\sum x_i / n$ | 1e-9 relative |
| Standard deviation | $\sqrt{\sum(x_i-\bar{x})^2/(n-1)}$ | 1e-9 relative |
| Standard error | $s/\sqrt{n}$ | 1e-9 relative |
| Margin of error | $t^{*} \times \text{SEM}$ | 1e-9 relative |
| Winsorized standard error | Clamp, then $s_w\sqrt{n}/h$ | 1e-9 relative |
| Coefficient of variation | $s/\bar{x}$ | 1e-9 relative |
| Percentiles (P1-P99) | Nearest-rank `ceil(p·n)−1` | Exact |

By using arbitrary inputs instead of a few hand-picked arrays, this layer provides a strong guard against regressions in descriptive statistics.

## 2. External cross-checks

`StatsCrossCheckTests` and `MannWhitneyCrossCheckTests` pin NBenchmark's output against values pre-computed with **SciPy 1.17.1** and **NumPy 2.4.6**. `WinsorizedErrorCrossCheckTests` pins the Winsorized standard error - a quantity not computed by those packages - against its reference formula.

| NBenchmark | Reference | Agreement |
|---|---|---|
| `StatsSummary` mean / stddev / SEM | `numpy.mean`, `numpy.std(ddof=1)` | $\le$ 1e-9 relative |
| `Percentile.Compute` | `numpy.percentile(method='inverted_cdf')` | Exact |
| `StudentT.CriticalValue` (df = 1, 2) | `scipy.stats.t.ppf` | $\le$ 1e-9 relative |
| `StudentT.CriticalValue` (df $\ge$ 3) | `scipy.stats.t.ppf` | < 1% (worst $\approx$ 0.79% at df = 3, 99%) |
| `StudentT.NormalQuantile` | `scipy.stats.norm.ppf` | $\le$ 1.15e-8 absolute |
| `MannWhitneyU.Test` (small, tie-free, $n \le 20$) | `scipy.stats.mannwhitneyu(method='exact')` | $\le$ 1e-9 relative |
| `MannWhitneyU.Test` (otherwise) | `scipy.stats.mannwhitneyu(method='asymptotic', use_continuity=True)` | < 1e-6 absolute |
| `ChiSquared.SurvivalFunction` | Closed forms and `scipy.stats.chi2.sf` | $\le$ 1e-9 (closed); $\le$ 1e-4 (spot) |
| `KruskalWallis.Test` (H, p) | `scipy.stats.kruskal` (with tie correction) | $H \le$ 1e-9; $p \le$ 1e-6 |
| `WinsorizedError.Compute` | Yuen's formula / R's `WRS2::trimse` | $\le$ 1e-9 relative |

Reference values are generated using the following Python logic:

```python
import numpy as np
from scipy import stats

np.mean(x)                                   # mean
np.std(x, ddof=1)                            # sample standard deviation
np.percentile(x, q, method='inverted_cdf')  # nearest-rank percentile
stats.t.ppf((1 + cl) / 2, df)                # two-tailed t critical value
stats.norm.ppf(p)                            # normal quantile
stats.mannwhitneyu(a, b, alternative='two-sided', method='exact') # exact p-value
stats.mannwhitneyu(a, b, alternative='two-sided', method='asymptotic', use_continuity=True) # asymptotic p-value
stats.chi2.sf(x, df)                         # chi-squared survival function
stats.kruskal(*groups)                       # Kruskal-Wallis H and p-value
```

### The Winsorized (Yuen) standard error

Since SciPy does not provide a reference for the Winsorized standard error, NBenchmark uses a self-contained generator based on Wilcox's definition (matching R's `WRS2::trimse`):

```python
import math

def winsorized_standard_error(x, g_low, g_high):
    xs = sorted(x)
    n = len(xs)
    h = n - g_low - g_high
    lo, hi = xs[g_low], xs[n - g_high - 1]
    w = [lo] * g_low + xs[g_low:n - g_high] + [hi] * g_high
    mean = sum(w) / n
    s_w = math.sqrt(sum((v - mean) ** 2 for v in w) / (n - 1))
    return s_w * math.sqrt(n) / h
```

`WinsorizedErrorCrossCheckTests` pins the output of this generator. Additionally, `StatsRecomputationTests` asserts the reduction property: when no samples are trimmed (`g_low = g_high = 0`), the result is bit-identical to $s/\sqrt{n}$.

### Exact vs. approximate Mann-Whitney U

For small, tie-free samples (combined $n \le 20$), NBenchmark computes the **exact** [permutation](https://en.wikipedia.org/wiki/Permutation_test) p-value, matching `scipy.stats.mannwhitneyu(method='exact')` to 1e-9. For larger samples, it uses a tie- and continuity-corrected normal approximation that matches SciPy's asymptotic method to better than 1e-6.

Using the exact test on small samples removes a potential gap of up to $\approx$ 0.05 that a normal approximation would introduce.

## 3. End-to-end measurement loop sanity

`TimingSanityTests.Engine_MinimumSample_Is_Near_Known_BusyWait_Floor` runs the full measurement engine against a CPU-bound busy-wait of a known duration. It asserts that the **minimum** sample lands near the target (within 0.9-3.0$\times$).

Because a CPU-bound busy-wait has a hard floor and preemption only adds time, the minimum is more stable than the mean. This test catches critical bugs that deterministic statistical tests might miss, such as unit errors (ns vs ms) or an incorrectly wired timer.

## Limitations of assertions

Some metrics are not asserted against an exact ground truth:
- **Allocation tracking** is treated as a smoke test (e.g., a 64 KiB allocation must report $\ge$ 1 KiB) rather than an exact byte comparison, as framework allocations can occur between counter reads.
- **Absolute timing accuracy** depends on the platform's `Stopwatch` resolution and the OS scheduler; timing tests bound this coarsely rather than precisely.
