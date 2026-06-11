---
title: CLI Reference
description: All command-line flags accepted by BenchmarkHost.
order: 6
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

Number of measured iterations per benchmark. Valid range: `0` to `100 000`. Default: `200`. Use `--dry-run` to skip measurement entirely rather than setting this to `0` manually.

```bash
dotnet run -- --iterations 1000
```

---

### `--warmup <n>`

Number of warmup iterations per benchmark. Valid range: `0` to `10 000`. Default: `25`.

```bash
dotnet run -- --warmup 50
```

---

### `--confidence <value>`

Confidence level for the margin of error in the Error column. Must be a decimal strictly between `0` and `1`. Default: `0.95`.

```bash
dotnet run -- --confidence 0.99
```

---

### `--alpha <value>`

Significance level (alpha) for the Mann-Whitney U test. A benchmark is flagged significant when its p-value is below this threshold. Must be a decimal strictly between `0` and `1`. Default: `0.05`.

```bash
dotnet run -- --alpha 0.01
```

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
| `1` | One or more argument errors were detected during parsing: unknown flag, missing flag value, value out of range (`--iterations`, `--warmup`), invalid format (`--confidence`, `--seed`), unknown reporter name (`--reporter`), or a benchmark exceeded the `--threshold-pct` regression limit. |

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
