---
title: Statistics
description: How NBenchmark measures, analyses, and reports benchmark data.
order: 5
---

# Statistics

This section explains how NBenchmark collects and analyses measurements - the mathematical methodology behind the numbers. The [Key Concepts](../getting-started/key-concepts.md) page covers the practical side. For a practical guide to interpreting the output you see on screen, see [Reading Your Results](../output/reading-your-results.md). For the engineering internals - how the clock is probed, what crosses the process boundary, how the engine resolves its numbers - see [Deep Dives](../deep-dives/).

## In this section

- **[Measurement](./measurement.md)** - the measurement loop, timer resolution, and warmup sequence.
- **[Allocation Measurement](./allocations.md)** - how per-iteration heap allocation is sampled.
- **[Outlier Trimming](./outliers.md)** - IQR fence, MAD, fixed-quota modes, custom detectors, and the bimodal-distribution warning.
- **[Descriptive Statistics](./descriptive.md)** - mean, median, percentiles, standard deviation, confidence intervals, CV, distribution shape (skewness, kurtosis, MAD), and the complete `BenchmarkResult` field reference.
- **[Ratios](./ratios.md)** - why the `Ratio` column is a paired per-launch estimate on the log scale rather than a quotient of two medians, how to read its interval, and what to do when `Sig` and the interval disagree.
- **[Significance Testing](./significance.md)** - the Mann-Whitney U test for two groups and the Kruskal-Wallis omnibus test (with post-hoc pairwise Mann-Whitney U and Holm-Bonferroni correction) for three or more: why non-parametric, the algorithms, p-value interpretation, **Cliff's delta effect size and Magnitude column**, the `MinimumPracticalEffect` practical-significance gate, and custom tests.
- **[Diagnostics](./diagnostics.md)** - runtime counters for GC collection counts, heap state, exceptions, and CPU time.
- **[Validation & Accuracy](./validation.md)** - how the numerical implementations are verified against SciPy and NumPy.

## See also

- [Deep Dives](../deep-dives/) - the engineering internals behind these numbers (clock probe, jitter auto-switch, optional-stopping correction, worker protocol)
