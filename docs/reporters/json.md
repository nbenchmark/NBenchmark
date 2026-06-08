---
title: JsonReporter
description: Save benchmark results to a JSON file for programmatic consumption.
order: 5
---

# JsonReporter

`JsonReporter` writes results to a `.json` file as a structured object. It is part of the core `NBenchmark` package (uses `System.Text.Json` with no additional dependencies).

JSON output is suitable for CI dashboards, performance tracking over time, or any tooling that consumes structured data.

## Setup

```csharp
using NBenchmark.Reporters;

// Default - writes to the current directory
.WithReporter(new JsonReporter())

// Explicit output directory
.WithReporter(new JsonReporter("results/"))
```

`JsonReporter` takes an **output directory**, not a file path. It creates a uniquely named file inside that directory on each run (see [File naming](#file-naming) below).

::: info
`JsonReporter` automatically creates the output directory if it does not exist. This is different from `MarkdownReporter` and `CsvReporter`, which require the directory to exist.
:::

## Output format

```json
{
  "generatedAt": "2026-06-06T03:40:00.000Z",
  "results": [
    {
      "name": "Compute",
      "description": null,
      "mean": 275.3,
      "median": 300.0,
      "p95": 500.0,
      "p99": 500.0,
      "min": 200.0,
      "max": 1100.0,
      "standardDeviation": 85.9,
      "standardError": 6.1,
      "marginOfError": 16.2,
      "confidenceLevel": 0.95,
      "coefficientOfVariation": 0.3122,
      "confidenceIntervalLower": 259.1,
      "confidenceIntervalUpper": 291.5,
      "meanAllocatedBytes": null,
      "pValue": 0.0012,
      "isSignificant": true,
      "errored": false,
      "errorMessage": null,
      "measuredIterations": 190,
      "warmupIterations": 25,
      "runAt": "2026-06-06T03:40:00.000Z",
      "totalDuration": "00:00:00.050",
      "measuredDuration": "00:00:00.040",
      "isBaseline": false,
      "outlierMode": "removeTop5Percent"
    }
  ]
}
```

All timing values are in **nanoseconds**. Property names use camelCase.

`totalDuration` is end-to-end wall-clock (warmup + pre-measure GC + measured loop); `measuredDuration` is the measured loop only. `measuredDuration <= totalDuration` always; the gap is dominated by warmup iterations and the pre-measure `GC.Collect`.

## File naming

Each call to `ReportAsync` writes a new file to prevent overwriting previous runs:

```
benchmarks-20260606-034000-001.json
```

The filename includes the UTC timestamp and a counter so multiple suites running in the same process do not collide.

## Notes

- The output directory is created automatically if it does not exist.
- `BenchmarkResult` is serialised with all properties, including `ConfidenceIntervalLower` and `ConfidenceIntervalUpper` (computed from `Mean ± MarginOfError`).

## Using with Benchmark (Quick mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToJsonAsync("results/");
```

## CLI usage (BenchmarkHost)

```bash
dotnet run -- --reporter json
dotnet run -- --reporter json --output ./results
```
