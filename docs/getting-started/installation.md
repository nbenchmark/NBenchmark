---
title: Installation
description: How to add NBenchmark to a .NET project.
order: 1
---

# Installation

## Requirements

NBenchmark targets **net8.0**, **net9.0**, and **net10.0**. You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later.

## Packages

NBenchmark ships as three NuGet packages. Install only what you need.

### Core package

The core package contains all measurement, statistics, and file-based reporters (JSON, Markdown, CSV). It has **no NuGet dependencies** - only the .NET BCL.

```bash
dotnet add package NBenchmark
```

### Console package (optional)

The console package adds a rich terminal table with colour-coded results and an optional progress display. It depends on [Spectre.Console](https://spectreconsole.net/).

```bash
dotnet add package NBenchmark.Reporters.Console
```

You only need `NBenchmark.Reporters.Console` if you want output in the terminal. File reporters (JSON, Markdown, CSV) work without it.

### Dependency Injection package (optional)

The DI package lets `[Benchmark]` classes have **constructor dependencies** that are resolved from an `IServiceProvider`. Without it, benchmark classes must have a public parameterless constructor (the same constraint as today). See the [Dependency Injection guide](../guides/dependency-injection.md) for full details.

```bash
dotnet add package NBenchmark.DependencyInjection
```

```bash
# Or, if you also want the concrete Microsoft.Extensions.DependencyInjection
# implementation (ServiceCollection, BuildServiceProvider, etc.):
dotnet add package Microsoft.Extensions.DependencyInjection
```

### Analyzers package (optional)

The analyzers package adds compile-time diagnostics that catch common NBenchmark configuration errors - missing parameterless constructors, static benchmark methods, out-of-range settings, and more.

```bash
dotnet add package NBenchmark.Analyzers
```

The analyzers run automatically in the IDE and during `dotnet build`. See the [Analyzers](../analyzers.md) page for the full diagnostic reference.

## Verify the installation

Create a new console project and add a quick sanity check:

```bash
dotnet new console -n MyBenchmarks
cd MyBenchmarks
dotnet add package NBenchmark
dotnet add package NBenchmark.Reporters.Console
```

Replace the contents of `Program.cs`:

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

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

```
  Benchmark: 1.20 µs median
    Mean: 1.24 µs, P95: 2.00 µs
    StdDev: 360 ns
    95% CI: 1.19 µs … 1.29 µs (±50 ns)
```

If you see numbers, everything is working.

## Next steps

Continue to the [Quick Start](./quick-start.md) guide to learn more about what you can do.
