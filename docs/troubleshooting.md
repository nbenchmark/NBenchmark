---
title: Troubleshooting
description: Symptom, cause, and fix for common measurement problems.
order: 8
---

# Troubleshooting

This page maps symptoms you may see in benchmark output to their likely causes and the specific NBenchmark configuration that addresses them.

## Measurement variability

| Symptom | Likely cause | Configuration fix |
|---|---|---|
| Large Error (wide CI) | Genuinely variable timings (auto-sampling already hit its sample ceiling or time cap) | Demand a tighter target: `.WithAutoTune(AutoTunePreset.Thorough)` or `--ci-target 0.01`. Raise `--max-samples` / `--max-tuning-time` if the loop is stopping on a cap - see [Configuration: AutoTune](./reference/configuration.md#autotune) |
| Large Error (wide CI) | OS scheduling / context-switch noise | Switch outlier mode to `.WithOutlierMode(OutlierMode.IqrFence)` - see [Configuration](./reference/configuration.md#outliermode) |
| Large Error (wide CI) | Thermal throttling on laptops | Pin a longer warmup with `.WithWarmup(50)` to let the CPU stabilise. Run plugged in. - see [Configuration](./reference/configuration.md#warmupiterations) |
| Sample count varies between runs | Auto-sampling working as designed - each run collects exactly enough samples to hit the CI target | Expected. Pin `.WithIterations(n)` / `--iterations n` for a fixed, reproducible sample count (e.g. in CI) - see [Configuration: Iterations](./reference/configuration.md#iterations) |
| High StdDev | GC pressure or allocation noise | Enable allocation tracking with `.WithAllocations()` to diagnose - see [Configuration](./reference/configuration.md#measureallocations). Under the default `Realistic` profile, natural GC pauses are included in the timing; switch to the `Independent` profile (`--profile independent`) to force per-iteration GC and isolate iterations from GC noise - see [Measurement Profiles](./statistics/measurement.md#measurement-profiles) |

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
| `MarginOfError` is `±0 ns` | Only one sample (`n < 2`, from a pinned `Iterations = 1`) or all measurements identical (timer resolution coarser than the benchmark duration) | Unpin `Iterations` to use auto mode (collects at least `AutoTune.MinSamples`), or pin a larger count. For a fast body, auto ops-per-sample calibration amortises a coarse timer - note it is skipped when setup/teardown is set |
| `Sig` column is blank | Too few samples for the [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (requires ≥2 per group), **or** the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) omnibus was not significant (three-plus benchmarks compared, no post-hoc ran) | Increase iterations or combine more runs - see [FAQ: significance](./faq.md#why-is-significance-sometimes-blank) |

## Discovery and setup errors

| Symptom | Likely cause | Configuration fix |
|---|---|---|
| `[Benchmark]` method not discovered | Method is static, class is abstract, or assembly not registered | Use `--list` to verify what the host finds - see [Host mode: listing](./guides/host-mode.md#listing-benchmarks-without-running). Check the class is public, not abstract, and the method is an instance method |
| "Could not instantiate MyClass" | No public parameterless constructor | Add one, use `[BenchmarkSetup]`, or add `NBenchmark.DependencyInjection` - see [FAQ: instantiation](./faq.md#the-host-throws-could-not-instantiate-myclass-how-do-i-fix-it) |
| Benchmarks run in different order each time | Random order is the default (prevents systematic bias) | Use `--order declaration` or `.WithRunOrder(RunOrder.Declaration)` for source order - see [Configuration](./reference/configuration.md#forcegcbetweenbenchmarks) |

## Still stuck?

- [Configuration](./reference/configuration.md) - full options reference
- [CLI Reference](./reference/cli.md) - all command-line flags
- [Key Concepts](./getting-started/key-concepts.md) - how warmup, outliers, and CIs work
- [FAQ](./faq.md) - frequently asked questions
