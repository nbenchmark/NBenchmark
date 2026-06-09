---
title: Extension Ideas
description: Ideas for extensions and companion packages for NBenchmark.
order: 9
---

# Extension Ideas

This document catalogs potential extensions and companion packages for NBenchmark. Each entry outlines the concept, the integration mechanism, and the value proposition.

---

## Reporter / Output Extensions

### NBenchmark.Reporting.HTML

Generate a self-contained HTML report with interactive charts.

- **Mechanism:** Implement `IReporter` and self-register as `"html"` via `[ModuleInitializer]`.
- **Dependencies:** A charting library (e.g. render inline SVG or bundle a lightweight JS chart library as embedded resources).
- **Output:** A single `.html` file with no external dependencies. Render box-and-whisker plots for timing distribution, a bar chart of medians with confidence intervals, and a table with the same columns as the markdown reporter.
- **Why:** Provides a shareable, visually-rich report without requiring external infrastructure.

---

### NBenchmark.Reporting.InfluxDB

Push results to InfluxDB for time-series performance tracking.

- **Mechanism:** Implement `IReporter` and self-register as `"influxdb"`. Accept an InfluxDB URL, token, and bucket via constructor.
- **Dependencies:** InfluxDB .NET client library.
- **Behavior:** Each benchmark result becomes a set of data points (mean, median, p95, p99, allocations, etc.) tag-keyed by benchmark name and run identifier. Each run creates a new series so historical trends can be plotted in Grafana.
- **Why:** Enables long-term performance monitoring and regression detection across CI pipeline runs.

---

### NBenchmark.Reporting.OpenTelemetry

Export results as OpenTelemetry metrics.

- **Mechanism:** Implement `IReporter` and self-register as `"opentelemetry"`. Accept an `MeterProvider` or configure one from environment variables.
- **Dependencies:** `OpenTelemetry` SDK.
- **Behavior:** Emit timing data as Histogram metrics and allocation data as Gauge metrics, each labeled with the benchmark name. Integrates with existing OTel collectors (Prometheus, Jaeger, OTLP exporters) without additional plumbing.
- **Why:** Fits into the .NET observability ecosystem so benchmark data flows through the same pipeline as production telemetry.

---

### NBenchmark.Reporting.Slack

Post a summary table to a Slack channel.

- **Mechanism:** Implement `IReporter` and self-register as `"slack"`. Accept a Slack incoming webhook URL and an optional message prefix.
- **Dependencies:** `System.Net.Http.HttpClient`.
- **Behavior:** Build a markdown table from `BenchmarkTable.Build(results)`, format it as a Slack Block Kit message, and POST to the webhook. Optionally highlight regressions (e.g. benchmarks slower than baseline by more than a configurable threshold) with a warning prefix.
- **Why:** Delivers performance results directly to the team's communication channel after CI runs.

---

### NBenchmark.Reporting.GitHubActions

Output results as GitHub Actions workflow commands.

- **Mechanism:** Implement `IReporter` and self-register as `"github-actions"`.
- **Dependencies:** None.
- **Behavior:** Write a job summary table (GitHub-flavored markdown via `GITHUB_STEP_SUMMARY`). Emit workflow annotations (`::warning` or `::error`) for benchmarks that regress beyond a threshold. Publish a JSON step output (`GITHUB_OUTPUT`) with per-benchmark metrics so downstream jobs can consume them.
- **Why:** First-class CI integration for teams using GitHub Actions, with zero external dependencies.

---

### NBenchmark.Reporting.Parquet

Output results as Apache Parquet files.

- **Mechanism:** Implement `IReporter` and self-register as `"parquet"`. Accept an output directory and optional filename.
- **Dependencies:** An Apache Parquet .NET library (e.g. `Parquet.NET`).
- **Behavior:** Write all benchmark results as rows in a Parquet file. Columns: name, mean, median, p95, p99, stddev, allocations, run timestamp, etc. The columnar format is ideal for aggregating thousands of historical runs.
- **Why:** Enables large-scale performance analysis with tools like Spark, DuckDB, and Pandas without the overhead of parsing JSON or CSV.

---

### NBenchmark.Reporting.TimescaleDB

Store results in TimescaleDB for time-series analysis.

- **Mechanism:** Implement `IReporter` and self-register as `"timescaledb"`.
- **Dependencies:** An ADO.NET PostgreSQL driver (e.g. `Npgsql`).
- **Behavior:** Insert each benchmark result as a row into a PostgreSQL hypertable. The `RunAtUtc` timestamp becomes the time dimension. Tag keys (benchmark name, suite name) enable filtering in dashboards.
- **Why:** Provides a scalable, SQL-queryable history of all benchmark runs.

---

