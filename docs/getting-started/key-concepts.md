---
title: Key Concepts
description: The six ideas behind every NBenchmark number - warmup, batching, outliers, median vs mean, confidence, and significance.
order: 5
---

# Key Concepts

You don't need to be a statistician to use NBenchmark. This page explains the six core ideas behind the measurements in plain English.

## Warmup

The first few runs of .NET code are often slow because the Just-In-Time (JIT) compiler hasn't compiled the method yet and the CPU caches are cold. Measuring these runs would provide data about startup time rather than the steady-state performance of your code.

To prevent this, NBenchmark runs your method several times and discards those initial timings. The engine determines the warmup duration by monitoring the timings and stopping once they stop improving. Methods that settle quickly receive a short warmup, while those that continue to speed up receive a longer one.

> [!TIP]
> If you want to measure cold-start cost specifically, skip the warmup by using `WithWarmup(0)`.

For more information, see [Measurement: warmup](../statistics/measurement.md#phase-b---warmup-plateau-detection), which covers the plateau rule, the time floor, and the JIT-quiescence gate.

## Samples and operations

Each timing that NBenchmark records is a **sample**. For very fast methods, a single call can take less time than it takes to read the system clock; timing a single call in this case would primarily measure the clock's overhead.

NBenchmark handles this by timing a batch of back-to-back calls as a single sample and then dividing the total time by the number of calls. The engine chooses the batch size automatically. For example, a method taking microseconds typically uses a batch size of 1, while a method taking nanoseconds uses a larger batch. You rarely need to adjust this setting.

For more information, see [Measurement: ops-per-sample calibration](../statistics/measurement.md#phase-a---ops-per-sample-calibration-k).

## Outliers

Occasional OS scheduling interrupts, context switches, or thermal throttling can cause individual measurements to spike. These spikes describe the environment of your machine rather than the performance of your code.

NBenchmark discards these outliers before computing statistics using a rule that adapts to each run. A clean run keeps nearly every sample, while a noisy run trims more. If discarded samples cluster tightly rather than scattering, it may indicate a real second code path rather than random noise. In this case, the engine provides a warning.

For more information, see [Outlier trimming](../statistics/outliers.md), which covers the five trimming modes, the bimodal warning, and custom detectors.

## Median vs. mean

- **Median**: The middle value when measurements are sorted. The median is robust because a few slow measurements do not significantly affect it. When comparing two benchmarks, the median is the most reliable single number.
- **Mean**: The average of all samples. Even after outlier trimming, the mean is more sensitive to skewed distributions than the median. The engine uses the mean to build the confidence interval.

For more information, see [Descriptive statistics](../statistics/descriptive.md).

## Error and confidence

The **Error** column indicates how precisely the engine estimated the mean, shown as `±X (Y%)`. For example, if the mean is `1.20 µs` and the Error is `±50 ns (4.2%)`, the true mean likely falls between `1.15 µs` and `1.25 µs`.

A small error indicates a solid measurement. A large error means your timings vary significantly from run to run. Since NBenchmark collects samples until it hits its precision target, a wide interval usually points to external interference rather than an insufficient number of samples.

For more information, see [Confidence intervals](../statistics/descriptive.md#confidence-interval-on-the-mean). If your error remains stubbornly large, see [Troubleshooting](../troubleshooting.md).

## Significance and magnitude

When comparing benchmarks, a lower median alone is not enough to prove one implementation is faster, as the difference might be due to noise. NBenchmark uses two columns to answer this:

- **Sig (Significance)**: Indicates if the difference is real. A **✓** means the difference is statistically significant, an **✗** means the measurements are too noisy to determine a result, and a blank space means the row is the baseline or was not tested.
- **Magnitude**: Indicates if the difference is large enough to matter. Magnitudes are categorized as Negligible, Small, Medium, or Large.

A **✓** tells you the difference exists, and the magnitude tells you whether the difference is meaningful. By default, NBenchmark does not provide a **✓** for differences that are real but negligible.

> [!NOTE]
> Statistical significance is not the same as importance. With enough samples, a 0.1 ns difference can be statistically significant. Always review the Magnitude and Ratio alongside the Sig column.

For more information, see [Significance testing](../statistics/significance.md), which covers the non-parametric test used, effect-size thresholds, and how to implement a custom test.

## Additional features

### Process isolation
By default, benchmarks run in a separate process. This is necessary because JIT and GC settings are fixed when a process starts; the only way to control them is to launch a fresh process. The `Iso` column in comparison output indicates where each row was measured. For more information, see [Isolated runs](../features/isolated-runs.md).

### Allocation tracking
The `Alloc/op` column shows the mean bytes allocated per operation. This is measured outside the timed window, so it does not affect performance results. Zero allocations in a hot path typically result in less GC pressure and steadier latency. For more information, see [Allocation measurement](../statistics/allocations.md).

## Next steps

For more information, see the following pages:

- [Reading your results](./reading-your-results.md) - Understand every column, indicator, and warning.
- [Usage modes](../usage-modes/) - See these concepts applied in real-world benchmarks.
- [Statistics](../statistics/) - Review the full mathematical details.
- [Configuration](../reference/configuration.md) - Learn how to tune for noisy CI environments, fast feedback, or high precision.
