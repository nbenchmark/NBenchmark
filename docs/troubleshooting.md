---
title: Troubleshooting
description: Symptom, cause, and fix for common measurement problems.
order: 9
---

# Troubleshooting

This page maps symptoms you may see in benchmark output to their likely causes and the specific NBenchmark configuration that addresses them.

## Measurement variability

| Symptom | Likely cause | Configuration fix |
|---|---|---|
| Large Error (wide CI) | Too few iterations | Increase with `.WithIterations(500)` or `--iterations 500` - see [Configuration](./configuration.md#iterations) |
| Large Error (wide CI) | OS scheduling / context-switch noise | Switch outlier mode to `.WithOutlierMode(OutlierMode.IqrFence)` - see [Configuration](./configuration.md#outliermode) |
| Large Error (wide CI) | Thermal throttling on laptops | Increase warmup with `.WithWarmup(50)` to let the CPU stabilise, or reduce iterations to shorten the run. Run plugged in. - see [Configuration](./configuration.md#warmupiterations) |
| High StdDev | GC pressure or allocation noise | Enable allocation tracking with `.WithAllocations()` to diagnose - see [Configuration](./configuration.md#measureallocations). If your benchmark intentionally exercises GC, you can disable `ForceGcBeforeEachIteration` - see [Configuration](./configuration.md#forcegcbeforeeachiteration) |

### Quick reference: Outlier modes

| Mode | When to use |
|---|---|
| `IqrFence` (default) | General-purpose. The [IQR](https://en.wikipedia.org/wiki/Interquartile_range)-based fence adapts to your data's spread, trimming spikes from OS scheduling interrupts without discarding clean samples. |
| `RemoveTop5Percent` | When you want a fixed quota - always removes the slowest 5% of iterations. |
| `RemoveTopAndBottom5Percent` | When very fast outliers (e.g. cache hits after warmup) also skew results. |
| `None` | When every sample matters (latency-tail analysis). |

## Zero or unexpected results

| Symptom | Likely cause | Configuration fix |
|---|---|---|
| Result shows `0 ns` | Dead code elimination - the compiler removed your benchmark body because it has no observable side effects | Use the `Func<T>` overload that returns a value, or add a side effect. See [FAQ: `0 ns`](./faq.md#my-benchmark-produces-0-ns-whats-happening) |
| All results zeroed | Dry-run mode active (`--dry-run`, `Iterations=0`, `WarmupIterations=0`) | Remove `--dry-run` flag or set `Iterations` > 0 |
| `MarginOfError` is `±0 ns` | Only one sample (`n < 2`) or all measurements identical (timer resolution coarser than the benchmark duration) | Increase iterations. If the timer is too coarse, run on a machine with a higher-resolution timer |
| `Sig` column is blank | Too few samples for the [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (requires ≥2 per group) | Increase iterations or combine more runs - see [FAQ: significance](./faq.md#why-is-significance-sometimes-blank) |

## Discovery and setup errors

| Symptom | Likely cause | Configuration fix |
|---|---|---|
| `[Benchmark]` method not discovered | Method is static, class is abstract, or assembly not registered | Use `--list` to verify what the host finds - see [Host mode: listing](./guides/host-mode.md#listing-benchmarks-without-running). Check the class is public, not abstract, and the method is an instance method |
| "Could not instantiate MyClass" | No public parameterless constructor | Add one, use `[BenchmarkSetup]`, or add `NBenchmark.DependencyInjection` - see [FAQ: instantiation](./faq.md#the-host-throws-could-not-instantiate-myclass-how-do-i-fix-it) |
| Benchmarks run in different order each time | Random order is the default (prevents systematic bias) | Use `--order declaration` or `.WithRunOrder(RunOrder.Declaration)` for source order - see [Configuration](./configuration.md#forcegcbetweenbenchmarks) |

## Still stuck?

- [Configuration](./configuration.md) - full options reference
- [CLI Reference](./cli-reference.md) - all command-line flags
- [Key Concepts](./getting-started/key-concepts.md) - how warmup, outliers, and CIs work
- [FAQ](./faq.md) - frequently asked questions
