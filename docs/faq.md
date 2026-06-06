---
title: FAQ
description: Frequently asked questions about NBenchmark.
order: 8
---

# FAQ

## General

### How is NBenchmark different from BenchmarkDotNet?

BenchmarkDotNet is the industry-standard .NET benchmarking tool and is feature-rich — process isolation, multiple runtimes, diagnosers, and extensive reporting. It is the right tool for serious performance work.

NBenchmark takes a different trade-off: **no out-of-process compilation, no XML configuration, minimal dependencies, and three lines of code to get started**. It is designed for day-to-day measurements during development where the overhead of a full BenchmarkDotNet run is too high.

The two tools are complementary. Use NBenchmark to get quick feedback while developing, and BenchmarkDotNet for rigorous, publishable results.

### Does NBenchmark require any special project type or configuration?

No. Add the NuGet package reference and start calling `Benchmark.Run`. No project template, no attribute on the project, no XML configuration.

### What .NET versions are supported?

NBenchmark targets **net10.0** only. It uses .NET 10 APIs and is not backported to earlier versions.

---

## Measurement

### Why does my benchmark show a large Error value?

A large Error (margin of error) means the measurements are highly variable. Common causes:

- **Too few iterations.** Try `WithIterations(500)` or higher.
- **Background processes.** Close browsers and other applications. Run on a quiet machine.
- **Thermal throttling.** On laptops, the CPU may reduce clock speed mid-run. Let the machine cool down or use a plugged-in desktop.
- **The code path varies.** If your benchmark hits different code paths each iteration (e.g. a cache that fills up), that variability is real and expected.

### Why should I care about the median vs. the mean?

If a few iterations are very slow (e.g. a GC pause), the mean is pulled upward but the median is not. For most comparisons, the **median** better represents the steady-state performance of your code. The **mean** is most useful when read alongside the confidence interval.

### My benchmark produces `0 ns`. What's happening?

The compiler or JIT has likely optimised the benchmark body away because it has no observable side effects. Make sure your benchmark either:

- Returns a value (use `Benchmark.Run(() => Compute())` which uses the generic overload that consumes the result), or
- Has a side effect (writes to a field, uses a passed-in output parameter, etc.)

### How does allocation tracking work? Does it include framework overhead?

NBenchmark uses `GC.GetTotalAllocatedBytes` sampled immediately before and after the action. Any allocations by the benchmark framework itself (setup/teardown delegates, etc.) that fall between the two reads would be included, but in practice this is zero for simple benchmarks.

The value is **thread-local** — allocations made by other threads are not counted.

### Can I benchmark async code?

Yes. Use `Benchmark.RunAsync`, the `Func<Task>` overload of `BenchmarkSuite.Add`, or a `Task`-returning `[Benchmark]` method. The timer captures the full async duration including all awaited work.

---

## Statistics

### What does the Sig column mean?

It shows the result of a **Mann-Whitney U test** comparing the benchmark to the baseline. A **✓** means the difference is statistically significant (p < 0.05) — unlikely to be random noise. A **~** means it is not significant.

See [Statistical Significance](./getting-started/key-concepts#statistical-significance) and the [Statistics Deep Dive](./advanced/statistics) for full details.

### Why is significance sometimes blank?

Significance requires at least **5 samples in each group**. With fewer samples the test cannot produce a reliable result.

It is also absent on the baseline itself and when `EnableSignificance` is set to `false`.

### The result is significant but the difference is tiny. Should I care?

Statistical significance does not imply practical importance. With many iterations, even a 0.1 ns difference can be statistically significant. Always combine the Sig column with the **Ratio** column to judge whether the difference is meaningful for your use case.

### What confidence level should I use?

The default **95%** is the standard choice for most purposes. Use **99%** when you need to be more conservative — for example, when asserting a performance budget in CI.

A higher confidence level produces a **wider** (larger) Error value.

### The Error column is showing `±0 ns`. Is that correct?

`MarginOfError` is zero when `n < 2` (only one sample was collected) or when the measured standard deviation is exactly zero (all iterations took the same time). The latter can happen when the timer resolution is coarser than the benchmark duration — if everything rounds to the same tick count, there is no measured spread.

---

## Reporters and output

### Can I use the Markdown or CSV reporter from a BenchmarkSuite?

Yes — all three tiers support any reporter:

```csharp
await new BenchmarkSuite("name")
    .WithReporter(new MarkdownReporter("results.md"))
    .WithReporter(new CsvReporter("results.csv"))
    .RunAsync();
```

### Why does the output directory need to already exist?

`MarkdownReporter` and `CsvReporter` do not create directories to avoid accidentally writing to unexpected locations. Create the directory before running:

```bash
mkdir -p results
dotnet run -- --reporter markdown --output ./results
```

`JsonReporter` is an exception — it creates the output directory automatically.

### Can I write my own reporter?

Yes. Implement `IReporter` from the `NBenchmark` package:

```csharp
public sealed class MyReporter : IReporter
{
    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default)
    {
        foreach (var r in results.Where(r => !r.Errored))
            System.Console.WriteLine($"{r.Name}: {r.Median:F0} ns");
        return Task.CompletedTask;
    }
}
```

---

## BenchmarkHost (Tier 3)

### Can I run benchmarks in source order instead of random order?

Yes:

```bash
dotnet run -- --order declaration
```

Or in code: `.WithRunOrder(RunOrder.Declaration)`.

### How do I make the run order reproducible?

Use `--seed`:

```bash
dotnet run -- --seed 42
```

### My `[Benchmark]` methods are not being discovered. Why?

Common causes:

1. The method is not `public`.
2. The class is abstract.
3. The assembly containing the class was not passed to `AddFromAssembly`.
4. The `[Benchmark]` attribute is from a different namespace (make sure you're using `NBenchmark.Attributes`).

Use `--list` to check what NBenchmark finds before running.

### The host throws "Could not instantiate MyClass". How do I fix it?

`BenchmarkHost` creates benchmark class instances using `Activator.CreateInstance`, which requires a **public parameterless constructor**. If your class has a constructor with parameters, add a separate parameterless one that provides defaults, or restructure to use `[BenchmarkSetup]` for initialisation.
