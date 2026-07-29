# index.md

---
title: Usage modes
description: The four ways to run NBenchmark benchmarks - Single mode, Suite mode, Harness mode, and the dotnet benchmark global tool.
order: 2
---

# Usage modes

NBenchmark has four usage modes. Pick the one that matches your situation.

## [Single mode - Benchmark](./single-mode.md)

A single static call. No classes, no attributes, no configuration required. Good for a quick measurement anywhere in your code.

```csharp
var result = Benchmark.Run(() => MyMethod());
result.Print();
```

## [Suite mode - BenchmarkSuite](./suite-mode.md)

A fluent builder for comparing multiple implementations. Produces a comparison table with ratios, confidence intervals, and significance testing.

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", BubbleSort)
    .Add("linq",   LinqSort)
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## [Harness mode - BenchmarkHarness](./harness-mode.md)

Attribute-based discovery driven by a command-line interface. Designed for dedicated benchmark projects.

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .RunAsync();

public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    [Benchmark]
    public int Compute() => SomeExpensiveWork();
}
```

## [Global Tool - dotnet benchmark](./global-tool.md)

A dotnet global tool that wraps `BenchmarkHarness` into a single command. Install once, then run benchmarks against any assembly without creating a project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark --project ./MyBenchmarks --filter "*Sort*"
```

## When to switch modes

The modes are designed as an evolutionary path. Start simple, upgrade when your needs grow:

1. **Start with Single mode** for a one-off measurement - a single `Benchmark.Run` call gives you a statistically rigorous result in three lines of code.

2. **Move to Suite mode** when you find yourself writing two `Benchmark.Run` calls to compare an old implementation against a new one. Suite mode handles the comparison automatically - ratios, confidence intervals, and significance testing against a baseline - so you don't have to mentally diff two separate outputs.

3. **Move to Harness mode** when your suite requires complex setup: mocked databases, loggers, `HttpClient`, or any dependency-injected service. Harness mode discovers benchmarks by attribute, parses CLI flags, and supports constructor injection via the optional `NBenchmark.DependencyInjection` package.

4. **Use the Global Tool** when you already have a project with `[Benchmark]` methods and want to run them from the CLI without adding a `Program.cs`, NuGet references, or any project setup. The tool wraps Harness mode into a single `dotnet benchmark` command.

Because all four modes produce the same `BenchmarkResult` type, upgrading from one mode to the next is seamless - your reporters, file output, and analysis code work unchanged.

## Next steps

Once you've picked a mode, the [Features](../features/) section covers advanced cross-cutting capabilities: [parameterized benchmarks](../features/parameterized-suite.md), [categories](../features/categories.md), [isolated runs](../features/isolated-runs.md), [multi-runtime comparison](../features/multi-runtime.md), [multiple launches](../features/multiple-launches.md), and [dependency injection](../features/dependency-injection.md).

The [Guides](../guides/) section assembles those features into real-world workflow recipes: [benchmarking ASP.NET Core services](../guides/aspnet-core-services.md), [tuning for CI/CD pipelines](../guides/ci-cd-pipelines.md), [comparing a refactor side-by-side](../guides/refactor-comparison.md), and more.


---

# harness-mode.md

---
title: "Harness mode: BenchmarkHarness"
description: Attribute-based benchmark discovery with a built-in command-line interface.
order: 3
---

# Harness mode: BenchmarkHarness

> **Tip:** Prefer not to create a project? Install the [global tool](./global-tool.md) once and run `dotnet benchmark` against any assembly with `[Benchmark]` methods.
>
> **Advanced features:** [Parameterized benchmarks](../features/parameterized-harness.md), [categories](../features/categories.md), [isolated runs](../features/isolated-runs.md), [dependency injection](../features/dependency-injection.md), [multi-runtime comparison](../features/multi-runtime.md), and [multiple launches](../features/multiple-launches.md) are covered in the Features section.

`BenchmarkHarness` discovers benchmarks by scanning assemblies for `[Benchmark]`-decorated methods. It also parses command-line arguments, so you can filter, configure, and drive runs entirely from the terminal without recompiling.

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

