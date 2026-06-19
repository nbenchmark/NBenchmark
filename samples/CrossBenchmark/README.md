# CrossBenchmark sample

This sample runs the same workloads through NBenchmark and BenchmarkDotNet, then prints a side-by-side median comparison.

The goal is not to prove one framework is always faster. The goal is to show how measurement design choices affect reported numbers.

## What this sample configures

Current sample settings in Program.cs:

| Area | NBenchmark | BenchmarkDotNet |
| --- | --- | --- |
| Run mode | Suite in one process | Per-benchmark process (normal BDN behavior) |
| Timing strategy | Realistic profile | Monitoring by default, Throughput with `--bdn-throughput` |
| Warmup | 25 | 25 |
| Measured iterations | 200 | 200 |
| Ops per sample | Fixed at 1 (`WithOpsPerSample(1)`) | Depends on strategy (Monitoring tends to 1; Throughput may batch) |
| Outliers | IQR fence | BDN built-in statistics pipeline |
| Comparison metric | Median | Median |

## Run the sample

From this directory:

```bash
dotnet run -c Release
```

Run with BDN Throughput mode:

```bash
dotnet run -c Release -- --bdn-throughput
```

From repository root:

```bash
dotnet run -c Release --project samples/CrossBenchmark/CrossBenchmark.csproj
```

Throughput from repository root:

```bash
dotnet run -c Release --project samples/CrossBenchmark/CrossBenchmark.csproj -- --bdn-throughput
```

## How to read the comparison table

The sample prints:

- `NBenchmark`: median time from NBenchmark
- `BDN`: median time from BenchmarkDotNet
- `Ratio`: `BDN / NBenchmark`

Interpretation:

- `1.00x` means medians are equal
- `> 1.00x` means BDN reported slower
- `< 1.00x` means BDN reported faster

A single workload around `0.9x` to `1.2x` can be normal. Look for broad, repeated patterns across runs before drawing conclusions.

## Why the numbers are not identical

Even with similar warmup and iteration counts, the frameworks do not run the same harness internally.

Key differences:

- Process model: BDN runs each benchmark in an isolated process. NBenchmark suite runs all workloads in one process in this sample.
- Work scheduling: Throughput mode in BDN can batch many operations per sample, which amortizes fixed overhead and may improve cache/JIT steady state.
- GC and allocation interactions: both frameworks expose allocation and GC differently in practice, especially on short or allocation-heavy workloads.
- Outlier/statistics policy: both compute medians, but trimming and distribution handling are not identical.

Because of this, medians should be close, not necessarily equal.

## Interpreting results like this sample's recent output

Example shape that indicates healthy agreement:

- Most workloads cluster near parity (roughly `1.05x` to `1.16x`)
- One workload may flip slightly (`~0.93x`), especially long CPU-heavy code

That pattern usually indicates framework-model differences plus normal variance, not a broken timer.

## Monitoring vs Throughput

Monitoring (default):

- Faster total run time
- Better for quick cross-framework sanity checks
- Often less aggressive operation batching

Throughput (`--bdn-throughput`):

- Slower total run time
- Better for stable throughput-style benchmarking
- Can shift short-workload medians due to operation batching and overhead amortization

If you compare results over time, keep this mode fixed.

## Troubleshooting and best practices

- Always run `Release`.
- Repeat runs and compare medians of medians, not one run.
- Watch BDN warnings such as minimum iteration time being very small.
- Keep machine load stable (no heavy background tasks).
- If you need strict reproducibility, keep strategy, warmup, iterations, and environment constant.

## Takeaway

This sample is a comparison aid, not a conformance test. Use it to understand trend agreement and harness effects, not to assert exact time equality between frameworks.
