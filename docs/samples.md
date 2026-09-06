---
title: Samples
description: Runnable sample projects included with NBenchmark.
order: 10
---

# Samples

The `samples/` directory contains several sample projects that demonstrate each usage mode. Run a sample project using `dotnet run`.

## Benchmarking prepared state

**`samples/PreparedState/`**

This sample runs the same work in two ways: closing over prepared data and passing the preparation as its own delegate. Both methods are isolated, and NBenchmark sends the `int[]` to the worker by value.

The sample also measures a capture that cannot be sent, such as a `Stream`, using `Benchmark.RunInProcess`. This demonstrates that a refusal to isolate results in an error rather than a silent downgrade. The sample also demonstrates `BenchmarkSuite.Over` for a suite.

```bash
cd samples/PreparedState
dotnet run
```

```csharp
// captures 'data' -> the array is sent to the worker, so this is isolated too
var data = BuildData();
Benchmark.Run(() => Sum(data), options, "captured");

// both delegates capture nothing -> isolated, and the worker builds the data itself
Benchmark.Run(
    prepare: () => BuildData(),
    body: values => Sum(values),
    options,
    "prepared");
```

## Single mode sample

**`samples/Single/`**

This is the simplest possible benchmark: `Benchmark.Run` on a tight loop, followed by `Print()` and `PrintAsync()`.

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
await result.PrintTableAsync();
```

Key observations:

- The plain-text output from `result.Print()` (core package only).
- The Spectre.Console table from `result.PrintTableAsync()` (requires `NBenchmark.Reporters.Console`).
- The 95% CI line in the plain-text output.

---

## Suite mode sample

**`samples/Suite/`**

This sample uses a `BenchmarkSuite` to compare bubble sort and LINQ sorting on a 100-element array. The sample uses a short iteration count for a fast demonstration.

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

Key observations:

- The comparison table includes Ratio and Sig columns.
- The bar chart rendered below the table.
- The significance indicator (✓ or ✗) showing whether the difference appears real.

---

## Harness mode sample

**`samples/Harness/`**

This sample uses a `BenchmarkHarness` with two attribute-based benchmarks: a fast `Compute` method and a slower `Baseline` method.

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

Key observations:

- The `--list` flag shows discovered benchmarks before the run starts.
- The `--filter` flag narrows the run to a single benchmark.
- The `--reporter markdown` flag writes a Markdown file.
- The `--confidence 0.99` flag widens the Error column compared to the default 95% confidence level.

---

## Harness mode with dependency injection

**`samples/DependencyInjection/`**

This sample uses a `BenchmarkHarness` where the benchmark class has constructor dependencies resolved from a `Microsoft.Extensions.DependencyInjection` container. It demonstrates the `NBenchmark.DependencyInjection` package and the `UseDependencyInjection<T>` method.

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

// Pass the container as a factory instead of a built provider.
// This allows the worker to rebuild the container and ensures the run remains isolated.
await BenchmarkHarness.Create(args)
    .AddFromAssembly<DependencyInjectionBenchmarks>().WithServices(BuildServices)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddSingleton<IDataStore, InMemoryDataStore>()
    .AddTransient<OrderRepository>()
    .AddTransient<DependencyInjectionBenchmarks>()
    .BuildServiceProvider();

public sealed class DependencyInjectionBenchmarks(OrderRepository repository)
{
    [Benchmark] public int Read()  => repository.GetCurrent();
    [Benchmark] public int Write() { repository.Save(42); return 42; }
}
```

Key observations:

- The benchmark class takes an `OrderRepository` in its primary constructor and does not require a parameterless constructor.
- `UseDependencyInjection<T>` combines assembly discovery and DI wiring in one call.
- `BuildServices` is a static factory; the worker runs it in its own process to ensure the run is isolated.
- For `DbContext`-style lifetimes, use the scoped variant: `UseScopedDependencyInjection<T>(BuildServices)`.

---

## Custom statistics sample

**`samples/ExtensibleStats/`**

This sample uses a `BenchmarkSuite` to compare three hash algorithms (`SHA256`, `SHA1`, and `MD5`) in two different runs.

The first run uses the built-in Median Absolute Deviation outlier mode. Because the suite contains three groups, NBenchmark automatically reports a Kruskal-Wallis omnibus verdict. The second run uses a custom `IOutlierDetector` and a custom `ISignificanceTest`.

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
    .WithOutlierDetector(static () => new KeepFastestDetector(0.90))
    .WithSignificanceTest(static () => new MedianRatioSignificanceTest(thresholdPercent: 25))
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

Key observations:

- Observe the `Outliers: MAD` header in the first run and the Kruskal-Wallis omnibus verdict line below the table.
- Observe the custom detector's name (`keep fastest 90%`) in the header and the custom test's name (`median ratio (>25%)`) in the footer of the second run.
- The `KeepFastestDetector` and `MedianRatioSignificanceTest` implementations in `Program.cs` serve as templates for custom statistics.

