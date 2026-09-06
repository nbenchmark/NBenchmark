---
title: Choose your path
description: Match what you want to do to the right NBenchmark API, in one table.
order: 2
---

# Choose your path

NBenchmark provides four primary ways to measure performance. You do not need to use all of them; instead, find the row in the following table that matches your goals.

| Goal | API / Tool | Documentation |
| --- | --- | --- |
| Measure a single method | `Benchmark.Run` | [Quick start](./quick-start.md) |
| Compare multiple implementations | `BenchmarkSuite` | [Suite mode](../usage-modes/suite-mode.md) |
| Build a benchmark project with a CLI | `BenchmarkHarness` | [Harness mode](../usage-modes/harness-mode.md) |
| Benchmark an existing assembly | `dotnet benchmark` | [Global tool](../usage-modes/global-tool.md) |
| Fail tests when performance regresses | `[PerformanceFact]` | [Test integration](../test-integration/index.md) |
| Fail CI builds on regression | `--max-regression-percent` | [CI/CD guide](../guides/ci-cd-pipelines.md) |

## Overview of each approach

### Measure one method
Use this approach for quick measurements without the need for setup, projects, or attributes. You can drop this code anywhere.

```csharp
var result = Benchmark.Run(() => int.Parse("12345"));
result.Print();
```

### Compare multiple implementations
Use `BenchmarkSuite` to compare two or more implementations. The results include a ratio and a verdict on whether the performance difference is statistically significant.

```csharp
await new BenchmarkSuite("parsing")
    .Add("int.Parse", () => int.Parse("12345"))
    .Add("Convert", () => Convert.ToInt32("12345"))
    .WithBaseline("int.Parse")
    .RunAsync();
```

### Build a benchmark project
Create a dedicated benchmark project by marking methods with attributes. This approach provides a built-in command-line interface (CLI).

```csharp
public class ParseBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Parse() => int.Parse("12345");
}

await BenchmarkHarness.Create(args).AddFromAssembly<ParseBenchmarks>().RunAsync();
```

You can then run benchmarks using the CLI:

```bash
dotnet run -- --filter "*Parse*" --reporter json
```

### Benchmark an existing assembly
Measure an assembly you have already built without modifying its code or adding package references to the project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyApp.Benchmarks
```

### Create a performance gate
Use `[PerformanceFact]` to integrate performance thresholds into your test suite. The test fails if the method's performance regresses.

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(OldParse))]
public void NewParse() => NewParser.Parse(Payload);
```

## Transitioning between modes

All modes produce the same `BenchmarkResult` object. Consequently, reporters, file exports, and analysis code work unchanged when you transition from one mode to another. Start with the simplest approach that answers your question.

## Next steps

For more information, see the following pages:

- [Quick start](./quick-start.md) - Run your first benchmark.
- [Usage modes](../usage-modes/) - A detailed walkthrough of each mode.
- [Guides](../guides/) - Complete recipes for common workflows.
