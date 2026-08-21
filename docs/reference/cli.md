---
title: CLI Reference
description: All command-line flags accepted by BenchmarkHarness.
order: 1
---

# CLI Reference

When using `BenchmarkHarness` (Harness mode), you can drive all configuration from the command line. `BenchmarkHarness.Create(args)` parses `args` automatically.

## Usage

Run your benchmarks from the terminal:

```bash
dotnet run -- [options]
```

Or using a published binary:

```bash
MyApp.Benchmarks [options]
```

## Common flags

| Flag | Purpose |
| --- | --- |
| [`--filter <pattern>`](#selection) | Run only benchmarks whose name matches the pattern. |
| [`--list`](#selection) | List benchmarks that would run without executing them. |
| [`--dry-run`](#measurement) | Validate discovery and wiring without measuring. |
| [`--reporter <type>`](#output) | Add an output format (such as `json`, `markdown`, `csv`, or `console`). |
| [`--output <directory>`](#output) | Specify where file reporters write their output. |
| [`--detail <level>`](#output) | Control how much statistical detail reporters show. |
| [`--launch-count <n>`](#measurement) | Repeat the run in N processes to measure run-to-run spread. |
| [`--threshold-pct <n>`](#run-control) | Fail the run if a benchmark regresses past this percentage. |

## All options

### Selection

| Flag | Description |
| --- | --- |
| `--filter <pattern>` | Run only benchmarks whose fully-qualified name (`ClassName.MethodName`) matches the glob pattern. Matching is case-insensitive. Use `*` as a wildcard. |
| `--category <name>` | Include benchmarks tagged with the given category. Repeatable; multiple flags are combined with OR. Untagged benchmarks are excluded when any `--category` flag is present. |
| `--exclude-category <name>` | Exclude benchmarks tagged with the given category. Repeatable; multiple flags are combined with OR. |
| `--list` | List all discovered benchmarks without running them. |

### Measurement

| Flag | Description |
| --- | --- |
| `--iterations <n>` | Pin the measured-sample count per benchmark and disable auto-sampling. Valid range: 0 to 100,000. Use `--dry-run` to skip measurement. |
| `--warmup <n>` | Pin the warmup-sample count per benchmark and disable the plateau rule. Valid range: 0 to 10,000. |
| `--profile <mode>` | Set the measurement profile (e.g., `realistic` or `independent`). Controls GC behavior during the run. |
| `--runtime-profile <profile>` | Set the runtime-startup configuration (JIT tiering, PGO, R2R, and GC flavor). |
| `--force-gc` | Force a Gen0 GC before every measured iteration. |
| `--no-gc-between-benchmarks` | Disable the full GC that runs between benchmarks. |
| `--no-allocations` | Disable allocation tracking and suppress the **Alloc/op** column. |
| `--launch-count <n>` | Repeat each benchmark N times in separate worker processes. Valid range: 1 to 100. |
| `--dry-run` | Skip measurement entirely to validate discovery and DI wiring. |

#### Adaptive tuning flags

When using auto mode, NBenchmark resolves warmup length, measured-sample count, and ops-per-sample (K) at runtime. These flags steer that resolution. For more information, see [Configuration: AutoTune](./configuration.md#autotune).

| Flag | Default | Effect |
| --- | --- | --- |
| `--auto-tune <preset>` | `default` | Apply a preset: `default`, `quick` (looser CI), or `thorough` (tighter CI). |
| `--ops-per-sample <n>` | auto | Pin K - the number of body invocations timed as one sample. |
| `--ci-target <0-1>` | `0.025` | The target relative CI half-width for auto sampling. |
| `--min-samples <n>` | `30` | The floor for auto-resolved measured samples. |
| `--max-samples <n>` | `5000` | The ceiling for auto-resolved measured samples. |
| `--min-warmup <n>` | `8` | The floor for auto-detected warmup samples. |
| `--max-warmup <n>` | `100000` | The ceiling for auto-detected warmup samples. |
| `--max-tuning-time <s>` | `20` | Per-benchmark wall-clock safety cap (seconds) for the adaptive loop. |
| `--autotune-cap-behavior <mode>` | `warn` | Action when wall-clock cap is hit: `warn` emits a warning; `error` marks the benchmark as errored. |
| `--warmup-budget-fraction <0-1>` | `0.4` | Max share of `--max-tuning-time` for calibration and warmup. |
| `--cap-grace-factor <n>` | `1.5` | Multiplier for the measurement phase when chasing `--min-samples` after the cap fires. |
| `--min-warmup-time <ms>` | `500` | Minimum in-body time auto-warmup must accumulate before it can settle. |
| `--no-jit-quiescence` | off | Disable the JIT-quiescence warmup gate. |
| `--jit-quiet-period <ms>` | `50` | Duration the JIT compiled-method count must remain unchanged before warmup settles. |
| `--min-measurement-time <ms>` | `100` | Minimum in-body time the measurement phase must span before stopping on the CI target. |
| `--drift-tolerance <0-1>` | `0.1` | Max disagreement between sample halves before measurement restarts. |
| `--max-drift-restarts <n>` | `2` | Max number of times drift may discard samples and restart measurement. |

### Statistics

| Flag | Description |
| --- | --- |
| `--confidence <value>` | Set the confidence level for the margin of error. Must be between 0 and 1. Default: `0.95`. |
| `--alpha <value>` | Set the significance level (alpha). A benchmark is significant when `p < alpha`. Default: `0.05`. |
| `--outlier <mode>` | Set the outlier-trimming mode (`none`, `top5`, `both5`, `iqr`, `mad`). Default: `iqr`. |
| `--no-interference-filter` | Disable evidence-based interference rejection. |
| `--tail-basis <basis>` | Set the sample set for order statistics (`raw` or `trimmed`). Default: `raw`. |
| `--percentiles <list>` | Set the computed percentiles (comma-separated fractions). Default: `0.50,0.95,0.99,0.999,1.0`. |
| `--min-practical-effect <0-1>` | Set the minimum practical effect required for a significant verdict. Default: `0.147`. |
| `--cross-class` | Compute significance across all classes in a single table. |

### Output

| Flag | Description |
| --- | --- |
| `--reporter <type>` | Add a reporter by name (e.g., `json`, `markdown`, `csv`, `console`). Repeatable. |
| `--output <directory>` | Set the output directory for file reporters. Must be under the CWD. |
| `--detail <level>` | Set the report detail level (`simple`, `standard`, `advanced`). Default: `simple`. |
| `--no-histogram` | Disable latency histogram computation. |
| `--emit-raw` | Return every raw sample from isolated workers instead of a representative subset. |
| `--no-samples` | Omit raw sample arrays from JSON output. |

### Isolation

| Flag | Description |
| --- | --- |
| `--in-process` | Disable process isolation for the entire run. |
| `--strict-isolation` | Fail the run if isolation was refused for any benchmark. |
| `--verify-isolation` | Measure benchmarks in both isolated and in-process modes to compare the difference. |
| `--runtimes <list>` | Run benchmarks under multiple .NET runtimes (comma-separated TFMs). |

### Environment

| Flag | Description |
| --- | --- |
| `--cpu-affinity <list>` | Pin the process and measuring thread to specific logical CPU cores. |
| `--priority <level>` | Request a process priority (e.g., `high`, `normal`, `idle`). |
| `--no-thread-control` | Disable thread-level OS controls (affinity, priority, QoS). |
| `--dedicated-host-guidance` | Warn if the host environment looks noisy or shared. |

### Diagnostics

| Flag | Description |
| --- | --- |
| `--diagnostics <mode>` | Control runtime diagnostics (`none`, `gc`, `gcandcpu`, `all`). Default: `gc`. |
| `--no-drift-canary` | Disable the host drift canary. |
| `--observer <type>` | Attach a measurement observer by name. Repeatable. |
| `--stream-samples` | Forward the live per-sample observer stream from isolated workers. |
| `--otlp-endpoint <url>` | Set the OTLP endpoint for OpenTelemetry export. |

### Run control

| Flag | Description |
| --- | --- |
| `--order <mode>` | Control run order (`random` or `declaration`). Default: `random`. |
| `--seed <n>` | Set a fixed integer seed for reproducible random ordering. |
| `--threshold-pct <n>` | Fail the run (exit code 1) if a benchmark regresses past this percentage. |
| `--help` / `-h` | Print help text and exit. |

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | The run completed successfully. Errored benchmarks are recorded but are not fatal. |
| `1` | A fatal error occurred. This includes argument parsing errors (unknown flags, out-of-range values), invalid formats, or a benchmark exceeding the `--threshold-pct` limit. |

When exit code `1` is set during argument parsing, the run still completes to allow you to see the results, but the non-zero code ensures CI pipelines catch the issue.

## Examples

```bash
# Run all benchmarks with 500 iterations and save to Markdown
dotnet run -- --iterations 500 --reporter markdown --output ./results

# Run only sorting benchmarks with a 99% confidence interval
dotnet run -- --filter Sort* --confidence 0.99

# Reproducible run in declaration order
dotnet run -- --order declaration --seed 12345

# Run all benchmarks with 3 launches to measure run-to-run spread
dotnet run -- --launch-count 3

# Pin to cores 2-3, raise priority, and warn if the host is noisy
dotnet run -- --cpu-affinity 2,3 --priority high --dedicated-host-guidance

# Collect all diagnostics and use standard detail level
dotnet run -- --diagnostics all --detail standard

# Stream live telemetry to a local OTLP collector
dotnet run -- --otlp-endpoint http://localhost:4317

# Verify discovered benchmarks before running
dotnet run -- --list
dotnet run -- --dry-run
```