For more information, see [Custom outlier detectors](./statistics/outliers.md#custom-outlier-detectors) and [Custom significance tests](./statistics/significance.md#custom-significance-tests).

---

## Advanced isolation sample

**`samples/IsolatedRuns/`**

This sample demonstrates process isolation.

- Single mode is isolated by default when using `Benchmark.Run`. Use `Benchmark.RunInProcess` to opt out of isolation.
- Suite mode measures in a single clean worker process. Use a `[BenchmarkPlan]` factory for suites that maintain live state.

```bash
cd samples/IsolatedRuns
dotnet run
```

Key observations:

- The quick in-process result.
- The isolated suite comparison, where the whole suite runs in one fresh worker.
- The tradeoff between cleaner measurements and additional process-launch overhead.

---

## Cross-launch aggregation sample

**`samples/MultiLaunch/`**

This sample demonstrates how to run each benchmark multiple times as independent launches using the `--launch-count` flag, the `WithLaunchCount()` method, or the `[Benchmark(LaunchCount)]` attribute. It also shows cross-launch aggregation statistics.

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

Key observations:

- The "Launch Aggregation" table below the main results shows the cross-launch mean, standard deviation, median, and confidence interval when the launch count is greater than one.
- The primary results are the average across launches. The reported interval reflects the spread between launches; if launches disagree, the interval is wider.
- The `--launch-count 5` CLI flag overrides the programmatic count.
- The `[Benchmark(LaunchCount = 3)]` attribute specifies a different launch count for a specific benchmark.

---

## Suite mode multi-runtime sample

**`samples/MultiRuntimeSuite/`**

This sample uses a `BenchmarkSuite` to compare three string concatenation methods across .NET 8, .NET 9, and .NET 10. It demonstrates the `WithRuntimes` method for cross-runtime comparison.

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

Key observations:

- The "Runtime" column in the comparison table groups results by target framework.
- Observe how the same benchmark body produces different timings across runtimes.
- The Ratio column compares each runtime to the .NET 8 baseline.
- The project must target all runtimes in the `.csproj` file using the `<TargetFrameworks>` element.

---

## Harness mode multi-runtime sample

**`samples/MultiRuntimeHarness/`**

This sample uses a `BenchmarkHarness` with attribute-based benchmarks that you can run across multiple .NET runtimes using the `--runtimes` CLI flag.

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

Key observations:

- The `--runtimes net8,net9,net10` flag triggers cross-runtime builds and execution.
- Observe the "Runtime" column in the console output.
- The host builds the project for each target framework, runs benchmarks in workers, and aggregates the results.
- You can combine the `--runtimes` flag with other CLI flags, such as `--iterations`, `--reporter`, and `--output`.

---

## Report detail levels sample

**`samples/ReportDetail/`**

This sample runs the same sorting benchmark three times using `ReportDetail.Simple`, `ReportDetail.Standard`, and `ReportDetail.Advanced` to demonstrate the differences between detail levels.

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

Key observations:

- **Simple** (default): Displays a single table with Benchmark, Median, Ops/s, Ratio, Sig, and Alloc/op, plus a counts-only footer. It contains no statistical jargon.
- **Standard**: Adds the Precision & Tail Latency table, a full Interpretation block (including the omnibus verdict, significance test, outlier detector, and measurement profile), and auto-tune summary lines.
- **Advanced**: Adds a per-benchmark Distribution Details block containing quartiles, fences, skewness, kurtosis, MAD, Cliff's delta, and an allocation breakdown.

For more information, see [Report Detail Levels](./output/report-detail-levels.md) for the full column reference.

---

## Telemetry sample

**`samples/Telemetry/`**

This sample uses a `BenchmarkHarness` to export OTLP telemetry to a Grafana stack in Docker using the `NBenchmark.Exporters.OpenTelemetry` package. The run is isolated, and telemetry from every `nbworker` child is aggregated into a single trace with the harness trace.

```bash
cd samples/Telemetry
docker compose up -d
dotnet run -c Release
open http://localhost:3000/d/nbenchmark-run
```

```csharp
using NBenchmark.Exporters.OpenTelemetry;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<TelemetryBenchmarks>()
    .WithOpenTelemetry(o => o.Endpoint = "http://localhost:4317")
    .RunAsync();
```

The programmatic call is optional. If you reference the package and pass the `--otlp-endpoint` flag, the exporter self-registers and attaches to any run with an export endpoint.

Key observations:

- A single run produces one trace. This trace includes a `benchmark.suite` span in the harness, one `nbenchmark.worker` span per measuring process, and `benchmark.run` spans with four `nbenchmark.phase.*` spans beneath each. A default run with four benchmarks and five launches produces 106 spans.
- Observe the span events, such as `warmup.plateau_reached` and `measurement.ci_target_met`, to understand why each phase ended and how warmup duration compares to measurement duration.
- The dashboard aggregates over the time range instead of using `rate()` because a worker only exists for a few seconds, providing only a few data points per series.
- The project targets both `.NET 8.0` and `.NET 10.0`. The `.NET 8.0` run serves as a regression test for `System.Diagnostics.DiagnosticSource` unification in the worker, which is required for complete worker telemetry.

For more information, see [BCL Instrumentation](./reference/bcl-instrumentation.md) for the full instrument, span, and resource-attribute reference.
