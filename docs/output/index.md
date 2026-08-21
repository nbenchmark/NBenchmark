---
title: Output
description: Reporters and output control - console, JSON, Markdown, CSV, custom reporters, and reading your results.
order: 5
---

# Output

Reporters process the finished `BenchmarkResult` list and produce output, such as terminal tables, Markdown files, CSVs, or JSON. You can attach multiple reporters to a single run.

> [!TIP]
> For more information about interpreting your results, see [Reading your results](../getting-started/reading-your-results.md).

## In this section

- [Console Reporter](./console-reporter.md) - A rich terminal table with color and a bar chart.
- [Markdown Reporter](./markdown-reporter.md) - A `.md` file with a formatted results table.
- [CSV Reporter](./csv-reporter.md) - A `.csv` file with all statistics, suitable for post-processing.
- [JSON Reporter](./json-reporter.md) - A `.json` file with full structured results.
- [Report Detail Levels](./report-detail-levels.md) - Simple, Standard, and Advanced detail modes.
- [Custom Reporters](./custom-reporters.md) - How to implement and register your own reporter.

## How reporters work

Every reporter implements the `IReporter` interface:

```csharp
public interface IReporter
{
    string Name { get; }

    Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default);
}
```

The `Name` property identifies the reporter for the `--reporter` CLI flag and for output directory rewriting. Built-in reporters return their canonical name (such as `json`, `markdown`, `csv`, or `console`). Custom reporters can return any unique string.

NBenchmark calls reporters after all benchmarks in the run complete. Reporters receive the full result list, including any benchmarks that errored.

## Attaching reporters

### Using BenchmarkSuite (Suite mode)

Use the `WithReporter` method to add reporters to your suite:

```csharp
await new BenchmarkSuite("name")
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithReporter(new CsvReporter("results/"))
    .RunAsync();
```

### Using BenchmarkHarness (Harness mode)

Add reporters to your harness during creation:

```csharp
BenchmarkHarness.Create(args)
    .WithReporter(new ConsoleReporter())
    .WithReporter(new JsonReporter("results/"))
    .RunAsync();
```

### Using Benchmark (Single mode)

Single mode benchmarks provide extension methods to emit reports directly from the result:

```csharp
var result = Benchmark.Run(() => MyMethod());

await result.ToMarkdownAsync("results/");
await result.ToCsvAsync("results/");
await result.ToJsonAsync("results/");
```

## Available reporters

| Reporter | Package | Output |
| --- | --- | --- |
| [`ConsoleReporter`](./console-reporter.md) | `NBenchmark.Reporters.Console` | Rich terminal table with color and a bar chart |
| [`MarkdownReporter`](./markdown-reporter.md) | `NBenchmark` | `.md` file with a formatted results table |
| [`CsvReporter`](./csv-reporter.md) | `NBenchmark` | `.csv` file with all statistics, suitable for post-processing |
| [`JsonReporter`](./json-reporter.md) | `NBenchmark` | `.json` file with full structured results |

## Output path validation

File reporters validate that the output directory is located under the current working directory (CWD). NBenchmark rejects paths outside the CWD (such as `/tmp/results` or `../../other-project`) with an `ArgumentException` to prevent accidental writes outside the project directory.

```csharp
// Works - relative path under CWD
new MarkdownReporter("results/")

// Throws ArgumentException - outside CWD
new MarkdownReporter("/tmp/results/")
```

NBenchmark creates the output directory automatically if it does not exist.

## Using the CLI reporter flag

When using `BenchmarkHarness`, you can add reporters by name using the `--reporter` flag:

```bash
dotnet run -- --reporter markdown --output ./results
dotnet run -- --reporter csv
dotnet run -- --reporter json
dotnet run -- --reporter console   # Requires NBenchmark.Reporters.Console reference
```

The `--reporter` flag constructs reporters using `ReporterRegistry.TryCreate`, which handles both built-in reporters and any reporters registered by external packages.

External packages (such as `NBenchmark.Reporters.Console`) register themselves via `[ModuleInitializer]` and `ReporterRegistry.Register()`. The `--reporter` flag discovers these reporters automatically, so you don't need to change your `BenchmarkHarness` code.

If you provide an unknown reporter name, the host prints a list of available reporters and a hint about the `console` package.

## Detail levels

Reporters support three detail levels that control how much statistical information appears in the output: **Simple** (default), **Standard**, and **Advanced**.

Set the level using `WithDetail(ReportDetail.Standard)` on `BenchmarkHarness` or `BenchmarkSuite`, or use the `--detail standard` CLI flag in harness mode. For a full reference of columns, see the [Report Detail Levels guide](./report-detail-levels.md).

## Writing a custom reporter

For a step-by-step guide to implementing `IReporter`, registering it with `ReporterRegistry`, and using `BenchmarkTable` for comparison output, see [Custom Reporters](./custom-reporters.md).

That page also documents **auto-attached reporters** (`ReporterRegistry.RegisterAutoAttach`). These are side-effect reporters that run on every run after the user's explicit reporters, with no opt-in required.

## Report format versioning

Every file-writing reporter stamps its output with two independent numbers. These stamps allow external tools - such as CI trend dashboards, regression scripts, or spreadsheets - to track changes over time. NBenchmark does not read its own reports.

| Stamp | Purpose | Bumped when |
| --- | --- | --- |
| `schemaVersion` | Determines if a parser can still read the file. | A field is renamed, removed, or changes type; or the envelope is restructured. Added fields do not trigger a bump. |
| `measurementEpoch` | Determines if numbers from different runs are comparable. | NBenchmark changes what a benchmark reports, such as harness overhead, the default runtime profile, or the definition of a reported statistic. |

The current schema version is `1` and the measurement epoch is `7`. These versions move independently. For example, replacing the boxing dispatch path with typed delegates moved the calibration standard from **9.34 ns / 24 B per op to 2.53 ns / 0 B** while leaving the JSON shape identical. A schema version alone would not indicate this change, and a dashboard would report a 3.7x improvement that was not earned by application code.

Stamps appear in these locations:

- **JSON**: `schemaVersion` and `measurementEpoch` are the first two fields of the envelope, allowing consumers to check compatibility before parsing the rest of the file.
- **CSV**: `SchemaVersion` and `MeasurementEpoch` are columns alongside `Detail`, `Profile`, `RuntimeProfile`, and `RuntimeKnobs`.
- **Markdown**: A `> Format:` line appears in the header block.

### Consuming stamps

Compare the epoch before comparing numbers. Treat a mismatch as a discontinuity rather than a result:

```python
import json

with open("benchmarks.json") as f:
    report = json.load(f)

# An absent stamp means the file predates the concept of epochs.
# Reject it rather than assuming it is epoch 0.
if "measurementEpoch" not in report:
    raise ValueError("report predates measurement epochs; not comparable")

if report["measurementEpoch"] != baseline_epoch:
    raise ValueError(
        f"epoch {report['measurementEpoch']} != baseline {baseline_epoch}; "
        "the harness changed, so a diff would measure NBenchmark, not your code"
    )
```

If you are writing a [custom reporter](./custom-reporters.md) and want to use these stamps, use the constants `NBenchmark.Reporters.ReportFormat.SchemaVersion` and `ReportFormat.MeasurementEpoch`.
