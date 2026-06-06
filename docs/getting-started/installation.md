---
title: Installation
description: How to add NBenchmark to a .NET project.
order: 1
---

# Installation

## Requirements

NBenchmark targets **net10.0**. You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) or later.

## Packages

NBenchmark ships as two NuGet packages. Install only what you need.

### Core package

The core package contains all measurement, statistics, and file-based reporters (JSON, Markdown, CSV). It has **no NuGet dependencies** — only the .NET BCL.

```bash
dotnet add package NBenchmark
```

### Console package (optional)

The console package adds a rich terminal table with colour-coded results and an optional progress display. It depends on [Spectre.Console](https://spectreconsole.net/).

```bash
dotnet add package NBenchmark.Console
```

You only need `NBenchmark.Console` if you want output in the terminal. File reporters (JSON, Markdown, CSV) work without it.

## Verify the installation

Create a new console project and add a quick sanity check:

```bash
dotnet new console -n MyBenchmarks
cd MyBenchmarks
dotnet add package NBenchmark
dotnet add package NBenchmark.Console
```

Replace the contents of `Program.cs`:

```csharp
using NBenchmark;
using NBenchmark.Console;

var result = Bench.Time(() =>
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

Continue to the [Quick Start](./quick-start) guide to learn more about what you can do.
