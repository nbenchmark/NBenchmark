---
title: Reading Your Results
description: How to interpret every column, indicator, and warning in NBenchmark's output.
order: 0
---

# Reading Your Results

This page explains what you see in the console output and what to do with it. For the mathematical detail behind any number, follow the links to the statistics pages.

## Console output example

```
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
  └─────────────────────────────────────────────────
```

In suite mode, the console reporter adds a comparison table with Ratio, Sig, and Magnitude columns. See [Console Reporter](./console-reporter.md) for the full table layout.

## The columns

### Median

The middle value when all measurements are sorted. This is the most reliable single number to compare two benchmarks because it ignores extreme outliers. If one benchmark has a lower median than another, it is generally faster.

See [Descriptive Statistics: Median](../statistics/descriptive.md#median).

### Mean

The arithmetic average. Close to the median for stable code; further away when timings vary widely. The mean is used to compute the confidence interval (the Error column).

See [Descriptive Statistics: Mean](../statistics/descriptive.md#mean).

### Error

The margin of error on the mean at the configured confidence level (default 95%). Shown as `±X (Y%)` - the absolute margin in nanoseconds followed by the margin as a percentage of the mean.

A small Error (e.g. under 1%) means the mean is precisely estimated. A large Error means your measurements are highly variable. In auto-sampling mode, NBenchmark keeps collecting samples until the Error meets the precision target, so a wide interval usually points to genuine run-to-run variability rather than too few samples.

**What to do about a large Error:**
- Check whether something external is interfering (background processes, thermal throttling)
- Use the `Thorough` preset to demand a tighter target
- If you pinned `Iterations`, raise the count or return to auto mode
- See [Troubleshooting](../troubleshooting.md) for more remedies

See [Descriptive Statistics: Confidence Interval](../statistics/descriptive.md#confidence-interval-on-the-mean).

### StdDev and CV

**StdDev** (standard deviation) measures how spread out your measurements are. A high StdDev relative to the mean means your timings are inconsistent.

**CV** (coefficient of variation) is StdDev divided by the mean - a dimensionless measure. A CV of 0.05 means the standard deviation is 5% of the mean. A CV of 0.5 or higher indicates high variability and the results should be treated with caution.

See [Descriptive Statistics](../statistics/descriptive.md#coefficient-of-variation).

### P95 / P99 / P99.9

Percentiles tell you about the distribution tail. P95 means 95% of individual measurements completed within this time. These are important for latency-sensitive code where you care about worst-case behaviour, not just the average.

The set of reported percentiles is configurable via `--percentiles` or `MeasurementOptions.ReportedPercentiles`.

See [Descriptive Statistics: Percentiles](../statistics/descriptive.md#percentiles).

### Ratio (suite mode)

Speed relative to the baseline. `0.75x` = 25% faster; `2.0x` = twice as slow. The baseline is either the benchmark you designated with `WithBaseline` or the benchmark with the lowest median.

### Sig (suite mode)

| Symbol | Meaning |
|---|---|
| **✓** | The difference from the baseline is statistically significant (p < 0.05). It is very unlikely to be noise. |
| **✗** | The difference is not statistically significant. You cannot confidently conclude one is faster than the other. |
| (blank) | The benchmark is the baseline, or significance was not tested (fewer than 2 samples in a group). |

**What to do:**
- A ✓ with a small Ratio (e.g. `1.01x`) means the difference is statistically real but may be too small to matter in practice. Check the Magnitude column.
- A ✗ with a large Ratio (e.g. `1.5x`) means the measurements are too noisy to tell. Try reducing noise (see [Tuning for noisy CI](../reference/configuration.md#tuning-for-noisy-ci-environments)) or collecting more samples.

See [Significance Testing](../statistics/significance.md) for the full detail.

### Magnitude (suite mode)

Classifies the effect size using Cliff's delta:

| Label | What it means |
|---|---|
| Negligible | The two distributions overlap almost completely. The difference is tiny. |
| Small | A modest but detectable shift. |
| Medium | A clear, practically meaningful difference. |
| Large | The distributions barely overlap. A very strong difference. |

The sign convention is: positive = candidate is slower than baseline (shown in red in the console reporter); negative = candidate is faster (shown in green).

**What to do:** A statistically significant result (✓) with a Negligible magnitude means the difference is real but too small to care about. Focus on results with Small, Medium, or Large magnitudes.

See [Significance Testing: Cliff's Delta](../statistics/significance.md#technical-detail-cliffs-delta).

### Alloc/op

The mean heap allocation per operation. Zero allocations in the hot path typically means less GC pressure and more predictable latency. If you see unexpected allocations, check for boxing of value types, LINQ overhead, or string formatting in the measured code.

See [Allocation Measurement](../statistics/allocations.md).

## The auto-tune diagnostic line

In Advanced detail mode (`--detail advanced`), the output includes an auto-tune line:

```
auto-tuned: K=64, warmup=12, samples=47, CI half-width=1.8%, jitter=0.03
```

| Field | Meaning |
|---|---|
| K | Ops per sample - how many back-to-back invocations were timed together |
| warmup | How many warmup samples were collected before measurement started |
| samples | How many measured samples were collected |
| CI half-width | The achieved confidence interval half-width when sampling stopped |
| jitter | The pre-flight jitter metric (lower is better; < 0.05 = quiet host) |

If the jitter metric is high (e.g. > 0.10) and the outlier detector was auto-switched, you will also see a warning explaining the switch.

See [Measurement: The measurement loop](../statistics/measurement.md#the-measurement-loop).

## The bimodal-distribution warning

If you see a warning like:

```
⚠ MyBench.FastPath: 5 discarded outlier(s) form a distinct cluster near 502 ns rather than
  scattered noise - possible bimodal distribution; investigate this tail latency
```

This means the slow samples were **not** random noise - they were a repeatable second execution profile (e.g. a cache miss, lock contention, or GC pause). The reported median describes the common case; the cluster centre describes a latency a real user will also hit.

**What to do:**
- Do not ignore it. The warning is telling you something real about your code's performance distribution.
- Re-run with `OutlierMode.None` to see the full distribution.
- Investigate the cause with a profiler.
- If you suspect GC, try `--profile independent`.

See [Outlier Trimming: Bimodal-distribution warning](../statistics/outliers.md#bimodal-distribution-warning).

## The Ops/s column

Operations per second, derived from the mean timing. `Median ops/s` is derived from the median. These are useful for throughput-oriented comparisons.

## When to trust the numbers

- **Low CV** (< 5%) and **small Error** (< 1%): the benchmark is stable and the numbers are reliable.
- **High CV** (> 20%) or **large Error** (> 5%): the benchmark is noisy. See [Troubleshooting](../troubleshooting.md) for configuration remedies.
- **Bimodal warning**: the median describes the common case, but a real second execution profile exists. Investigate before trusting the numbers as representative of all calls.

## See also

- [Key Concepts](../getting-started/key-concepts.md) - understand what the numbers mean conceptually
- [Descriptive Statistics](../statistics/descriptive.md) - the formulas behind every field
- [Significance Testing](../statistics/significance.md) - how Sig and Magnitude are computed
- [Outlier Trimming](../statistics/outliers.md) - how outliers are detected and removed
- [Measurement](../statistics/measurement.md) - how the adaptive loop works
- [Troubleshooting](../troubleshooting.md) - fix common measurement problems
