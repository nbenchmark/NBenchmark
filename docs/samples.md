---
title: Samples
description: Runnable sample projects included with NBenchmark.
order: 7
---

# Samples

The repository includes four sample projects in the `samples/` directory that demonstrate each usage mode. Run any of them with `dotnet run`.

## Quick - Quick mode

**`samples/Quick/`**

The simplest possible benchmark: `Benchmark.Run` on a tight loop, followed by `Print()` and `PrintAsync()`.

```bash
cd samples/Quick
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
    .WithProgress(new ConsoleBenchmarkProgress(50, 3))
    .RunAsync();
```

What to look at:

- The comparison table with Ratio and Sig columns.
- The bar chart rendered below the table.
- The significance indicator (✓ or ~) - does the difference appear real?

---

## Host - Host mode

**`samples/Host/`**

A `BenchmarkHost` with two attribute-based benchmarks: a fast `Compute` method and a slower `Baseline`.

```bash
cd samples/Host
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

await BenchmarkHost.Create(args)
    .AddFromAssembly<HostBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress(100, 5))
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

## DependencyInjection - Host mode with DI

**`samples/DependencyInjection/`**

A `BenchmarkHost` setup where the benchmark class has **constructor dependencies** resolved from a `Microsoft.Extensions.DependencyInjection` container. Demonstrates the `NBenchmark.Extensions.DependencyInjection` companion package and `UseDependencyInjection<T>`.

```bash
cd samples/DependencyInjection
dotnet run
dotnet run -- --filter DependencyInjectionBenchmarks.Read
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark.Reporters.Console;
using NBenchmark.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddSingleton<IDataStore, InMemoryDataStore>()
    .AddTransient<OrderRepository>()
    .AddTransient<DependencyInjectionBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHost.Create(args)
    .UseDependencyInjection<DependencyInjectionBenchmarks>(services)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress(100, 5))
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
