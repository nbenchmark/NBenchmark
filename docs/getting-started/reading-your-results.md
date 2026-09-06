---
title: Reading Your Results
description: How to interpret every column, indicator, and warning in NBenchmark's output.
order: 4
---

# Reading Your Results

This page explains how to interpret the console output and how to act on the results. For the mathematical details behind any specific value, see the linked statistics pages.

## Quick reference guide

Use this table to quickly evaluate your benchmark results:

| Observation | Meaning | Action |
| --- | --- | --- |
| **CV under 5%** and **Error under 1%** | The benchmark is stable. | Trust the result. |
| **CV over 20%** or **Error over 5%** | The benchmark is noisy. | Fix the noise before drawing conclusions. See [Troubleshooting](../troubleshooting.md). |
| **✓** in Sig, with Small, Medium, or Large magnitude | A real performance difference exists. | This result is worth acting on. |
| **✓** in Sig, with Negligible magnitude | The difference is real but tiny. | Likely too small to be meaningful in practice. |
| **✗** in Sig | The engine cannot distinguish between the versions. | Do not claim a performance win. |
| **Any warning** | The result requires a caveat. | Read the warning to understand the limitation. |

## Console output example

The following example shows the output of `result.Print(ReportDetail.Standard)`:

```text
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 342.1 ns       Mean: 348.7 ns
  │  Ops/s:  2.87 Mops/s    Median ops/s: 2.92 Mops/s
  │  P95: 361.2 ns  P99: 378.5 ns  P99.9: 380.0 ns
  │  StdDev: 8.3 ns         CV:   2.38%
  │  Error:  ±3.1 ns (0.89% of Mean)
  │  CI:     [345.6 ns … 351.8 ns] (95%)
  │  Alloc/op: 0 B
  │
  │  Measured in an isolated worker under 'steady-state'.
  │
  └─────────────────────────────────────────────────
```

A bare `result.Print()` call shows only the Median and Ops/s. Use `ReportDetail.Advanced` to include quartiles, fences, and distribution shape. For more information, see [Report detail levels](../output/report-detail-levels.md).

In suite mode, the console reporter adds a comparison table with Ratio, Sig, and Magnitude columns. For the full table layout, see the [Console Reporter](../output/console-reporter.md) page.

## Understanding the columns

### Median
The middle value when all measurements are sorted. The median is the most reliable single number for comparing two benchmarks because it ignores extreme outliers. If one benchmark has a lower median than another, it is generally faster.

