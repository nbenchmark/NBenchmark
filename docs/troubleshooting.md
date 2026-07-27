---
title: Troubleshooting
description: Symptom, cause, and fix for common measurement problems.
order: 10
---

# Troubleshooting

This page maps symptoms you may see in benchmark output to their likely causes and the specific NBenchmark configuration that addresses them.

## Measurement variability

| Symptom | Likely cause | Configuration fix |
|---|---|---|
| **Numbers are uniformly slow and not production-representative** | The entry assembly was built in `Debug` configuration (common with `dotnet run` without `-c Release`), or a debugger is attached. Both defeat JIT inlining and tier-1 optimization | Rebuild with `dotnet run -c Release` (or set the configuration to Release in your IDE) and detach the debugger. If measuring Debug is intentional, suppress the warning with `NBENCHMARK_SUPPRESS_DEBUG_WARNING=1` or `new MeasurementOptions { Environment = new EnvironmentOptions { SuppressBuildConfigurationWarning = true } }` |
| Large Error (wide CI) | Genuinely variable timings (auto-sampling already hit its sample ceiling or time cap) | Demand a tighter target: `.WithAutoTune(AutoTunePreset.Thorough)` or `--ci-target 0.01`. Raise `--max-samples` / `--max-tuning-time` if the loop is stopping on a cap - see [Configuration: AutoTune](./reference/configuration.md#autotune) |
| Large Error (wide CI) | OS scheduling / context-switch noise | Switch outlier mode to `.WithOutlierMode(OutlierMode.IqrFence)` - see [Configuration](./reference/configuration.md#outliermode) |
| Large Error (wide CI) | Thermal throttling on laptops | Pin a longer warmup with `.WithWarmup(50)` to let the CPU stabilise. Run plugged in. - see [Configuration](./reference/configuration.md#warmupiterations) |
| **Same benchmark, a different median each run — and every run reports a tight Error** | Warmup ended before the JIT finished tiering the body up, so the run measured pre-tier-1 (unoptimized) code. This is *not* noise: each run is internally consistent, which is why the error margin looks trustworthy. A body can read several times slow this way | Check `autoTune.warmupTimeFloorMet` in the JSON (or `warmup cut short` on the console summary), `autoTune.jitQuiescenceAchieved`, and `autoTune.splitHalfDrift`. `autoTune.warmupCurve` shows whether the body was still speeding up when warmup ended, and `autoTune.jitLastChangeAtNs` against `warmupElapsedNs` shows how much quiet time followed the last compilation. Raise `--min-warmup-time` (default 500 ms), and `--max-warmup` if the ceiling is what cut warmup short. For a nanosecond body with `--ops-per-sample 1`, raise the ops-per-sample so each sample spans more work - see [Measurement: Warmup](./statistics/measurement.md#phase-2---warmup-plateau-detection) and [the warmup curve](./statistics/measurement.md#the-warmup-curve) |
| **A tight Error next to a `maxCeiling` stop, or next to a `Max` hundreds of times the median** | The reported Error is computed on the **trimmed** set while the loop's stop rule ran on the **raw** stream, so when the variance lives in the outliers the reported margin tightens around what remains. A benchmark can show `MarginOfError` at ±1.3% of its mean while `autoTune.achievedRelativeCiWidth` is `1.05` (±105%) | Read `autoTune.sampleStop` before the Error column: a tight margin is evidence the measurement *converged* only when it reads `ciTargetMet`. Compare `autoTune.achievedRelativeCiWidth` against `marginOfError / mean`, and check `outliersRemoved` against the pre-trim sample count. Then either accept the variance as the finding (`--launch-count 5` is the honest signal) or raise `--max-samples` / loosen `--ci-target` - see [Raw vs. trimmed statistics](./statistics/measurement.md#raw-vs-trimmed-statistics) |
| Result reports a `driftUnresolved` stop | The measured timings kept moving while they were being collected, so the interval describes a moving target. Usually a JIT tier-up or dynamic-PGO re-optimization landing inside measurement; otherwise a thermal ramp, a filling cache, or a growing data structure | Raise `--min-warmup-time` so the transition lands during warmup instead. If the body is genuinely non-stationary, that is the finding - use `--launch-count 5` to measure across-launch spread, which is the honest signal - see [Measurement](./statistics/measurement.md#phase-3---measurement-ci-width-target) |
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
| `[Benchmark]` method not discovered | Method is static, class is abstract, or assembly not registered | Use `--list` to verify what the host finds - see [Harness mode: listing](./usage-modes/harness-mode.md#listing-benchmarks-without-running). Check the class is public, not abstract, and the method is an instance method |
| "Could not instantiate MyClass" | No public parameterless constructor | Add one, use `[BenchmarkSetup]`, or add `NBenchmark.DependencyInjection` - see [FAQ: instantiation](./faq.md#the-host-throws-could-not-instantiate-myclass-how-do-i-fix-it) |
| Benchmarks run in different order each time | Random order is the default (prevents systematic bias) | Use `--order declaration` or `.WithRunOrder(RunOrder.Declaration)` for source order - see [Configuration](./reference/configuration.md#forcegcbetweenbenchmarks) |

## Still stuck?

- [Configuration](./reference/configuration.md) - full options reference
- [CLI Reference](./reference/cli.md) - all command-line flags
- [Key Concepts](./getting-started/key-concepts.md) - how warmup, outliers, and CIs work
- [FAQ](./faq.md) - frequently asked questions
