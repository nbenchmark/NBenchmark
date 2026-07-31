# NBenchmark

[![Build](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml/badge.svg)](https://github.com/nbenchmark/nbenchmark/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NBenchmark.svg)](https://www.nuget.org/packages/NBenchmark)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-blue.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

**Straightforward benchmarking for .NET.**

Benchmarking code sounds simple - run it, time it, compare. In practice the numbers are easy to get wrong: the JIT compiler is still optimizing your method during the first runs, the timer can cost more than a fast method, a single GC pause or OS context switch skews an average, and a 2% improvement you were sure you measured can be statistical noise.

NBenchmark takes care of the statistical analysis. One line of code gives you a calibrated, warmed-up, outlier-trimmed result with a confidence interval.

```csharp
var result = Benchmark.Run(() => MandelbrotCalculation(name: "Mandelbrot calculation"));
result.Print();
```

[![NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-single.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-single.png)

## Why NBenchmark?

- **No setup required.** `Benchmark.Run(() => ...)` - no attributes, no class structure, no dedicated project. Drop it into a console app, a test, or a scratchpad.

- **Measured in a clean process, by default.** Each benchmark runs in its own process with a controlled runtime, so the numbers reflect your code rather than the state of whatever was running before it.

- **Adaptive measurement.** No iteration counts to guess. The engine calibrates ops-per-sample for fast methods so timer overhead doesn't dominate, and detects when warmup has plateaued so the JIT has settled. Pin any dimension when you want a fixed, reproducible run.

- **Statistical rigor built in.** Samples stream until the confidence interval is tight enough, then stop. Outlier trimming filters OS noise (IQR fence by default, with a bimodal-distribution warning when discarded samples look like real latency spikes rather than random jitter). A/B comparisons automatically determine whether a difference is statistically real or just noise, with an effect-size magnitude (Negligible / Small / Medium / Large) so a ✓ always means "real and at least a small effect". The built-in tests are non-parametric rank-based methods, cross-validated against SciPy and NumPy - see [Significance Testing](./docs/statistics/significance.md) for the methodology.

- **Pluggable statistics.** Swap in your own outlier detector (`IOutlierDetector`) or significance test (`ISignificanceTest`) when the built-in IQR/MAD trimming and rank-based tests don't fit your domain.

- **Low-overhead execution.** The measurement loop is reflection-free and uses typed delegates to avoid virtual dispatch and boxing during timing, so the JIT optimizes your benchmark body as it would in production.

- **Async-native.** Measures the true duration of `Task` and `Task<T>` work without sync-over-async wrappers.

- **Compile-time analysis.** The optional `NBenchmark.Analyzers` package catches common benchmark authoring mistakes - dead code elimination, implicit order dependence, missing return values - as Roslyn diagnostics during build, before you ever run a measurement.

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

- **Parameterized benchmarks.** Run the same body across multiple input values to see how an algorithm scales - `WithParameter` in Suite mode, `[BenchmarkCase]` in Harness mode. ([Suite](./docs/features/parameterized-suite.md) / [Harness](./docs/features/parameterized-harness.md))

- **Categories.** Tag benchmarks with `[BenchmarkCategory]` and include or exclude groups from a run via CLI flags or the programmatic filter API. ([Categories](./docs/features/categories.md))

- **Isolated runs.** Run benchmarks in freshly spawned child processes so JIT, GC, and thread-pool state from earlier work can't bias later measurements; isolated by default in Harness mode. ([Isolated runs](./docs/features/isolated-runs.md))

- **Multi-runtime comparison.** Build and run the same benchmarks across net8, net9, and net10 in separate child processes and compare side-by-side. ([Multi-runtime](./docs/features/multi-runtime.md))

- **Multiple launches.** Repeat each benchmark as independent launches to surface run-to-run variance and produce cross-launch aggregation stats. ([Multiple launches](./docs/features/multiple-launches.md))

- **Environment control.** Pin CPU affinity, raise process priority, and detect noisy hosts to reduce measurement noise at its source. ([Environment control](./docs/features/environment-control.md))

- **Performance gates in CI.** Enforce absolute or relative performance thresholds as xUnit, NUnit, or MSTest tests that fail on regression. ([Test integration](./docs/test-integration/index.md))

- **CI regression gate.** Fail the harness run with a non-zero exit code when any benchmark regresses beyond a percentage against the baseline (`--threshold-pct`). ([CLI reference](./docs/reference/cli.md))

- **Runtime diagnostics.** Record GC collection counts, heap state, exceptions, and CPU time per operation alongside timings. ([Diagnostics](./docs/statistics/diagnostics.md))

- **Live telemetry.** Stream per-sample, per-phase, and per-detector events to an `IMeasurementObserver`, or export spans and metrics to OpenTelemetry via the built-in `System.Diagnostics` instrumentation. ([Observers](./docs/reference/observers.md) / [OTel](./docs/reference/bcl-instrumentation.md))

---

View the full documentation at [nbenchmark.net](https://www.nbenchmark.net).
