---
title: "Multiple launches"
description: Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.
order: 6
---

# Multiple launches

By default each benchmark runs once. Use multiple launches to run each benchmark `N` times as independent launches. Each launch includes its own warmup and GC cycle, so variance across launches reflects real run-to-run differences (process state, ASLR, scheduler placement), not just intra-run noise.

The primary result fields (median, mean, percentiles, etc.) are the **average across launches**, and the reported confidence interval comes from the spread **between** launches rather than from within any one of them. Cross-launch statistics are also computed in full and displayed in a "Launch Aggregation" table below the main results when `LaunchCount > 1`.

Averaging matters more than it sounds, because each launch is a separate process. The differences between launches are a real systematic component — a different CPU draw, page layout, and address-space layout each time — not transient noise that a minimum would filter out. Reporting the fastest launch selected for the luckiest of those draws, so raising `LaunchCount` to get a *better* estimate produced a *more optimistic* number.

Taking the interval from between the launches is what makes it mean what a reader assumes. On this repository's own sample, three launches of the same in-process benchmark read 4.32, 3.63 and 1.66 ns:

| | reported median | reported interval |
| --- | --- | --- |
| fastest launch | 1.66 ns | ±0.02 ns (that launch's own precision) |
| average of launches | 3.20 ns | **±3.42 ns** |

The second row is the honest one. The first says a number that does not reproduce is known to within one percent.

Counts and durations are totals — `N`, `MeasuredIterations` and `TotalDuration` cover every launch — while `Min` and `Max` span everything observed across all of them.

`RawSamples` and the trimmed-sample marks come from the single launch nearest the averaged median, because the marks are positions *into* that sample array and marks from one launch against another's samples would point at the wrong ones. The pooled samples from every launch are what significance testing reads.

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

The launch count is **not** a field on `MeasurementOptions`, so `WithOptions` cannot carry one. A launch is a process: the count is spent by the coordinator launching workers, and a worker - which measures exactly once - is never sent it. See [why](../reference/configuration.md#launchcount).

## Per-method attribute override

Each `[Benchmark]` can specify its own launch count via the `LaunchCount` property (Harness mode):

```csharp
// 3 independent launches for this method only
[Benchmark(LaunchCount = 3)]
public int NoisyMethod() => Compute();

// Default launch count (1) - no aggregation
[Benchmark]
public int StableMethod() => Compute();
```

The per-method override is overridden by `--launch-count` if both are present. This matters when you want a single method to get extra launches without affecting the rest:

```csharp
public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    // This method runs 5 launches on its own;
    // Baseline and Fast keep the default (1).
    [Benchmark(LaunchCount = 5)]
    public int NoisyWork() => ExpensiveJob();

    [Benchmark]
    public int Fast() => QuickJob();
}
```

## Dry-run interaction

`--dry-run` (Iterations=0, WarmupIterations=0) takes neither the harness default nor `--launch-count`, so exactly one dry launch is performed. Extra launches would not add information since dry runs skip the body. An explicit `WithLaunchCount(n)` in code is still honoured.

## Isolation interaction

In isolated mode (the Harness mode default), the coordinator spawns N worker processes per isolated group. A worker is not merely unaware of the launch count - it is never sent one, so it cannot repeat the measurement internally and report within-process precision as though it were reproducibility. Per-method attribute overrides are respected: the coordinator uses the maximum launch count across all benchmarks in the group so that every benchmark receives at least the launches it requested.

In Suite mode the suite repeats in a fresh worker process per launch. The coordinator orchestrates the repeats, which is what makes the spread between them a run-to-run reproducibility estimate. This matters most on the `[BenchmarkPlan]` path, where the user's factory runs in the coordinator *and* in every worker: the worker's copy of the suite carries the same `WithLaunchCount(3)`, and the worker's measurement path does not read it.

## Test-integration interaction

A `[Performance]` test defaults to `LaunchCount = 1`, because replicates cost a worker launch each and a test suite should not be made to pay for them everywhere. Setting it on the attribute spends them the same way the coordinator does - one worker per replicate - and gives the test's ratio gate a paired confidence interval instead of a bare quotient. A test that names a `ReferenceMethod` measures both sides inside each of those workers, so the replicate count is the number of launches, not twice it. See [test integration](../test-integration/index.md#replicates-and-the-paired-ratio).

## Example

```bash
# Run each benchmark 3 times and show the launch aggregation table
dotnet run -- --launch-count 3

# With a single benchmark getting extra attention via attribute:
dotnet run -- --filter MyBenchmarks.NoisyWork
```

The "Launch Aggregation" table shows cross-launch mean, standard deviation, median, and 95% confidence interval for each benchmark that ran multiple launches. Only benchmarks with `LaunchCount > 1` appear in this table.

## See also

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Harness mode](../usage-modes/harness-mode.md) - attribute-based discovery and CLI
- [Isolated runs](./isolated-runs.md) - how launches interact with process isolation
- [CLI reference](../reference/cli.md) - all `BenchmarkHarness` flags
