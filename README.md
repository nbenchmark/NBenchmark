# NBenchmark

[![Build](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml/badge.svg)](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-blue.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Zero-ceremony benchmarking for .NET.**

NBenchmark provides a low-overhead measurement engine with built-in statistical analysis. It moves beyond raw averages by providing confidence intervals, outlier trimming, and significance testing out of the box - allowing you to differentiate between a real performance gain and background noise.

```csharp
var result = Benchmark.Run(() => JsonSerializer.Deserialize<MyDto>(json));
result.Print();
```

[<img src="https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/console-results.png" width="640" alt="NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark">](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/console.png)

## Why NBenchmark?

- **Zero-ceremony measurements.** `Benchmark.Run(() => ...)` requires no attributes, no class structures, and no dedicated project. Run a reliable benchmark directly in your existing code or scratchpad.

- **Statistical rigor by default.** Includes 25 warmup iterations, 200 measured iterations, IQR-fence outlier trimming, and 95% confidence intervals. It also includes a Mann-Whitney U significance test to validate A/B comparisons.

- **Low-overhead execution.** The measurement loop is reflection-free. The engine uses typed delegates to avoid virtual dispatch and boxing during timing, ensuring the JIT optimizes your benchmark body just as it would in production.

- **Async-native.** Measures the true duration of `Task` and `Task<T>` async work without sync-over-async wrappers.

- **Automated A/B comparisons.** `BenchmarkSuite` runs implementations side-by-side, calculates ratios, and flags whether differences are statistically significant.

- **Pragmatic package structure.** The core `NBenchmark` package is zero-dependency. Opt-in to additional features like Spectre.Console tables, Dependency Injection, or test framework integration as needed.
- **Compile-time analysis.** The optional `NBenchmark.Analyzers` package catches common benchmark authoring mistakes (dead code elimination, implicit order dependence, missing return values) as Roslyn diagnostics during build, before you ever run a measurement.

---

## Installation

```bash
dotnet add package NBenchmark
```

### Optional Packages

| Package | Purpose |
|---|---|
| `NBenchmark.Reporters.Console` | Rich terminal tables via Spectre.Console |
| `NBenchmark.Analyzers` | Roslyn analyzers to catch common authoring mistakes |
| `NBenchmark.DependencyInjection` | Constructor injection for benchmark classes |
| `NBenchmark.Integration.xUnit` | Enforce performance thresholds as xUnit tests |
| `NBenchmark.Integration.NUnit` | Enforce performance thresholds as NUnit tests |
| `NBenchmark.Integration.MSTest` | Enforce performance thresholds as MSTest tests |

## Usage Modes

### 1. Quick Mode (Ad-hoc checks)

The fastest way to get a reliable number.

```csharp
// Sync or Async
var result = await Benchmark.RunAsync(async () => await FetchDataAsync());

// Returns the value - the runner consumes it to keep the body alive for the JIT
var result = Benchmark.Run(() => int.Parse("12345"));

// Programmatic access to results
Console.WriteLine($"P95: {result.P95} ns, Alloc: {result.MeanAllocatedBytes} B");
```

### 2. Suite Mode (Side-by-side comparison)

Compare multiple implementations with a fluent API.

```csharp
var results = await new BenchmarkSuite("string concat")
    .Add("plus operator", () => "a" + "b" + "c")
    .Add("interpolation",  () => $"{"a"}{"b"}{"c"}")
    .WithBaseline("plus operator")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

The output includes a **Ratio** column and a **✓** signifier if the speed difference is statistically significant.

### 3. Host Mode (Dedicated projects)

Attribute-based discovery with a full CLI, designed for dedicated benchmark projects.

```csharp
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c";

    [Benchmark]
    public string Interpolate() => $"{"a"}{"b"}{"c"}";
}

// Program.cs
await BenchmarkHost.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

```bash
dotnet run -- --filter StringBenchmarks.Concat  # Run a specific benchmark
dotnet run -- --dry-run                         # Validate wiring without running
dotnet run -- --reporter json                   # Output results for CI/CD
```

## Performance Gates (CI/CD)

Enforce performance SLAs directly in your test suite. If a benchmark exceeds the threshold, the test fails.

```csharp
[PerformanceFact(MaxMeanNs = 500_000, MaxAllocatedBytes = 1024)]
public void CriticalPath_ShouldBeFast() => ProcessOrder(testOrder);
```

Supports P95 latency, allocation limits, and **baseline regression checks** (comparing against a previously saved JSON result).

---

## Documentation Index

- [Installation](./docs/getting-started/installation.md)
- [Quick Start Guide](./docs/getting-started/quick-start.md)
- [Key Concepts (Warmup, Outliers, Statistics)](./docs/getting-started/key-concepts.md)
- [Configuration Reference](./docs/reference/configuration.md)
- [CLI Reference](./docs/reference/cli.md)
- [Statistical Methodology](./docs/statistics/index.md)
- [Analyzers (NB0001-NB0010)](./docs/reference/analyzers.md)
