---
title: Troubleshooting
description: Symptom, cause, and fix for common measurement problems.
order: 11
---

# Troubleshooting

Each entry leads with the literal fix and then explains the why.

## Measurement variability

### Numbers are uniformly slow and not production-representative

> [!CAUTION] Quick fix
> Rebuild with `dotnet run -c Release` (or set the configuration to Release in your IDE) and detach the debugger.

The entry assembly was built in `Debug` configuration (common with `dotnet run` without `-c Release`), or a debugger is attached. Both defeat JIT inlining and tier-1 optimization. If measuring Debug behavior is intentional, suppress the warning with `NBENCHMARK_SUPPRESS_DEBUG_WARNING=1` or `new MeasurementOptions { Environment = new EnvironmentOptions { SuppressBuildConfigurationWarning = true } }`.

### Large Error (wide confidence interval)

> [!CAUTION] Pick one based on the cause
> **Demand a tighter target:** `.WithAutoTune(AutoTunePreset.Thorough)` or `--ci-target 0.01`
> **Raise the sample ceiling:** `--max-samples <n>` and `--max-tuning-time <s>` if the loop is stopping on a cap
> **Reduce OS scheduling noise:** switch outlier mode to `.WithOutlierMode(OutlierMode.IqrFence)` (the default; if you changed it, change it back)
> **Stabilise a hot laptop:** `--warmup 50` to let the CPU stabilise, and run plugged in

A wide confidence interval means genuinely variable timings. In auto-sampling mode NBenchmark keeps collecting samples until the Error meets the precision target, so a wide interval usually points to real run-to-run variability rather than too few samples. The right remedy depends on the cause: a tight target for slow-but-stable bodies, a higher sample ceiling for fast-but-noisy ones, an outlier-mode change for OS noise, or a longer warmup for thermal ramps.

