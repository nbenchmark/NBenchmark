---
title: "Multiple launches"
description: Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.
order: 6
---

# Multiple launches

**Harness mode launches each benchmark 5 times by default**; `Benchmark.Run` and `BenchmarkSuite` run once unless you raise the count. Each launch is a separate worker process with its own warmup and GC cycle, so variance across launches reflects real run-to-run differences (process state, ASLR, scheduler placement, clock granularity), not just intra-run noise.

A single launch cannot estimate the thing most readers assume the interval describes: how much the number moves between runs. Five is where the between-launch interval stops being too wide for a real regression to clear - see [Why five?](#why-five) below.

The primary result fields (median, mean, percentiles, etc.) are the **average across launches**, and the reported confidence interval comes from the spread **between** launches rather than from within any one of them - see [Why the average, not the best launch](#why-the-average-not-the-best-launch) at the end of this page. Cross-launch statistics are also computed in full and displayed in a "Launch Aggregation" table below the main results when `LaunchCount > 1`.

Use multiple launches when single-run noise is a concern and you want to understand how stable the measurement is at the launch level.

## Suite mode: `WithLaunchCount`

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort(data))
    .Add("array", () => Array.Sort(data))
    .WithBaseline("bubble")
    .WithLaunchCount(5)             // 5 independent launches per benchmark
    .WithIterations(100)
    .WithWarmup(10)
    .RunAsync();
```

## Harness mode: `--launch-count` CLI flag

```bash
dotnet run -- --launch-count 5
```

Or in code via `WithLaunchCount`:

```csharp
BenchmarkHarness.Create(args)
    .WithLaunchCount(5)
    .RunAsync();
```

The launch count is **not** a field on `MeasurementOptions`, so `WithOptions` cannot carry one. A launch is a process: the count is spent by the host process launching workers, and a worker - which measures exactly once - is never sent it. See [why](../reference/configuration.md#launchcount).

## Per-method attribute override

Each `[Benchmark]` can specify its own launch count via the `LaunchCount` property (Harness mode):

```csharp
// 10 independent launches for this method only
[Benchmark(LaunchCount = 10)]
public int NoisyMethod() => Compute();

// The harness default (5 launches, aggregated)
[Benchmark]
public int StableMethod() => Compute();
```

The per-method override is overridden by `--launch-count` if both are present. This matters when you want a single method to get extra launches without affecting the rest:

```csharp
public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    // This method runs 20 launches on its own;
    // Baseline and Fast keep the harness default (5).
    [Benchmark(LaunchCount = 20)]
    public int NoisyWork() => ExpensiveJob();

    [Benchmark]
    public int Fast() => QuickJob();
}
```

> [!NOTE] An isolated group takes the maximum
> NBenchmark spawns one set of workers per isolated group, using the highest launch count any
> member asked for - so raising it on one method above raises it for every benchmark measured
> alongside it. Give a method its own `[IsolatedProcess]` if you want the extra launches to stay
> confined to it.

## Reading the reproducibility warning

Two fields on `LaunchStatistics` answer "should I trust the interval on this row?", and they are worth reading together:

| Field | What it is |
| --- | --- |
| `LaunchStandardDeviation` | How much the median moves between launches - the reproducibility of the number. |
| `WithinLaunchStandardError` | How precisely a single launch measured its own median - the precision it claimed. |
| `ProcessVarianceRatio` | The first divided by the second. |
| `BetweenLaunchDispersion` | The between-launch spread as a fraction of the measurement. |

When the ratio passes 4, NBenchmark adds a warning:

```text
⚠ Run-to-run variation across 5 launches is 61x the precision any single launch reported, so
  this benchmark measures far more precisely than it reproduces. Run-to-run spread is 1.4% of
  the measurement. The Error on this row is the between-launch interval and already accounts
  for that, but the significance verdict does not - it pools samples across launches, so it
  inherits the power of the pooled count rather than the reproducibility of the measurement.
