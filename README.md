# NBenchmark

[![Build](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml/badge.svg)](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-blue.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Straightforward benchmarking for .NET.**

Benchmarking sounds simple - run it, time it, compare. In practice the numbers are easy to get wrong: the JIT is still optimizing your method during the first runs, the timer can cost more than a fast method, one GC pause skews an average, and the 2% improvement you were sure you measured can be noise.

NBenchmark handles that for you. One line gives you a calibrated, warmed-up, outlier-trimmed result with a confidence interval.

```csharp
var result = Benchmark.Run(() => MandelbrotCalculation());
result.Print();
```

[![NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-single.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-single.png)

## Why NBenchmark?

- **No setup.** One static call. No attributes, no class structure, no dedicated project.
- **No numbers to guess.** Warmup, batch size, and sample count all resolve themselves.
- **Clean process by default.** Your numbers reflect your code, not your process's history.
- **Real statistics.** Confidence intervals and significance testing, not just an average.
- **Zero dependencies.** The core package is BCL-only.

## Installation

```bash
dotnet add package NBenchmark
```

### Optional packages

| Package | Purpose |
| --- | --- |
| `NBenchmark.Analyzers` | Roslyn analyzers that catch authoring mistakes at build time |
| `NBenchmark.DependencyInjection` | Constructor injection for benchmark classes |
| `NBenchmark.Reporters.Console` | Rich terminal tables via Spectre.Console |
| `NBenchmark.Integration.xUnit` | Enforce performance thresholds as xUnit tests |
| `NBenchmark.Integration.NUnit` | Enforce performance thresholds as NUnit tests |
| `NBenchmark.Integration.MSTest` | Enforce performance thresholds as MSTest tests |

## Four modes, one engine

### 1. Single mode

The fastest way to get a reliable number.

```csharp
var result = Benchmark.Run(() => int.Parse("12345"));
Console.WriteLine($"P95: {result.GetPercentile(0.95)} ns, Alloc: {result.MeanAllocatedBytes} B");

var result = await Benchmark.RunAsync(async () => await FetchDataAsync());
```

### 2. Suite mode

Compare multiple implementations with a fluent API.

```csharp
var results = await new BenchmarkSuite("string concat")
    .Add("plus operator", () => "a" + "b" + "c")
    .Add("interpolation",  () => $"{"a"}{"b"}{"c"}")
    .WithBaseline("plus operator")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

The output includes a **Ratio** column and a **✓** in the **Sig** column when the speed difference is statistically significant.

### 3. Harness mode

Attribute-based discovery with a CLI, for dedicated benchmark projects.

```csharp
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c";

    [Benchmark]
    public string Interpolate() => $"{"a"}{"b"}{"c"}";
}

await BenchmarkHarness.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

```bash
dotnet run -- --filter StringBenchmarks.Concat  # Run a specific benchmark
dotnet run -- --dry-run                         # Validate wiring without running
dotnet run -- --reporter json                   # Output results for CI/CD
```

### 4. Global tool

Install once, benchmark any assembly with `[Benchmark]` methods - no project needed.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyApp.Benchmarks
dotnet benchmark --assembly ./bin/Release/net10.0/MyLib.dll
```

All harness CLI flags pass through (`--filter`, `--reporter`, `--output`, `--threshold-pct`, etc.).

## Features

| Feature | What it does | |
| --- | --- | --- |
| Isolated runs | Measures in a fresh worker process so earlier work can't bias the numbers. On by default. | [→](./docs/features/isolated-runs.md) |
| Parameterized benchmarks | Runs one body across many input values to show how it scales. | [→](./docs/features/parameterized-suite.md) |
| Categories | Tags benchmarks and includes or excludes groups from a run. | [→](./docs/features/categories.md) |
| Multi-runtime | Runs the same benchmarks on net8, net9, and net10 side-by-side. | [→](./docs/features/multi-runtime.md) |
| Multiple launches | Repeats a benchmark in separate processes to measure run-to-run variance. | [→](./docs/features/multiple-launches.md) |
| Environment control | Pins CPU affinity and process priority to cut noise at the source. | [→](./docs/features/environment-control.md) |
| Performance gates | Fails xUnit, NUnit, or MSTest tests on regression. | [→](./docs/test-integration/index.md) |
| CI regression gate | Fails the run when a benchmark regresses past a percentage. | [→](./docs/reference/cli.md) |
| Diagnostics | Records GC counts, heap state, exceptions, and CPU time per operation. | [→](./docs/statistics/diagnostics.md) |
| Live telemetry | Streams per-sample events to an observer or to OpenTelemetry. | [→](./docs/reference/observers.md) |
| Compile-time analysis | Catches benchmark authoring mistakes as build-time diagnostics. | [→](./docs/reference/analyzers.md) |
| Pluggable statistics | Swaps in your own outlier detector or significance test. | [→](./docs/guides/custom-statistics.md) |

## Built on real statistics

- **Adaptive measurement.** Samples stream until the confidence interval is tight enough, then stop. Warmup ends when the timings plateau and the JIT has settled - not after a guessed count. ([Measurement](./docs/statistics/measurement.md))
- **Error bars that survive trimming.** Discarding an outlier does not narrow the confidence interval: a discarded sample still counts as an observation, so the reported margin describes the run that happened rather than the samples that survived it. ([Outlier trimming](./docs/statistics/outliers.md))
- **Non-parametric significance testing.** Benchmark timings are not normally distributed, so the built-in tests are rank-based. A ✓ in the `Sig` column means "real, and at least a small effect", not merely `p < 0.05`. ([Significance testing](./docs/statistics/significance.md))
- **Verified against SciPy and NumPy.** Every statistical primitive is dependency-free and cross-validated on each build. ([Validation](./docs/statistics/validation.md))

---

View the full documentation at [docs.nbenchmark.net](https://docs.nbenchmark.net).
