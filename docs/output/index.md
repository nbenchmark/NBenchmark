---
title: Output
description: Reporters and output control - console, JSON, Markdown, CSV, custom reporters, and reading your results.
order: 4
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

The schema is at `1` and the measurement epoch is at `4` today. They are separate because they move independently, and the case that proves it
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
