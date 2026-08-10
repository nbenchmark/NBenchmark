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
| `--max-warmup <n>` | `100000` | Ceiling on auto-detected warmup samples. Deliberately far above what any body needs so the *time* bounds bind instead - a fast body needs ~25,000 samples to accumulate `--min-warmup-time`. (A *pinned* `--warmup` is still limited to 10,000.) |
| `--max-tuning-time <s>` | `20` | Per-benchmark wall-clock safety cap, in seconds, for the whole adaptive loop. |
| `--autotune-cap-behavior <mode>` | `warn` | What happens when the wall-clock cap is hit before the CI target or warmup plateau is reached: `warn` emits a warning; `error` marks the benchmark as errored. |
| `--warmup-budget-fraction <0-1>` | `0.4` | Max share of `--max-tuning-time` that calibration and warmup may consume together; the remainder is reserved for measurement. Must be in `(0, 1]`. |
| `--cap-grace-factor <n>` | `1.5` | Multiplier on `--max-tuning-time` that the measurement phase may reach while chasing `--min-samples` after the cap fires. Must be at least 1; set to 1 to disable the grace path. |
| `--min-warmup-time <ms>` | `500` | Minimum in-body time (milliseconds) auto-warmup must accumulate before it may settle, so background tiered JIT lands before measurement rather than mid-run. 5× the runtime's 100 ms tiered-compilation call-counting delay; a floor at or below that delay reliably lands the tier-up *inside* measurement. Chosen empirically - at 250 ms a `StringBuilder`-append loop still landed in either its tier-0 or its ~4.5× faster steady state depending on the run. Raise this first if a benchmark's median differs between runs while each run reports a tight interval. Must be ≥ 0; `0` disables the floor (and the JIT-quiescence gate). |
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

Disable process isolation for the whole run. Harness mode is isolated by default - each benchmark class runs in its own worker - and this flag forces every benchmark to run in the host process instead. It overrides `[IsolatedProcess]` and is equivalent to calling `WithIsolation(false)` in code.

```bash
dotnet run -- --in-process
```

`--dry-run` also always runs in-process. See [Isolated Runs](../features/isolated-runs.md) for the full isolation model.

---

### `--strict-isolation`

Fail the run if isolation was **refused** for any benchmark.

```bash
dotnet run -- --strict-isolation
```

Two things at once: it turns `MeasurementOptions.RequireIsolation` on for the run - so a refusal throws at discovery time, before anything is measured - and it audits the results afterwards as a backstop, setting exit code 1 and naming every offender grouped by cause:

```
--strict-isolation: 1 of 4 measured benchmark(s) ran in this process rather than an
isolated worker, so their numbers carry the host's JIT and GC configuration.
  in-process (captures): HarnessBenchmarks.InHarness
```

It keys on *refusal*, not on "was not isolated". A deliberate `--in-process` or `--dry-run` run passes, because there is nothing to act on.

