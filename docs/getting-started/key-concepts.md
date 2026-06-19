---
title: Key Concepts
description: What warmup, outlier trimming, confidence intervals, and statistical significance mean in a benchmarking context.
order: 3
---

# Key Concepts

You don't need to be a statistician to use NBenchmark, but understanding what it does under the hood will help you interpret results correctly and avoid common pitfalls.

## Warmup

Before any measurements are recorded, each benchmark runs a number of **warmup samples** that are discarded.

Warmup exists because the first few runs of .NET code are artificially slow:

- The **JIT compiler** hasn't compiled your method yet. The first call triggers [compilation](https://en.wikipedia.org/wiki/Just-in-time_compilation), which takes time.
- **CPU caches** are cold. Memory that your code accesses frequently won't be in the [L1/L2 cache](https://en.wikipedia.org/wiki/CPU_cache) yet.

If you skipped warmup, your first measurements would include JIT compilation time, which is not representative of steady-state performance. After warmup, subsequent runs use the compiled, cached version of your code.

By default NBenchmark **auto-detects** how much warmup each benchmark needs: it watches the per-sample timings and stops once they stop improving (a plateau). A method that JITs quickly gets a short warmup; one that keeps speeding up gets a longer one. Pin an exact count with `WithWarmup(n)` / `WarmupIterations = n` when you want a fixed budget.

> [!TIP]
> If your benchmark is a one-shot operation where you specifically want to measure cold-start time, set `WithWarmup(0)` to skip warmup entirely.

## Samples and ops

NBenchmark records one **sample** per measured timing. For a fast method, a single call can be quicker than the cost of reading the timer, so a per-call measurement would be mostly noise.

To handle this, NBenchmark times a batch of **K** back-to-back invocations (**ops per sample**) as one sample, then divides by K to report a per-operation number. K is **auto-calibrated** so each sample spans roughly 1 µs - long enough that timer overhead is negligible. A method that takes microseconds gets K = 1; a method that takes nanoseconds gets a larger K.

