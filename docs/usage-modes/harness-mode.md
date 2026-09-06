---
title: "Harness mode: BenchmarkHarness"
description: Attribute-based benchmark discovery with a built-in command-line interface.
order: 3
---

# Harness mode: BenchmarkHarness

> [!TIP]
> If you prefer not to create a separate project, install the [global tool](./global-tool.md) once and run `dotnet benchmark` against any assembly with `[Benchmark]` methods.

`BenchmarkHarness` discovers benchmarks by scanning assemblies for `[Benchmark]`-decorated methods. It also parses command-line arguments, allowing you to filter, configure, and drive runs from the terminal without recompiling.

This mode is designed for **dedicated benchmark projects** - a separate console project that you run against your library.

## Minimal setup

### 1. Create a console project

```bash
dotnet new console -n MyApp.Benchmarks
cd MyApp.Benchmarks
dotnet add package NBenchmark
dotnet add package NBenchmark.Reporters.Console
dotnet add reference ../MyApp/MyApp.csproj
```

### 2. Write benchmark classes

```csharp
using NBenchmark;

public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "hello" + " " + "world";

    [Benchmark]
    public string Interpolate() => $"hello {"world"}";
}
```

### 3. Wire up the host

```csharp
// Program.cs
using NBenchmark;
using NBenchmark.Reporters.Console;
using NBenchmark;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

### 4. Run the benchmarks

```bash
dotnet run
dotnet run -- --filter String*
dotnet run -- --reporter markdown --output ./results
```

## Benchmark attributes

### [Benchmark]

Mark a public instance method for measurement.

```csharp
[Benchmark]
public int MyMethod() => DoWork();

[Benchmark]
public async Task MyAsyncMethod() => await DoWorkAsync();

[Benchmark]
public async Task<int> MyAsyncMethodWithResult() => await ComputeAsync();
```

**Properties:**

| Property | Type | Description |
|---|---|---|
| `Baseline` | `bool` | Marks this method as the baseline for ratio and significance calculations. |
| `Description` | `string?` | An optional label shown in output when descriptions are present. |
| `Samples` | `int?` | Overrides the default sample count for this method only. |
| `WarmupSamples` | `int?` | Overrides the default warmup count for this method only. |
| `LaunchCount` | `int` | Overrides the default launch count for this method only. |

```csharp
[Benchmark(Baseline = true, Description = "current production implementation")]
public string CurrentImpl() => Production.DoWork();

[Benchmark(Description = "candidate replacement")]
public string NewImpl() => Candidate.DoWork();
```

### [BenchmarkCategory]

Tag a benchmark (or an entire class) with one or more categories. You can use categories to include or exclude benchmarks from a run. Apply the attribute multiple times to declare multiple categories.

```csharp
[BenchmarkCategory("String")]
public class StringBenchmarks
{
    [Benchmark]
    [BenchmarkCategory("Fast")]
    public string Concat() => "hello" + "world";

    [Benchmark]
    [BenchmarkCategory("Slow")]
    public string ManyConcat()
    {
        var s = "";
        for (var i = 0; i < 100; i++)
            s += (char)('a' + i % 26);
        return s;
    }
}
```

NBenchmark unions class-level categories with method-level categories. In the example above, `ManyConcat` is tagged with both `String` and `Slow`.

For the full filtering model (including CLI flags and programmatic filtering), see [Categories](../features/categories.md).

### [Arguments] and [ArgumentsSource]

Run the benchmark once for each case (argument set). The method must accept parameters that match the argument types.

```csharp
[Arguments(10)]
[Arguments(1_000)]
[Arguments(100_000)]
[Benchmark]
public void Sort(int n)
{
    var arr = Enumerable.Range(0, n).Reverse().ToArray();
    Array.Sort(arr);
}
```

Each case becomes a separate benchmark entry in the output, named `MethodName(name=value, ...)` using the method's parameter names. For programmatic case sources, generated values, or large parameter sweeps, use `[ArgumentsSource]` with a source method that yields named value tuples.

For the full API, display name rules, baselines, significance, filtering, and a comparison with suite mode, see [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md).

### Lifecycle attributes

These attributes control setup and teardown at the class and sample level. All decorated methods must have no parameters.

By default, the lifetime is `PerMethod` - both the instance and the lifecycle methods fire once per `[Benchmark]` method. To run setup and teardown once for the entire class, add `[InstanceLifetime(InstanceLifetime.PerClass)]` to the class.

| Attribute | When it runs | Timing |
| --- | --- | --- |
| `[GlobalSetup]` | Before each `[Benchmark]` method by default; once per suite under `[InstanceLifetime(PerClass)]` | Not measured |
| `[GlobalTeardown]` | After each `[Benchmark]` method by default; once per suite under `[InstanceLifetime(PerClass)]` | Not measured |
| `[SampleSetup]` | Before each individual sample | Not measured |
| `[SampleTeardown]` | After each individual sample | Not measured |

```csharp
public class DatabaseBenchmarks
{
    private DbConnection _conn = null!;