await BenchmarkHarness.Create(args)
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

See [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) for the full API, display name rules, baselines, significance, filtering, and a comparison with suite mode.

### Lifecycle attributes

These attributes control setup and teardown at the class and iteration level. All decorated methods must have no parameters. By default, the lifetime is `PerMethod` - both the instance and the lifecycle methods fire once per `[Benchmark]` method. Add `[InstanceLifetime(InstanceLifetime.PerClass)]` on the class to run setup/teardown once for the class.

| Attribute | Runs | Timing |
| --- | --- | --- |
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

Harness mode is **isolated by default**: every benchmark class runs in its own freshly spawned child process, so it is not influenced by JIT, GC, or thread-pool state warmed up by other classes. You don't need any attribute to get this behavior.

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

await BenchmarkHarness.Create(args)
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

## Cross-class significance

By default, Harness mode computes significance **per class**: each discovered class gets its own baseline, and `Sig` / `Magnitude` are relative to that class's baseline. The console reporter renders one comparison table per class.

When comparing implementations that live in separate classes (e.g. a legacy version and a refactored version), pass `--cross-class` on the CLI or call `WithCrossClassSignificance()` in code to compute significance across all classes in a single comparison table. The baseline is chosen from the whole group, and the reporter adds a `Class` column so rows can be distinguished.

```csharp
await BenchmarkHarness.Create(args)
    .WithCrossClassSignificance()
    .RunAsync();
```

Cross-class mode is opt-in because mixing unrelated benchmark classes into one significance table produces a baseline that may be semantically meaningless.

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

- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - `[BenchmarkCase]` and `[BenchmarkCases]` in depth
- [Multi-runtime comparison](../features/multi-runtime.md) - compare across .NET runtimes
- [Multiple launches](../features/multiple-launches.md) - measure run-to-run variance
- [CLI Reference](../reference/cli.md) - all command-line flags
- [Configuration](../reference/configuration.md) - options reference
- [Reporters](../output/index.md) - all available reporters


---

# global-tool.md

---
title: "Global Tool: dotnet benchmark"
description: Run benchmarks from the command line without creating a project. Install once, benchmark any assembly.
order: 4
---

# Global Tool: dotnet benchmark

The `dotnet benchmark` global tool wraps `BenchmarkHarness` into a single command. Install it once, then run benchmarks against any .NET assembly without creating a dedicated host project.

```bash
dotnet tool install -g NBenchmark.Tool
dotnet benchmark
```

## When to use the tool

The tool replaces Harness mode when you want to benchmark an existing project without adding a `Program.cs`, `Main`, and NuGet references. It is the fastest path from "I have a project with `[Benchmark]` methods" to "I have results."

| You want to... | Use |
| --- | --- |
| Benchmark a project you already built | `dotnet benchmark` in the output directory |
| Build and benchmark in one step | `dotnet benchmark --project ./MyBenchmarks` |
| Benchmark a specific assembly | `dotnet benchmark --assembly ./bin/Release/net10.0/MyLib.dll` |
| Filter, configure output, set thresholds | All `--filter`, `--reporter`, `--output`, `--threshold-pct` flags work |

## Installation

```bash
dotnet tool install -g NBenchmark.Tool
```

Verify it works:

```bash
dotnet benchmark --help
```

To update:

```bash
dotnet tool update -g NBenchmark.Tool
```

## Discovery modes

The tool finds benchmarks using one of three strategies.

### Default: scan the current directory

Run `dotnet benchmark` in a directory containing compiled `.dll` files. The tool loads each `.dll`, checks for `[Benchmark]` methods, and runs any it finds.

```bash
cd ./MyApp/bin/Release/net10.0
dotnet benchmark
```

Assemblies without `[Benchmark]` methods are skipped silently.

### --project: build and benchmark

Pass a `.csproj` path (or a directory containing one). The tool runs `dotnet build -c Release`, finds the output assembly, and benchmarks it.

```bash
dotnet benchmark --project ./MyApp.Benchmarks/MyApp.Benchmarks.csproj
dotnet benchmark --project ./MyApp.Benchmarks   # same, if only one .csproj
```