```

**Expect large ratios on nanosecond-scale bodies.** A ratio of 30-60 there is ordinary, not a defect: a cheap body collects thousands of samples, which drives its standard error toward zero while leaving real machine variance - code and heap layout, scheduler placement, clock granularity - completely untouched. The warning is a statement about *which interval to trust*, not a sign the benchmark is broken.

What it is telling you is narrow and specific. The `Error` column already carries this variance, because a multi-launch row reports the between-launch half-width. The **significance verdict** does not: it pools raw samples across every launch, so its power grows with the pooled sample count regardless of whether the difference reproduces. At a high ratio, treat `Sig` as provisional and compare the per-launch medians in the Launch Aggregation table.

Raising `--launch-count` sharpens the estimate of the spread; it does not narrow the spread itself. Nothing configurable inside a single process will, either - more samples and longer warmup both leave the between-process component untouched by construction. If the spread is too wide to gate on, reduce it at the source with [environment controls](./environment-control.md) or a quieter host.

> [!NOTE] The ratio divides by the standard error, not the standard deviation
> A within-process interval is `t × s / √n`, so comparing between-process spread against the
> per-sample `s` understates the problem by `√n` - and `n` reaches the thousands on exactly the
> cheap bodies where this matters. Dividing by `s` instead would put the ratio at 0.5-0.7
> against a threshold of 4 and leave the warning silent on a benchmark whose single-launch
> interval is 21× narrower than its true run-to-run spread.

## Dry-run interaction

`--dry-run` (Iterations=0, WarmupIterations=0) takes neither the harness default nor `--launch-count`, so exactly one dry launch is performed. Extra launches would not add information since dry runs skip the body. An explicit `WithLaunchCount(n)` in code is still honored.

## Isolation interaction

In isolated mode (the Harness mode default), NBenchmark spawns N worker processes per isolated group. A worker is not merely unaware of the launch count - it is never sent one, so it cannot repeat the measurement internally and report within-process precision as though it were reproducibility. Per-method attribute overrides are respected: NBenchmark uses the maximum launch count across all benchmarks in the group so that every benchmark receives at least the launches it requested.

In Suite mode the suite repeats in a fresh worker process per launch. The host process orchestrates the repeats, which is what makes the spread between them a run-to-run reproducibility estimate. This matters most on the `[BenchmarkPlan]` path, where the user's factory runs in the host process *and* in every worker: the worker's copy of the suite carries the same `WithLaunchCount(3)`, and the worker's measurement path does not read it.

## Test-integration interaction

A `[Performance]` test defaults to `LaunchCount = 1`, because replicates cost a worker launch each and a test suite should not be made to pay for them everywhere. Setting it on the attribute spends them the same way a suite run does - one worker per replicate - and gives the test's ratio gate a paired confidence interval instead of a bare quotient. A test that names a `ReferenceMethod` measures both sides inside each of those workers, so the replicate count is the number of launches, not twice it. See [test integration](../test-integration/index.md#replicates-and-the-paired-ratio).

## Example

```bash
# Run each benchmark 3 times and show the launch aggregation table
dotnet run -- --launch-count 3

# With a single benchmark getting extra attention via attribute:
dotnet run -- --filter MyBenchmarks.NoisyWork
```

The "Launch Aggregation" table shows cross-launch mean, standard deviation, median, and 95% confidence interval for each benchmark that ran multiple launches. Only benchmarks with `LaunchCount > 1` appear in this table.

## Why the average, not the best launch

Averaging matters more than it sounds, because each launch is a separate process. The differences between launches are a real systematic component - a different CPU draw, page layout, and address-space layout each time - not transient noise that a minimum would filter out. Reporting the fastest launch selected for the luckiest of those draws, so raising `LaunchCount` to get a *better* estimate produced a *more optimistic* number.

Taking the interval from between the launches is what makes it mean what a reader assumes. On this repository's own sample, three launches of the same in-process benchmark read 4.32, 3.63 and 1.66 ns:

| | reported median | reported interval |
| --- | --- | --- |
| fastest launch | 1.66 ns | ±0.02 ns (that launch's own precision) |
| average of launches | 3.20 ns | **±3.42 ns** |

The second row is the honest one. The first says a number that does not reproduce is known to within one percent.

Two further details of how the aggregate is built: counts and durations are totals - `N`, `MeasuredIterations` and `TotalDuration` cover every launch - while `Min` and `Max` span everything observed across all of them. `RawSamples` and the trimmed-sample marks come from the single launch nearest the averaged median, because the marks are positions *into* that sample array and marks from one launch against another's samples would point at the wrong ones; the pooled samples from every launch are what significance testing reads.

## Why five?

Five is where the between-launch interval stops being too wide for a real regression to clear. The interval is a Student-t half-width on `k - 1` degrees of freedom, and the critical value drops steeply over the first few replicates - 12.71 at `k = 2`, 4.30 at 3, 3.18 at 4, 2.78 at 5 - and then only slowly. Below five the interval is too wide for a regression to clear; five is where the curve flattens.

## See also

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Harness mode](../usage-modes/harness-mode.md) - attribute-based discovery and CLI
- [Isolated runs](./isolated-runs.md) - how launches interact with process isolation
- [CLI reference](../reference/cli.md) - all `BenchmarkHarness` flags
