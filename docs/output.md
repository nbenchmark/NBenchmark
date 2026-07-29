# json-reporter.md

---
title: JsonReporter
description: Save benchmark results to a JSON file for programmatic consumption.
order: 4
---

# JsonReporter

`JsonReporter` writes results to a `.json` file as a structured object. It is part of the core `NBenchmark` package (uses `System.Text.Json` with no additional dependencies).

JSON output is suitable for CI dashboards, performance tracking over time, or any tooling that consumes structured data.

## Setup

```csharp
using NBenchmark.Reporters;

// Default - writes to the current directory with auto-naming
.WithReporter(new JsonReporter())

// Explicit directory
.WithReporter(new JsonReporter("results/"))

// Explicit directory and filename
.WithReporter(new JsonReporter("results/", "benchmarks.json"))
```

### Constructor

```csharp
JsonReporter(string outputDirectory = ".", string? fileName = null)
```

- `outputDirectory` - The directory to write the file to. Created automatically if it does not exist. Must be under the current working directory.
- `fileName` - When `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. When specified, the exact filename is used (no counter or timestamp is appended).

### Auto-naming

When `fileName` is not provided, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```
benchmarks-20260606-034000-001.json
```

The counter increments each time `ReportAsync` is called within the same process, so multiple suite runs produce separate files instead of overwriting each other.

### Explicit filename

Pass a `fileName` when you want a stable output path:

```csharp
new JsonReporter("results/", "benchmarks.json")
```

When an explicit `fileName` is provided, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

The envelope opens with `schemaVersion` and `measurementEpoch` - see
[Report format versioning](./index.md#report-format-versioning) before diffing two files.

```json
{
  "schemaVersion": 1,
  "measurementEpoch": 1,
  "generatedAt": "2026-06-06T03:40:00.000Z",
  "detail": "simple",
  "profile": "realistic",
  "results": [
    {
      "name": "Compute",
      "description": null,
      "mean": 275.3,
      "median": 300.0,
      "percentiles": [
        { "percentile": 0.50, "value": 300.0 },
        { "percentile": 0.95, "value": 500.0 },
        { "percentile": 0.99, "value": 500.0 },
        { "percentile": 0.999, "value": 1000.0 },
        { "percentile": 1.0, "value": 1100.0 }
      ],
      "histogram": {
        "min": 200.0,
        "max": 1100.0,
        "sampleCount": 190,
        "buckets": [
          { "lower": 200.0, "upper": 245.0, "count": 10 },
          { "lower": 245.0, "upper": 290.0, "count": 45 }
        ]
      },
      "min": 200.0,
      "max": 1100.0,
      "standardDeviation": 85.9,
      "standardError": 6.1,
      "marginOfError": 16.2,
      "confidenceLevel": 0.95,
      "coefficientOfVariation": 0.3122,
      "confidenceIntervalLower": 259.1,
      "confidenceIntervalUpper": 291.5,
      "operationsPerSecond": 3636363.6,
      "medianOperationsPerSecond": 3333333.3,
      "nanosecondsPerOperation": 275.3,
      "totalOperations": 230,
      "meanAllocatedBytes": null,
      "pValue": 0.0012,
      "isSignificant": true,
      "errored": false,
      "errorMessage": null,
      "measuredIterations": 190,
      "warmupIterations": 40,
      "runAt": "2026-06-06T03:40:00.000Z",
      "totalDuration": "00:00:00.050",
      "measuredDuration": "00:00:00.040",
      "isBaseline": false,
      "outlierMode": "iqrFence",
      "outlierDetector": "IQR fence (1.5×)",
      "tailMetricsBasis": "raw",
      "autoTune": {
        "resolvedWarmup": 40,
        "resolvedSamples": 190,
        "opsPerSample": 1,
        "initialOpsPerSample": null,
        "totalBodyInvocations": 230,
        "warmupStop": "settled",
        "sampleStop": "ciTargetMet",
        "achievedRelativeCiWidth": 0.0184,
        "tuningWallClock": "00:00:00.050",
        "jitterMetric": 0.0271,
        "outlierDetectorSwitched": false,
        "ciWidthSeries": [0.212, 0.098, 0.041, 0.0184],
        "warmupTimeFloorMet": true,
        "warmupElapsedNs": 500049799.0,
        "warmupCurve": [412.0, 350.1, 288.4, 276.9, 275.4],
        "warmupSampleInterval": 8,
        "warmupJitCompiledMethods": 133,
        "warmupJitCompilationTime": "00:00:00.0205982",
        "warmupJitCompiledIlBytes": 7404,
        "jitLastChangeAtNs": 481309320.0,
        "jitQuiescenceAchieved": true,
        "measurementRestarts": 0,
        "splitHalfDrift": 0.0042
      }
    }
  ]
}
```

All timing values are in **nanoseconds**. Property names use camelCase.

Percentile values are now emitted in a `percentiles` array of `{ percentile, value }` objects (replacing the old `p95`/`p99` scalar properties). The set of reported percentiles is controlled by `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. When `EnableHistogram` is `true` (default), a `histogram` object with `min`, `max`, `sampleCount`, and `buckets` (array of `{ lower, upper, count }`) is also included.

### Which sample set each statistic describes

The result carries **two populations**, and `tailMetricsBasis` says which basis the tail metrics used. Under the default `raw`, the order statistics — `min`, `max`, `percentiles` and `histogram` — describe the **full pre-trim** sample set, while `mean`, `median`, `standardDeviation`, `standardError`, `marginOfError`, `coefficientOfVariation`, the confidence intervals and `measuredIterations` always describe the **trimmed (inlier)** set. That is deliberate: the outlier fence removes exactly the slow tail P99/Max exist to describe (see [Descriptive statistics](../statistics/descriptive.md)). But it means the two groups are not directly comparable — a consumer that displays both should label which is which, and `outlierDetector` names the detector that drew the line.

### The `autoTune` object

