---
title: Statistics
description: How NBenchmark measures, analyzes, and reports benchmark data.
order: 6
---

# Statistics

This section explains the mathematical methodology NBenchmark uses to collect and analyze measurements.

For related information, see the following pages:
- [Key Concepts](../getting-started/key-concepts.md) covers the practical side of measurement.
- [Reading Your Results](../getting-started/reading-your-results.md) provides a guide to interpreting the output.
- [Deep Dives](../deep-dives/) details the engineering internals, such as clock probing, process boundaries, and numerical resolution.

## In this section

- [Measurement](./measurement.md) - The measurement loop, timer resolution, the warmup sequence, and the host drift canary.
- [Allocation Measurement](./allocations.md) - How the engine samples per-operation heap allocation.
- [Outlier Trimming](./outliers.md) - IQR fence, MAD, fixed-quota modes, custom detectors, and bimodal-distribution warnings.
- [Descriptive Statistics](./descriptive.md) - mean, median, percentiles, standard deviation, confidence intervals, CV, distribution shape (skewness, kurtosis, MAD), and the `BenchmarkResult` field reference.
- [Ratios](./ratios.md) - Why the `Ratio` column is a paired per-launch estimate on the log scale rather than a quotient of two medians, how to read its interval, and how to handle disagreements between `Sig` and the interval.
- [Significance Testing](./significance.md) - Non-parametric tests for comparing groups, including the Mann-Whitney U test, the Kruskal-Wallis omnibus test (with post-hoc pairwise Mann-Whitney U and Holm-Bonferroni correction), p-value interpretation, Cliff's delta effect size, the `MinimumPracticalEffect` gate, and custom tests.
- [Diagnostics](./diagnostics.md) - Runtime counters for GC collection counts, heap state, exceptions, and CPU time.
- [Validation & Accuracy](./validation.md) - How the numerical implementations are verified against SciPy and NumPy.

## See also

For more information about the engineering internals behind these numbers (such as the clock probe, jitter auto-switch, optional-stopping correction, and worker protocol), see [Deep Dives](../deep-dives/).
