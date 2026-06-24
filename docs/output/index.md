---
title: Output
description: Reporters and output control - console, JSON, Markdown, CSV, custom reporters, and report detail levels.
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

### BenchmarkHost (Host mode)

```csharp
BenchmarkHost.Create(args)
    .WithReporter(new ConsoleReporter())
    .WithReporter(new JsonReporter("results/"))
    .RunAsync();
```

### Benchmark (Quick mode) - extension methods

```csharp
var result = Benchmark.Run(() => MyMethod());

await result.ToMarkdownAsync("results/");
await result.ToCsvAsync("results/");
await result.ToJsonAsync("results/");
```

## Available reporters

| Reporter | Package | Output |
|---|---|---|
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

With `BenchmarkHost`, the `--reporter` CLI flag adds reporters by name:

```bash
dotnet run -- --reporter markdown --output ./results
dotnet run -- --reporter csv
dotnet run -- --reporter json
dotnet run -- --reporter console   # works when NBenchmark.Reporters.Console is referenced
```

The `--reporter` flag constructs reporters through `ReporterRegistry.TryCreate`, which handles both built-in reporters (`json`/`markdown`/`csv`) and any reporters self-registered by external packages.

External packages (like `NBenchmark.Reporters.Console`) self-register via `[ModuleInitializer]` + `ReporterRegistry.Register()`. The `--reporter flag` discovers available reporters automatically - no per-reporter code changes needed in `BenchmarkHost`.

If you reference an unknown reporter name, the host prints the list of available reporters plus a hint about the `console` package.

## Detail levels

Reporters support three detail levels - **Simple** (default), **Standard**, and **Advanced** - that control how much statistical information is included in the output. Set the level via `WithDetail(ReportDetail.Standard)` on both `BenchmarkHost` and `BenchmarkSuite`, or via the `--detail standard` CLI flag in host mode. See the [Report Detail Levels guide](./report-detail-levels.md) for the full column reference.

## Writing a custom reporter

See the [Custom Reporters](./custom-reporters.md) page for a step-by-step guide to implementing `IReporter`, registering it with `ReporterRegistry`, and using `BenchmarkTable` for comparison output.