    [GlobalSetup]
    public void OpenConnection() => _conn = new DbConnection(connectionString);

    [GlobalTeardown]
    public void CloseConnection() => _conn.Dispose();

    [SampleSetup]
    public void BeginTransaction() => _conn.BeginTransaction();

    [SampleTeardown]
    public void RollbackTransaction() => _conn.RollbackTransaction();

    [Benchmark]
    public void RunQuery() => _conn.Execute("SELECT COUNT(*) FROM orders");
}
```

If your `[GlobalSetup]` is expensive and you want to share the resulting state across all `[Benchmark]` methods in the class, use `PerClass`:

```csharp
[InstanceLifetime(InstanceLifetime.PerClass)]
public class DatabaseBenchmarks
{
    [GlobalSetup] public void OpenConnection() { ... }
    [Benchmark] public void A() { ... }
    [Benchmark] public void B() { ... }
}
```

### [Isolation(Isolation.Required)]

Harness mode is **isolated by default**: every benchmark class runs in its own freshly spawned worker. This ensures the benchmark is not influenced by JIT, GC, or thread-pool state warmed up by other classes.

Use isolation attributes to change the granularity:

- **`[Isolation(Isolation.Required)]`** on a method gives that single benchmark its **own dedicated** worker. This is the finest granularity, isolating the benchmark even from sibling benchmarks in the same class.
- **`[Isolation(Isolation.Off)]`** on a method or class opts that benchmark back into the **host process**.

```csharp
public class StartupBenchmarks
{
    [Benchmark]
    public int Warm() => RunWarmWork();           // shares one per-class worker

    [Benchmark]
    [Isolation(Isolation.Required)]
    public int ColdPath() => RunColdSensitiveWork();  // its own dedicated worker

    [Benchmark]
    [Isolation(Isolation.Off)]
    public int InHost() => RunHostObservableWork();   // runs in the host process
}
```

To disable isolation for the **entire run**, pass `--in-process` on the command line or call `WithIsolation(Isolation.Off)` in code. The `--dry-run` flag also always runs in-process.

For the full isolation model across all modes, including how mixed `[Isolation(Isolation.Required)]` and `[Isolation(Isolation.Off)]` classes are dispatched, see [Isolated Runs](../features/isolated-runs.md).

## Class requirements

NBenchmark instantiates benchmark classes using `Activator.CreateInstance`. The class must have a **public parameterless constructor**.

```csharp
// Works - implicit parameterless constructor
public class MyBenchmarks { ... }

// Works - explicit parameterless constructor
public class MyBenchmarks
{
    public MyBenchmarks() { /* setup */ }
}

// Does not work - no parameterless constructor
public class MyBenchmarks(IDatabase db) { ... }
```

### Benchmark classes with dependencies

To use **constructor dependencies** (such as a repository, logger, `HttpClient`, or `DbContext`), add the optional `NBenchmark.DependencyInjection` companion package:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NBenchmark.DependencyInjection;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>().WithServices(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddSingleton<IOrderRepository, SqlOrderRepository>()
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

public sealed class OrderBenchmarks(IOrderRepository repository)
{
    [Benchmark]
    public int CountOrders() => repository.Count();
}
```

Pass the factory rather than a built container so the worker can rebuild it and the run remains isolated. For the full API, lifetime semantics, scoped variants, and how to plug in containers other than `Microsoft.Extensions.DependencyInjection`, see the [Dependency Injection guide](../features/dependency-injection.md).

