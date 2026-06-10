# NBenchmark

[![Build](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml/badge.svg)](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-blue.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

NBenchmark is the easiest way to measure the speed of your .NET code. With zero setup and a focus on simplicity, you can go from installation to your first accurate benchmark in seconds-perfect for quick checks and full performance suites alike.

📖 **[Full documentation →](./docs/index.md)**

---

## Packages

| Package | Description |
|---|---|
| `NBenchmark` | Zero-dependency core - all measurement, statistics, and file reporters. |
| `NBenchmark.Analyzers` | Roslyn analyzers that catch common benchmark authoring mistakes at compile time. |
| `NBenchmark.DependencyInjection` | Resolves benchmark classes from an `IServiceProvider` so they can have constructor dependencies. |
| `NBenchmark.Reporters.Console` | Adds a rich terminal table via [Spectre.Console](https://spectreconsole.net/). |
| `NBenchmark.Integration.xUnit` | Run NBenchmark benchmarks as xUnit tests with configurable performance thresholds. |
| `NBenchmark.Integration.NUnit` | Run NBenchmark benchmarks as NUnit tests with configurable performance thresholds. |
| `NBenchmark.Integration.MSTest` | Run NBenchmark benchmarks as MSTest tests with configurable performance thresholds. |

```bash
dotnet add package NBenchmark
dotnet add package NBenchmark.Analyzers            # optional, compile-time checks for benchmark correctness
dotnet add package NBenchmark.DependencyInjection   # optional, for benchmark classes with constructor dependencies
dotnet add package NBenchmark.Reporters.Console    # optional, for pretty terminal output
dotnet add package NBenchmark.Integration.xUnit    # optional, run benchmarks as xUnit tests
dotnet add package NBenchmark.Integration.NUnit    # optional, run benchmarks as NUnit tests
dotnet add package NBenchmark.Integration.MSTest   # optional, run benchmarks as MSTest tests
```

## Quick start

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

```
  Benchmark: 1.20 µs median
    Mean: 1.24 µs, P95: 2.00 µs
    StdDev: 360 ns
    95% CI: 1.19 µs … 1.29 µs (±50 ns)
```

## Three usage modes

**Quick mode - `Benchmark.Run`** - a single static call, no setup required.

**Suite mode - `BenchmarkSuite`** - a fluent builder that runs multiple benchmarks side-by-side and produces a comparison table with ratios and statistical significance.

**Host mode - `BenchmarkHost`** - attribute-based discovery (`[Benchmark]`, `[BenchmarkArguments]`, lifecycle attributes) driven by a built-in CLI. Designed for dedicated benchmark projects.

All three modes share the same measurement engine, produce the same `BenchmarkResult` type, and support the same reporters and configuration.

## Documentation

| | |
|---|---|
| [Getting Started](./docs/getting-started/installation.md) | Installation, quick start, key concepts |
| [Guides](./docs/guides/index.md) | Detailed walkthroughs for each mode |
| [Dependency Injection](./docs/guides/dependency-injection.md) | Benchmark classes with constructor dependencies |
| [Configuration](./docs/configuration.md) | All `MeasurementOptions` settings |
| [Reporters](./docs/reporters/index.md) | Console, Markdown, CSV, JSON |
| [CLI Reference](./docs/cli-reference.md) | All `BenchmarkHost` command-line flags |
| [Advanced: Statistics](./docs/advanced/statistics.md) | How every number is calculated |
| [Samples](./docs/samples.md) | Runnable sample projects |
| [FAQ](./docs/faq.md) | Common questions |