`autoTune` records what the [adaptive measurement loop](../statistics/measurement.md#the-measurement-loop) decided for this benchmark. It is `null` on dry-run and errored results.

| Group | Fields |
| --- | --- |
| **What it resolved** | `resolvedWarmup`, `resolvedSamples`, `opsPerSample`, `initialOpsPerSample` (the pre-recalibration cold K, or `null`), `totalBodyInvocations` |
| **Why it stopped** | `warmupStop` (`settled` / `maxCeiling` / `explicitCount` / `wallClockCap`), `sampleStop` (adds `ciTargetMet` and `driftUnresolved`) |
| **How well it converged** | `achievedRelativeCiWidth` (on the **raw** stream — see the caveat below), `ciWidthSeries` (the convergence trace, one entry per cadence check), `tuningWallClock` |
| **Host and stability** | `jitterMetric`, `outlierDetectorSwitched`, `measurementRestarts`, `splitHalfDrift` |
| **Warmup and tiered compilation** | `warmupTimeFloorMet`, `warmupElapsedNs`, `warmupCurve`, `warmupSampleInterval`, `warmupJitCompiledMethods`, `warmupJitCompilationTime`, `warmupJitCompiledIlBytes`, `jitLastChangeAtNs`, `jitQuiescenceAchieved` |

> **`achievedRelativeCiWidth` and `marginOfError` measure different things.** The former is the CI half-width the loop achieved on the **raw** stream at its stop decision; the latter is recomputed on the **trimmed** set. When the outliers carry most of the variance the two diverge sharply — a benchmark can report `marginOfError` at ±1% of the mean next to an `achievedRelativeCiWidth` of `1.05`. That is not a contradiction, but treat the trimmed margin as optimistic whenever `sampleStop` is not `ciTargetMet`.

`warmupCurve` is the mean per-op time of each warmup batch, oldest first — the shape of tiered compilation landing, since a body promoted from tier-0 to tier-1 (and re-optimized again under dynamic PGO) gets faster in steps. `warmupSampleInterval` gives the warmup iterations between consecutive points, so the array can be plotted against a real iteration axis. The array is bounded at 512 points: longer warmups are decimated by a doubling stride, keeping the points evenly spaced and the shape intact at coarser resolution. It is empty for pinned `warmupIterations` (which runs no plateau detection) and when `IncludeSamples` is off.

`jitLastChangeAtNs` is how far into warmup the JIT last compiled anything. With the body under continuous load that is typically the promotion of its own hot path, which makes it the closest thing to a tier-up marker to draw on the curve; compare it against `warmupElapsedNs` to see how much quiet time followed. The three `warmupJit*` counters are process-wide `System.Runtime.JitInfo` deltas, so in an in-process run the first benchmark to execute absorbs most of the startup compilation and later ones see almost none — that is real, and since benchmark order is randomised it is a large part of why the same benchmark's warmup differs between runs.

`totalDuration` is end-to-end wall-clock (warmup + pre-measure GC + measured loop); `measuredDuration` is the measured loop only. `measuredDuration <= totalDuration` always; the gap is dominated by warmup iterations and the pre-measure `GC.Collect`.

The `detail` and `profile` fields in the envelope report the active detail level (`simple`, `standard`, or `advanced`) and measurement profile. The result records always contain all available fields regardless of detail level.

## Notes

- The output directory is created automatically if it does not exist.
- `BenchmarkResult` is serialised with all properties, including `ConfidenceIntervalLower` and `ConfidenceIntervalUpper` (computed from `Mean ± MarginOfError`).
- The `autoTune` object is `null` for dry-run and errored results; for pinned runs the stop reasons are `explicitCount`.

## Using with Benchmark (Single mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToJsonAsync("results/");
await result.ToJsonAsync("results/", "benchmarks.json");
```

## CLI usage (BenchmarkHarness)

```bash
dotnet run -- --reporter json
dotnet run -- --reporter json --output ./results
```


---

# reading-your-results.md

---
title: Reading Your Results
description: How to interpret every column, indicator, and warning in NBenchmark's output.
order: 0
---

# Reading Your Results

This page explains what you see in the console output and what to do with it. For the mathematical detail behind any number, follow the links to the statistics pages.

## Console output example

```
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 342.1 ns       Mean: 348.7 ns
  │  Ops/s:  2.87 Mops/s    Median ops/s: 2.92 Mops/s
  │  P95: 361.2 ns  P99: 378.5 ns  P99.9: 380.0 ns
  │  StdDev: 8.3 ns         CV:   2.38%
  │  Error:  ±3.1 ns (0.89% of Mean)
  │  CI:     [345.6 ns … 351.8 ns] (95%)
  │  Alloc/op: 0 B
  │
  └─────────────────────────────────────────────────
```

In suite mode, the console reporter adds a comparison table with Ratio, Sig, and Magnitude columns. See [Console Reporter](./console-reporter.md) for the full table layout.

## The columns

### Median

The middle value when all measurements are sorted. This is the most reliable single number to compare two benchmarks because it ignores extreme outliers. If one benchmark has a lower median than another, it is generally faster.

See [Descriptive Statistics: Median](../statistics/descriptive.md#median).

### Mean

The arithmetic average. Close to the median for stable code; further away when timings vary widely. The mean is used to compute the confidence interval (the Error column).

See [Descriptive Statistics: Mean](../statistics/descriptive.md#mean).

### Error

The margin of error on the mean at the configured confidence level (default 95%). Shown as `±X (Y%)` - the absolute margin in nanoseconds followed by the margin as a percentage of the mean.

A small Error (e.g. under 1%) means the mean is precisely estimated. A large Error means your measurements are highly variable. In auto-sampling mode, NBenchmark keeps collecting samples until the Error meets the precision target, so a wide interval usually points to genuine run-to-run variability rather than too few samples.

**What to do about a large Error:**

- Check whether something external is interfering (background processes, thermal throttling)
- Use the `Thorough` preset to demand a tighter target
- If you pinned `Iterations`, raise the count or return to auto mode
- See [Troubleshooting](../troubleshooting.md) for more remedies

See [Descriptive Statistics: Confidence Interval](../statistics/descriptive.md#confidence-interval-on-the-mean).

### StdDev and CV

**StdDev** (standard deviation) measures how spread out your measurements are. A high StdDev relative to the mean means your timings are inconsistent.

**CV** (coefficient of variation) is StdDev divided by the mean - a dimensionless measure. A CV of 0.05 means the standard deviation is 5% of the mean. A CV of 0.5 or higher indicates high variability and the results should be treated with caution.

See [Descriptive Statistics](../statistics/descriptive.md#coefficient-of-variation).

### P95 / P99 / P99.9

Percentiles tell you about the distribution tail. P95 means 95% of individual measurements completed within this time. These are important for latency-sensitive code where you care about worst-case behaviour, not just the average.

The set of reported percentiles is configurable via `--percentiles` or `MeasurementOptions.ReportedPercentiles`.

See [Descriptive Statistics: Percentiles](../statistics/descriptive.md#percentiles).

### Ratio (suite mode)

Speed relative to the baseline. `0.75x` = 25% faster; `2.0x` = twice as slow. The baseline is either the benchmark you designated with `WithBaseline` or the fastest benchmark among those measured the same way as the rest of the table.

#### `n/a` in the Ratio column

A ratio is only formed between two rows measured under the same runtime configuration. When a row was not - typically a `[InProcess]` benchmark sitting in a table of isolated ones - its ratio reads `n/a`, an **Iso** column appears saying which rows were isolated, and a footer explains the withholding.

This is not the tool being coy. Runtime configuration is the dominant term in a small measurement: on four benchmark bodies of provably identical cost, the difference between an in-process reading and an isolated one moved the reported median by about 3.3x. Dividing one by the other produces a large, confident, entirely fabricated speedup. Compare rows measured the same way, or drop `[InProcess]` so the whole group is isolated.

### Sig (suite mode)

| Symbol | Meaning |
| --- | --- |
| **✓** | The difference from the baseline is statistically significant (p < 0.05). It is very unlikely to be noise. |
| **✗** | The difference is not statistically significant. You cannot confidently conclude one is faster than the other. |
| (blank) | The benchmark is the baseline, or significance was not tested (fewer than 2 samples in a group). |

**What to do:**

- A ✓ with a small Ratio (e.g. `1.01x`) means the difference is statistically real but may be too small to matter in practice. Check the Magnitude column.
- A ✗ with a large Ratio (e.g. `1.5x`) means the measurements are too noisy to tell. Try reducing noise (see [Tuning for noisy CI](../reference/configuration.md#tuning-for-noisy-ci-environments)) or collecting more samples.

See [Significance Testing](../statistics/significance.md) for the full detail.

### Magnitude (suite mode)

Classifies the effect size using Cliff's delta:

| Label | What it means |
| --- | --- |
| Negligible | The two distributions overlap almost completely. The difference is tiny. |
| Small | A modest but detectable shift. |
| Medium | A clear, practically meaningful difference. |
| Large | The distributions barely overlap. A very strong difference. |

The sign convention is: positive = candidate is slower than baseline (shown in red in the console reporter); negative = candidate is faster (shown in green).

**What to do:** A statistically significant result (✓) with a Negligible magnitude means the difference is real but too small to care about. Focus on results with Small, Medium, or Large magnitudes.

See [Significance Testing: Cliff's Delta](../statistics/significance.md#technical-detail-cliffs-delta).

### Alloc/op

The mean heap allocation per operation. Zero allocations in the hot path typically means less GC pressure and more predictable latency. If you see unexpected allocations, check for boxing of value types, LINQ overhead, or string formatting in the measured code.

See [Allocation Measurement](../statistics/allocations.md).

## The auto-tune diagnostic line

In Advanced detail mode (`--detail advanced`), the output includes an auto-tune line:

```
auto-tuned: K=64, warmup=12, samples=47, CI half-width=1.8%, jitter=0.03
```

| Field | Meaning |
| --- | --- |
| K | Ops per sample - how many back-to-back invocations were timed together |
| warmup | How many warmup samples were collected before measurement started |
| samples | How many measured samples were collected |
| CI half-width | The achieved confidence interval half-width when sampling stopped |
| jitter | The pre-flight jitter metric (lower is better; < 0.05 = quiet host) |

If the jitter metric is high (e.g. > 0.10) and the outlier detector was auto-switched, you will also see a warning explaining the switch.

See [Measurement: The measurement loop](../statistics/measurement.md#the-measurement-loop).

## The bimodal-distribution warning

If you see a warning like:

```
⚠ MyBench.FastPath: 5 discarded outlier(s) form a distinct cluster near 502 ns rather than
  scattered noise - possible bimodal distribution; investigate this tail latency
```

This means the slow samples were **not** random noise - they were a repeatable second execution profile (e.g. a cache miss, lock contention, or GC pause). The reported median describes the common case; the cluster centre describes a latency a real user will also hit.

**What to do:**

- Do not ignore it. The warning is telling you something real about your code's performance distribution.
- Re-run with `OutlierMode.None` to see the full distribution.
- Investigate the cause with a profiler.
- If you suspect GC, try `--profile independent`.

See [Outlier Trimming: Bimodal-distribution warning](../statistics/outliers.md#bimodal-distribution-warning).

## The Ops/s column

Operations per second, derived from the mean timing. `Median ops/s` is derived from the median. These are useful for throughput-oriented comparisons.

## When to trust the numbers

- **Low CV** (< 5%) and **small Error** (< 1%): the benchmark is stable and the numbers are reliable.
- **High CV** (> 20%) or **large Error** (> 5%): the benchmark is noisy. See [Troubleshooting](../troubleshooting.md) for configuration remedies.
- **Bimodal warning**: the median describes the common case, but a real second execution profile exists. Investigate before trusting the numbers as representative of all calls.

## See also

- [Key Concepts](../getting-started/key-concepts.md) - understand what the numbers mean conceptually
- [Descriptive Statistics](../statistics/descriptive.md) - the formulas behind every field
- [Significance Testing](../statistics/significance.md) - how Sig and Magnitude are computed
- [Outlier Trimming](../statistics/outliers.md) - how outliers are detected and removed
- [Measurement](../statistics/measurement.md) - how the adaptive loop works
- [Troubleshooting](../troubleshooting.md) - fix common measurement problems


---

# index.md

---
title: Output
description: Reporters and output control - console, JSON, Markdown, CSV, custom reporters, and reading your results.
order: 5
---

# Output

Reporters consume the finished `BenchmarkResult` list and produce output - terminal tables, Markdown files, CSVs, or JSON. You can attach as many reporters as you like to a single run.

## In this section

- **[Reading Your Results](./reading-your-results.md)** - interpret every column, indicator, and warning in the output.
- **[Console Reporter](./console-reporter.md)** - rich terminal table with colour and a bar chart.
- **[Markdown Reporter](./markdown-reporter.md)** - `.md` file with a formatted results table.
- **[CSV Reporter](./csv-reporter.md)** - `.csv` file with all statistics, suitable for post-processing.
- **[JSON Reporter](./json-reporter.md)** - `.json` file with full structured results.
- **[Report Detail Levels](./report-detail-levels.md)** - Simple, Standard, and Advanced detail modes.
- **[Custom Reporters](./custom-reporters.md)** - implement and register your own reporter.

## How reporters work

All reporters implement `IReporter`:

```csharp
public interface IReporter
{
    string Name { get; }

    Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default);
}
```

The `Name` property identifies the reporter for the `--reporter` CLI flag and the `--output` directory rewriting. Built-in reporters return their canonical name (`"json"`, `"markdown"`, `"csv"`, `"console"`). Custom reporters may return any unique string.

Reporters are called after all benchmarks in the run have completed. They receive the full result list including any errored benchmarks.

## Attaching reporters

### BenchmarkSuite (Suite mode)

```csharp
await new BenchmarkSuite("name")
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithReporter(new CsvReporter("results/"))
    .RunAsync();
