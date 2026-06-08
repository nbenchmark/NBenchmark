---
title: Validation & Accuracy
description: How NBenchmark's statistical results are verified against reference implementations.
order: 2
---

# Validation & Accuracy

NBenchmark's numerical core is dependency-free - it ships its own implementations
of the [Student's t quantile](https://en.wikipedia.org/wiki/Student%27s_t-distribution), the normal quantile, percentiles, and the
[Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test). This page documents how those implementations are verified,
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
| `MannWhitneyU.Test` | `scipy.stats.mannwhitneyu(method='asymptotic', use_continuity=False)` | < 1e-6 absolute |

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
                   method='asymptotic', use_continuity=False)  # [p-value](https://en.wikipedia.org/wiki/P-value)
```

### Exact vs. approximate Mann-Whitney U

NBenchmark uses the large-sample normal approximation (no continuity
correction). `MannWhitneyCrossCheckTests` also enumerates the **exact**
[permutation](https://en.wikipedia.org/wiki/Permutation_test) distribution in-process (validated against
`scipy.stats.mannwhitneyu(method='exact')` to 1e-9) and pins how far the
approximation can stray from it:

> For 8–10 samples per group the normal-approximation p-value differs from the
> exact permutation p-value by up to **≈ 0.05**. The gap shrinks as the sample
> count grows; with NBenchmark's default of 200 iterations it is negligible.

This is why the significance test requires at least five samples per group.

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