See [Configuration: AutoTune](./reference/configuration.md#autotune) for the `Thorough` preset and [Configuration: OutlierMode](./reference/configuration.md#outliermode) for the outlier modes.

### Same benchmark, a different median each run, and every run reports a tight Error

> [!CAUTION] Quick fix
> Run with `--launch-count 5` or higher (the Harness default) and read the `Error` column, which then describes reproducibility rather than one process's precision. Do **not** reach for more samples or longer warmup first — neither addresses the usual cause.

Three different things produce this symptom, and they need different fixes. Diagnose before treating, in this order — the first two are the ones you can fix, and both are rare with default settings.

**1. Warmup ended before the JIT finished tiering up.** The run measured pre-tier-1 (unoptimized) code, which can read several times slow. Each run is internally consistent, which is why the margin looks trustworthy.

Check `autoTune.warmupTimeFloorMet` in the JSON (or `warmup cut short` on the console summary), plus `autoTune.jitQuiescenceAchieved` and `autoTune.splitHalfDrift`. `autoTune.warmupCurve` shows whether the body was still speeding up when warmup ended, and `autoTune.jitLastChangeAtNs` against `warmupElapsedNs` shows how much quiet time followed the last compilation. Fix by raising `--min-warmup-time <ms>` (default 500) and `--max-warmup <n>` if the ceiling is what cut warmup short.

**2. The measurement is finer than the clock can resolve.** Check `autoTune.sampleQuantizationFraction` against the reported margin. If one timer step is a larger fraction of the sample than the margin is, the digits are describing the clock's step grid rather than your code — within a run every sample lands on the same step, and between runs a tiny shift moves them all to the next one. NBenchmark warns when it detects this. The default `MinQuantaPerSample` floor prevents it, so it generally only appears with a small pinned `--ops-per-sample` on a fast body; raise it so each sample spans hundreds of steps. See [Timer resolution](./statistics/measurement.md#timer-resolution).

**3. Neither — the number genuinely does not reproduce that precisely.** This is the common case on a healthy run, and there is nothing to fix in the measurement. A single process cannot see it: within-run sampling estimates how precisely *that process* measured, and drives the margin toward zero as `1/√n`, while the between-process component — code and heap layout, scheduler placement, CPU frequency and thermal state — is fixed inside a process and completely untouched by it. **More samples make the margin narrower without making it more honest, and longer warmup does not change it at all.**

Only replication across processes can measure it. Run `--launch-count 5` (or more) and read `launchStatistics`: `betweenLaunchDispersion` is the run-to-run spread, and `processVarianceRatio` is how far the single-process margin was understating it. On a nanosecond-scale body a ratio of 30-60 is ordinary. See [Reading the reproducibility warning](./features/multiple-launches.md#reading-the-reproducibility-warning).

To actually *reduce* that spread you have to work outside the statistics — [environment controls](./features/environment-control.md) for CPU affinity and process priority, or a quieter, less thermally constrained host. Note that CPU affinity is unavailable on macOS, so an Apple Silicon laptop with performance and efficiency cores will show more run-to-run spread than a pinned Linux box, and there is no in-process remedy for it.

### Tight Error next to a `maxCeiling` stop, or next to a Max hundreds of times the median

> [!CAUTION] Pick one
> **Accept the variance as the finding:** `--launch-count 5` (the honest signal of run-to-run spread across launches)
> **Chase precision:** raise `--max-samples` and loosen `--ci-target` (e.g. `--ci-target 0.05`)

The reported Error is computed on the **trimmed** set while the loop's stop rule ran on the **raw** stream. When the variance lives in the outliers, trimming removes it and the reported margin tightens around what remains. A benchmark can show `MarginOfError` at ±1.3% of its mean while `autoTune.achievedRelativeCiWidth` is `1.05` (±105%). Neither number is wrong; they describe different sample sets.

Read `autoTune.sampleStop` before the Error column: a tight margin is evidence the measurement *converged* only when it reads `ciTargetMet`. Compare `autoTune.achievedRelativeCiWidth` against `marginOfError / mean`, and check `outliersRemoved` against the pre-trim sample count.

See [Raw vs. trimmed statistics](./statistics/measurement.md#raw-vs-trimmed-statistics) for the full mechanism.

### Result reports a `driftUnresolved` stop

> [!CAUTION] Pick one
> **Land the transition during warmup instead:** `--min-warmup-time <ms>` (raise it; the default is 500 ms)
> **Accept non-stationarity as the finding:** `--launch-count 5` to measure the across-launch spread, which is the honest signal

The measured timings kept moving while they were being collected, so the interval describes a moving target. Usually a JIT tier-up or dynamic-PGO re-optimization landing inside measurement; otherwise a thermal ramp, a filling cache, or a growing data structure.

See [Measurement: Steady-state (drift) gate](./statistics/measurement.md#phase-3---measurement-ci-width-target) for the drift detection mechanism and the restart limit.

### Sample count varies between runs

> [!CAUTION] Quick fix
> Expected - this is auto-sampling working as designed. Pin `.WithIterations(n)` / `--iterations n` for a fixed, reproducible sample count (e.g. in CI).

Each run collects exactly enough samples to hit the CI target. Pin a fixed count only when you need reproducibility across runs (CI dashboards, regression baselines).

See [Configuration: Iterations](./reference/configuration.md#iterations).

### High StdDev

> [!CAUTION] Pick one
> **Diagnose allocation pressure:** enable allocation tracking with `.WithAllocations()` and read the `Alloc/op` column
> **Isolate iterations from GC noise:** switch to the `Independent` profile (`--profile independent`) to force per-iteration GC

A high standard deviation means timings are inconsistent. Under the default `Realistic` profile, natural GC pauses are included in the timing; allocation pressure from the body produces a noisy tail. The `Independent` profile forces a Gen0 GC before every iteration so GC pauses are deterministic rather than random, which removes them from the variance (at the cost of ecological validity).

See [Measurement Profiles](./statistics/measurement.md#measurement-profiles) for the worked example and [Configuration: MeasureAllocations](./reference/configuration.md#measureallocations).

### Bimodal-distribution warning

> [!CAUTION] Quick fix
>
> 1. **Check the body** for **lock contention** or **cache misses** - the cluster centre in the warning names the extra cost the slow path pays.
> 2. **If you suspect GC:** `dotnet run -- --profile independent` (forces per-iteration Gen0 collection, making GC pauses deterministic rather than bimodal).

The outlier detector found a tight secondary cluster of slow timings rather than scattered noise - a real, repeatable slow path in the code. The reported median describes the fast path; the cluster centre describes a latency a real user will also hit. The warning is non-fatal: the benchmark still completes and reports statistics on the trimmed (fast-cluster) set.

Do not silence it. Read the tail metrics as-is - by default the histogram and P99/P99.9/Max are computed from the full pre-trim distribution, so the cluster is still visible there. Investigate the cause with a profiler, and reduce noise at the source with [environment control](./features/environment-control.md) if OS scheduling contributed to the spread.

See [Outlier Trimming: Bimodal-distribution warning](./statistics/outliers.md#bimodal-distribution-warning) for the detector, the common-cause table, and the interaction with each outlier mode.

## Zero or unexpected results

### Result shows `0 ns`

> [!CAUTION] Quick fix
> Use the `Func<T>` overload that returns a value: `Benchmark.Run(() => ComputeHash(data))`. Or add a side effect to the body.

Dead code elimination - the compiler removed your benchmark body because it has no observable side effects. A returning overload writes the return value to a static field so the JIT cannot elide the call.

See [FAQ: `0 ns`](./faq.md#my-benchmark-produces-0-ns-whats-happening).

### All results zeroed

> [!CAUTION] Quick fix
> Remove the `--dry-run` flag, or set `Iterations` > 0.

Dry-run mode is active (`--dry-run`, or `Iterations=0` and `WarmupIterations=0`). The body is not invoked and no measurements are taken. `--dry-run` validates discovery and wiring; to run the body exactly once for a smoke test, use `--iterations 1 --warmup 0`.

### `MarginOfError` is `±0 ns`

> [!CAUTION] Quick fix
> Unpin `Iterations` to use auto mode (collects at least `AutoTune.MinSamples`), or pin a larger count.

Either only one sample was collected (`n < 2`, from a pinned `Iterations = 1`) or all measurements were identical (timer resolution coarser than the benchmark duration). For a fast body, auto ops-per-sample calibration amortises a coarse timer - note that calibration is skipped when setup/teardown is set, and bypassed when `OpsPerSample` is pinned.

Identical measurements are the extreme of the quantization case above: every sample landed on the same clock step. Compare `autoTune.clockResolutionNs` against `autoTune.sampleDurationNs` - if a sample spans only a step or two, the timer cannot distinguish your samples from each other. See [Timer resolution](./statistics/measurement.md#timer-resolution).

### `Sig` column is blank

> [!CAUTION] Quick fix
> Increase iterations or combine more runs: `--iterations <n>` or `--min-samples <n>`.

Too few samples for the significance test (requires ≥2 per group), **or** the Kruskal-Wallis omnibus was not significant (three-plus benchmarks compared, no post-hoc ran). In the second case the blank is correct - the omnibus gate refused to run pairwise comparisons because the groups look the same.

See [FAQ: significance](./faq.md#why-is-significance-sometimes-blank) and [Significance Testing](./statistics/significance.md) for the omnibus gate.

## Discovery and setup errors

### `[Benchmark]` method not discovered

> [!CAUTION] Quick fix
> Run `dotnet run -- --list` to verify what the host finds, then check the class is public, not abstract, and the method is an instance method (not static).

The host scans for public instance methods marked `[Benchmark]` on public, non-abstract classes. Static methods, abstract classes, and assemblies not registered with `AddFromAssembly` are not discovered.

See [Harness mode: listing benchmarks without running](./usage-modes/harness-mode.md#listing-benchmarks-without-running).

### "Could not instantiate MyClass"

> [!CAUTION] Pick one
> **Add a public parameterless constructor** to the benchmark class (simplest fix if the class has no real dependencies)
> **Install `NBenchmark.Analyzers`** for compile-time detection of NB0001 (missing parameterless constructor)
> **Use dependency injection:** add the `NBenchmark.DependencyInjection` package and `UseDependencyInjection<T>(BuildServices)` with a static `IServiceProvider BuildServices()` factory, so the worker rebuilds the container and the run stays isolated

The host uses `Activator.CreateInstance`, which requires a public parameterless constructor. Benchmark classes with real dependencies (a repository, a logger, an `HttpClient`, a `DbContext`) need the DI companion package.

See [Dependency Injection](./features/dependency-injection.md) for the full API and [FAQ: instantiation](./faq.md#the-host-throws-could-not-instantiate-myclass-how-do-i-fix-it).

### "cannot be sent to another process" - the run fails instead of measuring

> [!CAUTION] Pick one
> **Build the value in the worker** with a prepare delegate: `Benchmark.Run(prepare: () => BuildIt(), body: v => Use(v))`, or `BenchmarkSuite.Over(name, () => BuildIt())` in Suite mode
> **Mark your own type `[BenchmarkState]`** if its measured behaviour really is fully determined by its serialized contents
> **`AddInProcess(name, body)`** to keep just that one benchmark here, with the rest of the suite still in a worker
> **`WithRequireIsolation(false)`** to accept a labelled host-process number everywhere - right for a scratchpad, wrong for anything comparative

`RequireIsolation` defaults to `true`, so a benchmark that asked for a worker and cannot have one **fails the run** rather than being measured here and stamped. The message names the benchmark and the value.

Ordinary captured data is sent and the body stays isolated - an `int`, a `string`, an `int[]`, a `List<T>` or `Dictionary<K,V>` with its default comparer, a record of those. What reaches this error is a value whose behaviour is not carried by its contents:

| Message says | Why | Fix |
| --- | --- | --- |
| *is not one of the types whose measured behaviour is fully determined by its contents* | A `Stream`, `HttpClient`, `DbConnection`, mock or similar | A prepare delegate - build it in the worker |
| *was built with a custom comparer* | `new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase)` round-trips to identical entries with a different lookup cost | A prepare delegate, so the comparer is applied there |
| *carries a private field* / *a get-only property* | On a `[BenchmarkState]` type. The serializer writes those and cannot read them back, so they would arrive at their defaults | Make the member a public field, or a property with a setter |
| *holds a `X` where a `Y` was declared* | A collection of a base type holding a subclass; it would arrive as base instances with the override gone | A prepare delegate, or make the element type exact |
| *is larger than the transfer ceiling* | Past `MaxTransferredStateBytes` (8 MiB) | A prepare delegate. Raising the ceiling is possible but rarely the right answer - see [configuration](./reference/configuration.md#maxtransferredstatebytes) |
| *something else in this group already refers to the same object* | Two benchmarks share one array through different closures; rebuilding would make two arrays where your program has one | Build the shared state in one prepare delegate the bodies share |

A suite is measured by one worker, so the message names **every** offending benchmark at once rather than the first.

See [Isolated Runs](./features/isolated-runs.md#what-a-captured-value-costs).

### "Could not load file or assembly" from an ASP.NET Core or WPF project

> [!CAUTION] Quick fix
> Rebuild, so the `runtimeconfig.json` beside the assembly under test is present and current. That file is how the worker learns which shared frameworks to ask for.

Benchmarks that live in a `Microsoft.NET.Sdk.Web` (or WinForms/WPF) project run in an assembly whose dependency graph reaches a shared framework — `Microsoft.AspNetCore.App` or `Microsoft.WindowsDesktop.App`. Those assemblies ship with the framework rather than in your output directory, so they are absent from your project's `deps.json` and are expected to be supplied by the process. The measurement worker is a plain console application, so on its own it supplies only `Microsoft.NETCore.App`, and the load fails with a message naming an assembly that is not actually missing from disk — `Microsoft.Extensions.Hosting.Abstractions` is the usual one.

The framework set is extended automatically: before launching the worker, NBenchmark reads the `runtimeconfig.json` beside the assembly under test and adds any framework the worker does not already declare. `dotnet benchmark` does the same thing for itself — unlike every other mode it loads the target into its own process to discover benchmarks, so it restarts once under the right framework set before reading anything. Nothing needs configuring, and nothing changes for an ordinary console benchmark project.

Two cases remain:

- **No `runtimeconfig.json` beside the assembly**, or a stale one. Rebuild the project. The fault message names the frameworks the worker was actually started with, so it is clear when this is what happened.
- **A self-contained (`--self-contained`) target.** Its framework lives in its own output directory, so there is no shared framework to request. Build the benchmark project framework-dependent, or pass `--in-process` and accept host-fidelity numbers.

### Benchmarks run in a different order each time

> [!CAUTION] Quick fix
> `--order declaration` or `.WithRunOrder(RunOrder.Declaration)` for source order.

Random order is the default and prevents systematic bias (the first benchmark always benefits from a warm CPU cache). Pin declaration order for reproducibility across CI and local, or use `--seed <n>` for a reproducible shuffle.

See [Configuration: ForceGcBetweenBenchmarks](./reference/configuration.md#forcegcbetweenbenchmarks) (the run-order option lives in the same reference).

## Quick reference: Outlier modes

| Mode | When to use |
| --- | --- |
| `IqrFence` (default) | General-purpose. The [IQR](https://en.wikipedia.org/wiki/Interquartile_range)-based fence adapts to your data's spread, trimming spikes from OS scheduling interrupts without discarding clean samples. |
| `RemoveTop5Percent` | When you want a fixed quota - always removes the slowest 5% of iterations. |
| `RemoveTopAndBottom5Percent` | When very fast outliers (e.g. cache hits after warmup) also skew results. |
| `None` | When every sample matters (latency-tail analysis). |

## Still stuck?

- [Configuration](./reference/configuration.md) - full options reference
- [CLI Reference](./reference/cli.md) - all command-line flags
- [Key Concepts](./getting-started/key-concepts.md) - how warmup, outliers, and CIs work
- [Guides](./guides/) - real-world workflow recipes, including [Tuning for CI/CD pipelines](./guides/ci-cd-pipelines.md)
- [FAQ](./faq.md) - frequently asked questions
