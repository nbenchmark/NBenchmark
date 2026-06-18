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

> **2026-06-06 03:40:00 UTC** · 40 warmup · 190 measured · realistic profile

### Comparison

| Benchmark | Median | Mean | Ops/s | Ratio | Scale | Sig | Magnitude | Alloc/op |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Compute | 300.0 ns | 275.3 ns | 3.64 Mops/s | **0.75x** | `############` | ✓ | large | - |
| **Baseline** _(baseline)_ | 400.0 ns | 375.8 ns | 2.66 Mops/s | _baseline_ | `################` | - | - | - |

> The real `MarkdownReporter` emits Unicode block characters (`█`) in the `Scale` column. They are replaced with `#` above so the example stays aligned in source form.

### Precision & Tail Latency

| Benchmark | Error (±CI) | StdDev | CV | P95 | P99 |
|---|---:|---:|---:|---:|---:|
| Compute | ±16.2 ns (5.89%) | 85.9 ns | 31.22% | 500.0 ns | 500.0 ns |
| Baseline | ±21.6 ns (5.75%) | 114.3 ns | 30.43% | 500.0 ns | 900.0 ns |

---

### Interpretation

**Omnibus**: not run (fewer than 3 comparable groups).

- Significance: Mann-Whitney U (p < 0.05)
- Outliers: IQR fence (1.5×)
- Effect metric: Cliff's δ (Romano neg/small/med/large labels)

_2 benchmark(s) · 0.0s total · Mann-Whitney U (p < 0.05) · CI 95%_
```

When **three or more** benchmarks are compared, the Sig column shows the post-hoc pairwise verdict (candidate versus baseline, Holm-Bonferroni corrected) and the **Interpretation** section includes an omnibus line summarising the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) verdict across all groups:

```markdown
**Omnibus (Kruskal-Wallis)** across 3 groups: H(2) = 7.20, p = 0.027 → significant
```

## Columns

| Column | Description |
|---|---|
| **Benchmark** | Benchmark name. |
| **Median** | Median timing. |
| **Mean** | Arithmetic mean. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). `-` for errored or dry-run results. |
| **Ratio** | Speed relative to the baseline. |
| **Scale** | Visual bar scaled to the slowest successful benchmark. |
| **Sig** | `✓` = significant, `✗` = not significant, `-` = not applicable. |
| **Magnitude** | Strategy-defined qualitative effect label. With the built-in Mann-Whitney tests this is Cliff's delta classified by [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size): `neg` (abs(δ) < 0.147), `small` (< 0.33), `med` (< 0.474), `large` (≥ 0.474). `-` for the baseline or when significance is not tested. See [Effect size: Cliff's delta](../statistics/significance.md#effect-size-cliffs-delta). |
| **Alloc/op** | Mean bytes allocated per iteration, or `-` if not measured. |

## Notes

- Results are sorted by median (fastest first).
- Errored benchmarks are listed with a `-` in the Error, Ratio, and Sig columns. The Median, Mean, StdDev, P95, and P99 columns show `0.0 ns`.
- The output directory is created automatically if it does not exist.
- The report order is: Comparison -> Precision & Tail Latency -> (optional) Distribution Details -> Interpretation -> (optional) Warnings -> final summary line.
- In Advanced mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`), a per-benchmark details section is appended after the table showing quartiles, fences, CI, margin percent, CV, skewness, kurtosis, MAD, and allocation breakdown, followed by an `auto-tuned: …` line summarising the adaptive loop's decisions (resolved samples × ops-per-sample, warmup length, achieved CI half-width).

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
