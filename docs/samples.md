---
title: Samples
description: Runnable sample projects included with NBenchmark.
order: 7
---

# Samples

The repository includes three sample projects in the `samples/` directory that demonstrate each usage tier. Run any of them with `dotnet run`.

## Quick — Tier 1

**`samples/Quick/`**

The simplest possible benchmark: `Benchmark.Run` on a tight loop, followed by `Print()` and `PrintAsync()`.

```bash
cd samples/Quick
dotnet run
```

```csharp
using NBenchmark;
using NBenchmark.Console;

var result = Benchmark.Run(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
await result.PrintAsync();
```

What to look at:

- The plain-text output from `result.Print()` (core package only).
- The Spectre.Console table from `result.PrintAsync()` (requires `NBenchmark.Console`).
- The 95% CI line in the plain-text output.

---

## Suite — Tier 2

**`samples/Suite/`**

A `BenchmarkSuite` comparing bubble sort versus LINQ sorting on a 100-element array, with a short iteration count for a fast demo run.

```bash
cd samples/Suite
dotnet run
```

```csharp
using NBenchmark;
using NBenchmark.Console;

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
- The significance indicator (✓ or ~) — does the difference appear real?

---

## Host — Tier 3

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
using NBenchmark.Console;
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
