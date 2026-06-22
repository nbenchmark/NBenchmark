---
title: "Multiple launches"
description: Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.
order: 6
---

# Multiple launches

By default each benchmark runs once. Use multiple launches to run each benchmark `N` times as independent launches. Each launch includes its own warmup and GC cycle, so variance across launches reflects real run-to-run differences (process state, ASLR, scheduler placement), not just intra-run noise.

The primary result fields (median, mean, percentiles, etc.) come from the **best** (lowest median) launch. Cross-launch statistics (mean, stddev, median, CI across per-launch medians) are computed and displayed in a "Launch Aggregation" table below the main results when `LaunchCount > 1`.

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

## Host mode: `--launch-count` CLI flag

```bash
dotnet run -- --launch-count 5
```

Or in code via `WithOptions`:

```csharp
BenchmarkHost.Create(args)
    .WithOptions(new MeasurementOptions { LaunchCount = 5 })
    .RunAsync();
```

## Per-method attribute override

Each `[Benchmark]` can specify its own launch count via the `LaunchCount` property (Host mode):

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

When `--dry-run` (Iterations=0, WarmupIterations=0) is combined with `LaunchCount > 1`, exactly one dry launch is performed. Extra launches would not add information since dry runs skip the body.

## Isolation interaction

In isolated mode (the Host mode default), the parent spawns N child processes per isolated group. The child process is unaware of the launch count; the parent orchestrates the repeats. Per-method attribute overrides are respected: the parent uses the maximum launch count across all benchmarks in the group so that every benchmark receives at least the launches it requested.

When combined with `WithIsolation()` in Suite mode, the suite repeats in a fresh child process per launch. The child process is unaware of the launch count; the parent orchestrates the repeats.

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
- [Host mode](../usage-modes/host-mode.md) - attribute-based discovery and CLI
- [Isolated runs](./isolated-runs.md) - how launches interact with process isolation
- [CLI reference](../reference/cli.md) - all `BenchmarkHost` flags