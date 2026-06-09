---
title: FAQ
description: Frequently asked questions about NBenchmark.
order: 8
---

# FAQ

## General

### How is NBenchmark different from BenchmarkDotNet?

NBenchmark brings serious statistical rigor - non-parametric significance testing, confidence intervals, and percentile analysis - directly into your daily development cycle with zero configuration and zero external dependencies. Its numerical core is dependency-free and cross-validated against SciPy and NumPy to machine precision (see [Validation & Accuracy](./advanced/validation.md)).

NBenchmark takes a different trade-off from tools like BenchmarkDotNet: **no out-of-process compilation, no XML configuration, minimal dependencies, and three lines of code to get started**.

The two tools are complementary - NBenchmark for day-to-day development feedback, BenchmarkDotNet for publishable cross-platform results. See also the [Troubleshooting guide](./troubleshooting.md) for help with common measurement issues.

### Does NBenchmark require any special project type or configuration?

No. Add the NuGet package reference and start calling `Benchmark.Run`. No project template, no attribute on the project, no XML configuration.

### What .NET versions are supported?

NBenchmark targets **net8.0**, **net9.0**, and **net10.0**. You need the .NET 8 SDK or later.

---

## Measurement

### Why does my benchmark show a large Error value?

A large Error (margin of error) means the measurements are highly variable. Common causes:

- **Too few iterations.** Try `WithIterations(500)` or higher.
- **OS scheduling noise.** Switch to `.WithOutlierMode(OutlierMode.IqrFence)` to discard extreme measurements from context switches or scheduler interrupts.
- **Thermal throttling.** On laptops, the CPU may reduce clock speed mid-run. Increase warmup with `.WithWarmup(50)` to let the CPU stabilise before measurement, or reduce iterations to shorten the run.
- **The code path varies.** If your benchmark hits different code paths each iteration (e.g. a cache that fills up), that variability is real and expected.

See the [Troubleshooting guide](./troubleshooting.md) for the full symptom matrix and configuration remedies.

### Why should I care about the median vs. the mean?

If a few iterations are very slow (e.g. a GC pause), the mean is pulled upward but the median is not. For most comparisons, the **median** better represents the steady-state performance of your code. The **mean** is most useful when read alongside the confidence interval.

### My benchmark produces `0 ns`. What's happening?

The compiler or JIT has likely optimised the benchmark body away because it has no observable side effects. Make sure your benchmark either:

- Returns a value (use `Benchmark.Run(() => Compute())` which uses the generic overload that consumes the result), or
- Has a side effect (writes to a field, uses a passed-in output parameter, etc.)

Use `--dry-run` to verify the body is being invoked. See the [Troubleshooting guide](./troubleshooting.md) for more on dead code elimination and other zero-result causes.

### How does allocation tracking work? Does it include framework overhead?

NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` immediately before and after the action. If an async benchmark resumes on a different thread, it falls back to a `GC.GetTotalAllocatedBytes` delta for that iteration.

Any allocations by the benchmark framework itself (setup/teardown delegates, etc.) that fall between the two reads would be included, but in practice this is usually negligible for simple benchmarks.

### Can I benchmark async code?

Yes. Use `Benchmark.RunAsync`, the `Func<Task>` overload of `BenchmarkSuite.Add`, or a `Task`-returning `[Benchmark]` method. The timer captures the full async duration including all awaited work.

---

## Statistics

### What does the Sig column mean?

It shows the result of a **[Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test)** comparing the benchmark to the baseline. A **✓** means the difference is statistically significant (p < 0.05) - unlikely to be random noise. A **~** means it is not significant.

See [Statistical Significance](./getting-started/key-concepts.md#statistical-significance) and the [Statistics Deep Dive](./advanced/statistics.md) for full details.

### Why is significance sometimes blank?

Significance requires at least **5 samples in each group**. With fewer samples the test cannot produce a reliable result.

It is also absent on the baseline itself and when `EnableSignificance` is set to `false`.

### The result is significant but the difference is tiny. Should I care?

Statistical significance does not imply practical importance. With many iterations, even a 0.1 ns difference can be statistically significant. Always combine the Sig column with the **Ratio** column to judge whether the difference is meaningful for your use case.

### What confidence level should I use?

The default **95%** is the standard choice for most purposes. Use **99%** when you need to be more conservative - for example, when asserting a performance budget in CI.

A higher confidence level produces a **wider** (larger) Error value.

### The Error column is showing `±0 ns`. Is that correct?

`MarginOfError` is zero when `n < 2` (only one sample was collected) or when the measured standard deviation is exactly zero (all iterations took the same time). The latter can happen when the timer resolution is coarser than the benchmark duration - if everything rounds to the same tick count, there is no measured spread.

---

## Reporters and output

### Can I use the Markdown or CSV reporter from a BenchmarkSuite?

Yes - all three modes support any reporter:

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

`JsonReporter` is an exception - it creates the output directory automatically.

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

To make it available from the `--reporter` CLI flag, register it with the global `ReporterRegistry`:

```csharp
ReporterRegistry.Register("my-reporter", "Custom console output", _ => new MyReporter());
```

The registration can happen in a `[ModuleInitializer]` in your package or at app startup before `BenchmarkHost.Create(args)` is called.

---

## BenchmarkHost (Host mode)

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

1. The method is `static` (only instance methods are measured).
2. The class is abstract.
3. The assembly containing the class was not passed to `AddFromAssembly`.
4. The `[Benchmark]` attribute is from a different namespace (make sure you're using `NBenchmark.Attributes`).

Use `--list` to check what NBenchmark finds before running.

### The host throws "Could not instantiate MyClass". How do I fix it?

`BenchmarkHost` creates benchmark class instances using `Activator.CreateInstance`, which requires a **public parameterless constructor**. There are three ways to satisfy this:

1. **Add a parameterless constructor** that initialises dependencies itself (simplest, but couples the benchmark class to the dependency).
2. **Use `[BenchmarkSetup]`** to populate fields on a parameterless-constructed instance.
3. **Use the `NBenchmark.DependencyInjection` companion package** to resolve the class from an `IServiceProvider`:

   ```csharp
   await BenchmarkHost.Create(args)
       .UseDependencyInjection<MyBenchmarks>(services)
       .RunAsync();
   ```

   This is the cleanest approach when you already have a DI container in your application. See the [Dependency Injection guide](./guides/dependency-injection.md) for full details.

### My benchmark class needs dependencies. How do I inject them?

Add the optional `NBenchmark.DependencyInjection` package and pass an `IServiceProvider` to the host:

```csharp
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
    [Benchmark] public int CountOrders() => repository.Count();
}
```

The container resolves all constructor parameters. A scoped variant (`UseScopedDependencyInjection`) is available for `DbContext`-style lifetimes - the scope is created per suite and disposed after teardown. See the [Dependency Injection guide](./guides/dependency-injection.md) for the full API and lifetime semantics.

### Can I use a DI container other than `Microsoft.Extensions.DependencyInjection`?

Yes. The companion package only depends on `IServiceProvider` from the BCL. Any container that exposes one - Autofac, DryIoc, SimpleInjector, Lamar, etc. - works:

```csharp
var container = new ContainerBuilder()
    .RegisterType<SqlOrderRepository>().As<IOrderRepository>()
    .Build();

await BenchmarkHost.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(container.Resolve<IServiceProvider>())
    .RunAsync();
```
