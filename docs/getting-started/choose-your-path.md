---
title: Choose your path
description: Match what you want to do to the right NBenchmark API, in one table.
order: 2
---

# Choose your path

NBenchmark has four ways in. You do not need to understand all of them - find the row that matches
what you came to do.

| I want to... | Use | Start here |
| --- | --- | --- |
| Get a number for one method | `Benchmark.Run` | [Quick Start](./quick-start.md) |
| Compare two or three implementations | `BenchmarkSuite` | [Suite mode](../usage-modes/suite-mode.md) |
| Build a benchmark project with a CLI | `BenchmarkHarness` | [Harness mode](../usage-modes/harness-mode.md) |
| Benchmark an assembly I already built | `dotnet benchmark` | [Global tool](../usage-modes/global-tool.md) |
| Fail my tests when performance regresses | `[PerformanceFact]` | [Test integration](../test-integration/index.md) |
| Fail my CI build on regression | `--threshold-pct` | [CI/CD guide](../guides/ci-cd-pipelines.md) |

## The short version of each

**One method.** No setup, no project, no attributes. Drop it anywhere.

```csharp
var result = Benchmark.Run(() => int.Parse("12345"));
result.Print();
```

**Two implementations.** You get a ratio and a verdict on whether the difference is real.

```csharp
await new BenchmarkSuite("parsing")
    .Add("int.Parse", () => int.Parse("12345"))
    .Add("Convert", () => Convert.ToInt32("12345"))
    .WithBaseline("int.Parse")
    .RunAsync();
```

**A benchmark project.** Mark methods with an attribute; get a CLI for free.

```csharp
public class ParseBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Parse() => int.Parse("12345");
}

await BenchmarkHarness.Create(args).AddFromAssembly<ParseBenchmarks>().RunAsync();
```

```bash
dotnet run -- --filter "*Parse*" --reporter json
```

**An assembly you already have.** No `Program.cs`, no package reference in that project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyApp.Benchmarks
```

**A gate in your test suite.** The test fails when the method gets slower.

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(OldParse))]
public void NewParse() => NewParser.Parse(Payload);
```

## Switching later is cheap

All four modes produce the same `BenchmarkResult`, so reporters, file output, and any analysis code
you write work unchanged when you move from one to the next. Start with the simplest thing that
answers your question.

## Next

- **[Quick Start](./quick-start.md)** - run your first benchmark
- **[Usage modes](../usage-modes/)** - the detailed walkthrough of each mode
- **[Guides](../guides/)** - complete recipes for real workflows
