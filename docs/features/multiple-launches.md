---
title: "Multiple launches"
description: Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.
order: 6
---

# Multiple launches

**Harness mode launches each benchmark five times by default.** `Benchmark.Run` and `BenchmarkSuite` run once unless you increase the count. Each launch uses a separate worker process with its own warmup and GC cycle. Variance across launches reflects real run-to-run differences - such as process state, ASLR, scheduler placement, and clock granularity - rather than just intra-run noise.

A single launch cannot estimate how much a number moves between runs, which is what most readers assume the confidence interval describes. Five launches are used by default because the between-launch interval stops being too wide for a real regression to clear (see [Why five?](#why-five) below).

The primary result fields (median, mean, percentiles, etc.) are the **average across launches**. The reported confidence interval is derived from the spread **between** launches rather than from within any single run (see [Why the average, not the best launch](#why-the-average-not-the-best-launch)). When `LaunchCount` is greater than 1, the engine computes cross-launch statistics and displays them in a "Launch Aggregation" table below the main results.

Use multiple launches when single-run noise is a concern and you want to understand how stable the measurement is at the launch level.

## Suite mode: `WithLaunchCount`

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort(data))
    .Add("array", () => Array.Sort(data))
    .WithBaseline("bubble")
    .WithLaunchCount(5)             // Five independent launches per benchmark
    .WithIterations(100)
    .WithWarmup(10)
    .RunAsync();
```

## Harness mode: `--launch-count` CLI flag

```bash
dotnet run -- --launch-count 5
```

Alternatively, use `WithLaunchCount` in code:

```csharp
BenchmarkHarness.Create(args)
    .WithLaunchCount(5)
    .RunAsync();
```

The launch count is not a field on `MeasurementOptions`, so `WithOptions` cannot carry it. A launch is a process; the host process spends the count by launching workers. Because a worker measures exactly once, it is never sent the launch count. For more information, see [why](../reference/configuration.md#launchcount).

## Per-method attribute override

In Harness mode, each `[Benchmark]` can specify its own launch count via the `LaunchCount` property:

```csharp
// Ten independent launches for this method only
[Benchmark(LaunchCount = 10)]
public int NoisyMethod() => Compute();

// Use the harness default (five launches, aggregated)
[Benchmark]
public int StableMethod() => Compute();
```

The `--launch-count` flag overrides per-method attributes if both are present. This is useful when you want a specific method to receive extra launches without affecting other benchmarks:

```csharp
public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    // This method runs 20 launches; Baseline and Fast use the harness default (5).
    [Benchmark(LaunchCount = 20)]
    public int NoisyWork() => ExpensiveJob();

    [Benchmark]
    public int Fast() => QuickJob();
}
```

> [!NOTE]
> An isolated group takes the maximum launch count. NBenchmark spawns one set of workers per isolated group, using the highest launch count any member requested. Consequently, raising the count for one method also raises it for every benchmark measured alongside it. Use `[IsolatedProcess]` to confine extra launches to a single method.

## Reading the reproducibility warning

Two fields on `LaunchStatistics` help you determine if you should trust the interval on a row:

| Field | Description |
| --- | --- |
| `LaunchStandardDeviation` | How much the median moves between launches (the reproducibility of the number). |
| `WithinLaunchStandardError` | How precisely a single launch measured its own median (the precision it claimed). |
| `ProcessVarianceRatio` | The ratio of the two fields above. |
| `BetweenLaunchDispersion` | The between-launch spread as a fraction of the measurement. |

When the ratio exceeds 4, NBenchmark issues a warning:

```text
⚠ Run-to-run variation across 5 launches is 61x the precision any single launch reported, so
  this benchmark measures far more precisely than it reproduces. Run-to-run spread is 1.4% of
  the measurement. The Error on this row is the between-launch interval and already accounts
  for that, but the significance verdict does not - it pools samples across launches, so it
  inherits the power of the pooled count rather than the reproducibility of the measurement.
```

**Expect large ratios for nanosecond-scale bodies.** A ratio of 30-60 is ordinary for very fast code. A cheap body collects thousands of samples, which drives its standard error toward zero while leaving real machine variance - such as code and heap layout, scheduler placement, and clock granularity - untouched. The warning identifies which interval to trust; it does not mean the benchmark is broken.

The `Error` column already includes this variance because a multi-launch row reports the between-launch half-width. However, the **significance verdict** does not account for this, as it pools raw samples across every launch. At a high ratio, treat the `Sig` column as provisional and compare the per-launch medians in the Launch Aggregation table.

Raising `--launch-count` sharpens the estimate of the spread but does not narrow the spread itself. No configuration inside a single process can reduce this variance. If the spread is too wide to gate on, reduce it at the source using [environment controls](./environment-control.md) or a quieter host.

> [!NOTE]
> The ratio divides by the standard error, not the standard deviation.
> A within-process interval is `t × s / √n`. Comparing between-process spread against the
> per-sample `s` would understate the problem by `√n`. Since `n` reaches the thousands for
> fast bodies, dividing by `s` would leave the warning silent even when the single-launch
> interval is 21× narrower than the true run-to-run spread.

## Dry-run interaction

The `--dry-run` flag (Iterations=0, WarmupIterations=0) ignores both the harness default and `--launch-count`, performing exactly one dry launch. Extra launches provide no additional information because dry runs skip the body. However, an explicit `WithLaunchCount(n)` in code is still honored.

## Isolation interaction

In isolated mode (the Harness mode default), NBenchmark spawns N worker processes per isolated group. Because a worker is never sent the launch count, it cannot repeat the measurement internally. Per-method attribute overrides are respected: NBenchmark uses the maximum launch count across all benchmarks in the group to ensure every benchmark receives at least the requested number of launches.

In Suite mode, the suite repeats in a fresh worker process per launch. The host process orchestrates these repeats, which provides the run-to-run reproducibility estimate. This is particularly important for the `[BenchmarkPlan]` path, where the user's factory runs in both the host and every worker. The worker's copy of the suite carries the `WithLaunchCount(3)` setting, but the worker's measurement path ignores it.

## Test-integration interaction

A `[Performance]` test defaults to `LaunchCount = 1` to avoid the cost of multiple worker launches. Setting the launch count on the attribute spends them the same way a suite run does - one worker per replicate - and gives the test's ratio gate a paired confidence interval. A test that names a `ReferenceMethod` measures both sides inside each worker, so the replicate count equals the number of launches. For more information, see [test integration](../test-integration/index.md#replicates-and-the-paired-ratio).

## Example

```bash
# Run each benchmark three times and show the launch aggregation table
dotnet run -- --launch-count 3

# Run a specific benchmark with extra attention via attribute:
dotnet run -- --filter MyBenchmarks.NoisyWork
```

The "Launch Aggregation" table shows the cross-launch mean, standard deviation, median, and 95% confidence interval for each benchmark that ran multiple launches. Only benchmarks with `LaunchCount > 1` appear in this table.

## Why the average, not the best launch

Averaging is necessary because each launch is a separate process. Differences between launches result from systematic components - such as different CPU draws, page layouts, and address-space layouts - rather than transient noise. Reporting the fastest launch would simply select the luckiest draw and produce an overly optimistic number.

Taking the interval from the spread between launches provides an honest measurement. For example, three launches of the same in-process benchmark might read 4.32, 3.63, and 1.66 ns:

| | reported median | reported interval |
| --- | --- | --- |
| fastest launch | 1.66 ns | ±0.02 ns (that launch's own precision) |
| average of launches | 3.20 ns | **±3.42 ns** |

The second row is the honest result. The first claims a number that does not reproduce is known to within one percent.

Two additional details on how the aggregate is built:
- Counts and durations (`N`, `MeasuredIterations`, `TotalDuration`) are totals across all launches.
- `Min` and `Max` span everything observed across all launches.
- `RawSamples` and trimmed-sample marks come from the single launch nearest the averaged median. This is necessary because marks are positions into a specific sample array.
- Significance testing reads the pooled samples from every launch.

## Why five?

Five is where the between-launch interval stops being too wide for a real regression to clear. The interval is a Student-t half-width on `k - 1` degrees of freedom. The critical value drops steeply over the first few replicates (12.71 at `k = 2`, 4.30 at 3, 3.18 at 4, 2.78 at 5) and then flattens. Below five, the interval is often too wide to detect a regression.

## See also

For more information, see the following pages:

- [Suite mode](../usage-modes/suite-mode.md) - The full fluent API.
- [Harness mode](../usage-modes/harness-mode.md) - Attribute-based discovery and CLI.
- [Isolated runs](./isolated-runs.md) - How launches interact with process isolation.
- [CLI reference](../reference/cli.md) - All `BenchmarkHarness` flags.
