---
title: CLI Reference
description: All command-line flags accepted by BenchmarkHost.
order: 6
---

# CLI Reference

When using `BenchmarkHost` (Tier 3), all configuration can be driven from the command line. `BenchmarkHost.Create(args)` parses `args` automatically — no argument-parsing library required.

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

Number of measured iterations per benchmark. Valid range: `0` to `100 000`. Default: `200`. A value of `0` (combined with `--warmup 0`) is the dry-run signal — the body is not invoked.

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

### `--reporter <type>`

Add a file reporter. Can be specified multiple times to stack reporters.

| Value | Reporter | Output |
|---|---|---|
| `json` | `JsonReporter` | JSON file in the current directory (or `--output` directory) |
| `markdown` | `MarkdownReporter` | Markdown file |
| `csv` | `CsvReporter` | CSV file |
| `console` | — | Prints a message telling you to use `NBenchmark.Console` |

```bash
dotnet run -- --reporter markdown
dotnet run -- --reporter json --reporter csv
```

The `console` reporter must be added in code with `.WithReporter(new ConsoleReporter())` — it cannot be added via `--reporter console`.

---

### `--output <directory>`

Set the output directory for file reporters. Must be a path under the current working directory. Default: current directory.

```bash
dotnet run -- --reporter markdown --output ./results
```

::: warning
The output directory must already exist. `MarkdownReporter` and `CsvReporter` will throw a `DirectoryNotFoundException` if it does not (`JsonReporter` creates it automatically).
:::

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
    Concat — current production implementation
    Interpolate
── DatabaseBenchmarks ──
    RunQuery
```

---

### `--dry-run`

Skip the body entirely. `--dry-run` is implemented as `--iterations 0 --warmup 0`: the body is not invoked, only the discovery and wiring (setup, teardown, instantiation) runs. Use it to validate discovery and configuration without measurement.

> **Behavioural change:** in earlier versions `--dry-run` invoked the body once for a "smoke test". It now does not invoke the body at all. To run the body exactly once for smoke-testing, use `--iterations 1 --warmup 0`.

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

::: danger Not implemented
This flag is reserved for a future feature (fail the run if a benchmark regresses more than N% vs baseline). If you use it today, the run exits immediately with **exit code 1** and a message telling you to remove the flag.
:::

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | All benchmarks completed (including errored benchmarks — errors are not fatal). |
| `1` | An unknown flag was passed, or `--threshold-pct` was used. |

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
