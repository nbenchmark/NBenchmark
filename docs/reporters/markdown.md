---
title: MarkdownReporter
description: Save benchmark results to a Markdown file suitable for committing to source control or publishing.
order: 3
---

# MarkdownReporter

`MarkdownReporter` writes results to a `.md` file as a formatted table. It is part of the core `NBenchmark` package with no additional dependencies.

Markdown output is a good choice for committing results to source control, attaching to pull requests, or including in documentation.

## Setup

```csharp
using NBenchmark.Reporters;

// Default - writes to the current directory with auto-naming
.WithReporter(new MarkdownReporter())

// Explicit directory
.WithReporter(new MarkdownReporter("results/"))

// Explicit directory and filename
.WithReporter(new MarkdownReporter("results/", "benchmarks.md"))
```

### Constructor

```csharp
MarkdownReporter(string outputDirectory = ".", string? fileName = null)
```

- `outputDirectory` - The directory to write the file to. Created automatically if it does not exist. Must be under the current working directory.
- `fileName` - When `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. When specified, the exact filename is used (no counter or timestamp is appended).

### Auto-naming

When `fileName` is not provided, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```
benchmark-results-20260606-034000-001.md
```

The counter increments each time `ReportAsync` is called within the same process, so multiple suite runs produce separate files instead of overwriting each other.

### Explicit filename

Pass a `fileName` when you want a stable output path (e.g. for CI scripts that expect a known filename):

```csharp
new MarkdownReporter("results/", "BENCHMARKS.md")
```

When an explicit `fileName` is provided, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

```markdown
## Benchmark Results

_Run at 2026-06-06 03:40:00 UTC - 25 warmup / 190 measured_

| Benchmark | Median | Mean | Error | StdDev | P95 | P99 | Ratio | Sig | Alloc/op |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Compute | 300.0 ns | 275.3 ns | ±16.2 ns | 85.9 ns | 500.0 ns | 500.0 ns | 0.75x | ✓ | - |
| Baseline | 400.0 ns | 375.8 ns | ±21.6 ns | 114.3 ns | 500.0 ns | 900.0 ns | 1.00x | - | - |

_Error = ±95% confidence interval half-width on the mean._
```

## Columns

| Column | Description |
|---|---|
| **Benchmark** | Benchmark name. |
| **Median** | Median timing. |
| **Mean** | Arithmetic mean. |
| **Error** | ±Margin of error on the mean. |
| **StdDev** | Sample standard deviation. |
| **P95** | 95th percentile. |
| **P99** | 99th percentile. |
| **Ratio** | Speed relative to the baseline. |
| **Sig** | `✓` = significant, `~` = not significant, `-` = not applicable. |
| **Alloc/op** | Mean bytes allocated per iteration, or `-` if not measured. |

## Notes

- Results are sorted by median (fastest first).
- Errored benchmarks are listed with a `-` in the Error, Ratio, and Sig columns. The Median, Mean, StdDev, P95, and P99 columns show `0.0 ns`.
- The output directory is created automatically if it does not exist.

## Using with Benchmark (Quick mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToMarkdownAsync("results/");
await result.ToMarkdownAsync("results/", "benchmarks.md");
```

## CLI usage (BenchmarkHost)

```bash
dotnet run -- --reporter markdown
dotnet run -- --reporter markdown --output ./results
```

When `--output` is specified, files are written inside that directory.