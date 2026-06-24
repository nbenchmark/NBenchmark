---
title: Statistics
description: How NBenchmark measures, analyses, and reports benchmark data.
order: 5
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
