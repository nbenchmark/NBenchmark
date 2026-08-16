---
title: NBenchmark
description: Straightforward .NET benchmarking. Low-overhead measurement with built-in statistical analysis, confidence intervals, and significance testing.
order: 0
---

# NBenchmark

**Straightforward benchmarking for .NET.**

Benchmarking sounds simple - run it, time it, compare. In practice the numbers are easy to get
wrong: the JIT is still optimizing your method during the first runs, the timer can cost more than
a fast method, one GC pause skews an average, and the 2% improvement you were sure you measured can
be noise.

NBenchmark handles that for you. One line gives you a calibrated, warmed-up, outlier-trimmed result
with a confidence interval.

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

**New here?** [Install it](./getting-started/installation.md), then
[Quick Start](./getting-started/quick-start.md) gets you a first benchmark in 60 seconds. Not sure
which API you want? [Choose your path](./getting-started/choose-your-path.md).

## Four modes, one engine

### 1. Single mode

The fastest way to get a reliable number.

```csharp
var result = Benchmark.Run(() => MyMethod());
var result = await Benchmark.RunAsync(async () => await FetchAsync());
```

### 2. Suite mode

Compare implementations side-by-side, with ratios and significance testing.

```csharp
var results = await new BenchmarkSuite("sorting")
    .Add("Array.Sort", () => { var a = data.ToArray(); Array.Sort(a); })
    .Add("LINQ OrderBy", () => { _ = data.OrderBy(x => x).ToArray(); })
    .WithBaseline("Array.Sort")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

[![NBenchmark console output showing a suite comparison table with ratio and significance columns](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)](https://raw.githubusercontent.com/nbenchmark/nbenchmark/main/assets/output-suite.png)

### 3. Harness mode

Attribute-based discovery with a built-in CLI, for dedicated benchmark projects.

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

### 4. Global tool

Install once, benchmark any assembly with `[Benchmark]` methods - no project needed.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyApp.Benchmarks
```

All harness CLI flags pass through. See [Global tool](./usage-modes/global-tool.md).

## Features

| Feature | What it does | |
| --- | --- | --- |
| Isolated runs | Measures in a fresh worker process so earlier work can't bias the numbers. On by default. | [→](./features/isolated-runs.md) |
| Parameterized benchmarks | Runs one body across many input values to show how it scales. | [→](./features/parameterized-suite.md) |
| Categories | Tags benchmarks and includes or excludes groups from a run. | [→](./features/categories.md) |
| Multi-runtime | Runs the same benchmarks on net8, net9, and net10 side-by-side. | [→](./features/multi-runtime.md) |
| Multiple launches | Repeats a benchmark in separate processes to measure run-to-run variance. | [→](./features/multiple-launches.md) |
| Environment control | Pins CPU affinity and process priority to cut noise at the source. | [→](./features/environment-control.md) |
| Host drift canary | Times fixed control work between benchmarks, so a machine that drifted mid-run says so. On by default. | [→](./statistics/measurement.md#the-host-drift-canary) |
| Performance gates | Fails xUnit, NUnit, or MSTest tests on regression. | [→](./test-integration/index.md) |
| CI regression gate | Fails the run when a benchmark regresses past a percentage. | [→](./reference/cli.md) |
| Diagnostics | Records GC counts, heap state, exceptions, and CPU time per operation. | [→](./statistics/diagnostics.md) |
| Live telemetry | Streams per-sample events to an observer or to OpenTelemetry. | [→](./reference/observers.md) |
| Compile-time analysis | Catches benchmark authoring mistakes as build-time diagnostics. | [→](./reference/analyzers.md) |
| Pluggable statistics | Swaps in your own outlier detector or significance test. | [→](./guides/custom-statistics.md) |

## Built on real statistics

The numbers are not an average of a fixed loop.

- **Adaptive measurement.** Samples stream until the confidence interval is tight enough, then stop.
  Warmup ends when the timings plateau and the JIT has settled - not after a guessed count.
  ([Measurement](./statistics/measurement.md))
- **Error bars that survive trimming.** Discarding an outlier does not narrow the confidence
  interval: a discarded sample still counts as an observation, so the reported margin describes the
  run that happened rather than the samples that survived it.
  ([Outlier trimming](./statistics/outliers.md))
- **A control workload that catches a drifting host.** Fixed work is timed at every benchmark
  boundary, so a machine that got slower mid-run says so - and a comparison smaller than the drift
  that separated its two rows is flagged rather than reported as a result.
  ([Measurement](./statistics/measurement.md#the-host-drift-canary))
- **Non-parametric significance testing.** Benchmark timings are not normally distributed, so the
  built-in tests are rank-based. A ✓ in the `Sig` column means "real, and at least a small effect",
  not merely `p < 0.05`. ([Significance testing](./statistics/significance.md))
- **Verified against SciPy and NumPy.** Every statistical primitive is dependency-free and
  cross-validated on each build. ([Validation](./statistics/validation.md))

## Packages

| Package | Purpose |
| --- | --- |
| `NBenchmark` | Zero-dependency core engine and statistics |
| `NBenchmark.Analyzers` | Compile-time checks for benchmark correctness |
| `NBenchmark.DependencyInjection` | Constructor injection for benchmark classes |
| `NBenchmark.Tool` | dotnet global tool - run benchmarks from the CLI without a project |
| `NBenchmark.Reporters.Console` | Rich terminal tables via Spectre.Console |
| `NBenchmark.Integration.xUnit` | xUnit performance assertions |
| `NBenchmark.Integration.NUnit` | NUnit performance assertions |
| `NBenchmark.Integration.MSTest` | MSTest performance assertions |

## Where to next

- **[Usage modes](./usage-modes/)** - walkthroughs for each of the four modes
- **[Guides](./guides/)** - workflow recipes: CI/CD tuning, refactor comparison, ASP.NET services
- **[Features](./features/)** - isolated runs, parameters, categories, multi-runtime, launches, DI
- **[Statistics](./statistics/)** - how every number is calculated
- **[Reference](./reference/)** - configuration, CLI flags, and analyzer diagnostics
- **[Deep dives](./deep-dives/)** - the engineering internals
