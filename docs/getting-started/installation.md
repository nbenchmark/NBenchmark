---
title: Installation
description: How to add NBenchmark to a .NET project.
order: 1
---

# Installation

## Requirements

NBenchmark targets `net8.0`, `net9.0`, and `net10.0`. You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or a later version.

## Packages

NBenchmark provides several NuGet packages. Install only the packages you need for your project.

### Core package

The core package includes all measurement and statistics features, the file-based reporters for JSON, Markdown, and CSV, and the build-time analyzers. This package has no NuGet dependencies and relies only on the .NET Base Class Library (BCL).

```bash
dotnet add package NBenchmark
```

The analyzers arrive with it - there is no separate package to install. They run in your IDE and during `dotnet build`, and catch the authoring mistakes that produce a confident, wrong number: a capturing body, a body with no observable side effect, a static benchmark method, a class with no parameterless constructor. For the complete list, see [Analyzers](../reference/analyzers.md).

## Verify your installation

To verify your installation, create a new console project and run a simple benchmark:

```bash
dotnet new console -n MyBenchmarks
cd MyBenchmarks
dotnet add package NBenchmark
```

Replace the contents of `Program.cs` with the following code:

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

Run the project:

```bash
dotnet run
```

The output is similar to the following:

```text
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  MedianNs: 1.20 µs       Ops/s: 833.3 Kops/s
  │  Alloc/op: 0 B
  │
  │  Measured in an isolated worker under 'steady-state'.
  │
  └─────────────────────────────────────────────────
```

If the output displays measurement data, the installation is successful.

## Optional packages

### Console package

The console package adds a formatted terminal table with color-coded results and an optional progress display. This package depends on [Spectre.Console](https://spectreconsole.net/).

```bash
dotnet add package NBenchmark.Reporters.Console
```

Use `NBenchmark.Reporters.Console` only if you want to output results directly to the terminal. File reporters (JSON, Markdown, and CSV) work without this package.

### Dependency injection package

The dependency injection (DI) package allows `[Benchmark]` classes to use constructor dependencies resolved from an `IServiceProvider`. Without this package, benchmark classes must have a public parameterless constructor. For more information, see the [Dependency Injection guide](../features/dependency-injection.md).

```bash
dotnet add package NBenchmark.DependencyInjection
```

If you also need the concrete `Microsoft.Extensions.DependencyInjection` implementation (such as `ServiceCollection` or `BuildServiceProvider`), install the following package:

```bash
dotnet add package Microsoft.Extensions.DependencyInjection
```

### Test framework integration packages

The integration packages allow you to enforce performance thresholds as test assertions within your existing test project. The test fails if a benchmark exceeds a defined threshold.

```bash
dotnet add package NBenchmark.Integration.xUnit    # xUnit v2
dotnet add package NBenchmark.Integration.NUnit    # NUnit 3 / 4
dotnet add package NBenchmark.Integration.MSTest   # MSTest v2 / v3
```

These packages automatically include the core `NBenchmark` package. For usage details, see the [Test integration](../test-integration/index.md) guide.

## Global tool

The global tool allows you to run benchmarks from the command line without creating a separate project. After installation, you can run the tool against any assembly that contains `[Benchmark]` methods.

```bash
dotnet tool install -g NBenchmark.Tool
```

After installation, run `dotnet benchmark --help` to verify the tool is working. For more information, see the [Global Tool guide](../usage-modes/global-tool.md).

## Next steps

For more information, see the following pages:

- [Choose your path](./choose-your-path.md) - Match your goals to the appropriate API.
- [Quick start](./quick-start.md) - Write your first benchmark.