```

### BenchmarkHarness (Harness mode)

```csharp
BenchmarkHarness.Create(args)
    .WithReporter(new ConsoleReporter())
    .WithReporter(new JsonReporter("results/"))
    .RunAsync();
```

### Benchmark (Single mode) - extension methods

```csharp
var result = Benchmark.Run(() => MyMethod());

await result.ToMarkdownAsync("results/");
await result.ToCsvAsync("results/");
await result.ToJsonAsync("results/");
```

## Available reporters

| Reporter | Package | Output |
| --- | --- | --- |
| [ConsoleReporter](./console-reporter.md) | `NBenchmark.Reporters.Console` | Rich terminal table with colour and a bar chart |
| [MarkdownReporter](./markdown-reporter.md) | `NBenchmark` | `.md` file with a formatted results table |
| [CsvReporter](./csv-reporter.md) | `NBenchmark` | `.csv` file with all statistics, suitable for post-processing |
| [JsonReporter](./json-reporter.md) | `NBenchmark` | `.json` file with full structured results |

## Output path validation

File reporters validate that the output directory is **under the current working directory**. Paths outside the CWD (e.g. `/tmp/results` or `../../other-project`) are rejected with an `ArgumentException`. This prevents accidental writes outside the project directory.

```csharp
// Works - relative path under CWD
new MarkdownReporter("results/")