## Test Framework Integration

### NBenchmark.xUnit

Run benchmarks as xUnit tests.

- **Mechanism:** A NuGet package with custom test attributes and a runner. Does NOT need `IReporter`; it integrates into the xUnit lifecycle directly.
- **Usage:**

  ```csharp
  [PerformanceFact(MaxMeanNs = 1000, MaxP95Ns = 1500)]
  public void StringSort_IsFast() => MySorter.Sort(data);

  [PerformanceTheory(MaxMeanNs = 500)]
  [InlineData(10)]
  [InlineData(100)]
  public void Lookup_IsFast(int size) => dictionary[size / 2];
  ```

- **Behavior:** Wrap `BenchmarkRunner.Instance.Run` inside xUnit test execution. Parse the result and assert against user-specified thresholds (`MaxMeanNs`, `MaxP95Ns`, `MaxAllocatedBytes`). Provide a regression-check mode that loads a baseline JSON and fails if any benchmark exceeds a configurable slowdown ratio.
- **Why:** Performance testing becomes a first-class part of the existing test workflow. No need to run a separate harness.

---

### NBenchmark.NUnit

Run benchmarks as NUnit tests.

- **Mechanism:** Same concept as xUnit, adapted to NUnit's attribute model (`[Test]`, `[TestCase]`, `[TestCaseSource]`).
- **Usage:**

  ```csharp
  [PerformanceTest(MaxMeanNs = 1000)]
  [TestCase(10)]
  [TestCase(100)]
  public void Lookup_IsFast(int size) => dictionary[size / 2];
  ```

- **Behavior:** Mirror the xUnit package. Assert against thresholds. Support baseline regression checks from a JSON file.
- **Why:** Same value proposition for teams standardised on NUnit.

---

### NBenchmark.MSTest

Run benchmarks as MSTest tests.

- **Mechanism:** Same concept, adapted to MSTest's `[TestMethod]`, `[DataRow]`, and `[DynamicData]` attributes.
- **Why:** Completes the test-framework coverage for the .NET ecosystem.

---

## Development & CI Tooling

### NBenchmark.dotnet-tool

A global .NET tool for running benchmarks without boilerplate.

- **Mechanism:** A .NET tool project (`PackageAsTool` + `PackAsTool`) that wraps `BenchmarkHost`. Not a library, not a reporter.
- **Usage:**

  ```bash
  dotnet nbenchmark run ./MyLib.dll --filter "String*" --reporter console --reporter json --output ./results
  dotnet nbenchmark list ./MyLib.dll
  ```

- **Behavior:** Loads the target assembly, discovers benchmarks via reflection, and invokes the same `SuiteRunner.RunAsync` pipeline as `BenchmarkHost`. Accepts all standard CLI flags. Can run against compiled `.dll` files directly, so no separate console project is needed.
- **Why:** Removes the need to create a stand-alone console application just to run benchmarks. Lowers the adoption barrier significantly.

---

### NBenchmark.Diff

Diff two benchmark result files to detect regressions.

- **Mechanism:** A library and/or .NET tool. Reads two JSON result files (from `--reporter json --output ./results`) and produces a comparison table.
- **Usage:**

  ```bash
  dotnet nbenchmark diff ./results/main.json ./results/feature.json
  # Or as a library:
  var comparison = BenchmarkDiff.Compare(baselineResults, currentResults);
  ```

- **Behavior:** Pair benchmarks by name. Compute delta (faster/slower percentage), highlight regressions that exceed a configurable threshold, and produce a table identical in layout to the console reporter but with an additional "Delta" column. Supports exit codes for CI gating (exit 1 if any regression found).
- **Why:** Essential CI primitive — compare the current branch's performance against `main` and fail the build if something regressed.

---

### NBenchmark.Baseline

Persist and compare against stored baselines.

- **Mechanism:** A library that wraps `BenchmarkSuite.RunAsync` and writes/reads baseline JSON files.
- **Usage:**

  ```csharp
  var baseline = await BenchmarkBaseline.LoadAsync("baselines/string-ops.json");

  await new BenchmarkSuite("String Operations")
      .WithBaseline(baseline)
      .Add(StringOps.Intern)
      .Add(StringOps.Concat)
      .RunAsync();
  ```

- **Behavior:** On the first run, save results as a `.json` baseline. On subsequent runs, compare against the stored baseline and report deltas. Flag regressions as errors or warnings. Optionally auto-update the baseline when performance improves (or on an explicit `--update-baseline` flag).
- **Why:** Decouples baseline data from the source code. Useful for teams that want to track performance over time without re-running the baseline every time.

---

## Profiling / Memory

### NBenchmark.Diagnostics.EventPipe

