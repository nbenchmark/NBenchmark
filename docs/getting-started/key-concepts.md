---
title: Key Concepts
description: The six ideas behind every NBenchmark number - warmup, batching, outliers, median vs mean, confidence, and significance.
order: 5
---

# Key Concepts

You don't need to be a statistician to use NBenchmark. These are the six ideas behind the numbers,
in plain English. Each one links to the full treatment if you want it.

## Warmup

The first few runs of .NET code are artificially slow: the JIT hasn't compiled your method yet, and
CPU caches are cold. Measuring those runs would tell you about startup, not about your code.

So NBenchmark runs your method a while first and throws those timings away. It decides how long by
watching the timings and stopping once they stop improving - a method that settles quickly gets a
short warmup, one that keeps speeding up gets a longer one.

> [!TIP]
> If you specifically want to measure cold-start cost, skip warmup with `WithWarmup(0)`.

**Go deeper:** [Measurement: warmup](../statistics/measurement.md#phase-b---warmup-plateau-detection)
covers the plateau rule, the time floor, and the JIT-quiescence gate.

## Samples and ops

Each timing NBenchmark records is a **sample**. For a very fast method, a single call can take less
time than reading the clock, so timing one call would mostly measure the clock.

NBenchmark handles this by timing a batch of back-to-back calls as one sample, then dividing. The
batch size is chosen automatically - a method taking microseconds gets a batch of 1, a method taking
nanoseconds gets a large one. You rarely need to touch it.

**Go deeper:** [Measurement: ops-per-sample calibration](../statistics/measurement.md#phase-a---ops-per-sample-calibration-k).

## Outliers

Occasional OS scheduling interrupts, context switches, or thermal throttling spike an individual
measurement. Those spikes describe your machine, not your code.

NBenchmark discards them before computing statistics, using a rule that adapts to each run - a clean
run keeps nearly every sample, a noisy one trims more. If the discarded samples cluster tightly
rather than scattering, that's a sign of a real second code path rather than random noise, and
you'll get a warning saying so.

**Go deeper:** [Outlier trimming](../statistics/outliers.md) covers all five modes, the bimodal
warning, and custom detectors.

## Median vs mean

The **median** is the middle value when the measurements are sorted. It's robust: a few slow
measurements don't drag it around.

The **mean** is the average. Even after outlier trimming it's more sensitive to a skewed
distribution than the median.

For comparing two benchmarks, the median is the more reliable single number. The mean is what the
confidence interval is built on.

**Go deeper:** [Descriptive statistics](../statistics/descriptive.md).

## Error and confidence

The **Error** column says how precisely the mean was estimated, as `±X (Y%)`. If the mean is
`1.20 µs` and the Error is `±50 ns (4.2%)`, the true mean is very likely between `1.15 µs` and
`1.25 µs`.

A small Error means the number is solid. A large one means your timings genuinely vary run to run -
NBenchmark already collected samples until it hit its precision target, so a wide interval usually
points at something external interfering rather than at too few samples.

**Go deeper:** [Confidence intervals](../statistics/descriptive.md#confidence-interval-on-the-mean),
and [Troubleshooting](../troubleshooting.md) if your Error is stubbornly large.

## Significance and magnitude

When you compare benchmarks, a lower median isn't enough - the difference might be noise.
NBenchmark answers that in two columns:

- **Sig** - is the difference real? **✓** means yes, **✗** means the measurements are too noisy to
  say, and blank means this row is the baseline or wasn't tested.
- **Magnitude** - is the difference *big*? Negligible, Small, Medium, or Large.

Both matter. A ✓ tells you the difference exists; the Magnitude tells you whether to care. By
default NBenchmark won't give you a ✓ for a difference that's real but negligible, so a ✓ is always
worth acting on.

> [!NOTE]
> Statistical significance is not importance. With enough samples a 0.1 ns difference can be
> significant. Always read Magnitude and Ratio alongside Sig.

**Go deeper:** [Significance testing](../statistics/significance.md) covers which test is used, why
it's non-parametric, effect-size thresholds, and how to swap in your own.

## Two more things you'll meet

**Your benchmarks run in a separate process.** This is on by default in every mode. JIT and GC
settings are fixed when a process starts, so the only way to control them is to start a fresh one.
The `Iso` column in comparison output tells you where each row was measured. See
[Isolated runs](../features/isolated-runs.md).

**Allocations are tracked for free.** The `Alloc/op` column shows mean bytes allocated per
operation, measured outside the timed window so it costs nothing. Zero allocations in a hot path
usually means less GC pressure and steadier latency. See
[Allocation measurement](../statistics/allocations.md).

## Next steps

- **[Reading your results](./reading-your-results.md)** - every column, indicator, and warning
- **[Usage modes](../usage-modes/)** - these concepts applied in real benchmarks
- **[Statistics](../statistics/)** - the full mathematical detail
- **[Configuration](../reference/configuration.md)** - tune for noisy CI, fast feedback, or precision