// Throws ArgumentException - outside CWD
new MarkdownReporter("/tmp/results/")
```

The output directory is created automatically if it does not exist.

## Using the CLI reporter flag

With `BenchmarkHarness`, the `--reporter` CLI flag adds reporters by name:

```bash
dotnet run -- --reporter markdown --output ./results
dotnet run -- --reporter csv
dotnet run -- --reporter json
dotnet run -- --reporter console   # works when NBenchmark.Reporters.Console is referenced
```

The `--reporter` flag constructs reporters through `ReporterRegistry.TryCreate`, which handles both built-in reporters (`json`/`markdown`/`csv`) and any reporters self-registered by external packages.

External packages (like `NBenchmark.Reporters.Console`) self-register via `[ModuleInitializer]` + `ReporterRegistry.Register()`. The `--reporter flag` discovers available reporters automatically - no per-reporter code changes needed in `BenchmarkHarness`.

If you reference an unknown reporter name, the host prints the list of available reporters plus a hint about the `console` package.

## Detail levels

Reporters support three detail levels - **Simple** (default), **Standard**, and **Advanced** - that control how much statistical information is included in the output. Set the level via `WithDetail(ReportDetail.Standard)` on both `BenchmarkHarness` and `BenchmarkSuite`, or via the `--detail standard` CLI flag in harness mode. See the [Report Detail Levels guide](./report-detail-levels.md) for the full column reference.

## Report format versioning

Every file-writing reporter stamps its output with two independent numbers. They exist for whoever
stores NBenchmark output over time - a CI trend dashboard, a regression script, a spreadsheet.
NBenchmark itself never reads its own reports back.

| Stamp | Question it answers | Bumped when |
| --- | --- | --- |
| `schemaVersion` | Can my parser still read this file? | A field is renamed, removed, or changes type; the envelope is restructured. **Not** bumped for added fields. |
| `measurementEpoch` | Can I plot this number next to that one? | NBenchmark changes what a benchmark reports: harness overhead, the default runtime profile, or the definition of a reported statistic. |

Both are `1` today. They are separate because they move independently, and the case that proves it
is the one that prompted them: replacing NBenchmark's boxing dispatch path with typed delegates
moved the calibration standard from **9.34 ns / 24 B per op to 2.53 ns / 0 B** while leaving the
JSON shape byte-for-byte identical. A schema version alone would have said nothing had changed. A
dashboard would have drawn a 3.7x improvement that no application code earned.

Where the stamps appear:

- **JSON** - `schemaVersion` and `measurementEpoch`, the first two fields of the envelope, so a
  consumer can decide whether to read the rest without parsing the rest.
- **CSV** - `SchemaVersion` and `MeasurementEpoch` columns, alongside `Detail`, `Profile`,
  `RuntimeProfile` and `RuntimeKnobs`.
- **Markdown** - a `> Format:` line in the header block.

### Consuming them

Compare the epoch before comparing numbers, and treat a mismatch as a discontinuity rather than a
result:

```python
import json

with open("benchmarks.json") as f:
    report = json.load(f)

# An absent stamp is not epoch 0. The file predates the concept, and nothing is known
# about whether its numbers line up with anything - reject it rather than assume.
if "measurementEpoch" not in report:
    raise ValueError("report predates measurement epochs; not comparable")

if report["measurementEpoch"] != baseline_epoch:
    raise ValueError(
        f"epoch {report['measurementEpoch']} != baseline {baseline_epoch}; "
        "the harness changed, so a diff would measure NBenchmark, not your code"
    )
```

The constants are `NBenchmark.Reporters.ReportFormat.SchemaVersion` and
`ReportFormat.MeasurementEpoch` if you are writing a [custom reporter](./custom-reporters.md) and
want to stamp it the same way.

## Writing a custom reporter

See the [Custom Reporters](./custom-reporters.md) page for a step-by-step guide to implementing `IReporter`, registering it with `ReporterRegistry`, and using `BenchmarkTable` for comparison output. That page also documents **auto-attached reporters** (`ReporterRegistry.RegisterAutoAttach`) - side-effect reporters that fire on every run after the user's explicit reporters, with no opt-in required.


---

# custom-reporters.md

---
title: Custom Reporters
description: Implement IReporter to create your own output format, and register it with ReporterRegistry for CLI use.
order: 6
---

# Custom Reporters

## Writing a custom reporter

Implement `IReporter` from the `NBenchmark` package:

```csharp
public sealed class MyReporter : IReporter
{
    public string Name => "my-reporter";

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        foreach (var result in results.Where(r => !r.Errored))
        {
            Console.WriteLine($"{result.Name}: median={result.Median:F0}ns");
        }
    }
}
```

Add it to your harness or suite with `.WithReporter(new MyReporter())`.

If you want your custom reporter to be usable from the `--reporter` CLI flag, register it with the global `ReporterRegistry`:

```csharp
using NBenchmark.Reporters;

// In a static constructor or [ModuleInitializer]:
ReporterRegistry.Register("my-reporter", "Custom output", _ => new MyReporter());
```

After registration, `--reporter my-reporter` works from the CLI.

## Auto-attached reporters

Reporters come in two flavours:

- **Explicit opt-in** reporters, registered via `ReporterRegistry.Register`. These only fire when the user passes `--reporter <name>` on the CLI or calls `.WithReporter(...)` programmatically. The built-in `json`, `markdown`, and `csv` reporters, and the optional `console` reporter, are all explicit opt-in.
- **Auto-attached** reporters, registered via `ReporterRegistry.RegisterAutoAttach`. These fire on **every** run, after the user's explicit reporters, with no opt-in required. They are designed for side-effect reporters that integrate with an external system - for example, a reporter that writes run results to a file inbox for a separate Studio process to ingest.

The two registration paths are mutually exclusive: the same name cannot be registered via both `Register` and `RegisterAutoAttach` (case-insensitive). Auto-attached reporters are listed separately on `ReporterRegistry.AutoAttached` and shown in the `--reporter` flag's help line so users can see what is auto-firing.

### Self-registering an auto-attached reporter

External packages self-register their auto-attached reporters via a `[ModuleInitializer]` that calls `ReporterRegistry.RegisterAutoAttach`, mirroring how `NBenchmark.Reporters.Console` self-registers the `console` reporter via `Register`. The `[ModuleInitializer]` runs when the package's assembly is loaded by the host process, which happens on the first call to `ReporterRegistry.Available`, `ReporterRegistry.AutoAttached`, or `ReporterRegistry.CreateAutoAttachedReporters`.

```csharp
using System.Runtime.CompilerServices;
using NBenchmark.Reporters;

namespace MyPackage.Reporters;

