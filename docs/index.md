---
title: NBenchmark
description: Zero-ceremony .NET benchmarking. Low-overhead measurement with built-in statistical analysis, confidence intervals, and significance testing.
order: 0
---

# NBenchmark

**Zero-ceremony benchmarking for .NET.**

NBenchmark provides a low-overhead measurement engine with built-in statistical analysis. It moves beyond raw averages by providing confidence intervals, outlier trimming, and significance testing out of the box - allowing you to differentiate between a real performance gain and background noise.

```csharp
var result = Benchmark.Run(() => MandelbrotCalculation(name: "Mandelbrot calculation"));
result.Print();
```

[![NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-quick.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-quick.png)

## Why NBenchmark?

- **Zero-ceremony measurements.** `Benchmark.Run(() => ...)` requires no attributes, no class structures, and no dedicated project. Run a reliable benchmark directly in your existing code or scratchpad.
- **Statistical rigor by default.** An adaptive measurement loop auto-sizes warmup, sample count, and ops-per-batch for each benchmark, applies IQR-fence outlier trimming, and reports 95% confidence intervals. It also includes a Mann-Whitney U significance test to validate A/B comparisons.
- **Low-overhead execution.** The measurement loop is reflection-free. The engine uses typed delegates to avoid virtual dispatch and boxing during timing, ensuring the JIT optimizes your benchmark body just as it would in production.
- **Async-native.** Measures the true duration of `Task` and `Task<T>` async work without sync-over-async wrappers.
- **Automated A/B comparisons.** `BenchmarkSuite` runs implementations side-by-side, calculates ratios, and flags whether differences are statistically significant.
- **Pragmatic package structure.** The core `NBenchmark` package is zero-dependency. Opt-in to additional features like Spectre.Console tables, Dependency Injection, or test framework integration as needed.
- **Compile-time analysis.** The optional `NBenchmark.Analyzers` package catches common benchmark authoring mistakes (dead code elimination, implicit order dependence, missing return values) as Roslyn diagnostics during build, before you ever run a measurement.

## Three modes, one engine

### 1. Quick Mode (Ad-hoc)

The fastest way to get a reliable number. No setup required.

```csharp
var result = Benchmark.Run(() => MyMethod());
var result = await Benchmark.RunAsync(async () => await FetchAsync());
```

### 2. Suite Mode (Comparison)

Compare implementations side-by-side with ratios and significance testing.

```csharp
var results = await new BenchmarkSuite("sorting")
    .Add("Array.Sort", () => { var a = data.ToArray(); Array.Sort(a); })
    .Add("LINQ OrderBy", () => { _ = data.OrderBy(x => x).ToArray(); })
    .WithBaseline("Array.Sort")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

[![NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)

### 3. Host Mode (CLI)

Attribute-based discovery with a built-in CLI. Designed for dedicated benchmark projects.

```csharp
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c";

    [Benchmark]
    public string Interpolate() => $"{"a"}{"b"}{"c"}";

    [Benchmark]
    public string Format() => string.Format("{0}{1}{2}", "a", "b", "c");

    [Benchmark]
    public string Join() => string.Join("", "a", "b", "c");

    [Benchmark]
    public string Create() => new string(new[] { 'a', 'b', 'c' });
}

await BenchmarkHost.Create(args).AddFromAssembly<StringBenchmarks>().RunAsync();
```

[![NBenchmark console output showing median, mean, P95, P99, StdDev, CV, and confidence interval for a benchmark](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-host.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-host.png)

## Performance Gates (CI/CD)

Enforce performance SLAs directly in your test suite. If a benchmark exceeds the threshold, the test fails.

```csharp
[PerformanceFact(MaxMeanNs = 500_000, MaxAllocatedBytes = 1024)]
public void CriticalPath_ShouldBeFast() => ProcessOrder(testOrder);
```

## Packages

| Package | Purpose |
|---|---|
| `NBenchmark` | Zero-dependency core engine and statistics |
| `NBenchmark.Tool` | dotnet global tool - run benchmarks from the CLI without a project |
| `NBenchmark.Reporters.Console` | Rich terminal tables via Spectre.Console |
| `NBenchmark.Analyzers` | Compile-time checks for benchmark correctness |
| `NBenchmark.DependencyInjection` | Constructor injection for benchmark classes |
| `NBenchmark.Integration.xUnit` | xUnit performance assertions |
| `NBenchmark.Integration.NUnit` | NUnit performance assertions |
| `NBenchmark.Integration.MSTest` | MSTest performance assertions |

## Getting Started

- **[Installation](./getting-started/installation.md)** - add the NuGet packages
- **[Quick Start](./getting-started/quick-start.md)** - your first benchmark in 60 seconds
- **[Key Concepts](./getting-started/key-concepts.md)** - warmup, outliers, and statistics
- **[Guides](./guides/index.md)** - detailed walkthroughs for each mode
- **[Configuration](./reference/configuration.md)** - every option explained
- **[Analyzers](./reference/analyzers.md)** - compile-time diagnostics (NB0001-NB0010)
- **[Statistics](./statistics/index.md)** - how the numbers are calculated
