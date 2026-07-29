# cli.md

---
title: CLI Reference
description: All command-line flags accepted by BenchmarkHarness.
order: 1
---

# CLI Reference

When using `BenchmarkHarness` (Harness mode), all configuration can be driven from the command line. `BenchmarkHarness.Create(args)` parses `args` automatically - no argument-parsing library required.

## Usage

```bash
dotnet run -- [options]
```

Or with a published binary:

```bash
MyApp.Benchmarks [options]
```

## Options

### `--filter <pattern>`

Run only benchmarks whose fully-qualified name (`ClassName.MethodName`) matches the glob pattern.

```bash
dotnet run -- --filter String*          # all benchmarks in any class starting with "String"
dotnet run -- --filter *.Contains*      # any method containing "Contains"
dotnet run -- --filter StringBenchmarks.Concat   # exact match
```

**Glob rules:** `*` matches any sequence of characters. Matching is case-insensitive. If a class has no matching methods after filtering, it is skipped entirely.

---

### `--category <name>`

Include benchmarks tagged with the given category. Repeatable; multiple `--category` flags are combined with OR, so a benchmark runs if it has any of the included categories. Matching is case-insensitive and exact.

```bash
dotnet run -- --category String
dotnet run -- --category String --category Memory
```

Untagged benchmarks are excluded when any `--category` flag is present.

---

### `--exclude-category <name>`

Exclude benchmarks tagged with the given category. Repeatable; multiple `--exclude-category` flags are combined with OR, so a benchmark is removed if it has any of the excluded categories.

```bash
dotnet run -- --category String --exclude-category Slow
```

---

### `--iterations <n>`

Pin the measured-sample count per benchmark, disabling auto-sampling. Valid range: `0` to `100 000`. Default: **auto** (sampling stops when the confidence interval meets `--ci-target`). Use `--dry-run` to skip measurement entirely rather than setting this to `0` manually.

```bash
dotnet run -- --iterations 1000
```

This is the deterministic gate: pass it when a run must collect an exact, reproducible number of samples (for example, in CI). Leave it off to let each benchmark self-size.

---

### `--warmup <n>`

Pin the warmup-sample count per benchmark, disabling the plateau rule. Valid range: `0` to `10 000`. Default: **auto** (warmup stops once timings plateau).

```bash
dotnet run -- --warmup 50
```

---

### Adaptive tuning flags

