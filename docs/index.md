---
title: NBenchmark
description: Straightforward .NET benchmarking. Low-overhead measurement with built-in statistical analysis, confidence intervals, and significance testing.
order: 0
---

# NBenchmark

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

To get started, install NBenchmark and use the [Quick Start](./getting-started/quick-start.md) guide to create your first benchmark in 60 seconds. If you're not sure which API to use, [choose your path](./getting-started/choose-your-path.md).

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
var results = await new BenchmarkSuite("sorting")
    .Add("Array.Sort", () => { var a = data.ToArray(); Array.Sort(a); })
    .Add("LINQ OrderBy", () => { _ = data.OrderBy(x => x).ToArray(); })
    .WithBaseline("Array.Sort")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

[![NBenchmark console output showing a suite comparison table with ratio and significance columns](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)

### Harness mode

Harness mode provides attribute-based discovery with a built-in CLI for dedicated benchmark projects.

```csharp
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c";

    [Benchmark]
    public string Interpolate() => $"{"a"}{"b"}{"c"}";

    [Benchmark]
    public string Join() => string.Join("", "a", "b", "c");
}

await BenchmarkHarness.Create(args).AddFromAssembly<StringBenchmarks>().RunAsync();
```

[![NBenchmark console output showing a harness run with per-class comparison tables](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-harness.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-harness.png)

### Global tool

Install the global tool once to benchmark any assembly with `[Benchmark]` methods without needing a project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyApp.Benchmarks
```

All harness CLI flags pass through. For more information, see [Global tool](./usage-modes/global-tool.md).

## Features

| Feature | Description | Link |
| --- | --- | --- |
| Isolated runs | Measures in a fresh worker process to prevent earlier work from biasing the numbers. On by default. | [→](./features/isolated-runs.md) |
| Parameterized benchmarks | Runs one body across many input values to show how it scales. | [→](./features/parameterized-suite.md) |
| Categories | Tags benchmarks to include or exclude groups from a run. | [→](./features/categories.md) |
| Multi-runtime | Runs the same benchmarks on .NET 8, .NET 9, and .NET 10 side-by-side. | [→](./features/multi-runtime.md) |
| Multiple launches | Repeats a benchmark in separate processes to measure run-to-run variance. | [→](./features/multiple-launches.md) |
| Environment control | Pins CPU affinity and process priority for the process and measuring thread to reduce noise. | [→](./features/environment-control.md) |
| Host drift canary | Times fixed control work between benchmarks to detect if the machine drifted mid-run. On by default. | [→](./statistics/measurement.md#the-host-drift-canary) |
| Interference rejection | Discards samples that the OS preempted using the measuring thread's CPU occupancy. On by default. | [→](./statistics/outliers.md#evidence-based-interference-rejection) |
| Performance gates | Fails xUnit, NUnit, or MSTest tests on regression. | [→](./test-integration/index.md) |
| CI regression gate | Fails the run when a benchmark regresses past a specified percentage. | [→](./reference/cli.md) |
| Diagnostics | Records GC counts, heap state, exceptions, and CPU time per operation. | [→](./statistics/diagnostics.md) |
| Live telemetry | Streams per-sample events to an observer or to OpenTelemetry. | [→](./reference/observers.md) |
| Compile-time analysis | Catches benchmark authoring mistakes as build-time diagnostics. | [→](./reference/analyzers.md) |
| Pluggable statistics | Allows you to swap in your own outlier detector or significance test. | [→](./guides/custom-statistics.md) |

## Built on real statistics

NBenchmark doesn't use a simple average of a fixed loop.

- **Adaptive measurement.** Samples stream until the confidence interval is tight enough and then stop. Warmup ends when the timings plateau and the JIT settles, rather than after a guessed count. ([Measurement](./statistics/measurement.md))
- **Error bars that survive trimming.** Discarding an outlier doesn't narrow the confidence interval. A discarded sample still counts as an observation, so the reported margin describes the run that occurred rather than only the samples that survived. ([Outlier trimming](./statistics/outliers.md))
- **Host drift detection.** Fixed work is timed at every benchmark boundary. If a machine gets slower mid-run, NBenchmark detects it. A comparison smaller than the drift that separated its two rows is flagged rather than reported as a result. ([Measurement](./statistics/measurement.md#the-host-drift-canary))
- **Non-parametric significance testing.** Benchmark timings are not normally distributed, so the built-in tests are rank-based. A checkmark (✓) in the `Sig` column means the effect is real and at least small, not merely that `p < 0.05`. ([Significance testing](./statistics/significance.md))
- **Cross-validated results.** Every statistical primitive is dependency-free and cross-validated on each build against SciPy and NumPy. ([Validation](./statistics/validation.md))

## Packages

| Package | Purpose |
| --- | --- |
| `NBenchmark` | Core engine, statistics, and the compile-time checks for benchmark correctness |
| `NBenchmark.DependencyInjection` | Constructor injection for benchmark classes |
| `NBenchmark.Reporters.Console` | Terminal tables via Spectre.Console |
| `NBenchmark.Exporters.OpenTelemetry` | Per-sample telemetry over OTLP |
| `NBenchmark.Integration.xUnit` | xUnit performance assertions |
| `NBenchmark.Integration.NUnit` | NUnit performance assertions |
| `NBenchmark.Integration.MSTest` | MSTest performance assertions |
| `NBenchmark.Tool` | .NET global tool to run benchmarks from the CLI |

## Next steps

- **[Usage modes](./usage-modes/)** - Walkthroughs for each of the four modes
- **[Guides](./guides/)** - Workflow recipes for CI/CD tuning, refactor comparison, and ASP.NET services
- **[Features](./features/)** - Details on isolated runs, parameters, categories, multi-runtime, launches, and DI
- **[Statistics](./statistics/)** - Explanations of how every number is calculated
- **[Reference](./reference/)** - Configuration, CLI flags, and analyzer diagnostics
- **[Deep dives](./deep-dives/)** - Engineering internals