internal static class MyReporterRegistration
{
    [ModuleInitializer]
    internal static void Register() =>
        ReporterRegistry.RegisterAutoAttach(
            "my-sink",
            "Writes run results to a file inbox for MyTool to ingest",
            (_, detail) => new MySinkReporter { Detail = detail });
}
```

Reference the package from the user's benchmark project and the reporter fires on every `BenchmarkHarness.RunAsync` and `BenchmarkSuite.RunAsync` call - no per-run setup required.

### Dedup with explicit reporters

If the user also adds an auto-attached reporter as an explicit reporter (via `--reporter <name>` or `.WithReporter(...)` with an instance whose `Name` matches the auto-attached reporter's name), the auto-attached one is skipped for that run so the reporter does not fire twice. The dedup is by canonical name, case-insensitive.

### Resilience

A misbehaving auto-attached reporter cannot kill the run. Each auto-attached reporter's `ReportAsync` is wrapped in try/catch with `Trace.TraceWarning`; the exception is logged and the run continues to the next reporter. This mirrors `CompositeMeasurementObserver`'s per-dispatch isolation. The user's `BenchmarkResult` list is still returned from `RunAsync` and any subsequent explicit reporters still run.

### CI / opt-out convention

Because auto-attached reporters fire on every run by design, packages that ship one are expected to follow a standard opt-out convention so CI pipelines and explicit opt-out users are not polluted with side-effect writes:

- The reporter's `ReportAsync` should no-op when the `CI=true` environment variable is set (the standard convention set by GitHub Actions, GitLab CI, Azure Pipelines, etc.).
- The reporter should also accept a package-specific disable env var (e.g. `NBENCHMARK_MYTOOL_DISABLE=1`) as an explicit escape hatch for users who want the package referenced locally but do not want every benchmark run written to the sink.
- Both guards should run before any directory creation or file I/O so a CI runner with neither env var set pays only the cost of two `GetEnvironmentVariable` calls.

The contract is convention, not enforced by NBenchmark - the package owning the reporter is responsible for honouring it.

## Using BenchmarkTable in a custom reporter

For reporters that produce comparison tables, use `BenchmarkTable.Build(results)` rather than working with `IReadOnlyList<BenchmarkResult>` directly. It centralises the logic you would otherwise duplicate:

- **Baseline selection** - picks the first result marked `[Baseline]`, or falls back to the fastest (lowest median) if none is marked.
- **Ratio computation** - `row.Ratio` is `result.Median / baseline.Median`, or `NaN` for errored results or single-benchmark runs.
- **Significance labels** - `row.SignificanceLabel` is `"✓"` (significant), `"✗"` (not significant), or `""` (not applicable).
- **Ordering** - rows are sorted by median ascending.
- **Run metadata** - `table.RunAtUtc`, `table.WarmupIterations`, `table.MeasuredIterations`, `table.ConfidenceLevel`, `table.OutlierDetector` (the detector's display name, e.g. `"IQR fence (1.5×)"` or `"MAD (3×)"`), `table.SignificanceTestName` (the pairwise test's name), and `table.TotalDuration` are available for building a header without picking fields from individual results.
- **Omnibus verdict** - `table.Omnibus` is non-`null` when an omnibus test ran (Kruskal-Wallis across three or more groups). It exposes `TestName`, `Statistic`, `DegreesOfFreedom`, `GroupCount`, `PValue`, and `Verdict` so you can render a single across-all-groups line.

```csharp
public async Task ReportAsync(
    IReadOnlyList<BenchmarkResult> results,
    CancellationToken cancellationToken = default)
{
    var table = BenchmarkTable.Build(results);

    Console.WriteLine(
        $"Run at {table.RunAtUtc} UTC - {table.WarmupIterations} warmup / {table.MeasuredIterations} measured");

    foreach (var row in table.Rows)
    {
        if (row.Errored)
        {
            Console.WriteLine($"{row.Name}: ERROR - {row.ErrorMessage}");
            continue;
        }

        var sig = row.SignificanceLabel is "" ? "" : $" {row.SignificanceLabel}";
        Console.WriteLine($"{row.Name}{sig}: {row.Median:F0} ns  ratio={row.Ratio:F2}x");
    }
}
```


---

# console-reporter.md

---
title: ConsoleReporter
description: Rich terminal output with colour-coded tables, significance indicators, and a bar chart.
order: 1
---

# ConsoleReporter

`ConsoleReporter` renders results to the terminal as a colour-coded table using [Spectre.Console](https://spectreconsole.net/). It is part of the `NBenchmark.Reporters.Console` package.

## Setup

```bash
dotnet add package NBenchmark.Reporters.Console
```

```csharp
using NBenchmark.Reporters.Console;

.WithReporter(new ConsoleReporter())
```

### CLI usage

When the `NBenchmark.Reporters.Console` package is referenced, `ConsoleReporter` self-registers via `[ModuleInitializer]` and becomes available through the `--reporter console` CLI flag:

```bash
dotnet run -- --reporter console
```

No explicit `.WithReporter(new ConsoleReporter())` call is needed when using the CLI - the host discovers it automatically through `ReporterRegistry`.

## Example output

```
── BENCHMARK RESULTS  2026-06-06 03:40:00 UTC ──────────────────────────────────

  Benchmark              Median   Mean     Ops/s       Ratio                  Sig    Mag     Alloc/op
  Compute                300 ns   275 ns   3.64 Mops/s ████████ 0.75x         ✓      lrg     -
  Baseline (baseline)    400 ns   376 ns   2.66 Mops/s ████████████ baseline  -      -       -

── Precision & Tail Latency ────────────────────────────────────────────────────
... (error/stddev/cv/dynamic percentile columns)

── Interpretation ──────────────────────────────────────────────────────────────
Omnibus: not run (fewer than 3 comparable groups)
Significance: Mann-Whitney U (p < 0.05)
Outliers: IQR fence (1.5×)
Effect metric: Cliff's δ (Romano neg/small/med/large labels)
Profile: realistic (no per-iteration GC, no between-benchmark GC, alloc tracking on)
2 benchmark(s) · 0.0s total · CI 95%

Compute: auto-tuned: 190 samples × 1 ops, warmup 40, CI ±1.8%
Baseline: auto-tuned: 190 samples × 1 ops, warmup 40, CI ±1.9%

── Warnings ────────────────────────────────────────────────────────────────────
... (only shown when present)
```

When there are two or more benchmarks, a bar chart of median timings is also displayed below the table.

When **three or more** benchmarks are compared, the per-row Sig column shows the post-hoc pairwise verdict (candidate versus baseline, Holm-Bonferroni corrected) and a single omnibus line is printed above the footer, summarising the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) verdict across all groups:

```
Omnibus Kruskal-Wallis across 3 groups: H(2) = 7.20, p = 0.027 → significant

Significance: Kruskal-Wallis (p < 0.05)
Outliers: MAD (3×)
Effect metric: Cliff's δ (Romano neg/small/med/large labels)
Profile: realistic (no per-iteration GC, no between-benchmark GC, alloc tracking on)
3 benchmark(s) · 0.0s total · CI 95%
```

After the Interpretation section, ConsoleReporter prints a grey `auto-tuned: …` line per benchmark summarising what the [adaptive measurement loop](../statistics/measurement.md#the-measurement-loop) resolved - the measured-sample count, ops-per-sample (K), warmup length, and the achieved CI half-width. Pinned runs still show the line, with the resolved counts you set.

After the comparison and precision tables, ConsoleReporter prints an **Interpretation** section with omnibus/significance context, outlier mode, effect-metric semantics, and the measurement profile. If warnings exist, they are shown in a separate **Warnings** section below the auto-tune lines. The final summary line shows benchmark count, total run time, and confidence interval.

## Columns

| Column | Description |
| --- | --- |
| **Benchmark** | Name of the benchmark. Colour-coded: green (≤ 5% slower than baseline), yellow (≤ 50% slower), red (> 50% slower). Baseline is shown in bold. |
| **Median** | Median timing. |
| **Mean** | Arithmetic mean. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). `-` for errored or dry-run results. |
| **Ratio** | Visual bar plus ratio relative to the baseline. Green for faster, yellow for moderately slower, red for significantly slower. The baseline cell shows `baseline`. |
| **Sig** | **✓** = difference from baseline is statistically significant; **✗** = not significant; **-** = not applicable (baseline or significance not tested). |
| **Mag** | Strategy-defined qualitative effect label. With the built-in Mann-Whitney tests this is Cliff's delta classified by [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size): `neg` (abs(δ) < 0.147), `sml` (< 0.33), `med` (< 0.474), `lrg` (≥ 0.474). For `lrg`, the cell is bold-red when the candidate is slower and bold-green when faster. `-` for the baseline or when significance is not tested. See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| **Alloc/op** | Mean heap bytes per iteration (only visible when allocation tracking is enabled). |

An optional **Description** column appears if any benchmark has a `Description` set.

For [parameterized benchmarks](../features/parameterized-suite.md#reading-the-report), one column per parameter appears immediately after **Benchmark**, and the **Benchmark** cell shows the base method name. To save width, parametric tables label the comparison columns **Ratio**, **Sig** and **Mag**. When a single method is swept across parameter values, **Ratio** reports each point's scaling factor relative to the fastest point (the reference, shown as `baseline`); **Sig** and **Mag** stay `-`, because the engine does not test different workloads against one another. When a parameter group instead holds competing benchmarks, **Sig** and **Mag** carry the usual within-group significance and effect.

In Standard mode (`--detail standard` or `WithDetail(ReportDetail.Standard)`), the full multi-section output is shown: comparison table, Precision & Tail Latency, auto-tune, and Interpretation.

In Advanced mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`), each benchmark row is followed by an indented stats block.

