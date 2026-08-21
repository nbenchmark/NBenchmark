---
title: Usage modes
description: The four ways to run NBenchmark benchmarks - Single mode, Suite mode, Harness mode, and the dotnet benchmark global tool.
order: 2
---

# Usage modes

NBenchmark provides four usage modes. Choose the mode that matches your requirements.

## [Single mode - Benchmark](./single-mode.md)

Use a single static call to perform a quick measurement anywhere in your code. This mode requires no classes, attributes, or configuration.

```csharp
var result = Benchmark.Run(() => MyMethod());
result.Print();
```

## [Suite mode - BenchmarkSuite](./suite-mode.md)

Use a fluent builder to compare multiple implementations. This mode produces a comparison table with ratios, confidence intervals, and significance testing.

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", BubbleSort)
    .Add("linq",   LinqSort)
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## [Harness mode - BenchmarkHarness](./harness-mode.md)

Use attribute-based discovery driven by a command-line interface for dedicated benchmark projects.

```csharp
await BenchmarkHarness.Create(args)
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

## [Global tool - dotnet benchmark](./global-tool.md)

Use the `dotnet benchmark` global tool to wrap `BenchmarkHarness` into a single command. After installing the tool, you can run benchmarks against any assembly without creating a separate project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyBenchmarks --filter "*Sort*"
```

## When to switch modes

The usage modes follow an evolutionary path. Start with the simplest mode and upgrade as your needs grow:

1. **Start with Single mode** for a one-off measurement. A single `Benchmark.Run` call provides a statistically rigorous result in three lines of code.
2. **Move to Suite mode** when you compare an old implementation against a new one. Suite mode automatically calculates ratios, confidence intervals, and significance testing against a baseline, so you don't have to manually compare two separate outputs.
3. **Move to Harness mode** when your benchmarks require complex setup, such as mocked databases, loggers, `HttpClient`, or dependency-injected services. Harness mode discovers benchmarks by attribute, parses CLI flags, and supports constructor injection via the optional `NBenchmark.DependencyInjection` package.
4. **Use the global tool** when you have a project with `[Benchmark]` methods and want to run them from the CLI without adding a `Program.cs` file, NuGet references, or other project setup. The tool wraps Harness mode into the `dotnet benchmark` command.

Because all four modes produce the same `BenchmarkResult` type, you can upgrade between modes seamlessly. Your reporters, file output, and analysis code remain unchanged.

## Next steps

After choosing a mode, see the [Features](../features/) section for advanced capabilities:
- [Isolated runs](../features/isolated-runs.md)
- [Parameterized benchmarks](../features/parameterized-suite.md)
- [Categories](../features/categories.md)
- [Multi-runtime comparison](../features/multi-runtime.md)
- [Multiple launches](../features/multiple-launches.md)
- [Dependency injection](../features/dependency-injection.md)

For engineering internals, see [Deep dives](../deep-dives/).

The [Guides](../guides/) section provides workflow recipes for common tasks:
- [Benchmarking ASP.NET Core services](../guides/aspnet-core-services.md)
- [Tuning for CI/CD pipelines](../guides/ci-cd-pipelines.md)
- [Comparing a refactor side-by-side](../guides/refactor-comparison.md)
