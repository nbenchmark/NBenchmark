# NBenchmark

A lightweight, async-native .NET benchmarking library. No configuration files, no separate compilation step - add a reference and start measuring.

> **Pre-release.** NBenchmark targets .NET 8+ (net8.0, net9.0, net10.0) and is under active development. The API may change between versions.

📖 **[Full documentation →](./docs/index.md)**

---

## Packages

| Package | Description |
|---|---|
| `NBenchmark` | Zero-dependency core - all measurement, statistics, and file reporters. |
| `NBenchmark.Console` | Adds a rich terminal table via [Spectre.Console](https://spectreconsole.net/). |
| `NBenchmark.DependencyInjection` | Resolves benchmark classes from an `IServiceProvider` so they can have constructor dependencies. |

```bash
dotnet add package NBenchmark
dotnet add package NBenchmark.Console            # optional, for pretty terminal output
dotnet add package NBenchmark.DependencyInjection   # optional, for benchmark classes with constructor dependencies
```

## Quick start

```csharp
using NBenchmark;
using NBenchmark.Console;

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

## Three usage tiers

**Tier 1 - `Benchmark.Run`** - a single static call, no setup required.

**Tier 2 - `BenchmarkSuite`** - a fluent builder that runs multiple benchmarks side-by-side and produces a comparison table with ratios and statistical significance.

**Tier 3 - `BenchmarkHost`** - attribute-based discovery (`[Benchmark]`, `[BenchmarkArguments]`, lifecycle attributes) driven by a built-in CLI. Designed for dedicated benchmark projects.

All three tiers share the same measurement engine, produce the same `BenchmarkResult` type, and support the same reporters and configuration.

## Documentation

| | |
|---|---|
| [Getting Started](./docs/getting-started/installation.md) | Installation, quick start, key concepts |
| [Guides](./docs/guides/index.md) | Detailed walkthroughs for each tier |
| [Dependency Injection](./docs/guides/dependency-injection.md) | Benchmark classes with constructor dependencies |
| [Configuration](./docs/configuration.md) | All `MeasurementOptions` settings |
| [Reporters](./docs/reporters/index.md) | Console, Markdown, CSV, JSON |
| [CLI Reference](./docs/cli-reference.md) | All `BenchmarkHost` command-line flags |
| [Advanced: Statistics](./docs/advanced/statistics.md) | How every number is calculated |
| [Samples](./docs/samples.md) | Runnable sample projects |
| [FAQ](./docs/faq.md) | Common questions |
