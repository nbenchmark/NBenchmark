---
title: Statistics
description: How NBenchmark measures, analyses, and reports benchmark data.
order: 3
---

# Statistics

This section explains how NBenchmark collects and analyses measurements. The [Key Concepts](../getting-started/key-concepts.md) page covers the practical side. These pages are for readers who want the full mathematical picture.

## In this section

- **[Measurement](./measurement.md)** - the measurement loop, timer resolution, and warmup sequence.
- **[Outlier Trimming](./outliers.md)** - IQR fence, MAD, fixed-quota modes, custom detectors, and the bimodal-distribution warning.
- **[Descriptive Statistics](./descriptive.md)** - mean, median, percentiles, standard deviation, confidence intervals, CV, distribution shape (skewness, kurtosis, MAD), and the complete `BenchmarkResult` field reference.
- **[Significance Testing](./significance.md)** - the Mann-Whitney U test for two groups and the Kruskal-Wallis omnibus test (with post-hoc pairwise Mann-Whitney U and Holm-Bonferroni correction) for three or more: why non-parametric, the algorithms, p-value interpretation, **Cliff's delta effect size and Magnitude column**, the `MinimumPracticalEffect` practical-significance gate, and custom tests.
- **[Allocation Measurement](./allocations.md)** - how per-iteration heap allocation is sampled.
- **[Validation & Accuracy](./validation.md)** - how the numerical implementations are verified against SciPy and NumPy.