Since `RequireIsolation` defaults to `true`, the flag is mostly a way to re-assert the requirement over a program that turned it off, and to get the after-the-fact report. See [When isolation is refused](../features/isolated-runs.md#when-isolation-is-refused).

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

This exists because the case for isolation is not believable in the abstract - seeing it on your own benchmarks is what makes it stick. The output reports a **ratio per benchmark** rather than an aggregate, because host measurement is *unpredictable*: one row far from its isolated reading beside another that agrees is the point, and averaging would erase it. Rows are ordered by distance from parity.

When the two agree, it says so. A workload insensitive to the host's runtime configuration is a real result worth knowing - though it is a property of those benchmarks, not a general one.

The comparison pass runs no reporters, writes no files, and cannot change the exit code. It is a diagnostic, not a second set of results.

Combined with `--runtimes`, it is skipped and says why. The comparison re-measures in *this* process, and this process is one runtime - so there is no in-process counterpart for the other builds to be compared against, and comparing every runtime against the same host row would print a table that looks like a finding without being one.

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

**Why this exists.** These settings can only be applied to a process as it starts - the runtime reads them once. The effect is large: on four benchmarks with provably identical cost, `--runtime-profile host` spread 3.10x across runs while `steady-state` spread 1.02x, because tiered compilation means a short body may be measured as tier-0 or tier-1 code depending on unrelated process history.

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

Repeat each benchmark N times, each in its own worker process. The primary result is the **average across those launches**, and its confidence interval is derived from the spread between them - so it describes how well the number reproduces rather than how precisely one process measured it. An aggregation table with the per-launch detail appears below the main results when `n > 1`. Valid range: `1` to `100`. Harness-mode default: `3` when the user has not explicitly pinned launch count. See [Multiple launches](../features/multiple-launches.md).

```bash
dotnet run -- --launch-count 3
```

When combined with `--dry-run`, exactly one dry launch is performed regardless of the count. When combined with process isolation, the host spawns N workers per isolated group.

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

When `--runtimes` is specified, the host builds the project for each target framework via `dotnet build -f <tfm>`, runs the benchmarks in a worker under that runtime, and aggregates the results. The project must target all specified runtimes in its `.csproj` file:

```xml
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
```

The console and markdown reporters add a "Runtime" column when results span multiple runtimes. Significance testing is performed within each runtime (net8 results are compared against the net8 baseline, not the net10 one). The first runtime in the list is the implicit baseline for ratio calculations.

`--runtimes` overrides `--in-process`; cross-runtime always uses workers. When `--runtimes` is passed, it also overrides any `[Runtimes]` attribute on discovered classes.

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

By default a worker sends back at most 4,096 raw samples per benchmark. Every statistic NBenchmark reports is computed inside the worker over the complete sample array, so the cap cannot move a median, an interval, or an outlier count. The subset is drawn uniformly at random from the full array and kept in measurement order, seeded from the run's seed so a repeat ships the same samples.

Pass `--emit-raw` when you want the complete series for analysis outside NBenchmark:

```bash
dotnet run -- --emit-raw --reporter json
```

Programmatic equivalent: `WithOptions(new MeasurementOptions { MaxRawSamples = MeasurementOptions.UnboundedRawSamples })`. Any positive value sets a different cap.

> **In-process runs are unaffected.** There is no boundary to cross, so they always hold the complete array.

See also [`--no-samples`](#--no-samples), which omits the arrays from JSON output entirely.

---

### `--stream-samples`

Forward the live per-sample observer stream out of an isolated worker.

An attached observer's `OnSample` callback is the one event that does not cross the process boundary by default. Every other event - phase transitions, detector snapshots, results - is emitted a handful of times per benchmark, but samples arrive in the hundreds or thousands.

Pass this when an observer needs the samples *live* - a streaming histogram, a sample-level exporter:

```bash
dotnet run -- --observer live --stream-samples
```

It needs an observer to be attached; with nothing to replay into, the request is withdrawn and the flag costs nothing. It is unrelated to [`--emit-raw`](#--emit-raw): that bounds the sample array carried on each *result*, this is the live stream. The complete array arrives with the result whether or not this is set.

Programmatic equivalent: `WithOptions(o => o with { StreamSamples = true })`. Samples cross in batches - one frame per 128 samples or per 100 ms, whichever comes first - and your observer still sees one `OnSample` call per sample. See [Measurement Observer](observers.md#--stream-samples) for the batching rule and its ordering guarantee.

---

### `--no-samples`

Omit raw per-sample arrays from JSON reporter output. Samples are still collected, still feed significance testing and the Console histogram, and still cross the process boundary - this only controls whether they are written to the file.

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

Set the OTLP endpoint an OpenTelemetry SDK in the entry assembly should export to. The value must be an absolute `http://` or `https://` URL. The harness mirrors it into the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable before spawning isolated workers, so workers stream their telemetry to the same collector as the host. When `OTEL_EXPORTER_OTLP_ENDPOINT` is already set in the environment, the explicit flag does not override it.

This is the cross-process channel for live telemetry: the in-memory `IMeasurementObserver` callback cannot cross the process boundary, so OTLP export is how isolated workers stream live data to a collector. See [BCL instrumentation](bcl-instrumentation.md#cross-process-streaming) for the full topology and the env vars forwarded to workers.

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

# Stream live telemetry to a local OTLP collector (isolated workers inherit the endpoint)
dotnet run -- --otlp-endpoint http://localhost:4317

# Check what will run before committing to a full benchmark
dotnet run -- --list
dotnet run -- --dry-run
```