## Scanning multiple assemblies

Call `AddFromAssembly` once for each assembly:

```csharp
BenchmarkHarness.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .AddFromAssembly<DatabaseBenchmarks>()
    .AddFromAssembly(typeof(SomeOtherClass).Assembly)
    ...
```

## Applying options

Use `WithOptions` to set defaults that the CLI can override:

```csharp
BenchmarkHarness.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithOptions(new MeasurementOptions
    {
        Samples = 500,
        WarmupSamples = 50,
        MeasureAllocations = true,
        ConfidenceLevel = 0.99,
    })
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

The individual `With*` methods - `WithSamples`, `WithWarmupSamples`, `WithAllocations`, `WithConfidenceLevel`, `WithSignificanceLevel`, `WithOutlierMode`, `WithSeed` and the rest - set the same values one at a time, and `Configure(o => o with { ... })` merges into what is already set rather than replacing it:

```csharp
BenchmarkHarness.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .Configure(o => o with { ReportedPercentiles = [0.5, 0.95, 0.99] })
    .WithSamples(500)
    .RunAsync();
```

CLI flags, such as `--samples`, always override values set in code.

By default, benchmarks run in **random** order to reduce systematic bias. Call `WithRunOrder(RunOrder.Declaration)` or pass `--order declaration` to run them in declaration order.

## Cross-class significance

By default, Harness mode computes significance **per class**: each discovered class has its own baseline, and `Sig` and `Magnitude` are relative to that class's baseline. The console reporter renders one comparison table per class.

When comparing implementations that live in separate classes (for example, a legacy version and a refactored version), pass `--cross-class` on the CLI or call `WithCrossClassSignificance()` in code to compute significance across all classes in a single comparison table. In this mode, NBenchmark chooses a baseline from the entire group, and the reporter adds a `Class` column to distinguish rows.

```csharp
await BenchmarkHarness.Create(args)
    .WithCrossClassSignificance()
    .RunAsync();
```

Cross-class mode is opt-in because mixing unrelated benchmark classes into one significance table can produce a baseline that is semantically meaningless.

## Multi-runtime comparison

Use the `--runtimes` CLI flag or the `[Runtimes]` attribute to run the same benchmarks across multiple .NET runtimes and compare the results side-by-side. For the full guide, including the `[Runtimes]` attribute and its interaction with `--runtimes`, see [Multi-runtime comparison](../features/multi-runtime.md).

## Multiple launches

Use `--launch-count <n>` on the CLI or `WithLaunchCount(n)` in code to run each benchmark $N$ times as independent launches. For the full guide, including per-method attribute overrides and isolation interaction, see [Multiple launches](../features/multiple-launches.md).

## Category filtering

When you tag benchmarks with `[BenchmarkCategory]`, you can include or exclude them from the run using the `--include-category` and `--exclude-category` CLI flags, or `FilterCategories` in code. For the full filtering model, see [Categories](../features/categories.md).

## Listing benchmarks without running

```bash
dotnet run -- --list
```

The output is similar to the following:

```text
── StringBenchmarks ──
    Concat - current production implementation
    Interpolate - candidate replacement
── DatabaseBenchmarks ──
    RunQuery
```

## Dry run

A dry run validates that all benchmarks compile, discover, and wire up correctly without invoking the body:

```bash
dotnet run -- --dry-run
```

The `--dry-run` flag is implemented as `--samples 0 --warmup-samples 0`. The body is not invoked, and no measurements are taken. Use this to confirm discovery, setup, and instantiation work before a full run. To run the body exactly once for a smoke test, use `--samples 1 --warmup-samples 0`.

## Return value

`RunAsync` returns `IReadOnlyList<BenchmarkResult>` containing all results, including errored benchmarks. The exit code is 0 on success.

## Next steps

- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - `[Arguments]` and `[ArgumentsSource]` in depth
- [Multi-runtime comparison](../features/multi-runtime.md) - Compare across .NET runtimes
- [Multiple launches](../features/multiple-launches.md) - Measure run-to-run variance
- [CLI Reference](../reference/cli.md) - All command-line flags
- [Configuration](../reference/configuration.md) - Options reference
- [Reporters](../output/index.md) - All available reporters
