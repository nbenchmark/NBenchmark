---
title: Reporters
description: Overview of all NBenchmark reporters and how to use them.
order: 4
---

# Reporters

Reporters consume the finished `BenchmarkResult` list and produce output — terminal tables, Markdown files, CSVs, or JSON. You can attach as many reporters as you like to a single run.

## How reporters work

All reporters implement `IReporter`:

```csharp
public interface IReporter
{
    Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default);
}
```

Reporters are called after all benchmarks in the run have completed. They receive the full result list including any errored benchmarks.

## Attaching reporters

### BenchmarkSuite (Tier 2)

```csharp
await new BenchmarkSuite("name")
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results.md"))
    .WithReporter(new CsvReporter("results.csv"))
    .RunAsync();
```

### BenchmarkHost (Tier 3)

```csharp
BenchmarkHost.Create(args)
    .WithReporter(new ConsoleReporter())
    .WithReporter(new JsonReporter("results/"))
    .RunAsync();
```

### Benchmark (Tier 1) — extension methods

```csharp
var result = Benchmark.Run(() => MyMethod());

await result.ToMarkdownAsync("results.md");
await result.ToCsvAsync("results.csv");
await result.ToJsonAsync("results/");
```

## Available reporters

| Reporter | Package | Output |
|---|---|---|
| [ConsoleReporter](./console) | `NBenchmark.Console` | Rich terminal table with colour and a bar chart |
| [MarkdownReporter](./markdown) | `NBenchmark` | `.md` file with a formatted results table |
| [CsvReporter](./csv) | `NBenchmark` | `.csv` file with all statistics, suitable for post-processing |
| [JsonReporter](./json) | `NBenchmark` | `.json` file with full structured results |

## Output path validation

File reporters validate that the output path is **under the current working directory**. Paths outside the CWD (e.g. `/tmp/results` or `../../other-project`) are rejected with an `ArgumentException`. This prevents accidental writes outside the project directory.

```csharp
// Works — relative path under CWD
new MarkdownReporter("results/benchmark.md")

// Throws ArgumentException — outside CWD
new MarkdownReporter("/tmp/benchmark.md")
```

::: tip
When using `BenchmarkHost` with `--output`, the directory must already exist. Create it before running if it does not.
:::

## Using the CLI reporter flag

With `BenchmarkHost`, the `--reporter` CLI flag adds file reporters:

```bash
dotnet run -- --reporter markdown --output ./results
dotnet run -- --reporter csv
dotnet run -- --reporter json
```

The `console` reporter requires the `NBenchmark.Console` package and must be registered in code with `.WithReporter(new ConsoleReporter())`.

## Writing a custom reporter

Implement `IReporter` from the `NBenchmark` package:

```csharp
public sealed class MyReporter : IReporter
{
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