### --assembly: explicit assembly path

Pass one or more `.dll` paths directly. Repeatable.

```bash
dotnet benchmark --assembly ./Lib1.dll --assembly ./Lib2.dll
```

## All host flags pass through

Every flag supported by `BenchmarkHarness` works unchanged:

```bash
dotnet benchmark --filter "*Sort*"
dotnet benchmark --reporter json --output ./results
dotnet benchmark --iterations 500 --warmup 50
dotnet benchmark --detail advanced
dotnet benchmark --threshold-pct 20
dotnet benchmark --list
dotnet benchmark --dry-run
dotnet benchmark --in-process
```

See the [CLI reference](../reference/cli.md) for the full flag list.

## Default reporter

When no `--reporter` flag is given, the tool adds the console reporter automatically so you see results in the terminal. Pass any `--reporter` flag to override this default.

```bash
dotnet benchmark                              # console output
dotnet benchmark --reporter json              # JSON file only
dotnet benchmark --reporter json --reporter markdown  # both files
```

## Process isolation

The tool inherits Harness mode's isolated-by-default execution. Each benchmark class runs in a clean child process unless you pass `--in-process`.

```bash
dotnet benchmark                              # isolated (default)
dotnet benchmark --in-process                 # all in-process
```

When using `--project`, the tool forwards the built benchmark assembly paths to child processes automatically, so isolated runs work from any working directory.

## Examples

### Quick check on a library

```bash
cd ./MyApp/bin/Release/net10.0
dotnet benchmark --filter "*Parse*"
```

### Full CI gate

```bash
dotnet benchmark --project ./MyApp.Benchmarks \
  --reporter json --output ./bench-results \
  --threshold-pct 10
```

### Compare two builds

```bash
# Before
dotnet benchmark --assembly ./old/MyApp.dll --reporter json --output ./before

# After
dotnet benchmark --assembly ./new/MyApp.dll --reporter json --output ./after
```

## See also

- [Harness mode](./harness-mode.md) - the project-based alternative
- [CLI reference](../reference/cli.md) - all available flags
- [Reporters](../output/index.md) - output formats
- [Configuration](../reference/configuration.md) - measurement options


---

# single-mode.md

---
title: "Single mode: Benchmark"
description: Measure a single piece of code with one call using Benchmark.Run or Benchmark.RunAsync.
order: 1
---

# Single mode: Benchmark

`Benchmark` is the entry point for one-off measurements. It requires no class structure, no attributes, and no project setup beyond adding the NuGet reference. Use it anywhere you want a quick, reliable number.

## Basic usage

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    // code to measure
    for (int i = 0; i < 1000; i++) { }
});
```

`Benchmark.Run` warms up until the timings plateau, collects measured samples until the confidence interval is tight enough, trims outliers using the IQR fence rule, and returns a `BenchmarkResult`.

## Overloads

### Synchronous

```csharp
// Action - for code with no return value
var result = Benchmark.Run(() => DoWork());

// Func<T> - returns a value so the runner can prevent dead-code elimination
var result = Benchmark.Run(() => ComputeHash(data));
```

### Async

```csharp
// Func<Task>
var result = await Benchmark.RunAsync(async () => await FetchDataAsync());

// Func<Task<T>>
var result = await Benchmark.RunAsync(async () => await ComputeAsync(input));
```

### Raw outcome

`Benchmark.RunRaw` returns a `MeasurementOutcome` which includes both the `BenchmarkResult` and the raw per-iteration sample array. Use this if you need the underlying data.

```csharp
var outcome = Benchmark.RunRaw(() => DoWork());
double[] rawSamples = outcome.RawSamples;     // nanoseconds, before outlier trimming
BenchmarkResult result = outcome.Result;
```

## Custom options

Pass a `MeasurementOptions` instance to override the defaults:

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
    MeasureAllocations = true,
    ConfidenceLevel = 0.99,
};

var result = Benchmark.Run(() => MyMethod(), options: options);
```

See [Configuration](../reference/configuration.md) for the full list of options.