## Adding progress display

`ConsoleBenchmarkProgress` displays warmup and measurement progress for each benchmark as it runs. It is independent of `ConsoleReporter` and can be used without it.

```csharp
using NBenchmark.Reporters.Console;

await new BenchmarkSuite("name")
    .WithWarmup(25)        // pin so the progress bar has an exact total
    .WithIterations(200)   // pin so the progress bar has an exact total
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

Pinning the warmup and sample counts gives the progress bar an exact total to track. With the default auto-resolved counts the bar fills toward the `MaxSamples` ceiling and the run usually stops earlier, once the confidence interval is tight enough.

Progress output is a live, updating line per benchmark:

```
──────────────── Running 2 benchmark(s) ────────────────

  [1/2] Compute ████████████░░░░░░░░ 60% measuring (120/200) ETA 0.4s
  ✓ Compute 12.4 ns (0.8s)
  ✓ Baseline 41.9 ns (1.1s)

──────────────── Completed in 1.9s ────────────────
```

## Using with Benchmark (Single mode)

```csharp
using NBenchmark.Reporters.Console;

var result = Benchmark.Run(() => MyMethod());
await result.PrintAsync();
```

`PrintAsync` runs the single result through `ConsoleReporter` and renders a table.

## Printing markup from the summary line

The summary line at the bottom always shows the confidence level from the first successful result. If all benchmarks errored, only a list of error messages is shown.


---

# markdown-reporter.md

---
title: MarkdownReporter
description: Save benchmark results to a Markdown file suitable for committing to source control or publishing.
order: 2
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
> Runtime: **steady-state** (tiered=off pgo=off r2r=off)
> Format: schema 1, measurement epoch 1 (numbers are comparable only with the same epoch)

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

Percentile columns (P95, P99, etc.) are dynamic -- they appear only when the corresponding percentiles are configured via `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. With the default set of `[0.50, 0.95, 0.99, 0.999, 1.0]`, columns P95 and P99 are emitted in the tail-latency table (P50 is already shown as Median; Max appears as a separate stat).

---

### Interpretation

**Omnibus**: not run (fewer than 3 comparable groups).

- Significance: Mann-Whitney U (p < 0.05)
- Outliers: IQR fence (1.5×)
- Effect metric: Cliff's δ (Romano neg/small/med/large labels)
```

When **three or more** benchmarks are compared, the Sig column shows the post-hoc pairwise verdict (candidate versus baseline, Holm-Bonferroni corrected) and the **Interpretation** section includes an omnibus line summarising the [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) verdict across all groups:

```markdown
**Omnibus (Kruskal-Wallis)** across 3 groups: H(2) = 7.20, p = 0.027 → significant
```

## Columns

| Column | Description |
| --- | --- |
| **Benchmark** | Benchmark name. |
| **Median** | Median timing. |
| **Mean** | Arithmetic mean. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). `-` for errored or dry-run results. |
| **Ratio** | Speed relative to the baseline. |
| **Scale** | Visual bar scaled to the slowest successful benchmark. |
| **Sig** | `✓` = significant, `✗` = not significant, `-` = not applicable. |
| **Magnitude** | Strategy-defined qualitative effect label. With the built-in Mann-Whitney tests this is Cliff's delta classified by [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size): `neg` (abs(δ) < 0.147), `small` (< 0.33), `med` (< 0.474), `large` (≥ 0.474). `-` for the baseline or when significance is not tested. See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| **Alloc/op** | Mean bytes allocated per iteration, or `-` if not measured. |

## Notes

- Results are sorted by median (fastest first).
- For [parameterized benchmarks](../features/parameterized-suite.md#reading-the-report), one column per parameter appears after **Benchmark** (which shows the base method name). When a single method is swept across parameter values, the **Ratio** column reports each point's scaling factor relative to the fastest point (the reference, shown as `baseline`), while **Sig** and **Magnitude** stay `-`. When a parameter group holds competing benchmarks, **Sig** and **Magnitude** carry the usual within-group significance and effect.
- Errored benchmarks are listed with a `-` in the Error, Ratio, and Sig columns. The Median, Mean, and StdDev columns show `0.0 ns`. Percentile columns show empty cells.
- The output directory is created automatically if it does not exist.
- The report order is: Comparison -> Precision & Tail Latency -> (optional) Distribution Details -> Interpretation -> (optional) Warnings.
- In Standard mode (`--detail standard` or `WithDetail(ReportDetail.Standard)`), the full multi-section output is shown: comparison table, Precision & Tail Latency, and Interpretation.
- In Advanced mode (`--detail advanced` or `WithDetail(ReportDetail.Advanced)`), a per-benchmark details section is appended after the table showing quartiles, fences, CI, margin percent, CV, skewness, kurtosis, MAD, and allocation breakdown, followed by an `auto-tuned: …` line summarising the adaptive loop's decisions (resolved samples × ops-per-sample, warmup length, achieved CI half-width).

## Using with Benchmark (Single mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToMarkdownAsync("results/");
await result.ToMarkdownAsync("results/", "benchmarks.md");
```

## CLI usage (BenchmarkHarness)

```bash
dotnet run -- --reporter markdown
dotnet run -- --reporter markdown --output ./results
```

When `--output` is specified, files are written inside that directory.


---

# report-detail-levels.md

---
title: Report Detail Levels
description: Understand the difference between Simple, Standard, and Advanced detail modes, and how to control what reporters display.
order: 5
---

# Report Detail Levels

NBenchmark supports three report detail levels that control how much statistical information reporters display. **Simple** is the default; **Standard** adds the full multi-section output; **Advanced** adds a per-benchmark stats block with the full distribution summary.

## Simple mode (default)

Simple mode shows a compact table with the essential information an average developer needs to know whether their code performs well or how it compares to other implementations:

