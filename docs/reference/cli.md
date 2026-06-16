---
title: CLI Reference
description: All command-line flags accepted by BenchmarkHost.
order: 1
---

# CLI Reference

When using `BenchmarkHost` (Host mode), all configuration can be driven from the command line. `BenchmarkHost.Create(args)` parses `args` automatically - no argument-parsing library required.

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
|---|---|---|
| `--auto-tune <preset>` | `default` | Apply a preset bundle: `default`, `quick` (fewer samples, ±5% CI), or `thorough` (more samples, ±1% CI). |
| `--ops-per-sample <n>` | auto | Pin K - the number of body invocations timed as one sample. Auto-calibrated otherwise. |
| `--ci-target <0-1>` | `0.025` | Target relative CI half-width for auto sampling. Sampling stops once it is met. |
| `--min-samples <n>` | `30` | Floor on auto-resolved measured samples. |
| `--max-samples <n>` | `100000` | Ceiling on auto-resolved measured samples. |
| `--min-warmup <n>` | `8` | Floor on auto-detected warmup samples. |
| `--max-warmup <n>` | `10000` | Ceiling on auto-detected warmup samples. |
| `--max-tuning-time <s>` | `20` | Per-benchmark wall-clock safety cap, in seconds, for the whole adaptive loop. |

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
|---|---|
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

### `--reporter <type>`

Add a reporter by name. Can be specified multiple times to stack reporters. Built-in reporters:

| Name | Reporter | Output |
|---|---|---|
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
|---|---|
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

Disable process isolation for the whole run. Host mode is isolated by default - each benchmark class runs in its own child process - and this flag forces every benchmark to run in the host process instead. It overrides `[IsolatedProcess]` and is equivalent to calling `WithIsolation(false)` in code.

```bash
dotnet run -- --in-process
```

`--dry-run` also always runs in-process. See [Isolated Runs](../guides/isolated-runs.md) for the full isolation model.

---

### `--profile <mode>`

Set the measurement profile. Controls per-iteration GC, between-benchmark GC, and allocation tracking as a bundle.

| Value | Behaviour |
|---|---|
| `realistic` | No per-iteration GC, no between-benchmark GC, allocation tracking on. **(default)** |
| `independent` | Per-iteration Gen0 GC, between-benchmark full GC, allocation tracking off. |

```bash
dotnet run -- --profile independent
```

Individual behaviours can be overridden with `--force-gc` and `--no-allocations`. See [Measurement Profiles](../statistics/measurement.md#measurement-profiles) for a worked example.

---

### `--force-gc`

Override the profile to force a Gen0 GC before every measured iteration. Under the default `Realistic` profile, this enables per-iteration GC without switching to the `Independent` profile.

```bash
dotnet run -- --profile realistic --force-gc
```

There is no `--no-force-gc` flag because `Realistic` already disables per-iteration GC; users who want per-iteration GC under `Realistic` use `--force-gc`.

---

### `--no-allocations`

Override the profile to disable allocation tracking. Under the default `Realistic` profile, this suppresses the `Alloc/op` column without switching to the `Independent` profile.

```bash
dotnet run -- --profile realistic --no-allocations
```

There is no `--allocations` flag because `Realistic` already enables allocation tracking; users who want to disable it use `--no-allocations`.

---

### `--detail <level>`

Set the report detail level. Controls how much information reporters display.

| Value | Behaviour |
|---|---|
| `simple` | 10-column table with the essential statistics. **(default)** |
| `advanced` | Same table plus a per-benchmark stats block with quartiles, fences, confidence interval, skewness, kurtosis, MAD, and allocation percentiles. |

```bash
dotnet run -- --detail advanced
dotnet run -- --detail simple
```

The `--detail` flag affects all registered reporters. File-based reporters emit different column sets. Console reporter prints the stats block below each row; Markdown reporter appends a per-benchmark details section after the table. JSON always emits the full record regardless of detail level.

In host mode you can also set the detail level programmatically with `WithDetail(ReportDetail.Advanced)` before calling `RunAsync`.

See [Report Detail Levels](../guides/report-detail-levels.md) for the full column reference and `WithDetail()` examples for suite mode.

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

### `--threshold-pct <n>`

Causes the run to fail with **exit code 1** if any benchmark regresses more than `n`% against the baseline. `n` must be a positive integer (1 or greater). The regression check compares median execution times: a benchmark is considered regressed if `candidate.Median / baseline.Median > 1.0 + (n / 100.0)`.

If the selected baseline median is `0`, ratio math is undefined. In that case, any non-baseline benchmark with a positive median is treated as regressed.

The baseline is the benchmark marked `[Benchmark(Baseline = true)]`, or the fastest benchmark (lowest median) if no baseline is explicitly set. Errored benchmarks are excluded from the check.

**Example:** `--threshold-pct 10` fails the run when any benchmark is more than 10% slower than the baseline.

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | The run completed. Errored benchmarks are recorded in the results but are not fatal and do not affect the exit code. |
| `1` | One or more argument errors were detected during parsing: unknown flag, missing flag value, value out of range (`--iterations`, `--warmup`, `--ops-per-sample`, `--ci-target`, `--min-samples`, `--max-samples`, `--min-warmup`, `--max-warmup`, `--max-tuning-time`), invalid format (`--confidence`, `--seed`), unknown preset (`--auto-tune`), unknown outlier mode (`--outlier`), unknown reporter name (`--reporter`), invalid detail level (`--detail`), or a benchmark exceeded the `--threshold-pct` regression limit. |

When exit code `1` is set during argument parsing, the run still completes (discovery, measurement, and reporting proceed). This lets you see output even after a misconfigured invocation - but the non-zero exit code ensures CI pipelines catch the problem. When exit code `1` is caused by a `--threshold-pct` regression, reporters still flush their output so you retain the evidence.

## Examples

```bash
# Run all benchmarks with 500 iterations, save to Markdown
dotnet run -- --iterations 500 --reporter markdown --output ./results

# Run only sorting benchmarks with 99% confidence interval
dotnet run -- --filter Sort* --confidence 0.99

# Reproducible run in declaration order
dotnet run -- --order declaration --seed 12345

# Check what will run before committing to a full benchmark
dotnet run -- --list
dotnet run -- --dry-run
```