## Naming the benchmark

The `name` parameter sets the label used in output and file reporters:

```csharp
var result = Benchmark.Run(() => MyMethod(), name: "MyMethod with 1000-item input");
```

## Displaying results

### Plain text (core package)

```csharp
result.Print();
```

Output:

```
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 342.1 ns       Mean: 348.7 ns
  │  Ops/s:  2.87 Mops/s    Median ops/s: 2.92 Mops/s
  │  P95: 361.2 ns  P99: 378.5 ns  P99.9: 380.0 ns
  │  StdDev: 8.3 ns         CV:   2.38%
  │  Error:  ±3.1 ns (0.89% of Mean)
  │  CI:     [345.6 ns … 351.8 ns] (95%)
  │  Alloc/op: 0 B
  │
  └─────────────────────────────────────────────────
```

### Rich console table (NBenchmark.Reporters.Console)

```csharp
using NBenchmark.Reporters.Console;

await result.PrintAsync();
```

This runs the result through `ConsoleReporter` and renders a Spectre.Console table.

### File reporters

```csharp
await result.ToMarkdownAsync("results.md");
await result.ToCsvAsync("results.csv");
await result.ToJsonAsync("results/");   // output directory
```

## Accessing result fields directly

`BenchmarkResult` is a plain record - access any field directly:

```csharp
Console.WriteLine($"Median:  {result.Median} ns");
Console.WriteLine($"Mean:    {result.Mean} ns");
Console.WriteLine($"Ops/s:   {result.OperationsPerSecond}");
Console.WriteLine($"P95:     {result.GetPercentile(0.95)} ns");
Console.WriteLine($"StdDev:  {result.StandardDeviation} ns");
Console.WriteLine($"Error:   ±{result.MarginOfError} ns ({result.ConfidenceLevel * 100:0}% CI)");
Console.WriteLine($"CI:      {result.ConfidenceIntervalLower} … {result.ConfidenceIntervalUpper} ns");

if (result.MeanAllocatedBytes.HasValue)
    Console.WriteLine($"Alloc:   {result.MeanAllocatedBytes.Value} bytes/op");
```

## What Benchmark does not do

- **It does not compare benchmarks.** Use [BenchmarkSuite](./suite-mode.md) for A/B comparisons.
- **It does not run significance testing** between multiple results. Significance testing requires paired raw samples and is handled by `BenchmarkSuite` and `BenchmarkHarness`.

## Next steps

- [Suite mode: BenchmarkSuite](./suite-mode.md) - compare two or more implementations
- [Configuration](../reference/configuration.md) - full options reference
- [Reporters](../output/index.md) - save results to files


---

# suite-mode.md

---
title: "Suite mode: BenchmarkSuite"
description: Compare multiple implementations side-by-side using the fluent BenchmarkSuite API.
order: 2
---

# Suite mode: BenchmarkSuite

> **Advanced features:** [Parameterized benchmarks](../features/parameterized-suite.md), [categories](../features/categories.md), [isolated runs](../features/isolated-runs.md), [multi-runtime comparison](../features/multi-runtime.md), and [multiple launches](../features/multiple-launches.md) are covered in the Features section.

`BenchmarkSuite` is a fluent builder for running several benchmarks in the same run and comparing them. It handles run ordering, significance testing, setup and teardown, and reporter output automatically.

## Minimal example

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
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## Adding benchmarks

### Synchronous

```csharp
suite.Add("name", () => DoWork());

// Return a value - prevents dead-code elimination
suite.Add("name", () => ComputeHash(data));
```

### Async

```csharp
suite.Add("name", async () => await FetchDataAsync());

// Async with return value
suite.Add("name", async () => await ComputeAsync(input));
```

### With per-benchmark setup and teardown

The optional `setup` and `teardown` callbacks run before and after **each iteration**:

```csharp
suite.Add(
    name: "db query",
    action: () => db.Execute("SELECT 1"),
    setup: () => db.BeginTransaction(),
    teardown: () => db.Rollback()
);
```

> [!WARNING]
> Setup and teardown time is **not** included in the measurement. Only the `action` is timed.

