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

```json
{
  "generatedAt": "2026-06-06T03:40:00.000Z",
  "detail": "simple",
  "profile": "realistic",
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
      "outlierMode": "iqrFence"
    }
  ]
}
```

All timing values are in **nanoseconds**. Property names use camelCase.

`totalDuration` is end-to-end wall-clock (warmup + pre-measure GC + measured loop); `measuredDuration` is the measured loop only. `measuredDuration <= totalDuration` always; the gap is dominated by warmup iterations and the pre-measure `GC.Collect`.

The `detail` and `profile` fields in the envelope report the active detail level and measurement profile. The result records always contain all available fields regardless of detail level.

## Notes

- The output directory is created automatically if it does not exist.
- `BenchmarkResult` is serialised with all properties, including `ConfidenceIntervalLower` and `ConfidenceIntervalUpper` (computed from `Mean ± MarginOfError`).

## Using with Benchmark (Quick mode)

```csharp
var result = Benchmark.Run(() => MyMethod());
await result.ToJsonAsync("results/");
await result.ToJsonAsync("results/", "benchmarks.json");
```

## CLI usage (BenchmarkHost)

```bash
dotnet run -- --reporter json
dotnet run -- --reporter json --output ./results
```