For more information, see [Descriptive Statistics: Median](../statistics/descriptive.md#median).

### Mean
The arithmetic average. The mean is typically close to the median for stable code but diverges when timings vary widely. The engine uses the mean to compute the confidence interval (shown in the Error column).

For more information, see [Descriptive Statistics: Mean](../statistics/descriptive.md#mean).

### Error
The margin of error on the mean at the configured confidence level (default 95%). It is shown as `±X (Y%)`, where X is the absolute margin in nanoseconds and Y is the margin as a percentage of the mean.

- **Small Error (e.g., under 1%)**: The mean is precisely estimated.
- **Large Error**: Your measurements are highly variable.

In auto-sampling mode, NBenchmark collects samples until the error meets the precision target. Therefore, a wide interval usually indicates genuine run-to-run variability rather than an insufficient number of samples.

If you encounter a large error, try the following:
- Check for external interference, such as background processes or thermal throttling.
- Use the `Thorough` preset to demand a tighter target.
- Increase the iteration count if you pinned `Iterations`, or return to auto mode.
- See [Troubleshooting](../troubleshooting.md) for further remedies.

For more information, see [Descriptive Statistics: Confidence Interval](../statistics/descriptive.md#confidence-interval-on-the-mean).

### StdDev and CV
- **StdDev (Standard Deviation)**: Measures how spread out your measurements are. A high StdDev relative to the mean indicates inconsistent timings.
- **CV (Coefficient of Variation)**: The StdDev divided by the mean. A CV of 0.05 means the standard deviation is 5% of the mean. A CV of 0.5 or higher indicates high variability; treat these results with caution.

For more information, see [Descriptive Statistics](../statistics/descriptive.md#coefficient-of-variation).

### P95 / P99 / P99.9
Percentiles describe the distribution tail. P95 indicates that 95% of individual measurements completed within that time. These metrics are critical for latency-sensitive code where worst-case behavior is more important than the average.

You can configure the reported percentiles via `--percentiles` or `MeasurementOptions.ReportedPercentiles`.

For more information, see [Descriptive Statistics: Percentiles](../statistics/descriptive.md#percentiles).

### Ratio (suite mode)
The speed relative to the baseline. A ratio of `0.75x` means the implementation is 25% faster, while `2.0x` means it is twice as slow. The baseline is either the benchmark you designated with `WithBaseline` or the fastest benchmark in the group.

#### `n/a` in the Ratio column
The engine only calculates ratios between rows measured under the same runtime configuration. If a row was measured differently (for example, a `[Isolation(Isolation.Off)]` benchmark in a table of isolated ones), the ratio reads `n/a`. An **Iso** column will indicate which rows were isolated.

Runtime configuration significantly impacts small measurements. Because an in-process reading and an isolated reading of the same body can differ substantially, a ratio spanning them would report the configuration difference rather than the code difference. Compare rows measured using the same method, or remove `[Isolation(Isolation.Off)]` so the entire group is isolated. For more information, see [Isolated runs](../features/isolated-runs.md).

### Sig (suite mode)

| Symbol | Meaning |
| --- | --- |
| **✓** | The difference from the baseline is statistically significant (p < 0.05). It is very unlikely to be noise. |
| **✗** | The difference is not statistically significant. You cannot confidently conclude one is faster than the other. |
| (blank) | The benchmark is the baseline, or significance was not tested (fewer than 2 samples in a group). |

**Recommendations:**
- If you see a **✓** with a small Ratio (e.g., `1.01x`), the difference is statistically real but may be too small to matter. Check the Magnitude column.
- If you see an **✗** with a large Ratio (e.g., `1.5x`), the measurements are too noisy to be conclusive. Try reducing noise (see [Tuning for noisy CI](../guides/tuning-recipes.md#tuning-for-noisy-ci-environments)) or collecting more samples.

For more information, see [Significance Testing](../statistics/significance.md).

### Magnitude (suite mode)
The magnitude indicates how large the difference is, regardless of whether it is statistically significant:

| Label | Meaning |
| --- | --- |
| Negligible | The two distributions overlap almost completely. The difference is tiny. |
| Small | A modest but detectable shift. |
| Medium | A clear, practically meaningful difference. |
| Large | The distributions barely overlap. This is a very strong difference. |

The sign convention is as follows: a positive value indicates the candidate is slower than the baseline (shown in red in the console reporter), and a negative value indicates the candidate is faster (shown in green).

A statistically significant result (**✓**) with a Negligible magnitude means the difference is real but likely insignificant in practice. Focus on results with Small, Medium, or Large magnitudes.

For more information, see [Significance Testing: Cliff's Delta](../statistics/significance.md#technical-detail-cliffs-delta).

### Alloc/op
The mean heap allocation per operation. Zero allocations in the hot path typically result in less GC pressure and more predictable latency. If you see unexpected allocations, check for value type boxing, LINQ overhead, or string formatting in the measured code.

For more information, see [Allocation Measurement](../statistics/allocations.md).

## Auto-tune diagnostic line

In Advanced detail mode (`--detail advanced`), the output includes an auto-tune line:

```text
auto-tuned: K=64, warmup=12, samples=47, CI half-width=1.8%, jitter=0.03
```

| Field | Meaning |
| --- | --- |
| K | Operations per sample - the number of back-to-back invocations timed together. |
| warmup | The number of warmup samples collected before measurement began. |
| samples | The number of measured samples collected. |
| CI half-width | The achieved confidence interval half-width when sampling stopped. |
| jitter | The pre-flight jitter metric (lower is better; < 0.05 indicates a quiet host). |

If the jitter metric is high (e.g., > 0.10) and the outlier detector was auto-switched, the engine provides a warning explaining the switch.

For more information, see [Measurement: The measurement loop](../statistics/measurement.md#the-measurement-loop).

## Bimodal distribution warning

If you see a warning similar to the following:

```text
⚠ MyBench.FastPath: 5 discarded outlier(s) form a distinct cluster near 502 ns rather than
  scattered noise - possible bimodal distribution; investigate this tail latency
```

This indicates that the slow samples were not random noise, but instead represented a repeatable second execution profile, such as a cache miss, lock contention, or GC pause. The reported median describes the common case, while the cluster center describes a latency that real users will encounter.

**Recommended actions:**
- Do not ignore this warning; it reveals important information about your code's performance distribution.
- Review the tail metrics (P99, P99.9, and Max). These are computed from the full pre-trim distribution by default, so the second cluster is already visible.
- Use a profiler to investigate the cause.
- If you suspect GC issues, try using `--profile independent`.

For more information, see [Outlier Trimming: Bimodal-distribution warning](../statistics/outliers.md#bimodal-distribution-warning).

## Operations per second (Ops/s)

Operations per second is derived from the mean timing. `Median ops/s` is derived from the median. These metrics are useful for throughput-oriented comparisons.

## When to trust the numbers

- **Stable**: Low CV (< 5%) and small Error (< 1%) indicate a stable benchmark with reliable numbers.
- **Noisy**: High CV (> 20%) or large Error (> 5%) indicates a noisy benchmark. See [Troubleshooting](../troubleshooting.md) for configuration remedies.
- **Bimodal**: A bimodal warning indicates that the median describes the common case, but a second execution profile exists. Investigate this before trusting the numbers as representative of all calls.

## See also

For more information, see the following pages:

- [Key Concepts](./key-concepts.md) - Understand the conceptual meaning of the numbers.
- [Descriptive Statistics](../statistics/descriptive.md) - Review the formulas behind every field.
- [Significance Testing](../statistics/significance.md) - Learn how Sig and Magnitude are computed.
- [Outlier Trimming](../statistics/outliers.md) - Understand how outliers are detected and removed.
- [Measurement](../statistics/measurement.md) - Explore how the adaptive loop works.
- [Troubleshooting](../troubleshooting.md) - Fix common measurement problems.
