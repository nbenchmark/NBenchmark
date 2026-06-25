---
title: Samples
description: Runnable sample projects included with NBenchmark.
order: 8
---

# Samples

The repository includes several sample projects in the `samples/` directory that demonstrate each usage mode. Run any of them with `dotnet run`.

## Single - Single mode

**`samples/Single/`**

The simplest possible benchmark: `Benchmark.Run` on a tight loop, followed by `Print()` and `PrintAsync()`.

```bash
cd samples/Single
dotnet run
```

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
await result.PrintAsync();
```

What to look at:

- The plain-text output from `result.Print()` (core package only).
- The Spectre.Console table from `result.PrintAsync()` (requires `NBenchmark.Reporters.Console`).
- The 95% CI line in the plain-text output.

---

## Suite - Suite mode

**`samples/Suite/`**

A `BenchmarkSuite` comparing bubble sort versus LINQ sorting on a 100-element array, with a short iteration count for a fast demo run.

```bash
cd samples/Suite
dotnet run
```

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("sorting")
    .Add("bubble", () =>
    {
        var arr = Enumerable.Range(0, 100).Reverse().ToArray();
        Array.Sort(arr);
    })
    .Add("linq", () =>
    {
        _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray();
    })
    .WithBaseline("bubble")
    .WithWarmup(3)
    .WithIterations(50)
    .WithOutlierMode(OutlierMode.RemoveTop5Percent)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

What to look at:

- The comparison table with Ratio and Sig columns.
- The bar chart rendered below the table.
- The significance indicator (✓ or ✗) - does the difference appear real?

---

## Harness - Harness mode

**`samples/Harness/`**

A `BenchmarkHarness` with two attribute-based benchmarks: a fast `Compute` method and a slower `Baseline`.

```bash
cd samples/Harness
dotnet run
dotnet run -- --list
dotnet run -- --filter Compute
dotnet run -- --reporter markdown --output .
dotnet run -- --confidence 0.99
```

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;
using NBenchmark.Attributes;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<HostBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class HostBenchmarks
{
    [Benchmark]
    public int Compute() => 42;

    [Benchmark(Baseline = true)]
    public int Baseline() => 1;
}
```

What to look at:

- How `--list` shows discovered benchmarks before running.
- How `--filter` narrows the run to one benchmark.
- The Markdown file written by `--reporter markdown`.
- How `--confidence 0.99` widens the Error column compared to the default 95%.

---

## DependencyInjection - Harness mode with DI

**`samples/DependencyInjection/`**

A `BenchmarkHarness` setup where the benchmark class has **constructor dependencies** resolved from a `Microsoft.Extensions.DependencyInjection` container. Demonstrates the `NBenchmark.DependencyInjection` companion package and `UseDependencyInjection<T>`.

```bash
cd samples/DependencyInjection
dotnet run
dotnet run -- --filter DependencyInjectionBenchmarks.Read
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark.Reporters.Console;
using NBenchmark.DependencyInjection;

var services = new ServiceCollection()
    .AddSingleton<IDataStore, InMemoryDataStore>()
    .AddTransient<OrderRepository>()
    .AddTransient<DependencyInjectionBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHarness.Create(args)
    .UseDependencyInjection<DependencyInjectionBenchmarks>(services)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public sealed class DependencyInjectionBenchmarks(OrderRepository repository)
{
    [Benchmark] public int Read()  => repository.GetCurrent();
    [Benchmark] public int Write() { repository.Save(42); return 42; }
}
```

What to look at:

- The benchmark class takes an `OrderRepository` in its primary constructor - no parameterless constructor anywhere.
- `UseDependencyInjection<T>` combines assembly discovery and DI wiring in one call.
- A scoped variant (`UseScopedDependencyInjection<T>`) is also available for `DbContext`-style lifetimes.

---

## ExtensibleStats - Custom statistics

**`samples/ExtensibleStats/`**

