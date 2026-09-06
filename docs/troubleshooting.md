---
title: Troubleshooting
description: Symptom, cause, and fix for common measurement problems.
order: 11
---

# Troubleshooting

This guide provides symptoms, causes, and fixes for common measurement problems. Each entry leads with a quick fix followed by a detailed explanation.

## Measurement variability

### Numbers are uniformly slow

> [!CAUTION] Quick fix
> Rebuild your project with `dotnet run -c Release` (or set the configuration to Release in your IDE) and detach the debugger.

If the entry assembly is built in `Debug` configuration or a debugger is attached, JIT inlining and tier-1 optimization are disabled. If you intentionally want to measure Debug behavior, suppress the warning by setting `NBENCHMARK_SUPPRESS_DEBUG_WARNING=1` or using `new MeasurementOptions { SuppressedWarnings = BenchmarkWarnings.BuildConfiguration }`.

### Large error (wide confidence interval)

> [!CAUTION] Pick one based on the cause
> - **Demand a tighter target:** Use `.WithAutoTune(AutoTuneOptions.Thorough)` or `--ci-target 0.01`.
> - **Raise the sample ceiling:** Use `--max-samples <n>` and `--max-tuning-time <s>` if the loop stops at a cap.
> - **Reduce OS scheduling noise:** Use `.WithOutlierMode(OutlierMode.IqrFence)` (the default).
> - **Stabilize a hot laptop:** Use `--warmup-samples 50` to let the CPU stabilize and ensure the laptop is plugged in.

A wide confidence interval indicates genuinely variable timings. In auto-sampling mode, NBenchmark collects samples until the Error meets the precision target. Therefore, a wide interval usually points to real run-to-run variability rather than an insufficient sample size.

The correct remedy depends on the cause:
- Use a tight target for slow but stable bodies.
- Use a higher sample ceiling for fast but noisy bodies.
- Change the outlier mode for OS noise.
- Use a longer warmup for thermal ramps.