### With categories

Tag a benchmark with categories and filter the suite before running. See [Categories](../features/categories.md) for the full filtering model.

```csharp
var results = await new BenchmarkSuite("sorting")
    .Add("bubble", () => { }, categories: ["Classic"])
    .Add("linq", () => { }, categories: ["Modern"])
    .WithCategoryFilter(include: ["Classic"])
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

Use `.WithCategories(params string[])` to apply categories to every subsequent `.Add` call:

```csharp
await new BenchmarkSuite("string")
    .WithCategories("String")
    .Add("concat", () => "a" + "b")
    .Add("interpolate", () => $"a { "b" }")
    .RunAsync();
```

### Benchmark names must be unique

Each name within a suite must be distinct. The significance test keys raw samples by name, so duplicates would corrupt the results.

```csharp
// This throws ArgumentException:
suite.Add("sort", SortA).Add("sort", SortB);
```

## Fluent configuration

All configuration methods return `this`, so they can be chained:

```csharp
await new BenchmarkSuite("name")
    .Add(...)
    .Add(...)
    .WithBaseline("name")           // which benchmark is the 1.00x reference
    .WithParameter("size", 10, 100) // expand parameterized benchmarks across values
    .WithIterations(200)            // pin measured samples (default: auto)
    .WithWarmup(25)                 // pin warmup samples (default: auto)
    .WithLaunchCount(3)             // repeat each benchmark 3 times as separate launches (default: 1)
    .WithAllocations()              // enable allocation tracking
    .WithOutlierMode(OutlierMode.IqrFence)   // default
    .WithOutlierDetector(new MyDetector())   // custom IOutlierDetector (overrides WithOutlierMode)
    .WithConfidenceLevel(0.99)      // default: 0.95
    .WithSignificanceLevel(0.05)    // alpha for the significance test; default: 0.05
    .WithSignificance(false)        // disable significance testing
    .WithSignificanceTest(new MyTest())   // custom ISignificanceTest
    .WithRunOrder(RunOrder.Declaration)   // default: RunOrder.Random
    .WithSeed(1234)                 // pin the shuffle seed for a reproducible order
    .WithSuiteSetup(() => { })      // runs once before all benchmarks
    .WithSuiteTeardown(() => { })   // runs once after all benchmarks
    .WithIsolation(false)           // measure in this process; the default is a worker
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
```

See [Configuration](../reference/configuration.md) for details on every option.

## Custom statistics

The suite uses the same pluggable statistics as the rest of the engine. By default it trims outliers with the IQR fence and tests significance with `DefaultSignificanceTest` - Mann-Whitney U for two benchmarks, the Kruskal-Wallis omnibus test (followed by post-hoc pairwise Mann-Whitney U with Holm-Bonferroni correction) for three or more. Override either when your data needs it:

```csharp
using NBenchmark.Stats;

await new BenchmarkSuite("latency")
    .Add("a", RunA)
    .Add("b", RunB)
    .Add("c", RunC)
    .WithOutlierDetector(new KeepFastestDetector(0.90))   // custom trimming
    .WithSignificanceTest(new MedianRatioSignificanceTest(25))   // custom significance rule
    .RunAsync();
```

`WithOutlierDetector` takes priority over `WithOutlierMode`. See [Custom outlier detectors](../statistics/outliers.md#custom-outlier-detectors) and [Custom significance tests](../statistics/significance.md#custom-significance-tests) for the interfaces and contracts.

## Setting a baseline

Call `WithBaseline("name")` to designate one benchmark as the reference point. The **Ratio** column in the output shows how fast each other benchmark is relative to the baseline, and significance is tested against it.

If no baseline is set, NBenchmark uses the benchmark with the lowest median as the implicit baseline for ratio calculations.

## Suite setup and teardown

`WithSuiteSetup` and `WithSuiteTeardown` run once around the entire suite - useful for starting a server, opening a connection, or initialising shared state:

```csharp
await new BenchmarkSuite("http")
    .WithSuiteSetup(() => server.Start())
    .WithSuiteTeardown(() => server.Stop())
    .Add("get", async () => await httpClient.GetStringAsync("/"))
    .Add("post", async () => await httpClient.PostAsync("/", content))
    .RunAsync();
