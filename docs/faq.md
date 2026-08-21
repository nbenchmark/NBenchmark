---
title: FAQ
description: Frequently asked questions about NBenchmark.
order: 12
---

# FAQ

## General

### How is NBenchmark different from BenchmarkDotNet?

NBenchmark provides statistical rigor - including non-parametric significance testing, confidence intervals, and percentile analysis - directly for your daily development cycle with zero configuration and no external dependencies. Its numerical core is dependency-free and cross-validated against SciPy and NumPy to machine precision. For more information, see [Validation & Accuracy](./statistics/validation.md).

NBenchmark makes different trade-offs than tools like BenchmarkDotNet: it uses no out-of-process compilation, no XML configuration, and minimal dependencies. You can get started with three lines of code.

The two tools are complementary. Use NBenchmark for day-to-day development feedback and BenchmarkDotNet for publishable cross-platform results. For help with common measurement issues, see the [Troubleshooting guide](./troubleshooting.md).

### Does NBenchmark require a special project type or configuration?

No. Add the NuGet package reference and call `Benchmark.Run`. You do not need a project template, project attributes, or XML configuration.

### Which .NET versions are supported?

NBenchmark targets .NET 8, .NET 9, and .NET 10. You need the .NET 8 SDK or later.

---

## Measurement

### Why does my benchmark show a large error value?

A large error (margin of error) indicates that the measurements are highly variable. Common causes include:

- **Insufficient iterations.** Try `WithIterations(500)` or higher.
- **OS scheduling noise.** Use `.WithOutlierMode(OutlierMode.IqrFence)` to discard extreme measurements from context switches or scheduler interrupts.
- **Thermal throttling.** On laptops, the CPU may reduce clock speed mid-run. Increase warmup with `.WithWarmup(50)` to let the CPU stabilize before measurement, or reduce iterations to shorten the run.
- **Variable code paths.** If your benchmark hits different code paths each iteration (for example, a cache that fills up), that variability is real and expected.

For a full symptom-to-fix index and configuration remedies, see the [Troubleshooting guide](./troubleshooting.md).

### Why use the median instead of the mean?

If a few iterations are very slow (for example, due to a GC pause), the mean is pulled upward, but the median is not. For most comparisons, the **median** better represents the steady-state performance of your code. The **mean** is most useful when read alongside the confidence interval.

### My benchmark produces 0 ns. What's happening?

The compiler or JIT likely optimized the benchmark body away because it has no observable side effects. Ensure your benchmark does one of the following:

- Returns a value. Use `Benchmark.Run(() => Compute())`, which uses the generic overload that consumes the result.
- Has a side effect, such as writing to a field or using a passed-in output parameter.

Use `--dry-run` to verify the body is being invoked. For more information on dead code elimination and other zero-result causes, see the [Troubleshooting guide](./troubleshooting.md).

### How does allocation tracking work? Does it include framework overhead?

NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` immediately before and after the action. If an async benchmark resumes on a different thread, it falls back to a `GC.GetTotalAllocatedBytes` delta for that iteration.

Any allocations by the benchmark framework itself (such as setup/teardown delegates) that occur between the two reads are included, but this is usually negligible for simple benchmarks.

### Can I benchmark async code?

Yes. Use `Benchmark.RunAsync`, the `Func<Task>` overload of `BenchmarkSuite.Add`, or a `Task`-returning `[Benchmark]` method. The timer captures the full async duration, including all awaited work.

### What happens when a benchmark throws an exception?

NBenchmark captures the exception and reports the benchmark as an **errored result** (`Errored = true`, with the message in `ErrorMessage`). The rest of the run continues. This includes `OperationCanceledException` thrown by the benchmark body itself (for example, an `HttpClient` timeout). A misbehaving benchmark never aborts the entire run.

The only exception that propagates is a cancellation triggered through the `CancellationToken` you passed to `RunAsync`. In this case, suite teardown still runs.

---

## Statistics

### What does the Sig column mean?

The `Sig` column shows the result of a [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) comparing the benchmark to the baseline. A **✓** means the difference is statistically significant (p < 0.05) and unlikely to be random noise. A **✗** means it is not significant.

When you compare **three or more** benchmarks, NBenchmark first runs a [Kruskal-Wallis](https://en.wikipedia.org/wiki/Kruskal%E2%80%93Wallis_test) omnibus test. If the omnibus test is significant (meaning at least one group differs), NBenchmark runs post-hoc pairwise Mann-Whitney U tests with Holm-Bonferroni correction and populates the per-row `Sig` column with the corrected verdicts. If the omnibus test is not significant, the `Sig` column remains blank.

For more information, see [Statistical Significance](./getting-started/key-concepts.md#significance-and-magnitude) and the [Statistics Deep Dive](./statistics/).

### Why is significance sometimes blank?

Significance requires at least **two samples in each group**. The test cannot produce a reliable result with fewer samples.

The `Sig` column is also blank for the baseline itself and when `EnableSignificance` is set to `false`.

### The result is significant but the difference is tiny. Should I care?

Statistical significance does not imply practical importance. With many iterations, even a 0.1 ns difference can be statistically significant. Always combine the `Sig` column with the **Ratio** column to judge whether the difference is meaningful for your use case.

### Which confidence level should I use?

The default **95%** is the standard choice for most purposes. Use **99%** when you need to be more conservative, such as when asserting a performance budget in CI.

A higher confidence level produces a **wider** (larger) error value.

### The Error column shows ±0 ns. Is that correct?

`MarginOfError` is zero when `n < 2` (only one sample was collected) or when the measured standard deviation is exactly zero (all iterations took the same time). The latter occurs when the timer resolution is coarser than the benchmark duration; if every sample rounds to the same tick count, there is no measured spread.

---

## Reporters and output

### Can I use the Markdown or CSV reporter from a BenchmarkSuite?

Yes. All four modes support any reporter:

```csharp
await new BenchmarkSuite("name")
    .WithReporter(new MarkdownReporter("results/"))
    .WithReporter(new CsvReporter("results/"))
    .RunAsync();
