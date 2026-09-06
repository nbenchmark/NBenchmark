---
title: MarkdownReporter
description: Save benchmark results to a Markdown file suitable for committing to source control or publishing.
order: 2
---

# MarkdownReporter

`MarkdownReporter` writes results to a `.md` file as a formatted table. It is part of the core `NBenchmark` package and has no additional dependencies.

Markdown output is an ideal choice for committing results to source control, attaching them to pull requests, or including them in documentation.

## Setup

Attach the reporter to your benchmark suite:

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

- `outputDirectory`: The directory where the file is written. NBenchmark creates this directory automatically if it does not exist. The path must be under the current working directory.
- `fileName`: If `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. If specified, the reporter uses that exact filename without appending a counter or timestamp.

### Auto-naming

When you don't provide a `fileName`, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```text
benchmark-results-20260606-034000-001.md
```

The counter increments each time `ReportAsync` is called within the same process, so multiple suite runs produce separate files instead of overwriting each other.

### Explicit filenames

Pass a `fileName` when you need a stable output path, such as for CI scripts that expect a known filename:

```csharp
new MarkdownReporter("results/", "BENCHMARKS.md")
```

When you provide an explicit `fileName`, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

```markdown
## Benchmark Results

> **2026-06-06 03:40:00 UTC** · 40 warmup · 190 measured · realistic profile
> Runtime: **steady-state** (tiered=off pgo=off r2r=off concurrentGc=off)
> Format: schema 1, measurement epoch 7 (numbers are comparable only with the same epoch)

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
```

When three or more benchmarks are compared, the **Sig** column shows the post-hoc pairwise verdict (candidate versus baseline, Holm-Bonferroni corrected). NBenchmark also includes an omnibus line in the **Interpretation** section summarizing the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) verdict across all groups:

```markdown
**Omnibus (Kruskal-Wallis)** across 3 groups: H(2) = 7.20, p = 0.027 → significant
```

Percentile columns (such as P95, P99, etc.) are dynamic. They appear only when you configure the corresponding percentiles via `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. With the default set (`[0.50, 0.95, 0.99, 0.999, 1.0]`), NBenchmark emits columns P95 and P99 in the tail-latency table. P50 is already shown as Median, and Max appears as a separate statistic.

## Columns

| Column | Description |
| --- | --- |
| **Benchmark** | The benchmark name. |
| **Median** | The median timing. |
| **Mean** | The arithmetic mean. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). A `-` indicates errored or dry-run results. |
| **Ratio** | The speed relative to the baseline. |
| **Scale** | A visual bar scaled to the slowest successful benchmark. |
| **Sig** | `✓` = significant, `✗` = not significant, `-` = not applicable. |
| **Magnitude** | A qualitative effect label. For built-in Mann-Whitney tests, this is Cliff's delta classified by [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size): `neg` (abs(δ) < 0.147), `small` (< 0.33), `med` (< 0.474), and `large` (≥ 0.474). See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| **Alloc/op** | Mean bytes allocated per iteration, or `-` if not measured. |

## Notes

- NBenchmark sorts results by median (fastest first).
- For [parameterized benchmarks](../features/parameterized-suite.md#reading-the-report), NBenchmark adds one column per parameter after the **Benchmark** column.
    - When a single method is swept across parameter values, the **Ratio** column reports each point's scaling factor relative to the fastest point (the reference, shown as `baseline`), while **Sig** and **Magnitude** are `-`.
    - When a parameter group holds competing benchmarks, **Sig** and **Magnitude** show the usual within-group significance and effect.
- Errored benchmarks are listed with a `-` in the Error, Ratio, and Sig columns. The Median, Mean, and StdDev columns show `0.0 ns`, and percentile columns remain empty.
- NBenchmark creates the output directory automatically if it does not exist.
- The report order is: Comparison -> Precision & Tail Latency -> (optional) Distribution Details -> Interpretation -> (optional) Warnings.
- In Standard mode (`--detail standard` or `WithDetail(ReportDetail.Standard)`), NBenchmark shows the full multi-section output: the comparison table, Precision & Tail Latency, and Interpretation.
- In Advanced mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`), NBenchmark appends a per-benchmark details section after the table. This section shows quartiles, fences, CI, margin percent, CV, skewness, kurtosis, MAD, and allocation breakdown, followed by an `auto-tuned: …` line summarizing the adaptive loop's decisions.

## Using with Benchmark (Single mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.SaveMarkdownAsync("results/");
await result.SaveMarkdownAsync("results/", "benchmarks.md");
```

## CLI usage (BenchmarkHarness)

```bash
dotnet run -- --reporter markdown
dotnet run -- --reporter markdown --output ./results
```

When you specify `--output`, NBenchmark writes the files inside that directory.
