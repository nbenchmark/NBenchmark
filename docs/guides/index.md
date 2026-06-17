---
title: Guides
description: Step-by-step guides for each NBenchmark usage mode.
order: 2
---

# Guides

NBenchmark has four usage modes. Pick the one that matches your situation.

## [Quick mode - Benchmark](./quick-mode.md)

A single static call. No classes, no attributes, no configuration required. Good for a quick measurement anywhere in your code.

```csharp
var result = Benchmark.Run(() => MyMethod());
result.Print();
```

## [Suite mode - BenchmarkSuite](./suite-mode.md)

A fluent builder for comparing multiple implementations. Produces a comparison table with ratios, confidence intervals, and significance testing.

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", BubbleSort)
    .Add("linq",   LinqSort)
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## [Categories](./categories.md)

Tag benchmarks with `[BenchmarkCategory]` and include or exclude groups from the command line or fluent APIs in host/suite mode.

```csharp
await new BenchmarkSuite("string")
    .Add("concat", () => "a" + "b", categories: ["Fast"])
    .WithCategoryFilter(include: ["Fast"])
    .RunAsync();
```

## [Host mode - BenchmarkHost](./host-mode.md)

Attribute-based discovery driven by a command-line interface. Designed for dedicated benchmark projects - similar to BenchmarkDotNet's style.

```csharp
await BenchmarkHost.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .RunAsync();

public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    [Benchmark]
    public int Compute() => SomeExpensiveWork();
}
```

## [Global Tool - dotnet benchmark](./dotnet-tool.md)

A dotnet global tool that wraps `BenchmarkHost` into a single command. Install once, then run benchmarks against any assembly without creating a project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyBenchmarks --filter "*Sort*"
```

## When to switch modes

The modes are designed as an evolutionary path. Start simple, upgrade when your needs grow:

1. **Start with Quick mode** for a one-off measurement - a single `Benchmark.Run` call gives you a statistically rigorous result in three lines of code.

2. **Graduate to Suite mode** when you find yourself writing two `Benchmark.Run` calls to compare an old implementation against a new one. Suite mode handles the comparison automatically - ratios, confidence intervals, and significance testing against a baseline - so you don't have to mentally diff two separate outputs.

3. **Graduate to Host mode** when your suite requires complex setup: mocked databases, loggers, `HttpClient`, or any dependency-injected service. Host mode discovers benchmarks by attribute, parses CLI flags, and supports constructor injection via the optional `NBenchmark.DependencyInjection` package.

4. **Use the Global Tool** when you already have a project with `[Benchmark]` methods and want to run them from the CLI without adding a `Program.cs`, NuGet references, or any project setup. The tool wraps Host mode into a single `dotnet benchmark` command.

Because all four modes produce the same `BenchmarkResult` type, upgrading from one mode to the next is seamless - your reporters, file output, and analysis code work unchanged.

## [Report Detail Levels](./report-detail-levels.md)

Control how much statistical information is included in your reports. **Simple** mode (default) provides a compact 10-column table; **Advanced** mode adds per-benchmark statistics including quartiles, confidence intervals, and distribution shape.

## [Dependency Injection](./dependency-injection.md)

Optional companion package that lets benchmark classes have **constructor dependencies** resolved from an `IServiceProvider`. Adds support for repositories, loggers, `HttpClient`, EF Core `DbContext`, and any other registered service.

```csharp
await BenchmarkHost.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(services)
    .RunAsync();

public sealed class OrderBenchmarks(IOrderRepository repository)
{
    [Benchmark] public int CountOrders() => repository.Count();
}
```

## [Isolated Runs (Advanced)](./isolated-runs.md)

Run Suite and Host benchmarks in clean child processes when runtime state contamination matters more than raw execution speed. Host mode is isolated by default; suites opt in with `WithIsolation()`.