A `BenchmarkSuite` comparing three hash algorithms (`SHA256`, `SHA1`, `MD5`) twice. The first run uses the built-in Median Absolute Deviation outlier mode; because there are three groups it automatically reports a **Kruskal-Wallis** omnibus verdict. The second run swaps in a custom `IOutlierDetector` and a custom `ISignificanceTest`.

```bash
cd samples/ExtensibleStats
dotnet run
```

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;
using NBenchmark.Stats;

// Built-in: MAD trimming + Kruskal-Wallis (3 groups -> omnibus test)
await new BenchmarkSuite("hashing")
    .Add("sha256", () => SHA256.HashData(payload))
    .Add("sha1", () => SHA1.HashData(payload))
    .Add("md5", () => MD5.HashData(payload))
    .WithBaseline("md5")
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Custom: plug in your own detector and significance rule
await new BenchmarkSuite("hashing-custom")
    .Add("sha256", () => SHA256.HashData(payload))
    .Add("sha1", () => SHA1.HashData(payload))
    .Add("md5", () => MD5.HashData(payload))
    .WithBaseline("md5")
    .WithOutlierDetector(new KeepFastestDetector(0.90))
    .WithSignificanceTest(new MedianRatioSignificanceTest(thresholdPercent: 25))
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

What to look at:

- The `Outliers: MAD` header on the first run and the `Omnibus Kruskal-Wallis across 3 groups: H(2) = ... → significant` line below the table.
- The custom detector's name (`keep fastest 90%`) in the header and the custom test's name (`median ratio (>25%)`) in the footer of the second run.
- The `KeepFastestDetector : IOutlierDetector` and `MedianRatioSignificanceTest : ISignificanceTest` implementations in `Program.cs` - templates for your own statistics.

See [Custom outlier detectors](./statistics/outliers.md#custom-outlier-detectors) and [Custom significance tests](./statistics/significance.md#custom-significance-tests).

---

## IsolatedRuns - Advanced isolation sample

**`samples/IsolatedRuns/`**

Demonstrates process isolation:

- Single mode is always in-process (`Benchmark.Run`).
- Suite mode opts into a single clean child process with `WithIsolation()`.

```bash
cd samples/IsolatedRuns
dotnet run
```

What to look at:

- The quick in-process result.
- The isolated suite comparison, where the whole suite runs in one fresh child process.
- The tradeoff between cleaner measurements and additional process-launch overhead.

---

## MultiLaunch - Cross-launch aggregation

**`samples/MultiLaunch/`**

Demonstrates the `--launch-count` / `WithLaunchCount()` / `[Benchmark(LaunchCount)]` feature for running each benchmark multiple times as independent launches, with cross-launch aggregation statistics.

```bash
cd samples/MultiLaunch
dotnet run
dotnet run -- --launch-count 5
```

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

// Suite mode: each benchmark runs 3 times
await new BenchmarkSuite("sleep")
    .Add("sleep100", () => Task.Delay(1).Wait())
    .Add("sleep200", () => Task.Delay(2).Wait())
    .WithBaseline("sleep100")
    .WithLaunchCount(3)
    .WithWarmup(5)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

What to look at:

- The "Launch Aggregation" table below the main results, showing cross-launch mean, stddev, median, and CI when `LaunchCount > 1`.
- The primary result fields come from the **best** (lowest median) launch, so the main table reflects the most favourable reading.
- How `--launch-count 5` on the CLI overrides the programmatic count.
- How the per-method `[Benchmark(LaunchCount = 3)]` attribute specifies different counts per benchmark.

---

## MultiRuntimeSuite - Suite mode multi-runtime

**`samples/MultiRuntimeSuite/`**

A `BenchmarkSuite` comparing three string concatenation methods across .NET 8, 9, and 10. Demonstrates `WithRuntimes` for cross-runtime comparison.

```bash
cd samples/MultiRuntimeSuite
dotnet run
```

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("string-concat")
    .Add("concat", () => "a" + "b" + "c" + "d" + "e")
    .Add("interpolate", () => $"a {"b"} {"c"} {"d"} {"e"}")
    .Add("join", () => string.Join("", "a", "b", "c", "d", "e"))
    .WithBaseline("concat")
    .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)
    .WithWarmup(3)
    .WithIterations(50)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