For more information, see [Configuration: AutoTune](./reference/configuration.md#autotune) for the `Thorough` preset and [Configuration: OutlierMode](./reference/configuration.md#outliermode) for outlier modes.

### Median differs between runs with tight error

> [!CAUTION] Quick fix
> Run with `--launch-count 5` or higher (the Harness default). Read the `Error` column, which then describes reproducibility rather than the precision of a single process. Do not increase the sample count or warmup time first, as these do not address the usual cause.

Three different issues can produce this symptom. Diagnose them in the following order:

**1. Warmup ended before the JIT finished tiering up.**
The run measured unoptimized code, which can be several times slower. Each run is internally consistent, so the margin appears trustworthy.

Check `autoTune.warmupTimeFloorMet` in the JSON (or `warmup cut short` on the console summary), as well as `autoTune.jitQuiescenceAchieved` and `autoTune.splitHalfDrift`. The `autoTune.warmupCurve` shows whether the body was still speeding up when warmup ended. `autoTune.jitLastChangeAtNs` compared to `warmupElapsedNs` shows how much quiet time followed the last compilation. To fix this, raise `--min-warmup-time <ms>` (default 500) and `--max-warmup-samples <n>` if the ceiling cut warmup short.

**2. The measurement is finer than the clock can resolve.**
Compare `autoTune.sampleQuantizationFraction` against the reported margin. If one timer step is a larger fraction of the sample than the margin is, the results describe the clock's step grid rather than your code. Within a run, every sample lands on the same step; between runs, a tiny shift moves them all to the next one. NBenchmark warns you when it detects this. The default `MinQuantaPerSample` floor prevents this, so it generally only appears if you use a small pinned `--ops-per-sample` on a fast body. Raise the value so each sample spans hundreds of steps. For more information, see [Timer resolution](./statistics/measurement.md#timer-resolution).

**3. The number genuinely does not reproduce that precisely.**
This is the common case for a healthy run, and there is nothing to fix in the measurement. A single process cannot detect this because within-run sampling estimates how precisely *that process* measured, and the margin narrows as `1/√n`. The between-process component (code and heap layout, scheduler placement, CPU frequency, and thermal state) is fixed inside a process. Increasing the sample count makes the margin narrower without making it more honest, and longer warmup does not change it.

Only replication across processes can measure this. Run `--launch-count 5` (or more) and read `launchStatistics`. The `betweenLaunchDispersion` is the run-to-run spread, and `processVarianceRatio` is how much the single-process margin understated it. On a nanosecond-scale body, a ratio of 30-60 is ordinary. For more information, see [Reading the reproducibility warning](./features/multiple-launches.md#reading-the-reproducibility-warning).

To reduce this spread, use [environment controls](./features/environment-control.md) for CPU affinity and process priority, or use a quieter, less thermally constrained host. CPU affinity is unavailable on macOS. An Apple Silicon laptop with performance and efficiency cores will show more run-to-run spread than a pinned Linux box. NBenchmark requests a performance core when macOS permits it and [reports when it cannot](./features/environment-control.md#macos-and-apple-silicon).

### Tight error with max ceiling stop or high max values

> [!CAUTION] Pick one
> - **Accept the variance as the finding:** Use `--launch-count 5` to get an honest signal of run-to-run spread across launches.
> - **Chase precision:** Raise `--max-samples` and loosen `--ci-target` (for example, `--ci-target 0.05`).

The reported Error describes the **trimmed** mean, while the loop's stop rule runs on the **raw** stream. The Error accounts for how many samples the fence removed (it is a Winsorized interval), but not for how far out they were. A benchmark can show `MarginOfErrorNs` at ±1.3% of its mean while `autoTune.achievedRelativeCiWidth` is `1.05` (±105%). Both numbers are correct; they describe different sample sets.

Read `autoTune.sampleStop` before the Error column. A tight margin is evidence the measurement converged only when it reads `ciTargetMet`. Compare `autoTune.achievedRelativeCiWidth` against `marginOfError / mean`, and check `outliersRemoved` against the pre-trim sample count.

For more information, see [Raw vs. trimmed statistics](./statistics/measurement.md#raw-vs-trimmed-statistics).

### Result reports a drift unresolved stop

> [!CAUTION] Pick one
> - **Land the transition during warmup instead:** Raise `--min-warmup-time <ms>` (the default is 500 ms).
> - **Accept non-stationarity as the finding:** Use `--launch-count 5` to measure the across-launch spread.

The measured timings continued to move while they were being collected, so the interval describes a moving target. This is usually caused by a JIT tier-up or dynamic-PGO re-optimization during measurement. Other causes include thermal ramps, filling caches, or growing data structures.

For more information, see [Measurement: Steady-state (drift) gate](./statistics/measurement.md#phase-c---measurement-ci-width-target) for the drift detection mechanism and the restart limit.

### Host drift exceeds reported difference

> [!CAUTION] Pick one
> - **Measure the two co-resident, several times over:** Use `--launch-count 5`.
> - **Control the machine:** Use `--cpu-affinity 2,3 --priority high` on a host you control.
> - **Re-run just the pair:** Use `--filter 'Baseline|Candidate' --order declaration` on a quiet machine.

Between the moment the baseline was measured and the moment the candidate was measured, the host's effective speed changed by more than the reported difference. The machine drifted due to a laptop warming up, a build starting in another terminal, or a co-tenant arriving on the runner.

Each measurement is internally consistent, which is why the per-benchmark drift gate did not fire. The drift occurs *between* benchmarks. The control workload NBenchmark runs at every benchmark boundary detects this.

The most direct fix is to raise `--launch-count`. Each launch measures the pair close together, so the drift is averaged over the replicates rather than baked into one ordering.

For more information, see [The host drift canary](./statistics/measurement.md#the-host-drift-canary) for the mechanism, and [`--no-drift-canary`](./reference/cli.md#diagnostics) to disable the check.

### Sample count varies between runs

> [!CAUTION] Quick fix
> This is expected behavior. Auto-sampling works as designed. To get a fixed, reproducible sample count (for example, in CI), use `.WithSamples(n)` or `--samples n`.

Each run collects enough samples to hit the CI target. Use a fixed count only when you need reproducibility across runs for CI dashboards or regression baselines.

For more information, see [Configuration: Samples](./reference/configuration.md#samples).

### High standard deviation

> [!CAUTION] Pick one
> - **Diagnose allocation pressure:** Enable allocation tracking with `.WithAllocations()` and read the `Alloc/op` column.
> - **Isolate samples from GC noise:** Use the `PerSampleCollect` GC behavior (`--gc per-sample-collect`) to force per-sample GC.

A high standard deviation indicates inconsistent timings. Under the default `Natural` GC behavior, natural GC pauses are included in the timing, and allocation pressure from the body produces a noisy tail. The `PerSampleCollect` GC behavior forces a Gen0 GC before every sample, making GC pauses deterministic and removing them from the variance (at the cost of ecological validity).

For more information, see [GC behavior](./statistics/measurement.md#gc-behavior) and [Configuration: MeasureAllocations](./reference/configuration.md#measureallocations).

### Bimodal distribution warning

> [!CAUTION] Quick fix
> 1. **Check the body** for lock contention or cache misses. The cluster center in the warning names the extra cost of the slow path.
> 2. **If you suspect GC:** Use `dotnet run -- --gc per-sample-collect` to force per-sample Gen0 collection.

The outlier detector found a tight secondary cluster of slow timings rather than scattered noise, indicating a real, repeatable slow path in the code. The reported median describes the fast path, and the cluster center describes a latency a real user will encounter. The warning is non-fatal; the benchmark still completes and reports statistics on the trimmed (fast-cluster) set.

Do not silence this warning. Read the tail metrics as-is; by default, the histogram and P99/P99.9/max are computed from the full pre-trim distribution. Investigate the cause with a profiler and reduce noise at the source with [environment control](./features/environment-control.md) if OS scheduling contributed to the spread.

For more information, see [Outlier Trimming: Bimodal-distribution warning](./statistics/outliers.md#bimodal-distribution-warning).

### Samples confirmed preempted by the OS

> [!CAUTION] Quick fix
> 1. **Read this as good news.** These samples were removed based on direct evidence (the measuring thread's own CPU occupancy), not inferred from the timing. The reported numbers are more trustworthy because these samples were removed.
> 2. **If the rejected fraction is high** ("this host is too noisy to trust"), move to a less noisy host or re-run once background load clears.

This is [evidence-based interference rejection](./statistics/outliers.md#evidence-based-interference-rejection). This pre-stage runs before the statistical outlier detector and discards a sample only when the OS is known to have preempted it. The confirmed preempted and statistical outlier counts are reported together.

This is on by default and requires no action on a normal run. To use only the statistical detector, pass `--no-interference-filter`.

For more information, see [Outlier Trimming: Evidence-based interference rejection](./statistics/outliers.md#evidence-based-interference-rejection).

### Interference disabled reason is set

> [!CAUTION] Quick fix
> This is informational, not an error. Timings are unaffected.

The interference filter could not run for this benchmark. The reason is listed in `AutoTuneDiagnostic.InterferenceDisabledReason` (visible with `--detail advanced`).

| Reason | Meaning |
| --- | --- |
| Thread-CPU clock unavailable | The host platform has no thread-CPU-time API that the engine supports. |
| Probe cost exceeded its budget | Two clock reads would have cost more than `InterferenceOptions.ProbeCostBudgetFraction` of the sample-duration target. |
| Too few samples with a known occupancy reading | Usually an async body whose continuations mostly resumed on a different thread. |

You do not need to fix this unless you specifically want interference evidence. For the async case, a body whose `await` points resume on the original thread would restore it.

## Zero or unexpected results

### Result shows 0 ns

> [!CAUTION] Quick fix
> Use the `Func<T>` overload that returns a value: `Benchmark.Run(() => ComputeHash(data))`. Alternatively, add a side effect to the body.

The compiler removed your benchmark body because it has no observable side effects (dead code elimination). A returning overload writes the return value to a static field so the JIT cannot elide the call.

For more information, see [FAQ: `0 ns`](./faq.md#my-benchmark-produces-0-ns-whats-happening).

### All results are zero

> [!CAUTION] Quick fix
> Remove the `--dry-run` flag or set `Samples` > 0.

Dry-run mode is active (`--dry-run`, or `Samples=0` and `WarmupSamples=0`). The body is not invoked, and no measurements are taken. Use `--dry-run` to validate discovery and wiring. To run the body exactly once for a smoke test, use `--samples 1 --warmup-samples 0`.

### Margin of error is ±0 ns

> [!CAUTION] Quick fix
> Unpin `Samples` to use auto mode (which collects at least `AutoTune.MinSamples`), or pin a larger count.

This happens if only one sample was collected (`n < 2`) or all measurements were identical (timer resolution is coarser than the benchmark duration). For a fast body, auto ops-per-sample calibration amortizes a coarse timer. Note that calibration is skipped when setup/teardown is set and bypassed when `OpsPerSample` is pinned.

Identical measurements occur when every sample lands on the same clock step. Compare `autoTune.clockResolutionNs` against `autoTune.sampleDurationNs`. If a sample spans only one or two steps, the timer cannot distinguish samples. For more information, see [Timer resolution](./statistics/measurement.md#timer-resolution).

### Significance column is blank

> [!CAUTION] Quick fix
> Increase samples or combine more runs: `--samples <n>` or `--min-samples <n>`.

This happens if there are too few samples for the significance test (which requires at least two per group), or if the Kruskal-Wallis omnibus test was not significant. In the second case, the blank is correct; the omnibus gate refused to run pairwise comparisons because the groups appear the same.

For more information, see [FAQ: significance](./faq.md#why-is-significance-sometimes-blank) and [Significance Testing](./statistics/significance.md).

## Discovery and setup errors

### [Benchmark] method is not discovered

> [!CAUTION] Quick fix
> Run `dotnet run -- --list` to verify what the host finds. Ensure the class is public and not abstract, and the method is an instance method (not static).

The host scans for public instance methods marked `[Benchmark]` on public, non-abstract classes. Static methods, abstract classes, and assemblies not registered with `AddFromAssembly` are not discovered.

For more information, see [Harness mode: listing benchmarks without running](./usage-modes/harness-mode.md#listing-benchmarks-without-running).

### Could not instantiate benchmark class

> [!CAUTION] Pick one
> - **Add a public parameterless constructor** to the benchmark class.
> - **Install `NBenchmark.Analyzers`** for compile-time detection of NB0001 (missing parameterless constructor).
> - **Use dependency injection:** Add the `NBenchmark.DependencyInjection` package and use `UseDependencyInjection<T>(BuildServices)` with a static `IServiceProvider BuildServices()` factory.

The host uses `Activator.CreateInstance`, which requires a public parameterless constructor. Benchmark classes with real dependencies (such as a repository, a logger, an `HttpClient`, or a `DbContext`) require the DI companion package.

For more information, see [Dependency Injection](./features/dependency-injection.md) and [FAQ: instantiation](./faq.md#the-host-throws-could-not-instantiate-myclass-how-do-i-fix-it).

### Benchmark cannot be sent to another process

> [!CAUTION] Pick one
> - **Build the value in the worker** with a prepare delegate: `Benchmark.Run(prepare: () => BuildIt(), body: v => Use(v))`, or `BenchmarkSuite.Over(name, () => BuildIt())` in Suite mode.
> - **Mark your own type `[BenchmarkState]`** if its measured behavior is fully determined by its serialized contents.
> - **Use `AddInProcess(name, body)`** to keep a specific benchmark in the host process while others run in a worker.
> - **Use `WithIsolation(Isolation.Preferred)`** to accept a labeled host-process measurement.

`Isolation` defaults to `Isolation.Required`. A benchmark that requires a worker but cannot be sent to one fails the run.

Ordinary captured data (such as `int`, `string`, `int[]`, `List<T>`, `Dictionary<K,V>` with default comparers, or records of those) is sent and stays isolated. This error occurs for values whose behavior is not carried by their contents:

| Message | Cause | Fix |
| --- | --- | --- |
| *is not one of the types whose measured behavior is fully determined by its contents* | `Stream`, `HttpClient`, `DbConnection`, mock, or similar | Use a prepare delegate to build it in the worker |
| *was built with a custom comparer* | The dictionary's comparer is neither the default nor reproducible | Mark the comparer type `[BenchmarkState]` or use a prepare delegate |
| *carries a private field* / *a get-only property* | On a `[BenchmarkState]` type; the serializer cannot read these back | Make the member a public field or a property with a setter |
| *holds a `X` where a `Y` was declared* | A collection of a base type holding a subclass; overrides are lost upon arrival | Use a prepare delegate or make the element type exact |
| *pushing the captured state past N MiB once encoded* | Exceeds `MaxTransferredStateBytes` (8 MiB default) | Use a prepare delegate or raise `MaxTransferredStateBytes` |
| *something else in this group already refers to the same object* | Two benchmarks share one array through different closures | Build shared state in one prepare delegate |

A suite is measured by one worker, so the message names every offending benchmark.

For more information, see [Isolated Runs](./features/isolated-runs.md#when-isolation-is-refused).

### Could not load file or assembly in ASP.NET Core or WPF

> [!CAUTION] Quick fix
> Rebuild your project to ensure the `runtimeconfig.json` beside the assembly under test is present and current. The worker uses this file to identify required shared frameworks.

Benchmarks in a `Microsoft.NET.Sdk.Web` (or WinForms/WPF) project depend on shared frameworks like `Microsoft.AspNetCore.App` or `Microsoft.WindowsDesktop.App`. These are supplied by the process rather than the output directory. The measurement worker is a plain console application that only supplies `Microsoft.NETCore.App` by default.

NBenchmark extends the framework set automatically by reading the `runtimeconfig.json` beside the assembly under test. `dotnet benchmark` also performs this action.

Two cases may still cause failure:
- **Missing or stale `runtimeconfig.json`:** Rebuild the project.
- **Self-contained target:** The framework is in the output directory, so there is no shared framework to request. Build the benchmark project as framework-dependent, or use `--in-process`.

### Benchmarks run in a different order

> [!CAUTION] Quick fix
> Use `--order declaration` or `.WithRunOrder(RunOrder.Declaration)` for source order.

Random order is the default to prevent systematic bias, such as the first benchmark benefiting from a warm CPU cache. Use declaration order for reproducibility across CI and local environments, or use `--seed <n>` for a reproducible shuffle.

For more information, see [Configuration: ForceGcBetweenBenchmarks](./reference/configuration.md#forcegcbetweenbenchmarks).

## Quick reference: Outlier modes

| Mode | When to use |
| --- | --- |
| `IqrFence` (default) | General-purpose. The [IQR](https://en.wikipedia.org/wiki/Interquartile_range)-based fence adapts to data spread and trims spikes from OS scheduling interrupts. |
| `RemoveTop5Percent` | When you want a fixed quota; always removes the slowest 5% of samples. |
| `RemoveTopAndBottom5Percent` | When very fast outliers (such as cache hits after warmup) also skew results. |
| `None` | When every sample matters (latency-tail analysis). |

### Measurement thread quality of service could not be raised

> [!CAUTION] Quick fix
> This is a note, not a failure. The run is valid. To silence it, use `--no-thread-control`.

On Apple Silicon, the quality-of-service class requests a performance core instead of an efficiency core. macOS refuses this change on any thread with an explicit scheduling priority. The .NET runtime gives a priority to every thread it creates. Consequently, the elevation works on the process main thread but is refused for measurements on the thread pool, which includes the default isolated-worker path.

A refused thread is left at an unspecified class, which is still eligible for a performance core. The residual risk is that the scheduler uses an efficiency core under load, which appears as a [bimodal distribution](./statistics/outliers.md#bimodal-distribution-warning) or [host drift](./statistics/measurement.md#the-host-drift-canary).

For more information, see [macOS and Apple Silicon](./features/environment-control.md#macos-and-apple-silicon).

## Still stuck?

- [Configuration](./reference/configuration.md) - Full options reference
- [CLI Reference](./reference/cli.md) - All command-line flags
- [Key Concepts](./getting-started/key-concepts.md) - Explanations of warmup, outliers, and CIs
- [Guides](./guides/) - Real-world workflow recipes, including [Tuning for CI/CD pipelines](./guides/ci-cd-pipelines.md)
- [FAQ](./faq.md) - Frequently asked questions