You rarely need to touch this, but you can pin K with `WithOpsPerSample(n)` / `OpsPerSample = n`. Calibration is skipped (K stays 1) when per-iteration setup/teardown is configured, since a batch would then no longer represent a single call. See [Configuration: OpsPerSample](../reference/configuration.md#opspersample).

## Garbage collection

NBenchmark uses a **measurement profile** to control how GC interacts with your benchmarks. The default profile is **Realistic**, which does not force a GC between iterations. Numbers reflect natural GC pressure, which is what your code sees in production.

The alternative is the **Independent** profile, which forces a gen-0 GC before every iteration and a full GC between benchmarks. This keeps measurements independent of each other but suppresses the natural GC pressure your code would experience in production.

The profile is set via `WithMeasurementProfile(MeasurementProfile.Independent)` in code or `--profile independent` on the CLI. See [Measurement Profiles](../statistics/measurement.md#measurement-profiles) for a worked example.

The profile controls three behaviours:

| Behaviour | Realistic (default) | Independent |
|---|---|---|
| Per-iteration Gen0 GC | Off | On |
| Between-benchmark full GC | Off | On |
| Allocation tracking | On | Off |

Each behaviour can be overridden individually via the `*Override` fields on `MeasurementOptions` or the `--force-gc` / `--no-allocations` CLI flags.

## Outlier trimming

Even with warmup and forced GC, occasional **OS scheduling interrupts**, context switches, or thermal throttling can spike an individual measurement. These outliers are not representative of your code's performance - they reflect system noise.

By default, NBenchmark trims samples beyond an **[IQR fence](https://en.wikipedia.org/wiki/Interquartile_range)** before computing statistics (`OutlierMode.IqrFence`): anything below `Q1 - 1.5 × IQR` or above `Q3 + 1.5 × IQR`. Unlike a fixed quota, this adapts to the run - a clean run keeps almost every sample, while a noisy run trims more. If the discarded slow samples cluster tightly (a possible second execution profile rather than random noise), NBenchmark adds a bimodal-distribution warning to the result.

The fence values (`LowerFence`, `UpperFence`) are first-class fields on `BenchmarkResult` and are visible in Advanced detail mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`).

Available modes:

| Mode | What is removed |
|---|---|
| `None` | Nothing. All samples are used. |
| `RemoveTop5Percent` | The slowest 5% of samples. |
| `RemoveTopAndBottom5Percent` | The slowest and fastest 5%. |
| `IqrFence` | Any sample beyond 1.5× the [inter-quartile range](https://en.wikipedia.org/wiki/Interquartile_range). **(default)** |
| `MedianAbsoluteDeviation` | Any sample beyond 3× the scaled [median absolute deviation](https://en.wikipedia.org/wiki/Median_absolute_deviation) - more robust to heavy skew than `IqrFence`. |

## Median vs. mean

The **median** is the middle value when all measurements are sorted. It is robust to outliers - a few very slow measurements do not pull it upward.

The **mean** is the arithmetic average. Even after outlier trimming, the mean is more sensitive to skewed distributions than the median.

For most purposes, the **median** is the most reliable single number to compare two benchmarks. The mean is useful when you also care about the confidence interval (see below).

## Standard deviation

**Standard deviation (StdDev)** measures how spread out your measurements are. A [standard deviation](https://en.wikipedia.org/wiki/Standard_deviation) high relative to the mean means your timings are inconsistent - the code runs in very different amounts of time from iteration to iteration. This can be caused by:

- GC pressure and pauses
- Cache misses due to working-set size
- OS scheduling
- Thermal throttling on laptops

A low StdDev means your benchmark is stable and the mean is trustworthy.

> [!TIP]
> If you see high StdDev or a large Error, see the [Troubleshooting guide](../troubleshooting.md) for configuration remedies.

## Confidence intervals and the Error column

The **Error** column shows the **[margin of error](https://en.wikipedia.org/wiki/Margin_of_error)** on the mean at a given confidence level (default: 95%) in the format `±X (Y%)` -- the absolute margin in nanoseconds followed by the margin as a percentage of the mean in parentheses.

Concretely, at a 95% confidence level the true mean is likely within `Mean ± Error`. If the Error is `±50 ns (4.2%)` and the mean is `1.20 µs`, you can be 95% confident the true mean is somewhere between `1.15 µs` and `1.25 µs`.

**How it's calculated:** NBenchmark computes the [standard error of the mean](https://en.wikipedia.org/wiki/Standard_error) (`StdDev / √n`), then multiplies by the critical value of [Student's t-distribution](https://en.wikipedia.org/wiki/Student%27s_t-distribution) at the configured confidence level and `n-1` degrees of freedom. For large sample counts this converges to the familiar `z = 1.96` you might know from normal-distribution CIs.

**What a large Error means:** your measurements are highly variable. In the default auto-sampling mode NBenchmark keeps collecting samples until the Error meets the precision target, so a wide interval usually points to genuine run-to-run variability - check whether something external is interfering (e.g. background processes on your machine), or demand a tighter target with the `Thorough` preset. If you have pinned `Iterations`, raising it (or returning to auto mode) narrows the interval.

## Percentiles

Percentiles tell you about the distribution tail. By default NBenchmark reports P50 (the median, already shown separately), P95, P99, P99.9, and the maximum. Each percentile answers: "X% of measurements completed within this time."

**P95** means 95% of individual measurements completed within this time. **P99** means 99%. **P99.9** means 99.9%. These are important for **latency-sensitive** code where you care about worst-case behaviour, not just the average. A method might have a low median but a high P99 if it occasionally triggers GC or hits a slow path.

The set of reported percentiles is configurable via `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. Access a specific percentile value programmatically with `result.GetPercentile(0.95)`.

## Statistical significance

When comparing two or more benchmarks, it's not enough to see that one has a lower median. The difference might be random noise.

NBenchmark uses the **[Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test)** to answer: "Is this difference statistically significant?" The test implementation (along with every other statistical primitive in the library) is dependency-free and cross-validated against SciPy and NumPy - see [Validation & Accuracy](../statistics/validation.md).

- A **✓** in the Sig column means the difference would occur by chance less than 5% of the time (p < 0.05). It is very unlikely to be noise.
- A **✗** means the difference is not statistically significant - you cannot confidently conclude one is faster than the other.
- The test requires at least **2 samples** in each group; with fewer it returns no result.

The Mann-Whitney U test is **[non-parametric](https://en.wikipedia.org/wiki/Nonparametric_statistics)** - it makes no assumption that your timings follow a normal (bell-curve) distribution, which benchmark timings generally do not.

When you compare **three or more** benchmarks, NBenchmark first runs the **[Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) omnibus test**. If the omnibus is significant (at least one group differs), it follows up with pairwise Mann-Whitney U tests (candidate versus baseline) with Holm-Bonferroni correction, and the per-row Sig column shows the corrected verdicts. If the omnibus is not significant, no post-hoc comparisons run and the per-row Sig column stays blank. The default strategy is `DefaultSignificanceTest`; you can swap in your own via `ISignificanceTest` (see [Significance Testing](../statistics/significance.md#custom-significance-tests)).

Statistical significance is reported with a standardized magnitude: the **Magnitude** column shows Cliff's delta classified as Negligible / Small / Medium / Large. See [Effect size: Cliff's delta](../statistics/significance.md#effect-size-cliffs-delta) for the thresholds, the sign convention, and the opt-in `MinimumPracticalEffect` gate.

> [!NOTE]
> Statistical significance does not mean the difference is *large* or *important*. A tiny 0.1 ns difference can be statistically significant with many iterations. Read the Magnitude column alongside Sig and the Ratio column.

## Allocation tracking

When `MeasureAllocations` is enabled, NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` before and after each iteration and reports the **mean bytes allocated per iteration** in the Alloc/op column. If an async iteration resumes on a different thread, it falls back to a process-wide allocation delta for that sample.

This is useful for detecting unexpected heap allocations - boxing of value types, LINQ overhead, string formatting, etc. Zero allocations in the hot path typically means less GC pressure and more predictable latency.

Allocation tracking is **on by default** under the `Realistic` profile. It can be disabled with `--no-allocations` on the CLI or `MeasureAllocationsOverride = false` in code. Under the `Independent` profile it is off by default but can be re-enabled.

## Next steps

- **[Guides](../guides/)** - see these concepts applied in real benchmarks
- **[Statistics](../statistics/)** - the full mathematical detail
- **[Configuration](../reference/configuration.md)** - tune iterations, warmup, outlier mode, and confidence level
