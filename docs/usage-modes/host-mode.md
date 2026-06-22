---
title: "Host mode: BenchmarkHost"
description: Attribute-based benchmark discovery with a built-in command-line interface.
order: 3
---

# Host mode: BenchmarkHost

> **Tip:** Prefer not to create a project? Install the [global tool](./global-tool.md) once and run `dotnet benchmark` against any assembly with `[Benchmark]` methods.
>
> **Advanced features:** [Parameterized benchmarks](../features/parameterized-host.md), [categories](../features/categories.md), [isolated runs](../features/isolated-runs.md), [dependency injection](../features/dependency-injection.md), [multi-runtime comparison](../features/multi-runtime.md), and [multiple launches](../features/multiple-launches.md) are covered in the Features section.

`BenchmarkHost` discovers benchmarks by scanning assemblies for `[Benchmark]`-decorated methods. It also parses command-line arguments, so you can filter, configure, and drive runs entirely from the terminal without recompiling.

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
using NBenchmark.Attributes;

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
using NBenchmark.Attributes;

await BenchmarkHost.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

### 4. Run

```bash
dotnet run
dotnet run -- --filter String*
dotnet run -- --reporter markdown --output ./results
```

## Benchmark attributes

### `[Benchmark]`

Marks a public instance method for measurement.

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
|---|---|---|---|
| `Baseline` | `bool` | Marks this method as the baseline for ratio/significance calculations. |
| `Description` | `string?` | Optional label shown in output when descriptions are present. |
| `Iterations` | `int?` | Override the default iteration count for this method only. |
| `WarmupIterations` | `int?` | Override the default warmup count for this method only. |
| `LaunchCount` | `int` | Override the default launch count for this method only. |

```csharp
[Benchmark(Baseline = true, Description = "current production implementation")]
public string CurrentImpl() => Production.DoWork();

[Benchmark(Description = "candidate replacement")]
public string NewImpl() => Candidate.DoWork();
```

### `[BenchmarkCategory]`

Tags a benchmark (or an entire class) with one or more categories. Categories can be used to include or exclude benchmarks from a run. Multiple categories are declared by applying the attribute multiple times.

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

Class-level categories are unioned with method-level categories, so `ManyConcat` above is tagged with both `String` and `Slow`.

See [Categories](../features/categories.md) for the full filtering model (CLI flags, programmatic filtering, and how the two compose).

### `[BenchmarkCase]` and `[BenchmarkCases]`

Run the benchmark once for each case (argument set). The method must accept parameters matching the argument types.

```csharp
[BenchmarkCase(10)]
[BenchmarkCase(1_000)]
[BenchmarkCase(100_000)]
[Benchmark]
public void Sort(int n)
{
    var arr = Enumerable.Range(0, n).Reverse().ToArray();
    Array.Sort(arr);
}
```

Each case becomes a separate benchmark entry in the output, named `MethodName(name=value, ...)` using the method's parameter names. For programmatic case sources, generated values, or large parameter sweeps, use `[BenchmarkCases]` with a source method that yields named value tuples.

See [Parameterized benchmarks: Host mode](../features/parameterized-host.md) for the full API, display name rules, baselines, significance, filtering, and a comparison with suite mode.

### Lifecycle attributes

These attributes control setup and teardown at the class and iteration level. All decorated methods must have no parameters. By default, the lifetime is `PerMethod` - both the instance and the lifecycle methods fire once per `[Benchmark]` method. Add `[InstanceLifetime(InstanceLifetime.PerClass)]` on the class to run setup/teardown once for the class.

| Attribute | Runs | Timing |
|---|---|---|
| `[BenchmarkSetup]` | Once before each `[Benchmark]` method by default; once per suite under `[InstanceLifetime(PerClass)]` | Not measured |
| `[BenchmarkTeardown]` | Once after each `[Benchmark]` method by default; once per suite under `[InstanceLifetime(PerClass)]` | Not measured |
| `[BenchmarkIterationSetup]` | Before each individual iteration | Not measured |
| `[BenchmarkIterationTeardown]` | After each individual iteration | Not measured |

```csharp
public class DatabaseBenchmarks
{
    private DbConnection _conn = null!;

    [BenchmarkSetup]
    public void OpenConnection() => _conn = new DbConnection(connectionString);

    [BenchmarkTeardown]
    public void CloseConnection() => _conn.Dispose();

    [BenchmarkIterationSetup]
    public void BeginTransaction() => _conn.BeginTransaction();

    [BenchmarkIterationTeardown]
    public void RollbackTransaction() => _conn.RollbackTransaction();

    [Benchmark]
    public void RunQuery() => _conn.Execute("SELECT COUNT(*) FROM orders");
}
```

If your `[BenchmarkSetup]` is expensive and you want to share the resulting state across all `[Benchmark]` methods in the class, opt the class into `PerClass`:

```csharp
[InstanceLifetime(InstanceLifetime.PerClass)]
public class DatabaseBenchmarks
{
    [BenchmarkSetup] public void OpenConnection() { ... }
    [Benchmark] public void A() { ... }
    [Benchmark] public void B() { ... }
}
```