What to look at:

- The "Runtime" column in the comparison table, showing results grouped by target framework.
- How the same benchmark body produces different timings across runtimes.
- The Ratio column showing how each runtime compares to the net8 baseline.
- The project must target all runtimes in its `.csproj` (`<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>`).

---

## MultiRuntimeHarness - Harness mode multi-runtime

**`samples/MultiRuntimeHarness/`**

A `BenchmarkHarness` with attribute-based benchmarks that can be run across multiple .NET runtimes via the `--runtimes` CLI flag.

```bash
cd samples/MultiRuntimeHarness
dotnet run -- --runtimes net8,net9,net10
dotnet run -- --runtimes net8,net9 --iterations 500 --reporter markdown --output ./results
```

```csharp
using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c" + "d" + "e";

    [Benchmark]
    public string Interpolate() => $"a {"b"} {"c"} {"d"} {"e"}";

    [Benchmark]
    public string Join() => string.Join("", "a", "b", "c", "d", "e");

    [Benchmark]
    public string Create() => new string(['a', 'b', 'c', 'd', 'e']);
}
```

What to look at:

- How `--runtimes net8,net9,net10` triggers cross-runtime builds and execution.
- The "Runtime" column in the console output.
- How the host builds the project for each TFM, runs benchmarks in child processes, and aggregates results.
- Combining `--runtimes` with other CLI flags like `--iterations`, `--reporter`, and `--output`.

---

## ReportDetail - Simple, Standard, and Advanced output

**`samples/ReportDetail/`**

Runs the same sorting benchmark three times with `.WithDetail(ReportDetail.Simple)`, `.Standard`, and `.Advanced` so you can see exactly what each detail level adds.

```bash
cd samples/ReportDetail
dotnet run
```

```csharp
using NBenchmark;
using NBenchmark.Reporters;
using NBenchmark.Reporters.Console;

// Simple - one table, counts footer. The default.
await new BenchmarkSuite("sorting-simple")
    .Add("bubble", () => { var a = Enumerable.Range(0, 100).Reverse().ToArray(); Array.Sort(a); })
    .Add("linq",   () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmup(3).WithIterations(50)
    .WithDetail(ReportDetail.Simple)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Standard - full comparison table + precision/tail latency + auto-tune + interpretation
await new BenchmarkSuite("sorting-standard")
    .Add("bubble", () => { var a = Enumerable.Range(0, 100).Reverse().ToArray(); Array.Sort(a); })
    .Add("linq",   () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmup(3).WithIterations(50)
    .WithDetail(ReportDetail.Standard)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Advanced - everything in Standard plus per-benchmark distribution details
await new BenchmarkSuite("sorting-advanced")
    .Add("bubble", () => { var a = Enumerable.Range(0, 100).Reverse().ToArray(); Array.Sort(a); })
    .Add("linq",   () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmup(3).WithIterations(50)
    .WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

What to look at:

- **Simple** (the default): a single table - Benchmark, Median, Ops/s, Ratio, Sig, Alloc/op - plus a counts-only footer. No statistical jargon.
- **Standard**: adds the Precision & Tail Latency table, a full Interpretation block (omnibus verdict, significance test, outlier detector, measurement profile), and auto-tune summary lines.
- **Advanced**: adds a per-benchmark Distribution Details block with quartiles, fences, skewness, kurtosis, MAD, Cliff's delta, and allocation breakdown.

See [Report Detail Levels](./output/report-detail-levels.md) for the full column reference.