Capture .NET EventPipe traces during benchmark runs.

- **Mechanism:** A library that wraps `dotnet-trace` or the `Microsoft.Diagnostics.NETCore.Client` NuGet package. Not a reporter; it attaches to `BenchmarkSuite` via the progress callbacks.
- **Dependencies:** `Microsoft.Diagnostics.NETCore.Client`.
- **Behavior:** Start an EventPipe session (CPU sampling, GC stats, thread pool, JIT events) when `OnBenchmarkStarting` fires, stop it on `OnBenchmarkCompleted`, and save the `.nettrace` file alongside the benchmark name. Attach the file path to a metadata dictionary on `BenchmarkResult` so reporters can include or link to it.
- **Why:** Enables deep diagnostic traces for slow benchmarks without requiring manual `dotnet-trace` invocation.

---

### NBenchmark.Diagnostics.Memory

Per-type allocation breakdown.

- **Mechanism:** A library that uses `GC.GetAllocatedBytesForCurrentThread` at a finer granularity or integrates with the EventPipe GC provider to attribute allocations to specific types.
- **Dependencies:** Possibly `Microsoft.Diagnostics.NETCore.Client` or heap-snapshot tooling.
- **Behavior:** Extends `BenchmarkResult` with an `AllocationBreakdown` dictionary mapping type names to byte counts. Reports can render a "Top Allocations" table below the main results.
- **Why:** Knowing _which_ types are allocating is more actionable than a single byte count.

---

## Storage & Long-Term Tracking

### NBenchmark.Storage.SQLite

Store historical results in a local SQLite database.

- **Mechanism:** A library providing both a reporter (`"sqlite"`) and a query API.
- **Dependencies:** `Microsoft.Data.Sqlite` (ships with the .NET SDK, no extra NuGet needed).
- **Behavior:** The reporter inserts each `BenchmarkResult` row into a `benchmark_results` table. A companion CLI or API can query history: "show me StringSort median over the last 30 runs", "which benchmarks regressed this week", etc.
- **Why:** Enables local historical tracking without external infrastructure. Complements the InfluxDB/TimescaleDB extensions for teams that don't have observability stacks.

---

## Prioritisation Notes

High-impact candidates that should be built first:

1. **NBenchmark.Reporting.HTML** — visual reports are the most requested feature after console output.
2. **NBenchmark.xUnit** / **NBenchmark.NUnit** / **NBenchmark.MSTest** — brings benchmarks into the developer's existing test workflow with no learning curve.
3. **NBenchmark.dotnet-tool** — removes the need for boilerplate console apps, lowering the barrier to entry.
4. **NBenchmark.Reporting.GitHubActions** — CI/CD integration with zero dependencies, high demand.
5. **NBenchmark.Diff** — essential CI primitive for gating on regressions.

Medium-priority:

6. **NBenchmark.Storage.SQLite** — local historical tracking without external services.
7. **NBenchmark.Reporting.Slack** — team communication integration.
8. **NBenchmark.Baseline** — decoupled baseline persistence.

Lower-priority (more niche, heavier dependencies):

9. **NBenchmark.Reporting.InfluxDB** — requires observability infrastructure.
10. **NBenchmark.Reporting.OpenTelemetry** — requires OTel collector setup.
11. **NBenchmark.Reporting.Parquet** — data-lake scale analysis.
12. **NBenchmark.Reporting.TimescaleDB** — requires PostgreSQL infrastructure.
13. **NBenchmark.Diagnostics.EventPipe** — requires `Microsoft.Diagnostics.NETCore.Client`.
14. **NBenchmark.Diagnostics.Memory** — builds on EventPipe integration.

---

## Extension Pattern Template

Every reporter extension follows the same pattern. Use this template as a starting point:

```csharp
// NBenchmark.Reporting.MyThing.csproj:
//   <ProjectReference Include="..\NBenchmark\NBenchmark.csproj" />
//   <PackageReference ... />  // any transport/client dependencies

namespace NBenchmark.Reporting.MyThing;

public sealed class MyThingReporter : IReporter
{
    private readonly string _outputDirectory;
    private readonly string? _fileName;

    public MyThingReporter(string outputDirectory = ".", string? fileName = null)
    {
        _outputDirectory = outputDirectory;
        _fileName = fileName;
    }

    public string Name => "my-thing";

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        var table = BenchmarkTable.Build(results);
        // ... transform table into the target format ...
        // ... write file or POST to API ...
    }
}

// Module initializer for auto-registration:
public static class Registration
{
    [ModuleInitializer]
    public static void Register() =>
        ReporterRegistry.Register(
            "my-thing",
            "Short description of what this reporter does",
            outputDir => new MyThingReporter(outputDir ?? "."));
}
```