### `[IsolatedProcess]`

Host mode is **isolated by default**: every benchmark class runs in its own freshly spawned child process, so it is not influenced by JIT, GC, or thread-pool state warmed up by other classes. You don't need any attribute to get this behavior.

Use the isolation attributes to change the granularity:

- **`[IsolatedProcess]`** on a method gives that single benchmark its **own dedicated** child process - the finest granularity, isolated even from sibling benchmarks in the same class.
- **`[InProcess]`** on a method (or class) opts that benchmark back into the **host process**.

```csharp
public class StartupBenchmarks
{
    [Benchmark]
    public int Warm() => RunWarmWork();           // shares one per-class child

    [Benchmark]
    [IsolatedProcess]
    public int ColdPath() => RunColdSensitiveWork();  // its own dedicated child

    [Benchmark]
    [InProcess]
    public int InHost() => RunHostObservableWork();   // runs in the host process
}
```

To disable isolation for the **whole run**, pass `--in-process` on the command line or call `WithIsolation(false)` in code. `--dry-run` also always runs in-process.

See [Isolated Runs](../features/isolated-runs.md) for the full isolation model across all modes, including how mixed `[IsolatedProcess]` / `[InProcess]` classes are dispatched.

## Class requirements

NBenchmark instantiates benchmark classes using `Activator.CreateInstance`. The class must have a **public parameterless constructor** (the default for any class without explicit constructors).

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

If you want benchmark classes to have **constructor dependencies** (a repository, a logger, an `HttpClient`, a `DbContext`, etc.), add the optional `NBenchmark.DependencyInjection` companion package:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NBenchmark.DependencyInjection;

var services = new ServiceCollection()
    .AddSingleton<IOrderRepository, SqlOrderRepository>()
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHost.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(services)
    .RunAsync();

public sealed class OrderBenchmarks(IOrderRepository repository)
{
    [Benchmark]
    public int CountOrders() => repository.Count();
}
```

See the [Dependency Injection guide](../features/dependency-injection.md) for the full API, lifetime semantics, scoped variants, and how to plug in containers other than `Microsoft.Extensions.DependencyInjection`.

## Scanning multiple assemblies

Call `AddFromAssembly` once per assembly:

```csharp
BenchmarkHost.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .AddFromAssembly<DatabaseBenchmarks>()
    .AddFromAssembly(typeof(SomeOtherClass).Assembly)
    ...
```

## Applying options

Use `WithOptions` to set defaults that the CLI can override:

```csharp
BenchmarkHost.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithOptions(new MeasurementOptions
    {
        Iterations = 500,
        WarmupIterations = 50,
        MeasureAllocations = true,
        ConfidenceLevel = 0.99,
    })
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

CLI flags like `--iterations` always override `WithOptions` values.

By default benchmarks run in **random** order to reduce systematic bias. Call `WithRunOrder(RunOrder.Declaration)` (or pass `--order declaration`) to run them in declaration order instead.

## Multi-runtime comparison

Use the `--runtimes` CLI flag (or the `[Runtimes]` attribute) to run the same benchmarks across multiple .NET runtimes and compare results side-by-side. See [Multi-runtime comparison](../features/multi-runtime.md) for the full guide, including the `[Runtimes]` attribute and how it interacts with `--runtimes`.

## Multiple launches

Use `--launch-count <n>` on the CLI (or `WithOptions(new MeasurementOptions { LaunchCount = n })` in code) to run each benchmark N times as independent launches. See [Multiple launches](../features/multiple-launches.md) for the full guide, including per-method attribute overrides and isolation interaction.

## Category filtering

When benchmarks are tagged with `[BenchmarkCategory]`, you can include or exclude them from the run using the `--category` and `--exclude-category` CLI flags, or `WithCategoryFilter` in code. See [Categories](../features/categories.md) for the full filtering model.

## Listing benchmarks without running

```bash
dotnet run -- --list
```

Output:

```
── StringBenchmarks ──
    Concat - current production implementation
    Interpolate - candidate replacement
── DatabaseBenchmarks ──
    RunQuery
```

## Dry run

Validates that all benchmarks compile, discover, and wire up correctly - without invoking the body:

```bash
dotnet run -- --dry-run
```

`--dry-run` is implemented as `--iterations 0 --warmup 0`. The body is not invoked, and no measurements are taken. Use it to confirm discovery, setup, and instantiation work before a full run. To run the body exactly once for a smoke test, use `--iterations 1 --warmup 0`.

## Return value

`RunAsync` returns `IReadOnlyList<BenchmarkResult>` with all results, including errored benchmarks. Exit code is 0 on success.

## Next steps

- [Parameterized benchmarks: Host mode](../features/parameterized-host.md) - `[BenchmarkCase]` and `[BenchmarkCases]` in depth
- [Multi-runtime comparison](../features/multi-runtime.md) - compare across .NET runtimes
- [Multiple launches](../features/multiple-launches.md) - measure run-to-run variance
- [CLI Reference](../reference/cli.md) - all command-line flags
- [Configuration](../reference/configuration.md) - options reference
- [Reporters](../output/index.md) - all available reporters