| Column | Description |
| --- | --- |
| **Benchmark** | Benchmark name. |
| **Median** | Median timing. |
| **Ops/s** | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). |
| **Ratio** | Visual bar plus ratio relative to the baseline. |
| **Sig** | ✓ = significant, ✗ = not significant, - = not applicable. |
| **Alloc/op** | Mean bytes allocated per iteration, or - if not measured. |

A one-line footer shows the benchmark count, total duration, and confidence level. No statistical jargon, no auxiliary tables.

## Standard mode

Standard mode shows the same comparison table with additional columns (Mean, Mag, Description) plus several auxiliary sections:

- **Precision & Tail Latency** table: Error (±CI), StdDev, CV, and upper-tail percentiles (P95, P99, etc.).
- **Diagnostics** table (when diagnostics are enabled): GC Gen0/Gen1/Gen2 collection counts, heap info, CPU/wall ratio, and exceptions per op. See [Diagnostics](../statistics/diagnostics.md).
- **Launch Aggregation** table (when `LaunchCount > 1`): cross-launch mean, stddev, median, and CI.
- **Interpretation** block: omnibus verdict, significance test name, outlier detector, effect metric summary, and measurement profile.
- **Auto-tune summary** lines: resolved warmup, sample count, ops-per-sample, and achieved CI half-width.
- **Warnings** (when present).

This is the level for practitioners who want to understand variability and the statistical rigour behind the results.

## Advanced mode

Advanced mode shows everything in Standard **plus** a per-benchmark stats block. The console reporter prints each stats block below its row; the Markdown reporter emits a dedicated details section after the table. The stats block includes:

- **Outliers:** count of removed samples and the trimming method.
- **Range:** Min to Max spread.
- **Quartiles:** Q1, Q3, and IQR.
- **Fences:** Lower and upper fences (only for `IqrFence` mode).
- **Iterations:** pre-trim and post-trim sample counts and warmup count.
- **Confidence interval:** full CI bounds and margin percent of mean.
- **CV:** coefficient of variation as a percentage.
- **Skewness and Kurtosis:** shape of the distribution.
- **MAD:** median absolute deviation (scaled).
- **Percentiles:** the full set of configured percentile values (e.g. P50, P95, P99, P99.9, Max).
- **N:** post-trim sample count.
- **Allocation breakdown** (when `MeasureAllocations = true`): median, P95, and max allocation per iteration.
- **Diagnostics breakdown** (when diagnostics are enabled): GC collection counts, heap committed and fragmented bytes, CPU time and CPU/wall ratio, and exceptions per operation.

## Setting the detail level

### `WithDetail()` (Harness and Suite modes)

Both `BenchmarkHarness` and `BenchmarkSuite` expose a `WithDetail(ReportDetail)` method. The detail level is stamped onto all registered reporters, so calling `WithDetail` before or after `WithReporter` works in either order.

```csharp
// Harness mode
var host = BenchmarkHarness.Create(args);
host.WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Suite mode
var suite = new BenchmarkSuite("MySuite");
suite.WithDetail(ReportDetail.Standard)
     .WithReporter(new ConsoleReporter())
     .RunAsync();
```

### `--detail` flag (Harness mode)

```bash
dotnet run -- --detail advanced
dotnet run -- --detail standard
dotnet run -- --detail simple
```

| Value | Behaviour |
| --- | --- |
| `simple` | Compact table with the essential statistics. **(default)** |
| `standard` | Full comparison table plus Precision & Tail Latency, auto-tune, and Interpretation sections. |
| `advanced` | Same as standard plus a per-benchmark stats block with quartiles, fences, confidence interval, skewness, kurtosis, MAD, configured percentiles, and allocation breakdown. |

The `--detail` flag affects all registered reporters. JSON always emits the full record regardless of detail level.

### Single mode

Single mode (`Benchmark.Run` / `Benchmark.RunAsync`) always uses `Simple` detail and does not support `WithDetail()`.

## Reporter behaviour

| Reporter | Simple | Standard | Advanced |
| --- | --- | --- | --- |
| **Console** | 6-column table + counts footer | Full table + Precision & Tail Latency + Diagnostics + Interpretation + auto-tune | Standard + per-benchmark stats block (incl. diagnostics breakdown) |
| **Markdown** | 6-column table + counts footer | Full table + Precision & Tail Latency + Diagnostics + Interpretation | Standard + dedicated details section (incl. diagnostics breakdown) |
| **CSV** | 12 core columns (incl. GC counts) | 25 core columns (incl. GC counts) | 51 columns including quartiles, fences, shape stats, and full diagnostics |
| **JSON** | Full record (always) | Full record (always) | Full record (always) |

## See also

