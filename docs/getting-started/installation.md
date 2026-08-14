---
title: Installation
description: How to add NBenchmark to a .NET project.
order: 1
---

# Installation

## Requirements

NBenchmark targets **net8.0**, **net9.0**, and **net10.0**. You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later.

## Packages

NBenchmark ships as separate NuGet packages. Install only what you need.

### Core package

The core package contains all measurement, statistics, and file-based reporters (JSON, Markdown, CSV). It has **no NuGet dependencies** - only the .NET BCL.

```bash
dotnet add package NBenchmark
```

### Analyzers package (recommended)

The analyzers package adds compile-time diagnostics that catch common NBenchmark configuration errors - missing parameterless constructors, static benchmark methods, out-of-range settings, and more.

```bash
dotnet add package NBenchmark.Analyzers
```

The analyzers run automatically in the IDE and during `dotnet build`. See the [Analyzers](../reference/analyzers.md) page for the full diagnostic reference.

## Verify the installation

Create a new console project and add a quick sanity check:

```bash
dotnet new console -n MyBenchmarks
cd MyBenchmarks
dotnet add package NBenchmark
```

Replace the contents of `Program.cs`:

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

Run it:

```bash
dotnet run
```

You should see output similar to:

```text
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 1.20 µs       Ops/s: 833.3 Kops/s
  │  Alloc/op: 0 B
  │
  │  Measured in an isolated worker under 'steady-state'.
  │
  └─────────────────────────────────────────────────
```

If you see numbers, everything is working.

## Optional packages

### Console package

The console package adds a rich terminal table with color-coded results and an optional progress display. It depends on [Spectre.Console](https://spectreconsole.net/).

```bash
dotnet add package NBenchmark.Reporters.Console
```

You only need `NBenchmark.Reporters.Console` if you want output in the terminal. File reporters (JSON, Markdown, CSV) work without it.

### Dependency Injection package

The DI package lets `[Benchmark]` classes have **constructor dependencies** that are resolved from an `IServiceProvider`. Without it, benchmark classes must have a public parameterless constructor. See the [Dependency Injection guide](../features/dependency-injection.md) for full details.

```bash
dotnet add package NBenchmark.DependencyInjection
```

```bash
# Or, if you also want the concrete Microsoft.Extensions.DependencyInjection
# implementation (ServiceCollection, BuildServiceProvider, etc.):
dotnet add package Microsoft.Extensions.DependencyInjection
```

### Test framework integration packages

The integration packages let you enforce performance thresholds as ordinary test assertions inside your existing test project. When a threshold is exceeded, the test fails.

```bash
dotnet add package NBenchmark.Integration.xUnit    # xUnit v2
dotnet add package NBenchmark.Integration.NUnit    # NUnit 3 / 4
dotnet add package NBenchmark.Integration.MSTest   # MSTest v2 / v3
```

Each package pulls in `NBenchmark` automatically, so you don't need a separate `NBenchmark` reference. See the [Test integration](../test-integration/index.md) guide for usage.

## Global tool

The global tool lets you run benchmarks from the command line without creating a project. Install it once, then point it at any assembly with `[Benchmark]` methods.

```bash
dotnet tool install -g NBenchmark.Tool
```

After installation, run `dotnet benchmark --help` to verify. See the [Global Tool guide](../usage-modes/global-tool.md) for usage.

## Next steps

- **[Choose your path](./choose-your-path.md)** - match what you want to do to the right API
- **[Quick Start](./quick-start.md)** - your first benchmark in 60 seconds