In auto mode NBenchmark resolves warmup length, measured-sample count, and ops-per-sample (K) at runtime. These flags steer that resolution without pinning an exact count. See [Configuration: AutoTune](./configuration.md#autotune) for the full model.

| Flag | Default | Effect |
| --- | --- | --- |
| `--auto-tune <preset>` | `default` | Apply a preset bundle: `default`, `quick` (fewer samples, ±5% CI), or `thorough` (more samples, ±1% CI). |
| `--ops-per-sample <n>` | auto | Pin K - the number of body invocations timed as one sample. Auto-calibrated otherwise. |
| `--ci-target <0-1>` | `0.025` | Target relative CI half-width for auto sampling. Sampling stops once it is met. |
| `--min-samples <n>` | `30` | Floor on auto-resolved measured samples. |
| `--max-samples <n>` | `5000` | Ceiling on auto-resolved measured samples. Past a coefficient of variation of ~90% the CI rule needs samples growing as `(t × CV / target)²` and cannot converge, so the ceiling stops the chase and the warning names the CV. |
| `--min-warmup <n>` | `8` | Floor on auto-detected warmup samples. |
| `--max-warmup <n>` | `100000` | Ceiling on auto-detected warmup samples. Deliberately far above what any body needs so the *time* bounds bind instead — a fast body needs ~25,000 samples to accumulate `--min-warmup-time`. (A *pinned* `--warmup` is still limited to 10,000.) |
| `--max-tuning-time <s>` | `20` | Per-benchmark wall-clock safety cap, in seconds, for the whole adaptive loop. |
| `--autotune-cap-behavior <mode>` | `warn` | What happens when the wall-clock cap is hit before the CI target or warmup plateau is reached: `warn` emits a warning; `error` marks the benchmark as errored. |
| `--warmup-budget-fraction <0-1>` | `0.4` | Max share of `--max-tuning-time` that calibration and warmup may consume together; the remainder is reserved for measurement. Must be in `(0, 1]`. |
| `--cap-grace-factor <n>` | `1.5` | Multiplier on `--max-tuning-time` that the measurement phase may reach while chasing `--min-samples` after the cap fires. Must be at least 1; set to 1 to disable the grace path. |
| `--min-warmup-time <ms>` | `500` | Minimum in-body time (milliseconds) auto-warmup must accumulate before it may settle, so background tiered JIT lands before measurement rather than mid-run. 5× the runtime's 100 ms tiered-compilation call-counting delay; a floor at or below that delay reliably lands the tier-up *inside* measurement. Chosen empirically — at 250 ms a `StringBuilder`-append loop still landed in either its tier-0 or its ~4.5× faster steady state depending on the run. Raise this first if a benchmark's median differs between runs while each run reports a tight interval. Must be ≥ 0; `0` disables the floor (and the JIT-quiescence gate). |
| `--no-jit-quiescence` | off | Disable the JIT-quiescence warmup gate (warmup no longer waits for the JIT to stop compiling); the `--min-warmup-time` floor still applies. |
| `--jit-quiet-period <ms>` | `50` | How long (milliseconds) the JIT compiled-method count must stay unchanged before auto-warmup may settle. Clamped down to `--min-warmup-time` so it never becomes the binding floor. Must be ≥ 0; `0` disables the gate. |
| `--min-measurement-time <ms>` | `100` | Minimum in-body time (milliseconds) the measurement phase must span before it may stop on the CI target. Makes the sample count scale with body speed, so a cheap body collects hundreds of samples for milliseconds of extra work instead of stopping at a few dozen (where P95/P99/P99.9 all collapse onto the maximum). Costs nothing for a body already slower than `--min-measurement-time / --min-samples`. Must be ≥ 0; `0` disables the floor. |
| `--drift-tolerance <0-1>` | `0.1` | How far the first and second halves of the measured samples may disagree before the CI stop is refused and measurement restarts. Catches a JIT tier-up landing inside the measurement window, which otherwise reports a tight interval across a step change. Must be in `[0, 1]`; `0` disables the gate. |
| `--max-drift-restarts <n>` | `2` | How many times drift may discard the collected samples and restart measurement. Restarts share the `--max-tuning-time` budget, so they cannot lengthen a run. Exhausting the limit reports a `driftUnresolved` stop with a warning. Must be ≥ 0. |

```bash
# Quick feedback: fewer samples, looser CI
dotnet run -- --auto-tune quick

# Publication-grade: tighter CI, capped at 60s per benchmark
dotnet run -- --auto-tune thorough --max-tuning-time 60

# Pin K for a fast body, let sampling auto-resolve
dotnet run -- --ops-per-sample 256 --ci-target 0.01
```

---

### `--confidence <value>`

Confidence level for the margin of error in the Error column. Must be a decimal strictly between `0` and `1`. Default: `0.95`.

```bash
dotnet run -- --confidence 0.99
```

---

### `--alpha <value>`

Significance level (alpha) for the significance test. A benchmark is flagged significant when its p-value is below this threshold. Must be a decimal strictly between `0` and `1`. Default: `0.05`.

```bash
dotnet run -- --alpha 0.01
```

---

### `--outlier <mode>`

Outlier-trimming mode applied before statistics are computed. Default: `iqr`.

| Token | Mode |
| --- | --- |
| `none` | No trimming. |
| `top5` | Remove the slowest 5%. |
| `both5` | Remove the slowest and fastest 5%. |
| `iqr` | IQR fence (1.5×). **(default)** |
| `mad` | Median Absolute Deviation (3×) - robust to heavy skew. |

```bash
dotnet run -- --outlier mad
```

The `--outlier` flag always takes priority over a programmatic `OutlierDetector` set via `WithOptions`. See [Outlier Trimming](../statistics/outliers.md) for the algorithms.

---

### `--tail-basis <basis>`

Which sample set the order statistics - percentiles, `Min`, `Max`, and the histogram - are computed from. Default: `raw`.

| Token     | Basis                                                       |
|-----------|-------------------------------------------------------------|
| `raw`     | Full pre-trim distribution; includes the trimmed tail.      |
| `trimmed` | Inlier (post-trim) set; describes only the central process. |

```bash
dotnet run -- --tail-basis trimmed
```

Central-tendency and dispersion statistics (mean, standard deviation, CI, CV, skewness, kurtosis, MAD, median, median CI) always stay on the trimmed set regardless of this flag. See [Descriptive Statistics](../statistics/descriptive.md) for details.

---

### `--reporter <type>`

Add a reporter by name. Can be specified multiple times to stack reporters. Built-in reporters:

| Name | Reporter | Output |
| --- | --- | --- |
| `json` | `JsonReporter` | JSON file in the current directory (or `--output` directory) |
| `markdown` | `MarkdownReporter` | Markdown file in the current directory (or `--output` directory) |
| `csv` | `CsvReporter` | CSV file in the current directory (or `--output` directory) |

The `console` reporter is provided by the `NBenchmark.Reporters.Console` package. When the package is referenced, it self-registers automatically and becomes available via `--reporter console` - no special setup needed.

```bash
dotnet run -- --reporter markdown
dotnet run -- --reporter json --reporter csv
dotnet run -- --reporter console
```

Reporters from external packages self-register through the same mechanism: reference the package, use `--reporter <name>` from the CLI. No per-reporter configuration needed in the host.

The `--help` output also lists auto-attached reporters in a separate section after the explicit reporter list (e.g. `(auto-attached: studio)`). Auto-attached reporters fire on every run after the explicit reporters without requiring `--reporter`; see the [Custom Reporters](../output/custom-reporters.md#auto-attached-reporters) page for the full contract. Passing `--reporter <name>` for an auto-attached reporter is not supported - the auto-attached one is already firing, and adding an explicit reporter instance with the same canonical name via `.WithReporter(...)` is dedup'd out so it does not fire twice.

---

### `--observer <type>`

Attach a measurement observer by name. Observers receive live per-sample, per-detector, and phase-transition events during the adaptive measurement loop (see [Measurement Observer](observers.md) for the event model). Repeatable; multiple `--observer` flags compose the observers into a fan-out so every attached observer receives every event.

The core `NBenchmark` package ships no observers - `ObserverRegistry.Available` is empty until an external package self-registers one. The `NBenchmark.Live` package (planned) will register a `live` observer for the embedded web dashboard; any external package can register additional observers through the same mechanism.

```bash
dotnet run -- --observer live
dotnet run -- --observer live --observer logging
```

Observers from external packages self-register through the same mechanism as reporters: reference the package, use `--observer <name>` from the CLI. No per-observer configuration needed in the host.

---

### `--output <directory>`

Set the output directory for file reporters. Must be a path under the current working directory. The directory is created automatically if it does not exist. Default: current directory.

```bash
dotnet run -- --reporter markdown --output ./results
```

---

### `--order <mode>`

Control the order benchmarks run in.

| Value | Behaviour |
| --- | --- |
| `random` | Fisher-Yates shuffle, random seed each run. **(default)** |
| `declaration` | Run in the order methods are declared in the class. |

```bash
dotnet run -- --order declaration
```

---

### `--seed <n>`

Set a fixed integer seed for the random run order. Produces a reproducible ordering across runs.

```bash
dotnet run -- --seed 42
```

Has no effect when `--order declaration` is used.

---

### `--in-process`

Disable process isolation for the whole run. Harness mode is isolated by default - each benchmark class runs in its own child process - and this flag forces every benchmark to run in the host process instead. It overrides `[IsolatedProcess]` and is equivalent to calling `WithIsolation(false)` in code.

```bash
dotnet run -- --in-process
```

`--dry-run` also always runs in-process. See [Isolated Runs](../features/isolated-runs.md) for the full isolation model.

---

### `--strict-isolation`

Fail the run if any benchmark was **not** measured in an isolated worker.

```bash
dotnet run -- --strict-isolation
```

Every non-isolated result is already labelled in the table and explained on the console, but neither survives CI: a label scrolls past, and a warning in a log nobody reads is indistinguishable from no warning. This turns the label into exit code 1.

The failure names each benchmark, grouped by cause, with the remedy for each:

```
--strict-isolation: 1 of 4 benchmark(s) were measured in this process rather than an
isolated worker, so their numbers carry the host's JIT and GC configuration.
  in-process: HarnessBenchmarks.InHarness
```

Use it on any pipeline that gates on benchmark numbers. A benchmark that quietly fell back to the host process - because the worker was not deployed on the build agent, or because a body captures state - produces numbers that cannot be compared against a stored baseline measured under a different runtime configuration.

---

### `--verify-isolation`

Measure everything a second time in the host process and print the per-benchmark difference.

```bash
dotnet run -- --verify-isolation
```

```
Isolation verification - the same benchmarks measured both ways:

  Benchmark                        Isolated    In-process  Difference
  ---------------------------  ------------  ------------  ----------
  HarnessBenchmarks.Compute         9.32 ns      11.47 ns  1.23x
  HarnessBenchmarks.Baseline        9.56 ns      11.19 ns  1.17x
  HarnessBenchmarks.InHarness             -      10.99 ns  not isolated
```

This exists because the case for isolation is not believable in the abstract. On this library's own sample, in-process measurement of one body reported 7,009 ns and 320 ns on consecutive attempts - a 21x error, with a tight confidence interval on each. Reading that in a changelog persuades nobody; seeing it on your own benchmarks does.

The output reports a **ratio per benchmark** rather than an aggregate, because the finding is that host measurement is *unpredictable*: one row at 21x beside another at 1.0x is the point, and averaging would erase it. Rows are ordered by distance from parity, so a host reading at half the isolated one ranks alongside one at double.

When the two agree, it says so. A workload insensitive to the host's runtime configuration is a real result worth knowing - though it is a property of those benchmarks, not a general one.

The comparison pass runs no reporters, writes no files, and cannot change the exit code. It is a diagnostic, not a second set of results.

---

### `--cross-class`

Compute significance across all classes in a single comparison table instead of per class. The baseline is chosen from the whole group, and the reporter adds a `Class` column so rows can be distinguished.

```bash
dotnet run -- --cross-class
```

Use this when comparing implementations that live in separate classes (e.g. a legacy version and a refactored version). Cross-class mode is opt-in because mixing unrelated benchmark classes into one significance table produces a baseline that may be semantically meaningless.

Equivalent to calling `WithCrossClassSignificance()` in code.

---

### `--profile <mode>`

Set the measurement profile. Controls the per-iteration Gen0 GC and the pre-measurement full GC as a bundle. Between-benchmark GC and allocation tracking are on for **both** profiles.

| Value | Behaviour |
| --- | --- |
| `realistic` | No per-iteration GC, no pre-measurement GC (inherits the warmup heap). **(default)** |
| `independent` | Per-iteration Gen0 GC, full GC after warmup before measurement. |

```bash
dotnet run -- --profile independent
```

Individual behaviours can be overridden with `--force-gc`, `--no-gc-between-benchmarks`, and `--no-allocations`. See [Measurement Profiles](../statistics/measurement.md#measurement-profiles) for a worked example.

> [!NOTE]
> `--profile` controls *GC behaviour during* a run. `--runtime-profile` (below) controls the *runtime configuration a process starts with*. They are independent.

---

### `--runtime-profile <profile>`

Set the runtime-startup configuration benchmarks are measured under: JIT tiering, dynamic PGO, ReadyToRun, and GC flavour.

| Value | Configuration | What it is for |
| --- | --- | --- |
| `steady-state` | `TieredCompilation=0`, `TieredPGO=0`, `ReadyToRun=0` | **(default)** Fully-optimized steady-state throughput. |
| `production` | `TieredCompilation=1`, `TieredPGO=1`, `ReadyToRun=1` | What your users actually run. Reproducible, but imprecise. |
| `server-gc` | `steady-state` plus `gcServer=1`, `gcConcurrent=0` | Code destined for a server-GC host. |
| `host` | Nothing set | Inherit the host's configuration. Reproduces pre-profile numbers. |

**Why this exists.** None of these settings can be changed in a process that is already running - the runtime reads them once at startup. That, rather than cross-benchmark state contamination, is the real reason a measurement needs its own process: **the process boundary is how the configuration gets delivered.**

The effect is not subtle. On four benchmarks with provably identical cost:

| Configuration | Spread across runs | Largest fabricated difference |
| --- | --- | --- |
| `--runtime-profile host` | 3.10x | 3.06x |
| `--runtime-profile production` | 2.59x | 2.54x |
| `--runtime-profile steady-state` | **1.02x** | **1.01x** |

Under `host` and `production`, benchmarks of identical cost differed by up to 3x - each reported with a tight confidence interval. Tiered compilation means a short body may be measured as tier-0 or tier-1 code depending on unrelated process history.

```bash
dotnet run -- --runtime-profile production --launch-count 5   # imprecise: raise the replicate count
dotnet run -- --runtime-profile host                          # reproduce a pre-profile baseline
```

**Limits, stated plainly:**

- `steady-state` forbids on-stack replacement and changes startup behaviour, so it is **the wrong choice for measuring cold-start or first-call cost**. Use `production` for that.
- It also costs wall clock, because every method is compiled eagerly at full optimization.
- **It cannot apply to in-process benchmarks.** Anything measured in the host process - all of Simple mode, and `--in-process` or `[InProcess]` benchmarks - reports `host` and inherits the host's configuration. NBenchmark says so rather than claiming otherwise: every result carries the profile it was *actually* measured under, and results measured under different profiles are never compared against each other.

Set `RuntimeProfile.Host` (or `--runtime-profile host`) to accept the host's configuration everywhere and silence the guidance message; `NBENCHMARK_SUPPRESS_RUNTIME_PROFILE_WARNING=1` suppresses it without changing the profile.

---

### `--force-gc`

Override the profile to force a Gen0 GC before every measured iteration. Under the default `Realistic` profile, this enables per-iteration GC without switching to the `Independent` profile.

```bash
dotnet run -- --profile realistic --force-gc
```

There is no `--no-force-gc` flag because `Realistic` already disables per-iteration GC; users who want per-iteration GC under `Realistic` use `--force-gc`.

---

### `--no-allocations`

Disable allocation tracking, suppressing the `Alloc/op` column. Allocation tracking is on by default under both profiles (it is sampled outside the timed window, so it costs no timing purity), so this flag is the only opt-out.

```bash
dotnet run -- --no-allocations
```

There is no `--allocations` flag because both profiles already enable allocation tracking; users who want to disable it use `--no-allocations`.

---

### `--no-gc-between-benchmarks`

Disable the full GC that otherwise runs between benchmarks. That GC is on by default for both profiles so one benchmark's leftover heap cannot bias the next (which would make results order-dependent). Use this flag when the inter-benchmark heap carry-over is intended.

```bash
dotnet run -- --no-gc-between-benchmarks
```

This is distinct from the pre-measurement GC (`Independent` only), which clears the warmup heap before the measurement loop and is not affected by this flag.

---

### `--min-practical-effect <0-1>`

Set the minimum practical effect a change must reach to keep a significant (✓) verdict. Defaults to `0.147` (the Romano negligible/small boundary), so ✓ means "statistically real **and** at least a small effect". When a comparison's practical effect falls below the threshold, its verdict is downgraded to not-significant and a warning records the downgrade. Set to `0` to restore p-value-only verdicts.

```bash
dotnet run -- --min-practical-effect 0
```

---

### `--diagnostics <mode>`

Control which runtime diagnostics are collected during measurement. GC collection counts are cheap and always available; heap info, exceptions, and CPU time add more detail at a small overhead cost.

| Value | Behaviour |
| --- | --- |
| `none` | No diagnostics collected. |
| `gc` | GC Gen0/Gen1/Gen2 collection counts. **(default)** |
| `gcandcpu` | GC collection counts plus process CPU time and CPU/wall-clock ratio. |
| `all` | GC collection counts, heap info (committed and fragmented bytes), exception count, and CPU time. |

```bash
# Default - just GC collection counts
dotnet run -- --diagnostics gc

# Add CPU time to distinguish CPU-bound from IO-bound benchmarks
dotnet run -- --diagnostics gcandcpu

# Everything - useful for diagnosing exception-driven control flow
dotnet run -- --diagnostics all
```

The diagnostics table appears at `standard` and `advanced` detail levels, below the Precision & Tail Latency table. See [Diagnostics](../statistics/diagnostics.md) for what each counter measures and how it is collected.

Programmatic equivalent: `WithDiagnostics(DiagnosticsMode.All)` or `WithOptions(new MeasurementOptions { Diagnostics = DiagnosticsOptions.All })`.

---

### `--detail <level>`

Set the report detail level. Controls how much information reporters display.

| Value | Behaviour |
| --- | --- |
| `simple` | 6-column table with the essential statistics. **(default)** |
| `standard` | Full comparison table plus Precision & Tail Latency, Diagnostics, Interpretation, and auto-tune sections. |
| `advanced` | Same as standard plus a per-benchmark stats block with quartiles, fences, confidence interval, skewness, kurtosis, MAD, allocation breakdown, diagnostics breakdown, and the full set of configured percentiles. |

```bash
dotnet run -- --detail advanced
dotnet run -- --detail standard
dotnet run -- --detail simple
```

The `--detail` flag affects all registered reporters. File-based reporters emit different column sets. Console reporter prints the stats block below each row; Markdown reporter appends a per-benchmark details section after the table. JSON always emits the full record regardless of detail level.

In harness mode you can also set the detail level programmatically with `WithDetail(ReportDetail.Advanced)` before calling `RunAsync`.

See [Report Detail Levels](../output/report-detail-levels.md) for the full column reference and `WithDetail()` examples for suite mode.

---

### `--list`

List all discovered benchmarks without running them. Useful for verifying that your classes and methods are being found.

```bash
dotnet run -- --list
```

Output:

```
── StringBenchmarks ──
    Concat - current production implementation
    Interpolate
── DatabaseBenchmarks ──
    RunQuery
```

---

### `--dry-run`

Skip measurement entirely. Equivalent to `--iterations 0 --warmup 0`: benchmark classes are discovered, setup/teardown is wired up, and instances are created - but the benchmark body is never invoked. Use it to confirm that your classes are discovered and your DI wiring is correct before committing to a full run.

To invoke the body exactly once without warmup (e.g., a quick smoke test), use `--iterations 1 --warmup 0` instead.

```bash
dotnet run -- --dry-run
```

---

### `--help` / `-h`

Print help text and exit.

```bash
dotnet run -- --help
```

---

### `--launch-count <n>`

Repeat each benchmark N times as separate launches. Statistics (mean, stddev, median, CI) are computed across launch medians, and the best launch (lowest median) is displayed as the primary result. An aggregation table appears below the main results when `n > 1`. Valid range: `1` to `100`. Harness-mode default: `3` when the user has not explicitly pinned launch count.

```bash
dotnet run -- --launch-count 3
```

When combined with `--dry-run`, exactly one dry launch is performed regardless of the count. When combined with process isolation, the parent spawns N child processes per isolated group.

---

### `--percentiles <list>`

Control which percentile values are computed and displayed. A comma-separated list of fractions between `0` and `1`. Default: `0.50,0.95,0.99,0.999,1.0` (P50, P95, P99, P99.9, Max).

```bash
# Custom percentile set: report P90, P99, and P99.9
dotnet run -- --percentiles 0.90,0.99,0.999

# Report only the median and maximum
dotnet run -- --percentiles 0.50,1.0
```

Reporters render one column per percentile value between P50 and Max (P95, P99, etc.) in the tail-latency table. P50 is excluded from percentiles columns because it is already shown as Median. Max (`1.0`) is excluded because it appears as a separate Max stat.

Programmatic equivalent: `WithOptions(new MeasurementOptions { ReportedPercentiles = [0.90, 0.95, 0.99] })`.

---

### `--runtimes <list>`

Run the same benchmarks under multiple .NET runtimes and compare results side-by-side. The value is a comma-separated list of target framework monikers. Both short (`net8`) and full (`net8.0`) forms are accepted.

```bash
dotnet run -- --runtimes net8,net9,net10
dotnet run -- --runtimes net8.0,net10.0
```

When `--runtimes` is specified, the host builds the project for each target framework via `dotnet build -f <tfm>`, runs the benchmarks in a child process under that runtime, and aggregates the results. The project must target all specified runtimes in its `.csproj` file:

```xml
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
```

The console and markdown reporters add a "Runtime" column when results span multiple runtimes. Significance testing is performed within each runtime (net8 results are compared against the net8 baseline, not the net10 one). The first runtime in the list is the implicit baseline for ratio calculations.

`--runtimes` overrides `--in-process`; cross-runtime always uses child processes. When `--runtimes` is passed, it also overrides any `[Runtimes]` attribute on discovered classes.

---

### `--no-histogram`

Disable latency histogram computation. By default NBenchmark computes a latency histogram from the trimmed samples with 20 equal-width buckets. This flag skips histogram computation entirely.

The histogram is available on `BenchmarkResult.Histogram` (a `LatencyHistogram` with bucket boundaries and sample counts). When histogram computation is skipped, the property is `null`.

Programmatic equivalent: `WithOptions(new MeasurementOptions { EnableHistogram = false })`.

```bash
dotnet run -- --no-histogram
```

---

### `--emit-raw`

Return every raw sample from an isolated worker instead of a bounded representative subset.

By default a worker sends back at most 4,096 raw samples per benchmark. A benchmark can measure up to 100,000, and the whole array crossing the process boundary on every result is 800 KB of JSON for data the coordinator barely uses — **every statistic NBenchmark reports is computed inside the worker, over the complete sample array**. Raising or lowering the cap cannot move a median, an interval, or an outlier count.

What the samples that cross are used for is the sample dump in JSON output, the Console density sparkline, and significance testing. All three are distribution properties, and a few thousand samples describe a distribution as faithfully as a hundred thousand.

The subset is not a prefix. It is drawn uniformly at random from the full array and kept in measurement order, seeded from the run's seed so a repeat of the same configuration ships the same samples. A prefix would be the slice of the run nearest to warmup, which is the least representative part of it.

Pass `--emit-raw` when you want the complete series for analysis outside NBenchmark:

```bash
dotnet run -- --emit-raw --reporter json
```

Programmatic equivalent: `WithOptions(new MeasurementOptions { MaxRawSamples = MeasurementOptions.UnboundedRawSamples })`. Any positive value sets a different cap.

> **In-process runs are unaffected.** There is no boundary to cross, so they always hold the complete array. A run that mixes isolated and in-process benchmarks has more samples on its in-process rows. This changes none of the reported numbers, but it is worth knowing when comparing sample dumps.

See also [`--no-samples`](#--no-samples), which omits the arrays from JSON output entirely.

---

### `--no-samples`

Omit raw per-sample arrays from JSON reporter output. Samples are still collected, still feed significance testing and the Console histogram, and still cross the process boundary — this only controls whether they are written to the file.

```bash
dotnet run -- --no-samples --reporter json
```

---

### `--cpu-affinity <list>`

Pin the benchmark process to specific logical CPU cores for the duration of the run, reducing inter-core migration noise. The value is a comma-separated list of zero-based logical core indices (as reported by the OS). The prior affinity is restored when the run completes.

```bash
dotnet run -- --cpu-affinity 0          # pin to core 0 only
dotnet run -- --cpu-affinity 2,3        # pin to cores 2 and 3
```

Indices must be non-negative and within the host's logical core count (0 to `Environment.ProcessorCount - 1`). Out-of-range or non-numeric values produce a parse error and the run does not proceed.

**Platform support:** processor affinity is applied on Linux and Windows. On macOS the BCL does not expose the `setaffinity` syscall, so the flag is accepted but skipped with a warning - use a Linux or Windows host for affinity-pinned CI gates. See [Environment control](../features/environment-control.md) for the full model.

Programmatic equivalent: `WithHardwareAffinity(2, 3)` (suite/harness) or `new MeasurementOptions { Environment = new EnvironmentOptions { CpuAffinity = [2, 3] } }`.

---

### `--priority <level>`

Request a process priority for the benchmark run, reducing preemption by unrelated OS work. The prior priority is restored when the run completes. A refused elevation (common on locked-down CI runners) is surfaced as a warning, not an error - the run still proceeds.

| Value | Priority |
| --- | --- |
| `normal` | `ProcessPriorityClass.Normal` |
| `idle` | `ProcessPriorityClass.Idle` |
| `belownormal` | `ProcessPriorityClass.BelowNormal` |
| `abovenormal` | `ProcessPriorityClass.AboveNormal` |
| `high` | `ProcessPriorityClass.High` |
| `realtime` | `ProcessPriorityClass.RealTime` |

```bash
dotnet run -- --priority high
```

`high` is the recommended value for dedicated benchmark hosts. `realtime` can starve the OS and is discouraged. See [Environment control](../features/environment-control.md) for the rationale and the `--dedicated-host-guidance` probe that suggests this flag.

Programmatic equivalent: `WithProcessPriority(ProcessPriorityClass.High)` (suite/harness).

---

### `--dedicated-host-guidance`

Emit a non-fatal pre-run warning when the host looks like a shared or otherwise noisy benchmark environment: a low CPU core count (typical of shared-tenant CI runners), an unraisable process priority, or (on macOS) unobservable frequency scaling and thermal throttling. On a suitable host (>= 4 cores, no priority set) the probe actively suggests `--priority high`. The run still proceeds - this is guidance, not a gate.

```bash
dotnet run -- --dedicated-host-guidance
```

Use it on CI runners and dev laptops to surface hidden noise sources before you trust a comparison. See [Environment control](../features/environment-control.md) for what the probe checks.

Programmatic equivalent: `WithDedicatedHostGuidance()` (suite/harness).

Related warning: NBenchmark also emits a one-time build-configuration warning when the entry assembly is Debug-built or a debugger is attached. There is no CLI flag for this warning; suppress it with `NBENCHMARK_SUPPRESS_DEBUG_WARNING=1` or `.WithSuppressBuildConfigurationWarning()` / `new MeasurementOptions { Environment = new EnvironmentOptions { SuppressBuildConfigurationWarning = true } }` when measuring Debug behavior intentionally.

---

### `--otlp-endpoint <url>`

Set the OTLP endpoint an OpenTelemetry SDK in the entry assembly should export to. The value must be an absolute `http://` or `https://` URL. The harness mirrors it into the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable before spawning isolated children, so children stream their telemetry to the same collector as the parent. When `OTEL_EXPORTER_OTLP_ENDPOINT` is already set in the environment, the explicit flag does not override it.

This is the cross-process channel for live telemetry: the in-memory `IMeasurementObserver` callback cannot cross the process boundary, so OTLP export is how isolated children stream live data to a collector. See [BCL instrumentation](bcl-instrumentation.md#cross-process-streaming) for the full topology and the env vars forwarded to children.

```bash
dotnet run -- --otlp-endpoint http://localhost:4317
dotnet run -- --otlp-endpoint https://collector.example.com:4318
```

---

### `--threshold-pct <n>`

Causes the run to fail with **exit code 1** if any benchmark regresses more than `n`% against the baseline. `n` must be a positive integer (1 or greater). The regression check compares median execution times: a benchmark is considered regressed if `candidateMedian / baselineMedian > 1.0 + (n / 100.0)`.

When launch aggregation is present (`LaunchStatistics`), `candidateMedian` and `baselineMedian` come from the cross-launch median (`LaunchMedian`) rather than a single launch median. Otherwise they come from `BenchmarkResult.Median`.

If the selected baseline median is `0`, ratio math is undefined. In that case, any non-baseline benchmark with a positive median is treated as regressed.

The baseline is the benchmark marked `[Benchmark(Baseline = true)]`, or the fastest benchmark (lowest median) if no baseline is explicitly set. Errored benchmarks are excluded from the check.

**Example:** `--threshold-pct 10` fails the run when any benchmark is more than 10% slower than the baseline.

---

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | The run completed. Errored benchmarks are recorded in the results but are not fatal and do not affect the exit code. |
| `1` | One or more argument errors were detected during parsing: unknown flag, missing flag value, value out of range (`--iterations`, `--warmup`, `--ops-per-sample`, `--launch-count`, `--ci-target`, `--min-samples`, `--max-samples`, `--min-warmup`, `--max-warmup`, `--max-tuning-time`, `--warmup-budget-fraction`, `--cap-grace-factor`, `--min-warmup-time`, `--jit-quiet-period`, `--min-measurement-time`, `--drift-tolerance`, `--max-drift-restarts`), invalid format (`--confidence`, `--seed`, `--percentiles`, `--cpu-affinity`), unknown preset (`--auto-tune`), unknown outlier mode (`--outlier`), unknown diagnostics mode (`--diagnostics`), unknown reporter name (`--reporter`), unknown observer name (`--observer`), unknown priority level (`--priority`), invalid detail level (`--detail`), invalid OTLP endpoint URL (`--otlp-endpoint`), or a benchmark exceeded the `--threshold-pct` regression limit. |

When exit code `1` is set during argument parsing, the run still completes (discovery, measurement, and reporting proceed). This lets you see output even after a misconfigured invocation - but the non-zero exit code ensures CI pipelines catch the problem. When exit code `1` is caused by a `--threshold-pct` regression, reporters still flush their output so you retain the evidence.

## Examples

```bash
# Run all benchmarks with 500 iterations, save to Markdown
dotnet run -- --iterations 500 --reporter markdown --output ./results

# Run only sorting benchmarks with 99% confidence interval
dotnet run -- --filter Sort* --confidence 0.99

# Reproducible run in declaration order
dotnet run -- --order declaration --seed 12345

# Run all benchmarks with 3 launches and view cross-launch aggregation
dotnet run -- --launch-count 3

# Pin to cores 2-3, raise priority, and warn if the host looks noisy
dotnet run -- --cpu-affinity 2,3 --priority high --dedicated-host-guidance

# Collect all diagnostics (GC counts, heap info, exceptions, CPU time)
dotnet run -- --diagnostics all --detail standard

# Stream live telemetry to a local OTLP collector (isolated children inherit the endpoint)
dotnet run -- --otlp-endpoint http://localhost:4317

# Check what will run before committing to a full benchmark
dotnet run -- --list
dotnet run -- --dry-run
```


---

# observers.md

---
title: Measurement Observer
description: Live-telemetry callback surface for streaming measurement events during benchmark execution.
order: 3
---

# Measurement Observer

The `IMeasurementObserver` interface provides a live-telemetry callback surface for streaming measurement events as a benchmark runs. It is **not** a replacement for `IBenchmarkProgress` or `IReporter` - it complements them with sample-level and phase-level telemetry at the engine level.

## Contract

```csharp
public interface IMeasurementObserver
{
    void OnPhase(in MeasurementPhaseEvent e);
    void OnSample(in SampleEvent e);
    void OnDetector(in DetectorStateEvent e);
    void OnResult(BenchmarkResult result);
}
```

All four methods are `void`-returning. The contract is **"return immediately, never block, never allocate on the hot path."** The observer must not throw - doing so is undefined behaviour (the engine does not catch observer exceptions on the hot path).

## Getting started

Attach an observer to a suite or harness:

```csharp
// Suite mode
var suite = new BenchmarkSuite("example")
    .Add("myBenchmark", () => Work())
    .WithObserver(myObserver);

// Harness mode
BenchmarkHarness.Create(args)
    .WithObserver(myObserver);
```

Or attach via `RunSpec.Observer` when using `BenchmarkRunner` directly:

```csharp
var runner = BenchmarkRunner.Instance;
var spec = new RunSpec { Observer = myObserver };
runner.Run("myBenchmark", () => Work(), spec);
```

In Harness mode, observers can also be activated from the CLI via `--observer <name>` (see [CLI Reference](cli.md#--observer-type)). The CLI resolves the name through `ObserverRegistry` - external packages self-register their observers via `[ModuleInitializer]`, the same pattern reporters use.

## Multiple observers

`WithObserver` is additive and repeatable: each call appends another observer rather than replacing the previous one. Multiple attached observers are composed into a `CompositeMeasurementObserver` fan-out so every observer receives every event.

```csharp
var suite = new BenchmarkSuite("example")
    .Add("myBenchmark", () => Work())
    .WithObserver(dashboardObserver)
    .WithObserver(loggingObserver);
```

The composite wraps each per-observer dispatch in a try/catch so one throwing observer cannot kill the stream for the others. The observer contract is "must not throw"; the try/catch is defence-in-depth that isolates a misbehaving observer rather than propagating. Exceptions are traced (via `System.Diagnostics.Trace`) so a host with a `TraceListener` attached can see why an observer stopped receiving events.

The same stacking applies to the CLI: `--observer live --observer logging` composes both observers into a single fan-out. The `--observer` flag is repeatable, mirroring `--reporter`.

## Default

If no observer is attached, `NullMeasurementObserver.Instance` (a no-op singleton) is used. When unattached, the engine performs a single reference comparison (`observer != NullMeasurementObserver.Instance`) per hot-path entry and skips all struct construction. This is the zero-cost fast path. An empty observer list (no `WithObserver` calls and no `--observer` flags) collapses to the null singleton via `ResolveObserver()`, so the hot-path guard stays false and the loop pays no dispatch cost.

## Event types

All event types are `readonly record struct` (stack-allocated, value equality).

### MeasurementPhaseEvent

```csharp
public readonly record struct MeasurementPhaseEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    PhaseTransition Transition,
    double? JitterMetric = null,
    bool DetectorSwitched = false,
    int? ResolvedK = null,
    int? ResolvedWarmup = null,
    WarmupStopReason? WarmupStop = null,
    SampleStopReason? SampleStop = null);
```

- `Phase`: one of `Jitter`, `Calibration`, `Warmup`, `Measurement`
- `Transition`: `Starting` or `Completed`
- `JitterMetric`: present only for Phase 0 `Completed` events (null otherwise)
- `DetectorSwitched`: meaningful only for Phase 0 `Completed` events (true = IQR -> MAD auto-switch)
- `ResolvedK`: set on `Calibration` completed; the calibrated ops-per-sample count (null when calibration was skipped)
- `ResolvedWarmup`: set on `Warmup` completed; the number of warmup iterations that ran (null when warmup was skipped)
- `WarmupStop`: set on `Warmup` completed; why warmup stopped (`ExplicitCount`, `Settled`, `MaxCeiling`, `WallClockCap`)
- `SampleStop`: set on `Measurement` completed; why measurement stopped (`ExplicitCount`, `CiTargetMet`, `MaxCeiling`, `WallClockCap`)

### SampleEvent

```csharp
public readonly record struct SampleEvent(
    string BenchmarkName,
    int Ordinal,
    double PerOpNs,
    int K,
    long AllocDelta,
    bool Warmup);
```

`Warmup` distinguishes calibration/warmup samples (`true`) from measured samples (`false`). In calibration phase, samples are emitted per-probe with the calibration `K` value. In warmup/measurement, samples are emitted with the resolved `K`.

### DetectorStateEvent

```csharp
public readonly record struct DetectorStateEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    int SampleCount,
    double Mean,
    double StdDev,
    double CiHalfWidth,
    int CurrentK);
```

Emitted after detector updates. During calibration, `Mean`/`StdDev`/`CiHalfWidth` reflect the calibrator's probe readings (the CI fields are not meaningful until measurement). During measurement, this event is emitted when the stop rule resolves (or at phase completion fallback) and `CiHalfWidth` provides the convergence signal.

### BenchmarkResult

The final result fires once per benchmark from `BenchmarkRunner.OnResult`. It contains the runner-assembled per-benchmark statistics and diagnostics (before any suite-level post-processing such as cross-benchmark significance grouping).

## Lifecycle of events

A typical benchmark with auto-warmup and auto-measurement emits events in this order:

1. `OnPhase(MeasurementPhase.Jitter, Starting)` - if jitter calibration is enabled
2. `OnPhase(MeasurementPhase.Jitter, Completed)` - with JitterMetric and DetectorSwitched
3. `OnPhase(MeasurementPhase.Calibration, Starting)` - if OpsPerSample is null (auto)
4. `OnSample(Warmup=true)` - one per calibration probe
5. `OnDetector(Calibration)` - after each calibration step (one calibrated `K` candidate)
6. `OnPhase(MeasurementPhase.Calibration, Completed)` - with ResolvedK
7. `OnPhase(MeasurementPhase.Warmup, Starting)`
8. `OnSample(Warmup=true)` - throttled per batch
9. `OnPhase(MeasurementPhase.Warmup, Completed)` - with WarmupStop and ResolvedWarmup
10. `OnPhase(MeasurementPhase.Measurement, Starting)`
11. `OnSample(Warmup=false)` - throttled per batch
12. `OnDetector(Measurement)` - when measurement resolves (CI target met or max ceiling)
13. `OnPhase(MeasurementPhase.Measurement, Completed)` - with SampleStop
14. `OnResult(result)` - final assembled BenchmarkResult

When `OpsPerSample` is pinned (calibration skipped) or `WarmupIterations=0` (warmup skipped), the corresponding phases are omitted.

## Throttling

Sample events are throttled by `ProgressCadence(n) = Math.Min(Math.Max(1, n / 20), 50)` where `n` is the current sample count. For 5 samples all emit; for 100,000 samples every 50th emits. This prevents the observer from dominating the hot path on long runs.

## Thread safety

All observer calls are made from the single measurement thread. Implementations are not required to be thread-safe. If a consumer needs cross-thread access, it must synchronise internally (e.g. via `Channel<T>` in Phase 2).

## Implementation guide

A custom observer that logs phase transitions and samples:

```csharp
using NBenchmark;

public class LoggingObserver : IMeasurementObserver
{
    public void OnPhase(in MeasurementPhaseEvent e)
    {
        Console.WriteLine($"[{e.BenchmarkName}] Phase {e.Phase} {e.Transition}");
    }

    public void OnSample(in SampleEvent e)
    {
        if (!e.Warmup)
            Console.WriteLine($"[{e.BenchmarkName}] Sample #{e.Ordinal}: {e.PerOpNs:F2} ns/op");
    }

    public void OnDetector(in DetectorStateEvent e)
    {
        Console.WriteLine($"[{e.BenchmarkName}] Detector [{e.Phase}]: " +
            $"n={e.SampleCount}, mean={e.Mean:F2}, ci%={e.CiHalfWidth * 100:F2}");
    }

    public void OnResult(BenchmarkResult result)
    {
        Console.WriteLine($"[{result.Name}] Mean: {result.Statistics.Mean}");
    }
}
```

**Important**: all four methods must return immediately and never allocate on the hot path. If you need to persist telemetry, buffer it (e.g. via a pre-allocated ring buffer or `System.Threading.Channels.Channel<T>` for Phase 2 back-pressure) and flush it asynchronously outside the observer call.

## See also

- `docs/reference/bcl-instrumentation.md` - the `System.Diagnostics` Meter/ActivitySource instrumentation.
- `docs/reference/configuration.md` - the `MeasurementOptions` surface.


---

# bcl-instrumentation.md

---
title: BCL Instrumentation
description: First-class System.Diagnostics Meter and ActivitySource instrumentation emitted during benchmark execution.
order: 4
---

# BCL Instrumentation (Meter + ActivitySource)

NBenchmark emits first-class `System.Diagnostics` BCL instrumentation from the same emit points that feed `IMeasurementObserver`. No NuGet packages are required -- `Meter` and `ActivitySource` are part of the .NET BCL since .NET 8. When no OpenTelemetry SDK or listener is attached, the BCL internal checks ensure near-zero overhead.

## Instrument naming

All instrument and tag names use the `nbenchmark.*` namespace for OpenTelemetry compatibility:

| Instrument | Type | Unit | Description |
| --- | --- | --- | --- |
| `nbenchmark.sample.duration` | Histogram | ns/op | Per-op sample duration |
| `nbenchmark.alloc.bytes_per_op` | Histogram | B/op | Per-op allocation delta (recorded per sample) |
| `nbenchmark.outliers.removed` | Counter | samples | Cumulative outliers removed |
| `nbenchmark.outliers.removed_total` | ObservableGauge | samples | Running total of removed outliers |
| `nbenchmark.jitter.detector_switches` | Counter | switches | Outlier-detector auto-switches triggered by jitter |
| `nbenchmark.gc.gen0` | Counter | collections | Generation 0 GC collections during measurement |
| `nbenchmark.gc.gen1` | Counter | collections | Generation 1 GC collections during measurement |
| `nbenchmark.gc.gen2` | Counter | collections | Generation 2 GC collections during measurement |
| `nbenchmark.ci.relative_half_width` | ObservableGauge | ratio | CI relative half-width of the running mean |
| `nbenchmark.jitter.metric` | ObservableGauge | ratio | Host jitter metric (MAD / median) |
| `nbenchmark.sample.mean_per_op` | ObservableGauge | ns/op | Running mean per-op duration |
| `nbenchmark.ops_per_second` | ObservableGauge | ops/s | Running operations per second (1e9 / mean per-op ns) |
| `nbenchmark.samples.count` | ObservableGauge | samples | Running sample count |

## Trace span hierarchy

NBenchmark emits nested `Activity` spans that render the autotune lifecycle as a flame-graph-shaped trace:

```
benchmark.suite
  └── benchmark.run
        ├── nbenchmark.phase.jitter
        ├── nbenchmark.phase.calibration
        ├── nbenchmark.phase.warmup
        └── nbenchmark.phase.measurement
```

- `benchmark.suite` (root): created at `OnSuiteStarting`, tags include `nbenchmark.suite.name`, `nbenchmark.suite.benchmark_count`, `nbenchmark.profile`, `nbenchmark.runtime`, `nbenchmark.seed`, `nbenchmark.run_order`; stopped at `OnSuiteCompleted` with `nbenchmark.suite.result_count`.
- `benchmark.run` (per-benchmark): created at `OnBenchmarkRunStarting`, tags include `nbenchmark.name`, `nbenchmark.class`, `nbenchmark.baseline`, `nbenchmark.parameter_set`; stopped at `OnBenchmarkRunCompleted` with `nbenchmark.result.median_ns`, `nbenchmark.result.mean_ns`, `nbenchmark.result.sample_count`, `nbenchmark.result.outliers_removed`.

### Phase spans

Each phase transition creates an Activity span named `nbenchmark.phase.<phase>` where `<phase>` is one of `jitter`, `calibration`, `warmup`, or `measurement`. Phase spans nest under their parent `benchmark.run` span. Tags include:

| Tag | Set on | Value |
| --- | --- | --- |
| `nbenchmark.benchmark.name` | start + stop | Benchmark name |
| `nbenchmark.phase` | start + stop | Phase enum name |
| `nbenchmark.sample_stop_reason` | stop (measurement) | Why measurement ended |
| `nbenchmark.warmup_stop_reason` | stop (warmup) | Why warmup ended |
| `nbenchmark.resolved_k` | stop (calibration) | Calibrated ops-per-sample count |
| `nbenchmark.resolved_warmup` | stop (warmup) | Resolved warmup iteration count |
| `nbenchmark.jitter_metric` | stop (jitter) | Host jitter metric value |
| `nbenchmark.detector_switched` | stop (jitter) | Whether the outlier detector was auto-switched |

### Span events

Span events are discrete annotations on a phase span that explain *why* a phase ended. A trace UI renders these as markers on the flame-graph row, making the autotune decision visible at a glance:

| Event | Parent span | Fired when | Key tags |
| --- | --- | --- | --- |
| `detector.switched` | `nbenchmark.phase.jitter` | The outlier detector auto-switched IQR -> MAD | `nbenchmark.from`, `nbenchmark.to`, `nbenchmark.jitter_metric` |
| `warmup.plateau_reached` | `nbenchmark.phase.warmup` | Warmup stopped because the body settled (plateau rule) | - |
| `measurement.ci_target_met` | `nbenchmark.phase.measurement` | Measurement stopped because the CI half-width target was met | `nbenchmark.achieved_ci_width`, `nbenchmark.ci_target` |
| `phase.cap_hit` | `nbenchmark.phase.warmup` / `nbenchmark.phase.measurement` | A phase ended early at the wall-clock tuning cap | - |

## Getting started with OpenTelemetry

Install the OpenTelemetry SDK and the OTLP exporter:

```bash
dotnet add package OpenTelemetry
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
```

Then configure in your application:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddMeter("NBenchmark")
    .AddOtlpExporter()
    .Build();

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("NBenchmark")
    .AddOtlpExporter()
    .Build();
```

All NBenchmark instruments are automatically picked up when a Meter named `NBenchmark` or an ActivitySource named `NBenchmark` is subscribed to.

## Resource attributes

Every `benchmark.suite` span is stamped with resource attributes that identify the run across commit, branch, CI pipeline, and machine. A downstream backend (Grafana, Jaeger, Honeycomb) can join on these to render cross-commit trend lines and regression alarms without NBenchmark shipping its own storage layer.

The attributes are read once per process from environment variables and cached for the process lifetime.

### CI identification

| Attribute | Source env vars |
| --- | --- |
| `nbenchmark.ci_provider` | `GITHUB_ACTIONS`, `GITLAB_CI`, `AZURE_PIPELINES`/`TF_BUILD`, `CIRCLECI`, `APPVEYOR`, `TEAMCITY_VERSION`, `JENKINS_URL`, `TRAVIS`, `BUILDKITE` |
| `nbenchmark.ci_run_id` | `GITHUB_RUN_ID`, `CI_PIPELINE_ID`, `BUILD_BUILDID`, `CIRCLE_BUILD_NUM`, `APPVEYOR_BUILD_ID`, `TEAMCITY_BUILDID`, `BUILDKITE_BUILD_ID`, `TRAVIS_BUILD_ID` |
| `nbenchmark.ci_run_url` | `GITHUB_SERVER_URL`, `CI_JOB_URL`, `BUILD_BUILDURI`, `CIRCLE_BUILD_URL` |
| `nbenchmark.ci_repository` | `GITHUB_REPOSITORY`, `CI_REPOSITORY_URL` |
| `nbenchmark.ci_ref` | `GITHUB_REF`, `CI_COMMIT_REF_NAME` |
| `nbenchmark.ci_attempt` | `GITHUB_RUN_ATTEMPT` |

### Git identification

| Attribute | Source env vars | Fallback |
| --- | --- | --- |
| `nbenchmark.commit_sha` | `GITHUB_SHA`, `CI_COMMIT_SHA`, `GIT_COMMIT` | `git rev-parse --short HEAD` |
| `nbenchmark.branch` | `GITHUB_HEAD_REF`, `CI_COMMIT_BRANCH`, `GIT_BRANCH` | `git rev-parse --abbrev-ref HEAD` (detached HEAD produces no branch attribute) |

CI-sourced values take precedence over the git CLI fallback. When no CI or git env vars are present and the git CLI is unavailable (or outside a repo), the commit and branch attributes are omitted.

### Host identification

| Attribute | Value |
| --- | --- |
| `nbenchmark.host.machine_name` | `Environment.MachineName` |
| `nbenchmark.host.os` | `windows`, `macos`, or `linux` |
| `nbenchmark.host.arch` | `arm64`, `x64`, `x86`, etc. |
| `nbenchmark.host.runtime` | `RuntimeInformation.FrameworkDescription` (e.g. `.NET 8.0.22`) |

### OpenTelemetry-standard env vars

`OTEL_RESOURCE_ATTRIBUTES` and `OTEL_SERVICE_NAME` are honoured verbatim. `OTEL_RESOURCE_ATTRIBUTES` is parsed as a comma-separated `key=value` list (the OTel convention) and each pair is copied onto the span. `OTEL_SERVICE_NAME` is mapped to `service.name`. A user who has already configured these for the rest of their service does not need to repeat themselves.

## Cross-process streaming

Harness mode runs each discovered class in an isolated child process by default. The in-memory `IMeasurementObserver` callback cannot cross the process boundary, so OTLP export is the cross-process channel: instrument the child, point its exporter at a collector, and live telemetry crosses the process boundary cleanly.

### Env-var forwarding

`ChildProcessLauncher` forwards the following environment variables from parent to every spawned child:

| Env var | Purpose |
| --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP exporter endpoint |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP transport (`grpc` or `http/protobuf`) |
| `OTEL_EXPORTER_OTLP_HEADERS` | OTLP exporter headers (e.g. auth) |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | OTLP export timeout |
| `OTEL_RESOURCE_ATTRIBUTES` | Resource attributes (passed through) |
| `OTEL_SERVICE_NAME` | Service name (passed through) |
| `NBENCHMARK_OTEL_ENDPOINT` | NBenchmark-specific endpoint mirror (see `--otlp-endpoint` CLI flag) |

When `NBENCHMARK_OTEL_ENDPOINT` is set and `OTEL_EXPORTER_OTLP_ENDPOINT` is not, the launcher mirrors it into `OTEL_EXPORTER_OTLP_ENDPOINT` so an SDK wired only against the standard variable picks it up without extra configuration.

### `--otlp-endpoint` CLI flag

```bash
dotnet run -- --otlp-endpoint http://localhost:4317
```

The harness mirrors this into `OTEL_EXPORTER_OTLP_ENDPOINT` before spawning isolated children, so children stream to the same collector as the parent. When the user has already set `OTEL_EXPORTER_OTLP_ENDPOINT` explicitly, the CLI flag does not override it.

### Observer forwarding

When `--observer <name>` is supplied, the parent forwards the observer names to isolated children via the `IsolatedRunRequest.ObserverNames` field. The child resolves each name through `ObserverRegistry` (populated identically by `[ModuleInitializer]` self-registration in the child's fresh process), so the same observers fire in the child as in the parent. Programmatic observers added via `WithObserver(IMeasurementObserver)` are live objects and cannot cross a process boundary; only registry-resolvable names are forwarded.

### Topology

```
In-process / local dev:
  AdaptiveLoop -> Observer shim -> Embedded web host -> React SPA in browser

Isolated / CI:
  Child process -> OTLP -> Collector
  Host process  -> OTLP -> Collector
  Collector -> Grafana / Jaeger / Honeycomb
```

In-process and isolated runs look identical to the dashboard: both are OTLP producers.

## See also

- `docs/reference/observers.md` - the `IMeasurementObserver` interface and event types.
- `docs/reference/cli.md` - the `--otlp-endpoint` CLI flag.
- `docs/statistics/diagnostics.md` - runtime diagnostics counters (GC, heap, exceptions, CPU).


---

# index.md

---
title: Reference
description: Configuration, CLI flags, and compile-time diagnostics.
order: 8
---

# Reference

Technical reference for NBenchmark.

## In this section

- **[Configuration](./configuration.md)** - task-based guides and the full MeasurementOptions reference.
- **[CLI Reference](./cli.md)** - all command-line flags accepted by `BenchmarkHarness`.
- **[Analyzers](./analyzers.md)** - compile-time Roslyn diagnostics (NB0001-NB0013).
- **[Measurement Observer](./observers.md)** - live-telemetry callback surface for streaming measurement events.


---

# configuration.md

---
title: Configuration
description: Task-based configuration guides and the full MeasurementOptions reference.
order: 0
---

# Configuration

## Guides

Task-based configuration guides for common benchmarking situations. Each guide shows the code and CLI equivalent, explains why the settings work together, and links to the property definitions below.

---

### Tuning for noisy CI environments

**When to use:** Your benchmark runs on a shared CI runner (GitHub Actions, Azure Pipelines, etc.) where CPU cycles, memory bandwidth, and scheduler time are shared with other containers. Results are noisy and comparisons are unreliable.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Environment.ProcessPriority = High` | Reduces preemption by unrelated OS work. The benchmark thread is less likely to be paused mid-sample. |
| `OutlierMode.MedianAbsoluteDeviation` | More robust than the default IQR fence when a heavy tail of preempted samples distorts the quartile-based fence. MAD has a 50% breakdown point. |
| `LaunchCount = 3` | Runs the benchmark 3 times as independent launches. The best (lowest-median) launch is reported, giving you a second layer of noise rejection. |
| `AutoTune.CapBehavior = Error` | If the wall-clock cap is hit before the CI target is met, the benchmark errors instead of silently reporting a wide interval. |

**Fluent API:**

```csharp
await new BenchmarkSuite("ci-suite")
    .Add("myBenchmark", () => MyMethod())
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithLaunchCount(3)
    .WithAutoTune(new AutoTuneOptions { CapBehavior = CapBehavior.Error })
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --priority high --outlier mad --launch-count 3 --autotune-cap-behavior error
```

**See also:**

- [Environment](#environment)
- [OutlierMode](#outliermode)
- [LaunchCount](#launchcount)
- [AutoTune](#autotune)
- [Environment Control](../features/environment-control.md)

---

### Fast feedback during development

**When to use:** You are iterating on code and need a quick signal - seconds, not minutes. Precision is less important than turnaround time.

**What to combine:**

| Setting | Why |
| --- | --- |
| `AutoTune = AutoTuneOptions.Quick` | Lowers the CI target to ±5%, reduces minimum samples to 15, and minimum warmup to 4. |
| `WarmupIterations = 4` | Pins a short warmup instead of auto-detecting. |
| `Iterations = 20` | Pins a small measured sample count. |
| `ConfidenceLevel = 0.90` | A 90% CI is narrower and requires fewer samples to satisfy. |

**Fluent API:**

```csharp
await new BenchmarkSuite("fast-feedback")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTunePreset.Quick)
    .WithWarmup(4)
    .WithIterations(20)
    .WithConfidenceLevel(0.90)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --auto-tune quick --warmup 4 --iterations 20 --confidence 0.90
```

**See also:**

- [AutoTune](#autotune)
- [WarmupIterations](#warmupiterations)
- [Iterations](#iterations)
- [ConfidenceLevel](#confidencelevel)

---

### Publication-grade precision

**When to use:** You are publishing benchmark results, comparing across commits in a blog post, or establishing a baseline that others will rely on. Accuracy matters more than run time.

**What to combine:**

| Setting | Why |
| --- | --- |
| `AutoTune = AutoTuneOptions.Thorough` | Raises the CI target to ±1%, minimum samples to 100, and minimum warmup to 16. |
| `ConfidenceLevel = 0.99` | A 99% CI is wider and more conservative. |
| `LaunchCount = 5` | Multiple independent launches let you report cross-launch statistics and the best representative run. |
| `EnableHistogram = true` | The latency histogram gives you the full distribution, not just summary statistics. |

**Fluent API:**

```csharp
await new BenchmarkSuite("publication")
    .Add("myBenchmark", () => MyMethod())
    .WithAutoTune(AutoTunePreset.Thorough)
    .WithConfidenceLevel(0.99)
    .WithLaunchCount(5)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --auto-tune thorough --confidence 0.99 --launch-count 5
```

**See also:**

- [AutoTune](#autotune)
- [ConfidenceLevel](#confidencelevel)
- [LaunchCount](#launchcount)
- [EnableHistogram](#enablehistogram)

---

### Pure CPU measurement

**When to use:** You want to measure CPU time only, excluding GC pressure, allocation overhead, and cache effects. Suitable for cryptographic algorithms, numeric kernels, and other CPU-bound work.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Profile = Independent` | Forces Gen0 GC before every iteration, full GC between benchmarks, and disables allocation tracking. |
| `OpsPerSample = 1` | Each sample is a single invocation. Calibration is skipped when per-iteration GC is on, so K stays 1 by default - pin it explicitly if you want a different value. |

**Fluent API:**

```csharp
await new BenchmarkSuite("cpu-only")
    .Add("myBenchmark", () => MyMethod())
    .WithMeasurementProfile(MeasurementProfile.Independent)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --profile independent
```

**See also:**

- [Profile](#profile)
- [ForceGcBeforeEachIteration](#forcegcbeforeeachiteration)
- [MeasureAllocations](#measureallocations)
- [Measurement Profiles](../statistics/measurement.md#measurement-profiles)

---

### Debugging unstable results

**When to use:** Your benchmark produces wildly different numbers across runs, the Error column is large, or you see a bimodal-distribution warning and want to understand why.

**What to combine:**

| Setting | Why |
| --- | --- |
| `Diagnostics = DiagnosticsOptions.All` | Enables GC collection counts, heap info, exception tracking, and CPU time. Lets you correlate timing spikes with GC pauses or CPU throttling. |
| `OutlierMode = MedianAbsoluteDeviation` | More robust to heavy-tailed distributions. If the default IQR fence is being distorted by a long tail, MAD gives a clearer picture. |
| `Detail = Advanced` | Shows the auto-tune diagnostic line (K, warmup, samples, CI half-width, jitter metric) and the outlier fence values. |

**Fluent API:**

```csharp
await new BenchmarkSuite("debug")
    .Add("myBenchmark", () => MyMethod())
    .WithDiagnostics(DiagnosticsMode.All)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .RunAsync();
```

**CLI:**

```bash
dotnet run -- --diagnostics all --outlier mad --detail advanced
```

**What to look for:**

- High jitter metric (> 0.10) in the auto-tune diagnostic: the host is noisy. Consider [environment controls](../features/environment-control.md).
- GC collection counts that correlate with slow samples: GC pressure is affecting your timings. Try `--profile independent`.
- A bimodal-distribution warning: investigate the cause (lock contention, cache misses, GC pauses) rather than silencing it.

**See also:**

- [Diagnostics](#diagnostics)
- [OutlierMode](#outliermode)
- [Reading Your Results](../output/reading-your-results.md)
- [Troubleshooting](../troubleshooting.md)

---

## Reference

All measurement settings are controlled by `MeasurementOptions`. The defaults are sensible for most benchmarks - only change what you have a reason to change.

### Using MeasurementOptions

#### With Benchmark (Single mode)

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
};

var result = Benchmark.Run(() => MyMethod(), options: options);
```

#### With BenchmarkSuite (Suite mode)

Use the fluent `With*` methods - they each update a single option:

```csharp
await new BenchmarkSuite("name")
    .WithIterations(500)
    .WithWarmup(50)
    .WithAllocations()
    .WithOutlierMode(OutlierMode.IqrFence)
    .WithConfidenceLevel(0.99)
    .RunAsync();
```

#### With BenchmarkHarness (Harness mode)

Call `WithOptions` or use CLI flags. CLI flags always take priority over `WithOptions`:

```csharp
BenchmarkHarness.Create(args)
    .WithOptions(new MeasurementOptions { Iterations = 500 })
    ...
```

```bash
dotnet run -- --iterations 500 --warmup 50
```

### Options reference

### Iterations

```csharp
Iterations = null   // default - auto-resolved from a CI-width target
```

The number of measured samples per benchmark, typed as `int?`:

| Value | Behaviour |
| --- | --- |
| `null` **(default)** | Auto-resolved. NBenchmark streams samples until the confidence interval on the mean is tight enough (the `AutoTune.CiTarget` half-width), bounded by `AutoTune.MinSamples` and `AutoTune.MaxSamples`. |
| `0` | Dry-run. The body is not invoked and no measurements are taken. See [CLI Reference: `--dry-run`](./cli.md#--dry-run). |
| `> 0` | Pins an exact measured-sample count, disabling auto-sampling. Valid range: `1` to `100 000`. |

Pinning an exact count makes a run deterministic in sample size - useful for reproducible CI gates. Leaving it `null` lets each benchmark collect exactly as many samples as it needs to hit the precision target and no more.

> [!TIP]
> In auto mode a large Error resolves itself: NBenchmark keeps sampling until the interval is tight. To demand tighter intervals, lower `AutoTune.CiTarget` (or use the `Thorough` preset). To cap a long run, lower `AutoTune.MaxSamples` or `AutoTune.MaxTuningTime`.

CLI flag: `--iterations <n>` (pins the count). The auto-mode bounds map to `--ci-target`, `--min-samples`, and `--max-samples`.

### WarmupIterations

```csharp
WarmupIterations = null   // default - auto-detected with a plateau rule
```

The number of warmup samples discarded before measurement begins, typed as `int?`:

| Value | Behaviour |
| --- | --- |
| `null` **(default)** | Auto-detected. NBenchmark watches the per-sample timings and stops warmup once they plateau (stop improving), bounded by `AutoTune.MinWarmup` and `AutoTune.MaxWarmup`. |
| `0` | Skips warmup entirely - the first measured sample includes any cold-start cost. |
| `> 0` | Pins an exact warmup count. Valid range: `1` to `10 000`. |

Warmup lets the JIT compiler optimise your code and brings data into CPU caches. The plateau rule spends just enough warmup to reach steady state instead of a fixed budget. See [Key Concepts: Warmup](../getting-started/key-concepts.md#warmup) for more detail.

CLI flag: `--warmup <n>` (pins the count). The auto-mode bounds map to `--min-warmup` and `--max-warmup`.

### OpsPerSample

```csharp
OpsPerSample = null   // default - auto-calibrated (K)
```

The number of back-to-back body invocations timed together as one sample, called **K**. Typed as `int?`:

| Value | Behaviour |
|---|---|
| `null` **(default)** | Auto-calibrated. NBenchmark doubles K until one sample spans roughly `AutoTune.TargetSampleDurationNs` (1 µs by default), so a single timer read covers enough work to be meaningful. Reported per-op timings divide the batch time by K. |
| `> 0` | Pins an exact K (always honoured). Valid range: `1` to `16 777 216`. |

Calibration matters for **fast bodies**: a method that runs in a few nanoseconds is dominated by the cost of reading the timer. Timing K invocations as a batch amortises that fixed overhead, then NBenchmark divides back down to a per-operation number.

Auto-calibration is skipped (K stays `1`) when per-iteration `IterationSetup`/`IterationTeardown` is configured, since that makes a K-batch unrepresentative of a single call. It is **not** skipped under the `Independent` profile: the forced Gen0 GC runs once per sample (K-batch), before the timestamp and outside the timed window — the same semantics a pinned `OpsPerSample` already gets — so a nano-scale CPU body still amortises timer overhead. (When `Independent` bodies allocate and `K > 1`, a warning notes a GC may land inside a timed batch; pin `--ops-per-sample 1` to avoid it.) An explicit `OpsPerSample` is always honoured.

BenchmarkSuite/BenchmarkHarness fluent method: `.WithOpsPerSample(64)`  
CLI flag: `--ops-per-sample <n>` (pins K). The calibration target is `AutoTune.TargetSampleDurationNs`.

Unlike `Iterations` and `WarmupIterations`, `OpsPerSample` cannot be pinned per method via `[Benchmark]` - it is set suite- or harness-wide only (`.WithOpsPerSample(n)` or `--ops-per-sample n`).

### LaunchCount

```csharp
LaunchCount = 1   // default
```

The number of times to repeat each benchmark as a separate launch, typed as `int`:

`MeasurementOptions` default is `1`; Harness mode applies `3` by default when launch count is not explicitly pinned via `WithLaunchCount`, `WithOptions`, `--launch-count`, or `[Benchmark(LaunchCount = ...)]`.

| Value | Behaviour |
|---|---|
| `1` **(default)** | Run the benchmark once. No aggregation. |
| `> 1` | Repeat the full benchmark (warmup + measurement) N times. Cross-launch statistics (mean, stddev, median, CI across launch medians) are computed and stored in `BenchmarkResult.LaunchStatistics`. The primary result fields reflect the **best** launch (lowest median). Valid range: `2` to `100`. |

Use multiple launches when single-run noise is a concern and you want to see how much the median itself varies across independent measurements. Each launch includes its own warmup and GC cycle, so consecutive launches are independent measurements of the same body - not correlated samples.

**Dry-run interaction:** When `--dry-run` (Iterations=0, WarmupIterations=0) is combined with `LaunchCount > 1`, exactly one dry launch is performed. The extra launches would not add information since dry runs skip the body.

**Isolation interaction:** When the benchmark runs in a child process (Harness mode default, or `WithIsolation()` in suite mode), the parent spawns N children. The child process is unaware of the launch count.

**Attribute override:** In Harness mode each `[Benchmark]` can override the launch count per-method:

```csharp
// This method runs 5 independent launches regardless of the host setting.
[Benchmark(LaunchCount = 5)]
public void MyNoisyBenchmark() => SlowOperation();
```

The CLI flag `--launch-count` always takes priority over both `WithOptions` and the per-method attribute.

BenchmarkSuite fluent method: `.WithLaunchCount(5)`  
CLI flag: `--launch-count <n>`

### AutoTune

```csharp
AutoTune = AutoTuneOptions.Default   // default
```

Bounds and steers the adaptive measurement loop - the warmup plateau rule, the CI-width sample-count rule, and ops-per-sample calibration. Three named presets trade measurement time for precision:

| Preset | MinWarmup | MinSamples | MaxSamples | CiTarget | MinWarmupTime | MinMeasurementTime | Use it for |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `AutoTuneOptions.Quick` | 4 | 15 | 2 000 | 0.05 (±5%) | **500 ms** | 50 ms | Fast inner-loop feedback. |
| `AutoTuneOptions.Default` | 8 | 30 | 5 000 | 0.025 (±2.5%) | 500 ms | 100 ms | The balanced default. |
| `AutoTuneOptions.Thorough` | 16 | 100 | 20 000 | 0.01 (±1%) | 1 s | 500 ms | Publication-grade numbers. |

> `Quick` deliberately does **not** shorten `MinWarmupTime`. That floor is a measurement-correctness requirement, not a speed/accuracy trade-off: too short a warmup does not give you a rougher number, it gives you a confidently wrong one — a body measured on tier-0 code can report a median several times off with a ±1% error bar, and it will not reproduce between runs. `Quick` gets its speed from `CiTarget`, `MinSamples`, and `MaxTuningTime`.

Pick a preset with `.WithAutoTune(AutoTunePreset.Thorough)` (suite/harness) or `--auto-tune thorough` on the CLI, or build your own `AutoTuneOptions` record. The individual knobs:

| Knob | Default | Meaning |
| --- | --- | --- |
| `MinWarmup` / `MaxWarmup` | `8` / `100 000` | Floor and ceiling for auto-detected warmup length, as sample counts. `MaxWarmup` is deliberately far above what any body needs so that the *time* bounds bind instead: a fast body needs ~25 000 samples to accumulate `MinWarmupTime` at the 10 µs sample target, and a count ceiling binding first would silently defeat that floor. (The tighter `10 000` limit still applies to a *pinned* `WarmupIterations`.) |
| `WarmupEpsilon` | `0.02` | Minimum relative improvement a warmup batch must show to count as "still warming up". |
| `PlateauPatience` | `3` | Consecutive non-improving batches that end warmup. |
| `MinWarmupTime` | `500 ms` | Minimum in-body time auto-warmup must accumulate before it may settle, so background tiered JIT (tier-0 → tier-1 → dynamic PGO) lands before measurement rather than mid-run. 5× the runtime's `TieredCompilation.CallCountingDelayMs` (100 ms) — that delay restarts while tier-0 methods are still being first-called and tier-1 is only *queued* when it expires, so a floor at or below 100 ms reliably lands the tier-up inside measurement. In practice this is the binding constraint on warmup length for almost every body. Bounded above by the calibration+warmup budget share and `MaxWarmup`. `0` disables the floor (and the JIT-quiescence gate). `Thorough` uses 1 s; `Quick` inherits 500 ms. Chosen empirically: at 250 ms a `StringBuilder`-append loop still landed in either its tier-0 or its ~4.5× faster steady state depending on the run (4.8× run-to-run spread); 500 ms cost 55% more wall-clock and made it consistent, while 1 s cost a further 76% for one more benchmark. |
| `RequireJitQuiescence` | `true` | Whether auto-warmup also refuses to settle until the JIT has been quiet for `JitQuietPeriod`, read from `System.Runtime.JitInfo` at each batch boundary. Deactivates once warmup has run 4 × `MinWarmupTime` so a busy in-process host cannot block warmup forever; inactive when `MinWarmupTime = 0`. |
| `JitQuietPeriod` | `50 ms` | How long the JIT compiled-method count must stay unchanged before the quiescence gate opens. A *sustained* interval is required because a per-batch check cannot work: one batch of a fast body spans tens of microseconds, so a background compilation almost never lands inside it and a per-batch delta reads zero essentially always. Clamped down to `MinWarmupTime` so it never becomes the binding floor. `0` disables the gate. `Thorough` uses 100 ms. |
| `MinSamples` / `MaxSamples` | `30` / `5 000` | Floor and ceiling for the auto-resolved measured-sample count. `MinSamples` is the *validity* floor (below it the interval is untrustworthy however narrow) and is also what the `CapGraceFactor` grace budget chases. `MaxSamples` was formerly 100 000: at 5 000 the CI rule still reaches ±2.5% for a coefficient of variation up to ~90%, and past that the required count grows as `(t × CV / target)²` and runs away — a CV of 580% needs ~50 000 samples for ±5% — where the variance *is* the finding rather than something more samples fix. |
| `CiTarget` | `0.025` | Target relative half-width of the confidence interval; sampling stops once it is met. |
| `MinMeasurementTime` | `100 ms` | Minimum in-body time the measurement phase must span before it may stop on the CI target — the measurement analogue of `MinWarmupTime`, and what makes the sample count scale with body speed instead of being a flat number. A cheap body collects hundreds or thousands of samples for milliseconds of extra work, which is what makes its percentiles and significance test meaningful (at n ≈ 16 the reported P95/P99/P99.9 all collapse onto the maximum). The rule is: measurement spans at least this long, or reaches `MaxSamples` samples, whichever comes first — so worst-case added cost is `MinMeasurementTime` per benchmark and **zero** for any body already slower than `MinMeasurementTime / MinSamples` (≈3.3 ms by default). `0` disables the floor. `Quick` uses 50 ms, `Thorough` 500 ms. |
| `MeasurementDriftTolerance` | `0.10` | How far the first-half and second-half means of the measured samples may disagree (as a fraction of the smaller half-mean) before the CI stop is refused. Guards the failure mode that is hardest to spot: a JIT tier-up landing inside the measurement window is a step change, and the CI-on-the-mean rule will report a tight interval straight across it — a 10× wrong number with a ±0.9% error bar. The gap must also exceed 4 standard errors, so a heavy-tailed body whose half-means differ by pure noise is not flagged. `0` disables the gate; either way `AutoTune.SplitHalfDrift` records the gap. |
| `MeasurementRestartLimit` | `2` | How many times the drift gate may discard the collected samples and restart measurement — one for tier-0 → tier-1, one for instrumented → optimized under dynamic PGO. Restarts draw on the same `MaxTuningTime` budget as ordinary sampling, so they can never make a benchmark run longer. A body still drifting after the limit reports `SampleStopReason.DriftUnresolved` with a warning, which is a finding rather than something more restarts fix. `Thorough` uses 3. |
| `TargetSampleDurationNs` | `10 000` | Per-sample duration that ops-per-sample calibration aims for. 10 µs keeps timer quantization (~0.1% vs ~±10% at 1 µs on a 100 ns timer) and timestamp-read overhead (~0.2% vs ~1-3%) negligible against the CI target. Bodies ≥ 10 µs keep K = 1; sub-10 µs bodies are batched, so their percentiles describe batch means. `Thorough` preset uses 50 µs. |
| `MaxOpsPerSample` | `1 048 576` | Ceiling on auto-calibrated K. |
| `BatchSize` | `8` | Warmup batch size and the cadence on which the CI-width rule is evaluated. |
| `MaxTuningTime` | `20 s` | Per-benchmark safety cap on cumulative in-body sample time (calibration + warmup + measurement). Setup, teardown, and GC are excluded, so real wall-clock can exceed it. |
| `WarmupBudgetFraction` | `0.4` | Max share of `MaxTuningTime` that calibration (Phase A) and warmup (Phase B) may consume together. Once the share is exhausted, each phase stops at the wall-clock cap and the loop moves on, reserving the remainder for measurement. Must be in `(0, 1]`. |
| `CapGraceFactor` | `1.5` | Hard ceiling multiplier the measurement phase may reach while chasing `MinSamples` after the `MaxTuningTime` cap fires. When the cap fires before `MinSamples` is reached, the loop keeps sampling up to `MaxTuningTime × CapGraceFactor` so the reported statistics have enough samples to be meaningful (a one-sample result reports StdDev = 0 and MarginOfError = 0 - dangerously clean-looking). Must be at least 1; set to 1 to disable the grace path. `CapBehavior = Error` users are unaffected - the error fires at the base cap either way. |
| `CapBehavior` | `Warn` | What happens when `MaxTuningTime` is reached before the CI target or warmup plateau is reached. `Warn` emits a warning; `Error` marks the benchmark as errored. |
| `EnableJitterCalibration` | `true` | Whether the pre-flight jitter probe runs. When `false`, the jitter metric is `null` and the outlier detector is never auto-switched. |
| `JitterCalibrationSamples` | `32` | Number of timed samples the jitter probe collects. |
| `JitterCalibrationWorkPerSample` | `4096` | Number of deterministic arithmetic iterations each jitter sample performs. |
| `JitterAutoSwitchThreshold` | `0.10` | Jitter metric value above which the outlier detector auto-switches from IQR fence to MAD. Set to `0` to disable the auto-switch while keeping the probe. |

The interval's confidence level is `ConfidenceLevel` (below) - the CI-width rule targets that same level, so it is not duplicated on `AutoTune`.

BenchmarkSuite/BenchmarkHarness fluent method: `.WithAutoTune(AutoTunePreset.Quick)` or `.WithAutoTune(customOptions)`  
CLI flags: `--auto-tune <default|quick|thorough>`, plus `--ci-target`, `--min-samples`, `--max-samples`, `--min-warmup`, `--max-warmup`, `--max-tuning-time`, `--autotune-cap-behavior`, `--warmup-budget-fraction`, `--cap-grace-factor`, `--min-warmup-time`, `--no-jit-quiescence`, `--jit-quiet-period`, `--min-measurement-time`, `--drift-tolerance`, `--max-drift-restarts`.

### Profile

```csharp
Profile = MeasurementProfile.Realistic   // default
```

The measurement profile is the authoritative setting behind two GC behaviours: the per-iteration Gen0 GC and the pre-measurement full GC. The resolved booleans (`ForceGcBeforeEachIteration`, `ForceGcBeforeMeasurement`) are computed from `Profile` unless explicitly overridden via the corresponding `*Override` field. Two related behaviours are on for **both** profiles: `ForceGcBetweenBenchmarks` (so one benchmark cannot bias the next) and `MeasureAllocations` (measured outside the timed window, so it is free).

| Profile | ForceGcBeforeEachIteration | ForceGcBeforeMeasurement | ForceGcBetweenBenchmarks | MeasureAllocations |
| --- | --- | --- | --- | --- |
| `Realistic` (default) | `false` | `false` | `true` | `true` |
| `Independent` | `true` | `true` | `true` | `true` |

Each resolved boolean can be overridden individually:

```csharp
// Enable per-iteration GC under Realistic
options with { ForceGcBeforeEachIterationOverride = true }

// Inherit the warmup heap under Independent (skip the pre-measurement GC)
options with { ForceGcBeforeMeasurementOverride = false }

// Disable the between-benchmark GC (both profiles)
options with { ForceGcBetweenBenchmarksOverride = false }
```

BenchmarkHarness fluent method: `.WithMeasurementProfile(MeasurementProfile.Independent)`
BenchmarkSuite fluent method: `.WithMeasurementProfile(MeasurementProfile.Independent)`
CLI flag: `--profile independent`

### RuntimeProfile

```csharp
RuntimeProfile = RuntimeProfile.SteadyState   // default
```

The runtime-startup configuration a benchmark is measured under: JIT tiering, dynamic PGO, ReadyToRun, and GC flavour. Distinct from `Profile` above, which controls GC behaviour *during* a run.

| Profile | Configuration | Use for |
| --- | --- | --- |
| `RuntimeProfile.SteadyState` | tiering off, PGO off, R2R off | **(default)** fully-optimized steady-state throughput |
| `RuntimeProfile.Production` | tiering on, PGO on, R2R on | what ships; reproducible but imprecise |
| `RuntimeProfile.ServerGc` | `SteadyState` + non-concurrent server GC | code destined for a server-GC host |
| `RuntimeProfile.Host` | nothing set | inherit the host's configuration |

**These settings can only be applied to a process as it starts** - the runtime reads them once and never re-reads them. So they can be honoured for benchmarks that run in a child process, and cannot be honoured for anything measured in the host process.

NBenchmark therefore reports what was *actually* applied rather than what was requested. Every result carries:

- `RuntimeProfileName` - the profile actually in effect, or `"host"` when the measuring process inherited its configuration.
- `RuntimeKnobs` - the knobs in effect, e.g. `"tiered=off pgo=off r2r=off"`, read from the measuring process's own environment. A knob you set by hand is reported just as faithfully as one NBenchmark applied.

Results measured under different runtime profiles are **never placed in the same comparison group**, so no significance test, effect size, ratio or threshold gate ever spans them. A table that mixes them (a class combining `[InProcess]` benchmarks with isolated ones) is flagged.

Custom profiles are supported; `ExtraEnvironment` forwards any additional variables verbatim:

```csharp
var profile = RuntimeProfile.SteadyState with
{
    Name = "steady-state-big-gen0",
    ExtraEnvironment = new Dictionary<string, string> { ["DOTNET_GCgen0size"] = "1E00000" },
};
```

BenchmarkHarness fluent method: `.WithRuntimeProfile(RuntimeProfile.Production)`
BenchmarkSuite fluent method: `.WithRuntimeProfile(RuntimeProfile.Production)`
CLI flag: `--runtime-profile production`

See [`--runtime-profile`](cli.md#--runtime-profile-profile) for the measured impact and the full list of limitations.

### ForceGcBeforeEachIteration

```csharp
ForceGcBeforeEachIteration => ForceGcBeforeEachIterationOverride ?? (Profile == MeasurementProfile.Independent)
```

This is a **computed property** derived from `Profile` (or the `ForceGcBeforeEachIterationOverride` field when set). When `true`, a gen-0 GC collection is triggered before each measured sample (the K-batch), before the timestamp and outside the timed window. This keeps allocation side-effects from previous samples out of your measurement.

Under the `Realistic` profile (the default), this resolves to `false`. To enable per-iteration GC under `Realistic`, set `ForceGcBeforeEachIterationOverride = true` or use `--force-gc` on the CLI.

### ForceGcBeforeMeasurement

```csharp
ForceGcBeforeMeasurement => ForceGcBeforeMeasurementOverride ?? (Profile == MeasurementProfile.Independent)
```

A **computed property** derived from `Profile` (or the `ForceGcBeforeMeasurementOverride` field when set). When `true`, a full Gen2 GC (with finalizer wait) runs once after warmup and before the measurement loop begins, clearing the warmup heap so it cannot trigger a collection mid-measurement.

Under the `Realistic` profile (the default), this resolves to `false`: the benchmark body inherits whatever heap state the warmup left behind, matching production behaviour. It is distinct from `ForceGcBetweenBenchmarks` below, which runs *between* benchmarks rather than before measurement.

### ForceGcBetweenBenchmarks

```csharp
ForceGcBetweenBenchmarks => ForceGcBetweenBenchmarksOverride ?? true
```

A **computed property** (or the `ForceGcBetweenBenchmarksOverride` field when set). When `true`, a full Gen2 GC (with finalizer wait) runs between benchmarks so one benchmark's leftover heap cannot bias the next — which would make results order-dependent and undermine the significance test's independence assumption.

On by default for **both** profiles. Set `ForceGcBetweenBenchmarksOverride = false` or use `--no-gc-between-benchmarks` on the CLI when the inter-benchmark heap carry-over is intended.

### MeasureAllocations

```csharp
MeasureAllocations => MeasureAllocationsOverride ?? true
```

A **computed property** (or the `MeasureAllocationsOverride` field when set). When `true`, NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` around each iteration and reports the mean bytes allocated per operation in the **Alloc/op** column (with a process-wide fallback for async thread hops).

On by default for **both** profiles — the snapshot is taken outside the timed window, so it costs no timing purity and surfaces the "this 'pure-CPU' benchmark actually allocates" signal even under `Independent`. To disable allocation tracking, set `MeasureAllocationsOverride = false` or use `--no-allocations` on the CLI.

BenchmarkSuite fluent method: `.WithAllocations()`

> [!NOTE]
> Allocation tracking adds a small overhead to each iteration and may slightly affect timing measurements.

### Diagnostics

```csharp
Diagnostics = DiagnosticsOptions.Default   // default - GC collection counts on
```

Runtime diagnostics collected alongside timing and allocations. Typed as a `DiagnosticsOptions` record with four boolean toggles:

| Toggle | Default | What it collects |
| --- | --- | --- |
| `GcCollectionCounts` | `true` | Gen0, Gen1, Gen2 collection counts during the measurement phase (totals, not per-op). Cheap - two `GC.CollectionCount` reads per sample. |
| `GcHeapInfo` | `false` | Heap committed bytes and fragmented bytes delta across the measurement phase, via `GC.GetGCMemoryInfo`. |
| `Exceptions` | `false` | Total first-chance exceptions during the measurement phase, via an `AppDomain.FirstChanceException` subscription. Divided by total measurement ops to give exceptions per operation. |
| `CpuTime` | `false` | Process CPU time (TotalProcessorTime) delta per sample, divided by total measurement ops. Also reports the CPU/wall-clock ratio. |

Three named presets are available via `DiagnosticsOptions.FromMode(DiagnosticsMode)`:

| Mode | Toggles enabled |
| --- | --- |
| `DiagnosticsMode.None` | None |
| `DiagnosticsMode.Gc` | `GcCollectionCounts` |
| `DiagnosticsMode.GcAndCpu` | `GcCollectionCounts`, `CpuTime` |
| `DiagnosticsMode.All` | All four toggles |

```csharp
// Programmatic - enable everything
var options = new MeasurementOptions
{
    Diagnostics = DiagnosticsOptions.All,
};

// Or build a custom combination
var options = new MeasurementOptions
{
    Diagnostics = new DiagnosticsOptions { GcCollectionCounts = true, Exceptions = true },
};
```

BenchmarkSuite/BenchmarkHarness fluent methods: `.WithDiagnostics(DiagnosticsMode.All)` or `.WithDiagnostics(DiagnosticsOptions.All)`  
CLI flag: `--diagnostics <none|gc|gcandcpu|all>`

> [!NOTE]
> GC collection counts are on by default because they are cheap (`GC.CollectionCount` is a counter read, not a measurement). The other toggles add varying overhead: exception counting subscribes to `FirstChanceException` for the measurement phase, and CPU time reads `Process.TotalProcessorTime` per sample. Enable them only when you need the data.

See [Diagnostics](../statistics/diagnostics.md) for what each counter measures and how collection works.

### OutlierMode

```csharp
OutlierMode = OutlierMode.IqrFence   // default
```

Controls which samples are discarded before statistics are computed.

| Value | Behaviour |
| --- | --- |
| `OutlierMode.None` | No samples are removed. |
| `OutlierMode.RemoveTop5Percent` | The slowest 5% of samples are removed. |
| `OutlierMode.RemoveTopAndBottom5Percent` | The slowest and fastest 5% are removed. |
| `OutlierMode.IqrFence` | Samples beyond 1.5× the [IQR (inter-quartile range)](https://en.wikipedia.org/wiki/Interquartile_range) are removed. **(default)** |
| `OutlierMode.MedianAbsoluteDeviation` | Samples more than 3× the scaled [MAD](https://en.wikipedia.org/wiki/Median_absolute_deviation) from the median are removed - a robust alternative to `IqrFence` for heavily skewed data. |

`IqrFence` adapts to the actual spread of each benchmark instead of always discarding a fixed 5%: a clean run keeps nearly every sample, while a noisy run trims more. When the discarded slow samples form a tight secondary cluster (rather than scattered scheduling noise), NBenchmark adds a bimodal-distribution warning to the result so you can investigate the tail latency.

BenchmarkSuite fluent method: `.WithOutlierMode(mode)`  
CLI flag: `--outlier <none|top5|both5|iqr|mad>`

See [Outlier Trimming](../statistics/outliers.md) for the full algorithms.

### TailMetricsBasis

```csharp
TailMetricsBasis = TailMetricsBasis.Raw   // default
```

Which sample set the order statistics - percentiles, `Min`, `Max`, and the histogram - are computed from.

| Value | Behaviour |
| --- | --- |
| `TailMetricsBasis.Raw` | Full pre-trim distribution. Tail metrics describe the tail the outlier fence removed - so a GC pause the `Realistic` profile deliberately timed shows up in `Max`. **(default)** |
| `TailMetricsBasis.Trimmed` | Inlier (post-trim) set. Tail metrics describe only the central process. |

The resolved basis is recorded on the result (`BenchmarkResult.TailMetricsBasis`) so a consumer can label which sample set each statistic describes instead of inferring it. See [Descriptive statistics](../statistics/descriptive.md).

Central-tendency and dispersion statistics (mean, standard deviation, CI, CV, skewness, kurtosis, MAD, median, median CI) always stay on the trimmed set regardless of this setting.

CLI flag: `--tail-basis <raw|trimmed>`

See [Descriptive Statistics](../statistics/descriptive.md) for details.

### OutlierDetector

```csharp
OutlierDetector = null   // default - falls back to OutlierMode
```

A custom `IOutlierDetector` (from `NBenchmark.Stats`) that **takes priority over `OutlierMode`** when set. Use it to plug in a trimming rule that the built-in modes do not cover - a tail-preserving filter, a fixed physical threshold, and so on. `MeasurementOptions.ResolveOutlierDetector()` returns this detector when present, otherwise the detector mapped from `OutlierMode`.

```csharp
using NBenchmark.Stats;

new MeasurementOptions { OutlierDetector = new KeepFastestDetector(0.90) };
```

BenchmarkSuite fluent method: `.WithOutlierDetector(detector)`

> [!NOTE]
> The `--outlier` CLI flag always wins: passing it clears any programmatic `OutlierDetector` so the command line stays authoritative. See [Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors).

### ConfidenceLevel

```csharp
ConfidenceLevel = 0.95   // default
```

The confidence level for the margin of error reported in the Error column. Must be strictly between `0` and `1`.

| Value | Meaning |
| --- | --- |
| `0.90` | 90% confidence - narrower interval, less conservative |
| `0.95` | 95% confidence - the standard choice **(default)** |
| `0.99` | 99% confidence - wider interval, more conservative |

A higher confidence level produces a wider (larger) Error value. Use `0.99` when a result will be used to make an important decision and you want to be very conservative.

BenchmarkSuite fluent method: `.WithConfidenceLevel(0.99)`  
CLI flag: `--confidence 0.99`

### ReportedPercentiles

```csharp
ReportedPercentiles = [0.50, 0.95, 0.99, 0.999, 1.0]   // default
```

The set of percentile values to compute from the trimmed samples, typed as `IReadOnlyList<double>`. Each value must be between `0` and `1` inclusive. Values `> 0.50` and `< 1.0` appear as columns in reporter tail-latency tables (e.g. P95, P99, P99.9).

| Value | Behaviour |
| --- | --- |
| `[0.50, 0.95, 0.99, 0.999, 1.0]` **(default)** | Reports P50 (Median), P95, P99, P99.9, and Max. |
| Custom list | Only the specified percentile values are computed. P50 (`0.50`) does not produce a separate percentile column because it is already shown as Median. Max (`1.0`) is reported via the existing Max stat field. |
| `[0.90]` | Single custom percentile - P90 is computed and displayed. |

The computed values are stored in `BenchmarkResult.Percentiles` as `IReadOnlyList<PercentileEntry>`, where each entry has a `Percentile` (double, 0-1) and `Value` (double, in nanoseconds). Use `result.GetPercentile(0.95)` to retrieve a specific percentile value.

CLI flag: `--percentiles <list>` (comma-separated, e.g. `--percentiles 0.90,0.99,0.999`).

### EnableHistogram

```csharp
EnableHistogram = true   // default
```

When `true`, NBenchmark computes a latency histogram from the trimmed samples. The histogram is available on `BenchmarkResult.Histogram` as a `LatencyHistogram` record containing an ordered list of `HistogramBucket` values (each with `Lower`, `Upper`, and `Count`), plus `Min`, `Max`, and `SampleCount`.

Set to `false` to skip histogram computation and keep `BenchmarkResult.Histogram` as `null`. Useful when you do not need the histogram and want to save a small amount of computation.

CLI flag: `--no-histogram` (disables histogram).

### HistogramBucketCount

```csharp
HistogramBucketCount = 20   // default
```

The number of equal-width buckets in the latency histogram. Only used when `EnableHistogram` is `true`. Must be between `5` and `100`. More buckets provide finer granularity at the cost of fewer samples per bucket.

### EnableSignificance

```csharp
EnableSignificance = true   // default
```

When `true` and there are two or more benchmarks, NBenchmark tests whether the differences are statistically significant. With exactly two benchmarks it runs a [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) (exact permutation p-value for small, tie-free samples; normal approximation with tie and continuity corrections otherwise). With **three or more** benchmarks it runs the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) omnibus test instead, reporting a single verdict across all groups.

Disable it if you don't need significance testing and want to reduce overhead:

```csharp
.WithSignificance(false)
```

### SignificanceLevel

```csharp
SignificanceLevel = 0.05   // default
```

The significance threshold (alpha) a result's p-value is compared against. A result is flagged significant when `p < SignificanceLevel`. Must be strictly between `0` and `1`. Lower it (e.g. `0.01`) to demand stronger evidence before calling a difference real.

CLI flag: `--alpha 0.01`

### SignificanceTest

```csharp
SignificanceTest = null   // default - DefaultSignificanceTest (group-count aware)
```

A custom `ISignificanceTest` (from `NBenchmark.Stats`) that replaces the built-in strategy. When `null`, `ResolveSignificanceTest()` returns `DefaultSignificanceTest`, which picks Mann-Whitney U for two groups and Kruskal-Wallis + post-hoc Mann-Whitney U (Holm-Bonferroni corrected) for three or more. Implement the interface to supply a bootstrap, Bayesian, post-hoc, or domain-specific rule:

```csharp
using NBenchmark.Stats;

new MeasurementOptions { SignificanceTest = new MedianRatioSignificanceTest(25) };
```

BenchmarkSuite fluent method: `.WithSignificanceTest(test)`

See [Custom significance tests](../statistics/significance.md#custom-significance-tests).

### Environment

```csharp
Environment = null   // default - no hardware/OS controls applied
```

Opt-in hardware/OS controls applied for the duration of a run, typed as `EnvironmentOptions?`. When `null` (the default), the benchmark runs with whatever CPU affinity and process priority the host started it with - the zero-ceremony path. Set it to reduce measurement noise at its source (CPU migration, preemption, shared-host jitter) before the timer starts.

| Field | Type | Default | Effect |
| --- | --- | --- | --- |
| `CpuAffinity` | `IReadOnlyList<int>?` | `null` | Logical CPU core indices to pin the process to (e.g. `[2, 3]`). Restored on run exit. Linux/Windows only; ignored with a warning on macOS. |
| `ProcessPriority` | `ProcessPriorityClass?` | `null` | Process priority to request. `High` is recommended for dedicated hosts. Restored on run exit. A refused elevation is a warning, not an error. |
| `DedicatedHostGuidance` | `bool` | `false` | Emit a non-fatal pre-run warning when the host looks noisy (low core count, unraisable priority, or on macOS unobservable frequency scaling/thermal throttling). On a suitable host, actively suggests `--priority high`. |
| `SuppressBuildConfigurationWarning` | `bool` | `false` | Suppress the always-on warning that appears when the entry assembly is Debug-built or a debugger is attached. Use this only when measuring Debug behavior intentionally. |

```csharp
var options = new MeasurementOptions
{
    Environment = new EnvironmentOptions
    {
        CpuAffinity = [2, 3],
        ProcessPriority = ProcessPriorityClass.High,
        DedicatedHostGuidance = true,
    },
};
```

BenchmarkSuite/BenchmarkHarness fluent methods: `.WithHardwareAffinity(2, 3)`, `.WithProcessPriority(ProcessPriorityClass.High)`, `.WithDedicatedHostGuidance()`  
CLI flags: `--cpu-affinity <list>`, `--priority <level>`, `--dedicated-host-guidance`  
Additional suppression knobs: `.WithSuppressBuildConfigurationWarning()` (suite/harness) or `NBENCHMARK_SUPPRESS_DEBUG_WARNING=1` (environment variable)

This is the proactive counterpart to the statistical noise handling in [Outlier Trimming](../statistics/outliers.md): trimming reacts to noise after the fact; environment control reduces it at the source. See [Environment control](../features/environment-control.md) for the full model, platform notes, and isolated-process propagation.

## Applying options per-method (Harness mode)

In Harness mode, the `[Benchmark]` attribute accepts per-method overrides that take priority over the host-level options:

```csharp
// This method uses 1000 iterations regardless of the host setting.
[Benchmark(Iterations = 1000, WarmupIterations = 100)]
public void MyExpensiveBenchmark() => SlowOperation();
```

> [!NOTE]
> Only `Iterations`, `WarmupIterations`, and `LaunchCount` are pinnable per method. `OpsPerSample` is not exposed on `[Benchmark]` - pin it host-wide with `.WithOpsPerSample(n)` or `--ops-per-sample n`.

## Categories

Categories are not part of `MeasurementOptions`; they are metadata declared with `[BenchmarkCategory]` and used for filtering. See the [Categories guide](../features/categories.md) for the full feature.

## Valid ranges summary

| Option | Type | Default | Valid range |
| --- | --- | --- | --- |
| `Iterations` | `int?` | `null` (auto) | `0` – `100 000` when set (`0` = dry-run) |
| `WarmupIterations` | `int?` | `null` (auto) | `0` – `10 000` when set |
| `OpsPerSample` | `int?` | `null` (auto) | `1` – `16 777 216` when set |
| `LaunchCount` | `int` | `1` | `1` – `100` |
| `AutoTune` | `AutoTuneOptions` | `AutoTuneOptions.Default` | See [AutoTune](#autotune) |
| `ConfidenceLevel` | `double` | `0.95` | `>0` and `<1` |
| `SignificanceLevel` | `double` | `0.05` | `>0` and `<1` |
| `Profile` | `enum` | `Realistic` | `Realistic` or `Independent` |
| `ForceGcBeforeEachIteration` | `bool` (computed) | `false` | Derives from `Profile`; override via `ForceGcBeforeEachIterationOverride` |
| `ForceGcBeforeMeasurement` | `bool` (computed) | `false` | Derives from `Profile` (`true` under `Independent`); override via `ForceGcBeforeMeasurementOverride` |
| `MeasureAllocations` | `bool` (computed) | `true` | On for both profiles; override via `MeasureAllocationsOverride` |
| `ForceGcBetweenBenchmarks` | `bool` (computed) | `true` | On for both profiles; override via `ForceGcBetweenBenchmarksOverride` |
| `MinimumPracticalEffect` | `double?` | `0.147` | `0` – `1` when set (`0` = p-value-only verdicts; `null` disables the gate) |
| `OutlierMode` | `enum` | `IqrFence` | See above |
| `OutlierDetector` | `IOutlierDetector?` | `null` | Overrides `OutlierMode` when set |
| `ReportedPercentiles` | `IReadOnlyList<double>` | `[0.50, 0.95, 0.99, 0.999, 1.0]` | Each value 0-1 |
| `EnableHistogram` | `bool` | `true` | - |
| `HistogramBucketCount` | `int` | `20` | `5` – `100` |
| `EnableSignificance` | `bool` | `true` | - |
| `SignificanceTest` | `ISignificanceTest?` | `null` | Defaults to `DefaultSignificanceTest` |
| `Environment` | `EnvironmentOptions?` | `null` | See [Environment](#environment) |
| `Diagnostics` | `DiagnosticsOptions` | `DiagnosticsOptions.Default` (GC counts on) | See [Diagnostics](#diagnostics) |

Values outside the valid range throw `ArgumentOutOfRangeException`.

---

**Still having issues?** See the [Troubleshooting guide](../troubleshooting.md) for symptom-to-configuration mappings for common measurement problems.


---

# analyzers.md

---
title: Analyzers
description: Compile-time diagnostics that catch common NBenchmark configuration errors before you run your benchmarks.
order: 2
---

# Analyzers

NBenchmark.Analyzers ships a set of Roslyn diagnostic analyzers that detect configuration issues at edit time. Install the package to get live warnings and errors in your IDE and during `dotnet build`.

## Installation

```bash
dotnet add package NBenchmark.Analyzers
```

The analyzers run automatically. No additional configuration is needed. The package ships both analyzers (diagnostics) and code fixes (automatic corrections).

## Diagnostic reference

| ID | Title | Severity | Description |
| --- | --- | --- | --- |
| NB0001 | Benchmark class must have a public parameterless constructor | Warning | A class or record with `[Benchmark]` methods has no public parameterless constructor. Add one, or use `NBenchmark.DependencyInjection`. |
| NB0002 | `[Benchmark]` method must not be static | Error | A method is marked `[Benchmark]` but is `static`. Only instance methods are discovered. Remove the `static` keyword. |
| NB0003 | `[BenchmarkCase]` / `[BenchmarkCases]` must match method parameters | Error | The number of `[BenchmarkCase]` values does not match the method's parameter count, or the `[BenchmarkCases]` source yields a tuple arity that does not match. Also covers missing or non-existent source methods. |
| NB0004 | `[Benchmark]` body has no observable side effects | Error | A void `[Benchmark]` method body has no observable side effects. The JIT may eliminate it, producing 0 ns results. |
| NB0005 | `[Benchmark]` body does no observable work | Error | A void `[Benchmark]` method has an empty body (no statements at all). The JIT will eliminate it. |
| NB0006 | Multiple `[Benchmark(Baseline = true)]` methods in the same class | Error | Only one benchmark per class can have `Baseline = true`. Remove the attribute from all but one. |
| NB0007 | Duplicate lifecycle method in benchmark class | Error | Two methods in the same class share the same lifecycle attribute (`[BenchmarkSetup]`, `[BenchmarkTeardown]`, `[BenchmarkIterationSetup]`, `[BenchmarkIterationTeardown]`). Remove the duplicate. |
| NB0008 | `[Benchmark]` property value out of range | Error | `Iterations` or `WarmupIterations` on `[Benchmark]` is outside the valid range (0-100000 for iterations, 0-10000 for warmup, or -1 for the default). |
| NB0009 | `MeasurementOptions` property value out of range | Error | `Iterations`, `WarmupIterations`, or `ConfidenceLevel` in a `MeasurementOptions` object initializer or `with` expression is outside the valid range. |
| NB0010 | Benchmark body is throwaway | Warning | A lambda passed to the `Action` overloads of `Benchmark.Run()`, `Benchmark.RunAsync()`, `Benchmark.RunRaw()`, or `Benchmark.RunRawAsync()` has no observable side effects. The JIT may eliminate it, producing 0 ns results. |
| NB0011 | `PerClass` lifetime with scoped service may contaminate state | Warning | A benchmark class uses `[InstanceLifetime(InstanceLifetime.PerClass)]` and injects a constructor dependency that may hold per-instance state (any non-primitive, non-ambient reference type), which can leak warmed state across benchmark methods. |
| NB0012 | `[BenchmarkCases]` cannot be combined with `[BenchmarkCase]` | Error | A method has both `[BenchmarkCase]` and `[BenchmarkCases]`. Use one or the other. |
| NB0013 | `PerClass` lifetime with mutable instance field may contaminate state | Warning | A benchmark class uses `[InstanceLifetime(InstanceLifetime.PerClass)]` and has a mutable instance field that is read or written by at least two `[Benchmark]` methods, which can leak warmed state across methods. |
| NB0014 | Benchmark body captures state and cannot be isolated | Info | A lambda passed to `Benchmark.Run()`, `Benchmark.RunAsync()`, `Benchmark.RunRaw()`, or `Benchmark.RunRawAsync()` captures a local, a parameter, or `this`. Captured state cannot cross a process boundary, so the body is measured in the host process instead of an isolated worker. |

### NB0001 - Missing parameterless constructor

Applies to any class or record that contains declared `[Benchmark]` methods (inherited methods do not count - they are not discovered) but has no public parameterless constructor. NBenchmark uses `Activator.CreateInstance` by default, which requires a public parameterless constructor. Structs are not flagged because the implicit zero-init constructor satisfies the discovery pipeline.

```csharp
// Bad - no public parameterless constructor
public class MyBenchmarks
{
    private readonly IDependency _dep;

    public MyBenchmarks(IDependency dep) { _dep = dep; }

    [Benchmark]
    public void Measure() { }
}
```

Fix options:

1. Add a public parameterless constructor
2. Use `NBenchmark.DependencyInjection` to resolve from a DI container

### NB0002 - Static benchmark method

The `[Benchmark]` discovery pipeline only looks for instance methods. Static methods are silently skipped.

```csharp
// Bad
[Benchmark]
public static void Measure() { }

// Good
[Benchmark]
public void Measure() { }
```

This diagnostic has an automatic code fix that removes the `static` keyword.

### NB0003 - BenchmarkCase arity mismatch

The `[BenchmarkCase]` attribute must match the method's parameter count. Each attribute corresponds to one invocation of the method. When using `[BenchmarkCases]`, the source method must yield tuples whose arity matches the benchmark method's parameter count.

```csharp
// Bad - method takes no parameters but has [BenchmarkCase]
[BenchmarkCase(42)]
[Benchmark]
public void Measure() { }

// Bad - method expects one parameter, argument supplies none
[BenchmarkCase]
[Benchmark]
public void Measure(int x) { }

// Bad - [BenchmarkCases] source yields tuple with wrong arity
[BenchmarkCases(nameof(Cases))]
[Benchmark]
public void Measure(int x, int y) { }

public static IEnumerable<(int a,)> Cases() { yield return (1,); } // arity 1, expected 2
```

### NB0004 / NB0005 - No observable side effects

If a `[Benchmark]` method body contains only pure operations (local variable assignments, empty loops, no method calls, no field writes, no return value), the JIT may optimise the entire body away, producing a result of 0 ns. A syntax-level heuristic detects when a body has no observable side effects:

- No method calls
- No field/property writes
- No `ref`/`out` arguments
- No `return` statements with values
- No `await` expressions
- No object or array allocations

These diagnostics are `Error` severity in harness mode because a benchmark with no observable work is not a measurement issue - it is an invalid benchmark definition. The build fails so the problem is caught in CI/CD before the suite runs.

```csharp
// Bad - build fails with NB0005
[Benchmark]
public void Empty() { }

// Bad - build fails with NB0004
[Benchmark]
public void PureLoop() { for (var i = 0; i < 1000; i++) { } }

// Good - side effect through a consumed return value
[Benchmark]
public int Measure() { return Compute(); }

// Good - observable side effect
[Benchmark]
public void Mutate() { _counter++; }
```

When the analyzer cannot see the work because it happens outside the method syntax (for example native interop, external state mutation, or calls the analyzer does not recognize), suppress the diagnostic locally and document why:

```csharp
#pragma warning disable NBenchmark.NB0004 // P/Invoke call mutates native state
[Benchmark]
public void NativeBuffer()
{
    NativeMethods.FillBuffer(_buffer);
}
#pragma warning restore NBenchmark.NB0004
```

You can also lower the severity project-wide in `.editorconfig` if your codebase frequently encounters false positives:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = warning
dotnet_diagnostic.NB0005.severity = warning
```

### NB0006 - Multiple baselines

Only one benchmark per class can be the baseline. When multiple methods have `Baseline = true`, only the first one discovered is used and the others are ignored.

```csharp
// Bad
[Benchmark(Baseline = true)] public void MethodA() { }
[Benchmark(Baseline = true)] public void MethodB() { }
```

### NB0007 - Duplicate lifecycle methods

Each lifecycle attribute (`[BenchmarkSetup]`, `[BenchmarkTeardown]`, `[BenchmarkIterationSetup]`, `[BenchmarkIterationTeardown]`) should appear at most once per class. If two methods share the same attribute, the second one is silently ignored.

```csharp
// Bad - duplicate [BenchmarkSetup]
[BenchmarkSetup] public void Init() { }
[BenchmarkSetup] public void InitAgain() { }
```

### NB0008 / NB0009 - Range violations

`[Benchmark]` attribute properties and `MeasurementOptions` object initializer values are checked against their valid ranges at compile time rather than waiting for an `ArgumentOutOfRangeException` at runtime.

```csharp
// Bad - Iterations exceeds MaxIterations (100000)
[Benchmark(Iterations = 200000)]
public void Measure() { }

// Bad - ConfidenceLevel must be strictly between 0 and 1
var opts = new MeasurementOptions { ConfidenceLevel = 1.5 };

// Bad - 'with' expression is also checked
var opts2 = new MeasurementOptions() with { Iterations = 200000 };
```

### NB0010 - Throwaway lambda body

When a lambda expression passed to an `Action` overload of `Benchmark.Run()`, `Benchmark.RunAsync()`, `Benchmark.RunRaw()`, or `Benchmark.RunRawAsync()` has no observable side effects, the JIT may eliminate it. An empty lambda or one that only assigns to a local variable has no observable effect on the program state.

NB0010 is a `Warning` because Single mode is intended for ad-hoc exploration. Warnings do not break the build, so you can start with a simple lambda and iterate.

```csharp
// Warning - empty lambda, nothing to measure
Benchmark.Run(() => { });

// Warning - assigns to a local; local is discarded
Benchmark.Run(() => { var x = 42; });

// No warning - has observable side effects (field write, method call, etc.)
Benchmark.Run(() => { _x = 42; });
Benchmark.Run(() => Compute());  // method call
```

Value-returning overloads such as `Benchmark.Run<T>`, `Benchmark.RunAsync<T>`, `Benchmark.RunRaw<T>`, and `Benchmark.RunRawAsync<T>` are not flagged because NBenchmark consumes the returned value internally, which prevents dead-code elimination.

### NB0011 - `PerClass` lifetime with scoped service

When a class uses `[InstanceLifetime(InstanceLifetime.PerClass)]`, all `[Benchmark]` methods in that class share one object instance. If the class constructor takes a dependency that may hold per-instance state, one method can warm caches that the next method reads, which distorts timing.

The analyzer flags any non-primitive, non-ambient reference-type constructor parameter. Well-known stateless types (`ILogger<T>`, `IOptions<T>`) and ambient types (`HttpContext`, `IServiceProvider`, `CancellationToken`) are excluded.

```csharp
// Warning NB0011
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class OrderBenchmarks(MyDbContext db)
{
    [Benchmark] public int A() => db.Orders.Count();
    [Benchmark] public int B() => db.Orders.Where(o => o.Total > 100).Count();
}
```

**Why this matters.** The Mann-Whitney U test used for significance assumes samples are independent. When method A warms a shared cache that method B reads, method B's timings are artificially linked to method A running first. The shuffling math breaks and the significance verdict becomes unreliable. This is not a measurement-quality concern - it is a correctness concern for the statistical model.

Typical fixes:

1. Remove the attribute so the class uses `PerMethod`
2. Keep `PerClass` and implement `IStateReset` so shared state is reset between benchmark methods
3. Keep `PerClass` and suppress with `#pragma warning disable NB0011` when sharing state is intentional

> **CI note.** This is a compile-time warning, not a runtime error. In CI/CD pipelines the warning scrolls past in the build log and is easy to miss. If you suppress NB0011, verify that the shared state does not create a timing dependency between methods - for example, by running each method in isolation and comparing results.

### NB0013 - `PerClass` lifetime with mutable instance field

When a class uses `[InstanceLifetime(InstanceLifetime.PerClass)]` and has a non-`readonly` instance field that is accessed by at least two `[Benchmark]` methods, the field can carry warmed state from one method to the next, violating the statistical-independence assumption.

```csharp
// Warning NB0013
[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class CacheBenchmarks
{
    private int _counter;

    [Benchmark] public int A() => _counter++;
    [Benchmark] public int B() => _counter++;
}
```

Typical fixes:

1. Remove the attribute so the class uses `PerMethod`
2. Make the field `readonly` if it is only assigned once
3. Keep `PerClass` and suppress with `#pragma warning disable NB0013` when sharing state is intentional

### NB0014 - Capturing body cannot be isolated

NBenchmark measures a benchmark body in a separate worker process, because the runtime configuration a process starts under is the dominant term in a small measurement - on bodies of provably identical cost it moved the reported number by ~3.3x. It gets the body there by resolving the method the compiler already emitted; it never serializes or regenerates it.

A lambda that captures state cannot be addressed that way. Its captured values live in your process, and there is no honest way to reproduce them elsewhere - reconstructing them was tried and rejected, because a fabricated closure did not throw, it returned plausible wrong numbers. So a capturing body is measured in the test host instead, correctly labelled but less precise.

```csharp
var data = BuildInput();

// Info NB0014: captures 'data' - measured in this process
Benchmark.Run(() => Process(data));
```

The runtime already reports this after the fact, in the `Iso` column and the isolation status. NB0014 moves the news to where you can still act on it, and names the symbols responsible - which the runtime cannot do as precisely, because by then they are fields on a compiler-generated class.

**It is `Info`, not a warning**, because capturing is the idiomatic way to benchmark over prepared data. Warning on it would push you towards contorted code to silence a build. What it costs is fidelity, not correctness.

To isolate the body, move the state inside it:

```csharp
// No capture: the body builds what it needs
Benchmark.Run(() => Process(BuildInput()));
```

That measures the setup too, so it is not always what you want. When it is not, use a `[Benchmark]` class - discovery runs inside the worker, so `[GlobalSetup]` and fields are built there and nothing has to cross:

```csharp
public class ProcessBenchmarks
{
    private Input _data = null!;

    [BenchmarkSetup] public void Setup() => _data = BuildInput();

    [Benchmark] public Output Run() => Process(_data);
}
```

**What NB0014 does not catch.** Bodies handed to NBenchmark as method groups over live objects (`Benchmark.Run(widget.Compute)`) are refused at runtime for the same reason, but are not lambdas and so are outside this rule. Raise the severity if you want capture to fail a build:

```ini
[*.cs]
dotnet_diagnostic.NB0014.severity = warning
```

## Runtime independence warning

In addition to the compile-time analyzers above, NBenchmark emits a runtime warning on every `BenchmarkResult.Warnings` list when a class uses `InstanceLifetime.PerClass` and has more than one `[Benchmark]` method. This covers suite mode (where analyzers do not run) and cases where the analyzer package is not installed.

The runtime warning is opt-out: set `SuppressPerClassIndependenceWarning` to `true` on `MeasurementOptions` to silence it when sharing is intentional.

```csharp
// Suppress the runtime warning
var host = BenchmarkHarness.Create(args)
    .WithOptions(new MeasurementOptions { SuppressPerClassIndependenceWarning = true });
```

## Disabling a rule

Use a `#pragma` directive to suppress a specific diagnostic. Always add a comment explaining why the suppression is legitimate:

```csharp
#pragma warning disable NB0004 // P/Invoke mutates native state that the analyzer cannot see
[Benchmark]
public void Measure()
{
    NativeMethods.FillBuffer(_buffer);
}
#pragma warning restore NB0004
```

Or set the severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = none
```

## Severity

Diagnostics use the default severity listed in the table above. The default is chosen by where the problem sits on the invalid-to-suspicious spectrum:

- **Errors** mean the benchmark cannot run or will produce meaningless results. NB0002, NB0003, NB0004, NB0005, NB0006, NB0007, NB0008, and NB0009 are errors.
- **Warnings** mean the code can run but the measurements may be invalid. NB0001, NB0010, NB0011, and NB0013 are warnings.
- **Info** means the code and the measurement are both fine, but something about how the measurement was taken is worth knowing. NB0014 is informational.

You can override the severity of any diagnostic in `.editorconfig`. For example, to make all throwaway-lambda warnings errors in Single mode too:

```ini
[*.cs]
dotnet_diagnostic.NB0010.severity = error
```

Or to downgrade harness-mode body-effect errors to warnings in a legacy codebase while you migrate:

```ini
[*.cs]
dotnet_diagnostic.NB0004.severity = warning
dotnet_diagnostic.NB0005.severity = warning
```


---

