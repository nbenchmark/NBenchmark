---
title: "Tier 3: BenchmarkHost"
description: Attribute-based benchmark discovery with a built-in command-line interface.
order: 3
---

# Tier 3: BenchmarkHost

`BenchmarkHost` discovers benchmarks by scanning assemblies for `[Benchmark]`-decorated methods. It also parses command-line arguments, so you can filter, configure, and drive runs entirely from the terminal without recompiling.

This tier is designed for **dedicated benchmark projects** — a separate console project that you run against your library.

## Minimal setup

### 1. Create a console project

```bash
dotnet new console -n MyApp.Benchmarks
cd MyApp.Benchmarks
dotnet add package NBenchmark
dotnet add package NBenchmark.Console
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
using NBenchmark.Console;
using NBenchmark.Attributes;

await BenchmarkHost.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress(200, 25))
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
|---|---|---|
| `Baseline` | `bool` | Marks this method as the baseline for ratio/significance calculations. |
| `Description` | `string?` | Optional label shown in output when descriptions are present. |
| `Iterations` | `int?` | Override the default iteration count for this method only. |
| `WarmupIterations` | `int?` | Override the default warmup count for this method only. |

```csharp
[Benchmark(Baseline = true, Description = "current production implementation")]
public string CurrentImpl() => Production.DoWork();

[Benchmark(Description = "candidate replacement")]
public string NewImpl() => Candidate.DoWork();
```

### `[BenchmarkArguments]`

Runs the benchmark once for each set of arguments. The method must accept parameters matching the argument types.

```csharp
[BenchmarkArguments(10)]
[BenchmarkArguments(1_000)]
[BenchmarkArguments(100_000)]
[Benchmark]
public void Sort(int n)
{
    var arr = Enumerable.Range(0, n).Reverse().ToArray();
    Array.Sort(arr);
}
```

Each argument set becomes a separate benchmark entry in the output, named `MethodName(arg1, arg2, ...)`.

### Lifecycle attributes

These attributes control setup and teardown at the class and iteration level. All decorated methods must have no parameters.

| Attribute | Runs | Timing |
|---|---|---|
| `[BenchmarkSetup]` | Once before any benchmark in the class | Not measured |
| `[BenchmarkTeardown]` | Once after all benchmarks in the class | Not measured |
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

## Class requirements

NBenchmark instantiates benchmark classes using `Activator.CreateInstance`. The class must have a **public parameterless constructor** (the default for any class without explicit constructors).

```csharp
// Works — implicit parameterless constructor
public class MyBenchmarks { ... }

// Works — explicit parameterless constructor
public class MyBenchmarks
{
    public MyBenchmarks() { /* setup */ }
}

// Does not work — no parameterless constructor
public class MyBenchmarks(IDatabase db) { ... }
```

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

## Listing benchmarks without running

```bash
dotnet run -- --list
```

Output:

```
── StringBenchmarks ──
    Concat — current production implementation
    Interpolate — candidate replacement
── DatabaseBenchmarks ──
    RunQuery
```

## Dry run

Invokes each benchmark once without measurement — useful for checking that all benchmarks compile and run without errors:

```bash
dotnet run -- --dry-run
```

## Return value

`RunAsync` returns `IReadOnlyList<BenchmarkResult>` with all results, including errored benchmarks. Exit code is 0 on success.

## Next steps

- [CLI Reference](../cli-reference) — all command-line flags
- [Configuration](../configuration) — options reference
- [Reporters](../reporters/) — all reporters