```

### Can I specify a custom filename?

Yes. All file reporters accept an optional `fileName` parameter:

```csharp
new JsonReporter("results/", "benchmarks.json")
new MarkdownReporter("results/", "BENCHMARKS.md")
new CsvReporter("results/", "results.csv")
```

If you omit `fileName`, the reporter auto-generates a timestamped filename with a per-process counter (for example, `benchmark-results-20260606-034000-001.md`). If you specify a filename, the reporter uses that exact name and overwrites the file in subsequent runs.

### Can I write my own reporter?

Yes. Implement `IReporter` from the `NBenchmark` package:

```csharp
public sealed class MyReporter : IReporter
{
    public string Name => "my-reporter";

    public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default)
    {
        foreach (var r in results.Where(r => !r.Errored))
            System.Console.WriteLine($"{r.Name}: {r.Median:F0} ns");
        return Task.CompletedTask;
    }
}
```

To make the reporter available via the `--reporter` CLI flag, register it with the global `ReporterRegistry`:

```csharp
ReporterRegistry.Register("my-reporter", "Custom console output", _ => new MyReporter());
```

Register the reporter in a `[ModuleInitializer]` in your package or at app startup before `BenchmarkHarness.Create(args)` is called.

### What is an auto-attached reporter?

Auto-attached reporters fire on every run after the user's explicit reporters, without requiring an opt-in. Register these via `ReporterRegistry.RegisterAutoAttach` (which is distinct from `Register`, which only makes a reporter available via `--reporter`). Auto-attached reporters are designed for side-effect reporters that integrate with an external system.

For more information, see the [Custom Reporters](output/custom-reporters.md#auto-attached-reporters) page for the full contract, including the `CI=true` opt-out convention and deduplication with explicit reporters.

---

## BenchmarkHarness (Harness mode)

### Can I run benchmarks in source order instead of random order?

Yes. Use the CLI flag:

```bash
dotnet run -- --order declaration
```

Or use code: `.WithRunOrder(RunOrder.Declaration)`.

### How do I make the run order reproducible?

Use the `--seed` flag:

```bash
dotnet run -- --seed 42
```

### My [Benchmark] methods are not being discovered. Why?

Common causes include:

1. The method is `static` (NBenchmark only measures instance methods).
2. The class is abstract.
3. The assembly containing the class was not passed to `AddFromAssembly`.
4. The `[Benchmark]` attribute is from a different namespace (ensure you are using `NBenchmark.Attributes`).

Use `--list` to check which benchmarks NBenchmark finds before running.

### The host throws "Could not instantiate MyClass". How do I fix it?

`BenchmarkHarness` creates benchmark class instances using `Activator.CreateInstance`, which requires a **public parameterless constructor**. You can satisfy this requirement in three ways:

1. **Add a parameterless constructor** that initializes dependencies. This is the simplest fix if the class has no real dependencies.
2. **Use `[BenchmarkSetup]`** to populate fields on a parameterless-constructed instance.
3. **Use the `NBenchmark.DependencyInjection` companion package** to resolve the class from a container built by a factory:

```csharp
await BenchmarkHarness.Create(args)
    .UseDependencyInjection<MyBenchmarks>(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddTransient<MyBenchmarks>()
    .BuildServiceProvider(); // Register the class's dependencies here
```

Pass the factory instead of a built container so the worker can rebuild it and the run remains isolated. For more information, see the [Dependency Injection guide](./features/dependency-injection.md).

### My benchmark class needs dependencies. How do I inject them?

Add the optional `NBenchmark.DependencyInjection` package and pass a container factory to the host:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NBenchmark.DependencyInjection;

await BenchmarkHarness.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddSingleton<IOrderRepository, SqlOrderRepository>()
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

public sealed class OrderBenchmarks(IOrderRepository repository)
{
    [Benchmark] public int CountOrders() => repository.Count();
}
```

The worker runs `BuildServices` in its own process and resolves the class from the container it builds there, ensuring the run stays isolated. You cannot pass a built `IServiceProvider` because a live container cannot cross a process boundary. A scoped variant (`UseScopedDependencyInjection`) is available for `DbContext`-style lifetimes; the scope is created per instance and disposed after teardown. For more information, see the [Dependency Injection guide](./features/dependency-injection.md) for the full API and lifetime semantics.

### Can I use a DI container other than Microsoft.Extensions.DependencyInjection?

Yes. The companion package only depends on `IServiceProvider` from the BCL. Any container that exposes an `IServiceProvider` (such as Autofac, DryIoc, SimpleInjector, or Lamar) works. Build the container inside a static factory and pass that factory to the host:

```csharp
await BenchmarkHarness.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices()
{
    var container = new ContainerBuilder()
        .RegisterType<SqlOrderRepository>().As<IOrderRepository>()
        .Build();
    return container.Resolve<IServiceProvider>();
}
```

