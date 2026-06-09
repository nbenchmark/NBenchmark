---
title: Reporters
description: Overview of all NBenchmark reporters and how to use them.
order: 4
---

# Reporters

Reporters consume the finished `BenchmarkResult` list and produce output - terminal tables, Markdown files, CSVs, or JSON. You can attach as many reporters as you like to a single run.

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
| [ConsoleReporter](./console.md) | `NBenchmark.Reporters.Console` | Rich terminal table with colour and a bar chart |
| [MarkdownReporter](./markdown.md) | `NBenchmark` | `.md` file with a formatted results table |
| [CsvReporter](./csv.md) | `NBenchmark` | `.csv` file with all statistics, suitable for post-processing |
| [JsonReporter](./json.md) | `NBenchmark` | `.json` file with full structured results |

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

Add it to your host or suite with `.WithReporter(new MyReporter())`.

If you want your custom reporter to be usable from the `--reporter` CLI flag, register it with the global `ReporterRegistry`:

```csharp
using NBenchmark.Reporters;

// In a static constructor or [ModuleInitializer]:
ReporterRegistry.Register("my-reporter", "Custom output", _ => new MyReporter());
```

After registration, `--reporter my-reporter` works from the CLI.

### Using BenchmarkTable in a custom reporter

For reporters that produce comparison tables, use `BenchmarkTable.Build(results)` rather than working with `IReadOnlyList<BenchmarkResult>` directly. It centralises the logic you would otherwise duplicate:

- **Baseline selection** - picks the first result marked `[Baseline]`, or falls back to the fastest (lowest median) if none is marked.
- **Ratio computation** - `row.Ratio` is `result.Median / baseline.Median`, or `NaN` for errored results or single-benchmark runs.
- **Significance labels** - `row.SignificanceLabel` is `"✓"` (significant), `"~"` (not significant), or `""` (not applicable).
- **Ordering** - rows are sorted by median ascending.
- **Run metadata** - `table.RunAtUtc`, `table.WarmupIterations`, `table.MeasuredIterations`, `table.ConfidenceLevel`, `table.OutlierMode`, and `table.TotalDuration` are available for building a header without picking fields from individual results.

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