```

Once suite setup has succeeded, suite teardown is **guaranteed to run** - even when the run is cancelled through a `CancellationToken` - so resources opened in setup are always released.

## Multi-runtime comparison

Use `WithRuntimes` to run the same benchmarks across multiple .NET runtimes and compare results side-by-side. See [Multi-runtime comparison](../features/multi-runtime.md) for the full guide.

## Process isolation

Suites are measured in a dedicated worker process by default - no configuration, no change to how you write them:

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort())
    .Add("array", () => ArraySort())
    .WithBaseline("bubble")
    .RunAsync();
```

This matters because JIT tiering, dynamic PGO, ReadyToRun and GC flavour are fixed when a process starts and can never be changed afterwards - so they can only be chosen for a process that has not started yet. The whole suite shares one worker, which keeps every ratio between its benchmarks a paired, within-process comparison.

`WithIsolation(false)` opts back into the host process, deliberately and silently.

A suite that holds live state a worker cannot be handed - captured locals, suite setup/teardown, parameters, or a custom detector instance - is measured in the host process instead, with the reason named per benchmark. Move it into a static `[BenchmarkPlan]` factory and use `BenchmarkSuite.RunPlanAsync(BuildSuite)`; the worker runs your factory in its own process, so all of that is constructed there. See [Isolated Runs](../features/isolated-runs.md) for the full model.

## Multiple launches

Use `WithLaunchCount(n)` to run each benchmark in the suite N times as independent launches. See [Multiple launches](../features/multiple-launches.md) for the full guide.

## Parameterized benchmarks

Use `WithParameter` and typed `Add` overloads to run the same benchmark body across multiple input values. Each parameter combination produces a separate benchmark entry with a distinct name like `"sort(size=10)"`. See [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) for the full guide.

```csharp
var results = await new BenchmarkSuite("sorting")
    .WithParameter("size", 10, 100, 1000)
    .Add("sort", (int size) =>
    {
        var arr = Enumerable.Range(0, size).Reverse().ToArray();
        Array.Sort(arr);
    })
    .WithRunOrder(RunOrder.Declaration)
    .RunAsync();
```

## Run order

By default benchmarks run in a **random** order (Fisher-Yates shuffle). This guards against systematic bias where the first benchmark always benefits from a warm CPU cache.

```csharp
.WithRunOrder(RunOrder.Declaration)   // run in the order Add() was called
.WithRunOrder(RunOrder.Random)        // default
```

## Multiple reporters

You can attach any number of reporters. They all receive the same results:

```csharp
suite
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/"))
    .WithReporter(new CsvReporter("results/"))
```

## Progress display

`ConsoleBenchmarkProgress` (from `NBenchmark.Reporters.Console`) shows warmup and measurement progress for each benchmark:

```csharp
.WithProgress(new ConsoleBenchmarkProgress())
```

Pass the same values you gave to `WithIterations` and `WithWarmup` so the progress display is accurate.

## Return value

`RunAsync()` returns `IReadOnlyList<BenchmarkResult>`. You can process the results programmatically after the run:

```csharp
var results = await suite.RunAsync();

foreach (var result in results.Where(r => !r.Errored))
    Console.WriteLine($"{result.Name}: {result.Median:F0} ns median");
```

Errored benchmarks have `result.Errored == true` and a message in `result.ErrorMessage`. They are included in the list so reporters can display them.

## Next steps

- [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) - run benchmarks across multiple input values
- [Multi-runtime comparison](../features/multi-runtime.md) - compare across .NET runtimes
- [Multiple launches](../features/multiple-launches.md) - measure run-to-run variance
- [Isolated runs](../features/isolated-runs.md) - run in a clean child process
- [Harness mode: BenchmarkHarness](./harness-mode.md) - attribute-based discovery and CLI control
- [Configuration](../reference/configuration.md) - full options reference
- [Reporters](../output/index.md) - all available reporters


---