- [Reporters](./index.md) - available reporters and how to attach them
- [CLI Reference: `--detail`](../reference/cli.md#--detail-level) - full flag documentation
- [Descriptive Statistics](../statistics/descriptive.md) - what each field measures


---

# csv-reporter.md

---
title: CsvReporter
description: Save benchmark results to a CSV file for post-processing in Excel, Python, or other tools.
order: 3
---

# CsvReporter

`CsvReporter` writes results to a `.csv` file with all computed statistics, including the full confidence interval. It is part of the core `NBenchmark` package with no additional dependencies.

CSV output is well-suited for post-processing in spreadsheets, Python/pandas, R, or any tool that can read delimited data.

## Setup

```csharp
using NBenchmark.Reporters;

// Default - writes to the current directory with auto-naming
.WithReporter(new CsvReporter())

// Explicit directory
.WithReporter(new CsvReporter("results/"))

// Explicit directory and filename
.WithReporter(new CsvReporter("results/", "benchmarks.csv"))
```

### Constructor

```csharp
CsvReporter(string outputDirectory = ".", string? fileName = null)
```

- `outputDirectory` - The directory to write the file to. Created automatically if it does not exist. Must be under the current working directory.
- `fileName` - When `null` (the default), the reporter generates a timestamped filename to avoid overwriting previous runs. When specified, the exact filename is used (no counter or timestamp is appended).

### Auto-naming

When `fileName` is not provided, the reporter generates a filename that includes the UTC timestamp and a per-process counter:

```
benchmark-results-20260606-034000-001.csv
```

The counter increments each time `ReportAsync` is called within the same process.

### Explicit filename

Pass a `fileName` when you want a stable output path:

```csharp
new CsvReporter("results/", "benchmarks.csv")
```

When an explicit `fileName` is provided, subsequent calls to `ReportAsync` overwrite the same file.

## Output format

```csv
ClassName,Name,Median,OpsPerSecond,Ratio,Significant,AllocPerOp,Detail,Profile
"SortingBenchmarks","Compute",300.0,3636363.6,0.75,"true",96,simple,realistic
"SortingBenchmarks","Baseline",400.0,2660985.4,1.00,"",120,simple,realistic

Percentile columns (P95, P99, etc.) are dynamic -- they appear only in Standard and Advanced modes when the corresponding percentiles are configured via `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. With the default set of `[0.50, 0.95, 0.99, 0.999, 1.0]`, columns P95 and P99 are emitted. P50 and Max (1.0) are excluded from percentile columns because they are shown separately as Median and Max. Values are in nanoseconds. Empty cells indicate the percentile was not in the configured set or the row is errored.
```

All timing values are in **nanoseconds**. `EffectMetric` / `EffectValue` / `Magnitude` reflect the active significance strategy's effect output. With built-in Mann-Whitney tests, `EffectMetric` is `Cliff's δ`, `EffectValue` is signed (positive = candidate slower), and `Magnitude` is one of `neg`, `small`, `med`, `large` per the [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size) thresholds.

## Column reference

### Simple mode (16 columns)

| Column | Type | Description |
| --- | --- | --- |
| `ClassName` | string | Benchmark class name (double-quote escaped). |
| `Name` | string | Benchmark name (double-quote escaped). |
| `Median` | float | Median timing in nanoseconds. |
| `OpsPerSecond` | float | Mean operations per second (`1e9 / Mean` when timing is in nanoseconds). Empty for errored or dry-run results. |
| `Ratio` | float or `null` | Speed relative to the baseline. `null` if no baseline or only one benchmark. |
| `Significant` | `"true"` / `"false"` / empty | [Mann-Whitney U](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) significance result. Empty for the baseline or when significance testing is disabled. |
| `AllocPerOp` | integer or `null` | Mean heap bytes per iteration. `null` if allocation tracking is disabled. |
| `Gen0`, `Gen1`, `Gen2` | integer or empty | Collection counts per generation. Empty when GC diagnostics are off. |
| `SchemaVersion` | integer | The report shape. See [Report format versioning](./index.md#report-format-versioning). |
| `MeasurementEpoch` | integer | Whether these numbers may be compared with another file's. A different epoch means NBenchmark itself changed what it measures, so a diff would report the harness rather than your code. |
| `Detail` | string | Active detail level (`simple`, `standard`, or `advanced`). |
| `Profile` | string | Active measurement profile (`realistic` or `independent`). |
| `RuntimeProfile` | string | The runtime profile the measuring process was launched under (`steady-state`, `host`, ...). |
| `RuntimeKnobs` | string | The environment variables that profile applied, or empty when the configuration was inherited rather than chosen. |

### Standard mode (dynamic columns - adds the following after the simple columns)

| Column | Type | Description |
| --- | --- | --- |
| `Mean` | float | Arithmetic mean in nanoseconds. |
| `StdDev` | float | Sample standard deviation in nanoseconds. |
| `StdErr` | float | Standard error of the mean (`StdDev / √n`) in nanoseconds. |
| `MarginOfError` | float | Half-width of the confidence interval in nanoseconds. |
| `CiLower` | float | Lower bound of the confidence interval on the mean (`Mean - MarginOfError`). |
| `CiUpper` | float | Upper bound of the confidence interval on the mean (`Mean + MarginOfError`). |
| `ConfidenceLevel` | float | The confidence level used (e.g. `0.95`). |
| `CoefficientOfVariation` | float | `StdDev / Mean`. Dimensionless measure of relative variability. |
| `P{key}` | float | Dynamic percentile columns. One column per configured percentile value between P50 and Max (e.g. `P95`, `P99`, `P99.9`). Controlled by `MeasurementOptions.ReportedPercentiles` or the `--percentiles` CLI flag. Values in nanoseconds. |
| `EffectMetric` | string or empty | Strategy-defined effect metric name (for example `Cliff's δ`, `median-ratio`, `A12`). Empty for the baseline or when significance is not tested. |
| `EffectValue` | float or empty | Strategy-defined numeric effect value. For built-in Mann-Whitney tests this is **Cliff's delta** (positive = candidate slower than baseline, negative = candidate faster, range `[-1, 1]`). Empty for the baseline or when significance is not tested. See [Cliff's delta](../statistics/significance.md#technical-detail-cliffs-delta). |
| `Magnitude` | string or empty | Strategy-defined qualitative effect label. For built-in Mann-Whitney tests this is [Romano (2006)](https://en.wikipedia.org/wiki/Effect_size) classification of `abs(Cliff's δ)`: `neg` < 0.147, `small` < 0.33, `med` < 0.474, `large` ≥ 0.474. Empty for the baseline or when significance is not tested. |
| `MarginPercent` | float | `MarginOfError / Mean * 100`. |
| `OutliersRemoved` | integer | Number of samples removed by outlier trimming. |

### Advanced mode (dynamic columns - all standard columns plus the following)

| Column | Type | Description |
| --- | --- | --- |
| `Q1` | float | First quartile (P25) in nanoseconds. |
| `Q3` | float | Third quartile (P75) in nanoseconds. |
| `Iqr` | float | Q3 - Q1 in nanoseconds. |
| `LowerFence` | float or empty | Lower IQR fence. Empty when `OutlierMode` is not `IqrFence`. |
| `UpperFence` | float or empty | Upper IQR fence. Empty when `OutlierMode` is not `IqrFence`. |
| `Range` | float | Max - Min in nanoseconds. |
| `N` | integer | Post-trim sample count. |
| `Skewness` | float | Sample skewness. Zero for `n < 3`. |
| `Kurtosis` | float | Excess kurtosis. Zero for `n < 4`. |
| `Mad` | float | Median absolute deviation (scaled by 1.4826). |
| `AllocMedian` | integer or empty | Median allocation per iteration. Empty if allocation tracking is disabled. |
| `AllocP95` | integer or empty | P95 allocation per iteration. Empty if allocation tracking is disabled. |
| `AllocMax` | integer or empty | Max allocation per iteration. Empty if allocation tracking is disabled. |
| `StandardErrorPercent` | float | `StdErr / Mean * 100`. |
| `CoefficientOfVariationPercent` | float | `CoefficientOfVariation * 100`. |
| `WarmupIterations` | integer | Resolved warmup samples (excluded from stats). |
| `AutoTuneWarmup` | integer or empty | Resolved warmup length from the adaptive loop. Empty on dry-run/errored. |
| `AutoTuneSamples` | integer or empty | Resolved measured-sample count (pre-trim). Empty on dry-run/errored. |
| `AutoTuneOpsPerSample` | integer or empty | Resolved ops-per-sample (K). Empty on dry-run/errored. |
| `AutoTuneSampleStop` | string or empty | Why measurement stopped: `CiTargetMet`, `MaxCeiling`, `ExplicitCount`, or `WallClockCap`. Empty on dry-run/errored. |
| `AutoTuneCiWidth` | float or empty | Raw relative CI half-width achieved at stop. Empty on dry-run/errored. |
| `AutoTuneTuningMs` | float or empty | Wall-clock time spent in the adaptive loop, in milliseconds. Empty on dry-run/errored. |
| `Categories` | string or empty | Semicolon-separated category names. Empty if no categories. |

## Notes

- Results are sorted by median (fastest first).
- The output directory is created automatically if it does not exist.
- Names containing double-quotes are escaped by doubling the quote character (standard CSV escaping).
- Simple mode CSV has 9 fixed columns. Standard mode has 22 base columns plus one column per configured tail-latency percentile. Advanced mode adds 23 advanced fields on top of the standard columns and therefore also has a dynamic total column count.

## Using with Benchmark (Single mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToCsvAsync("results/");
await result.ToCsvAsync("results/", "benchmarks.csv");
```

## CLI usage (BenchmarkHarness)

```bash
dotnet run -- --reporter csv
dotnet run -- --reporter csv --output ./results
```

When `--output` is specified, files are written inside that directory.


---

