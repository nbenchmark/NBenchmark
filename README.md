# NBenchmark

[![Build](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml/badge.svg)](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-blue.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Straightforward benchmarking for .NET.**

Benchmarking sounds simple: run it, time it, and compare. In practice, you can easily get the numbers wrong. The JIT might still be optimizing your method during the first runs, the timer can cost more than a fast method, a single GC pause can skew an average, and a small improvement might just be noise.

NBenchmark handles these issues for you. One line of code provides a calibrated, warmed-up, and outlier-trimmed result with a confidence interval.

```csharp
var result = Benchmark.Run(() => MandelbrotCalculation());
result.Print();
```

[![NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-single.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-single.png)

## Why NBenchmark?

- **No setup.** Use one static call. You don't need attributes, a specific class structure, or a dedicated project.
- **No guessing.** Warmup, batch size, and sample count resolve automatically.
- **Clean processes by default.** Results reflect your code rather than the history of your process.
- **Real statistics.** Use confidence intervals and significance testing instead of simple averages.
- **Zero dependencies.** The core package uses only the BCL.

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

### Single mode

Single mode is the fastest way to get a reliable number.

```csharp
var result = Benchmark.Run(() => MyMethod());
var result = await Benchmark.RunAsync(async () => await FetchAsync());
```

### Suite mode

Use suite mode to compare implementations side-by-side with ratios and significance testing.

```csharp
var results = await new BenchmarkSuite("string concat")
    .Add("plus operator", () => "a" + "b" + "c")
    .Add("interpolation",  () => $"{"a"}{"b"}{"c"}")
    .WithBaseline("plus operator")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

The output includes a **Ratio** column and a **✓** in the **Sig** column when the speed difference is statistically significant.

### Harness mode

Harness mode provides attribute-based discovery with a built-in CLI for dedicated benchmark projects.

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

### Global tool

Install the global tool once to benchmark any assembly with `[Benchmark]` methods without needing a project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyApp.Benchmarks
dotnet benchmark --assembly ./bin/Release/net10.0/MyLib.dll
```

All harness CLI flags pass through (`--filter`, `--reporter`, `--output`, `--threshold-pct`, etc.).

## Features

| Feature | Description | Link |
| --- | --- | --- |
| Isolated runs | Measures in a fresh worker process to prevent earlier work from biasing the numbers. On by default. | [→](./docs/features/isolated-runs.md) |
| Parameterized benchmarks | Runs one body across many input values to show how it scales. | [→](./docs/features/parameterized-suite.md) |
| Categories | Tags benchmarks to include or exclude groups from a run. | [→](./docs/features/categories.md) |
| Multi-runtime | Runs the same benchmarks on .NET 8, .NET 9, and .NET 10 side-by-side. | [→](./docs/features/multi-runtime.md) |
| Multiple launches | Repeats a benchmark in separate processes to measure run-to-run variance. | [→](./docs/features/multiple-launches.md) |
| Environment control | Pins CPU affinity and process priority for the process and measuring thread to reduce noise. | [→](./docs/features/environment-control.md) |
| Interference rejection | Discards samples that the OS preempted using the measuring thread's CPU occupancy. On by default. | [→](./docs/statistics/outliers.md#evidence-based-interference-rejection) |
| Performance gates | Fails xUnit, NUnit, or MSTest tests on regression. | [→](./docs/test-integration/index.md) |
| CI regression gate | Fails the run when a benchmark regresses past a specified percentage. | [→](./docs/reference/cli.md) |
| Diagnostics | Records GC counts, heap state, exceptions, and CPU time per operation. | [→](./docs/statistics/diagnostics.md) |
| Live telemetry | Streams per-sample events to an observer or to OpenTelemetry. | [→](./docs/reference/observers.md) |
| Compile-time analysis | Catches benchmark authoring mistakes as build-time diagnostics. | [→](./docs/reference/analyzers.md) |
| Pluggable statistics | Allows you to swap in your own outlier detector or significance test. | [→](./docs/guides/custom-statistics.md) |

## Built on real statistics

NBenchmark doesn't use a simple average of a fixed loop.

- **Adaptive measurement.** Samples stream until the confidence interval is tight enough and then stop. Warmup ends when the timings plateau and the JIT settles, rather than after a guessed count. ([Measurement](./docs/statistics/measurement.md))
- **Error bars that survive trimming.** Discarding an outlier doesn't narrow the confidence interval. A discarded sample still counts as an observation, so the reported margin describes the run that occurred rather than only the samples that survived. ([Outlier trimming](./docs/statistics/outliers.md))
- **Non-parametric significance testing.** Benchmark timings are not normally distributed, so the built-in tests are rank-based. A checkmark (✓) in the `Sig` column means the effect is real and at least small, not merely that `p < 0.05`. ([Significance testing](./docs/statistics/significance.md))
- **Cross-validated results.** Every statistical primitive is dependency-free and cross-validated on each build against SciPy and NumPy. ([Validation](./docs/statistics/validation.md))

---

View the full documentation at [docs.nbenchmark.net](https://docs.nbenchmark.net).
