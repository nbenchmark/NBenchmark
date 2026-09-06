---
title: Benchmarking ASP.NET Core services
description: Benchmark an EF Core query or ASP.NET service end-to-end with Harness mode, scoped dependency injection, parameterized cases, and categories.
order: 1
---

# Benchmarking ASP.NET Core services

## Scenario

If you have a service or repository method that interacts with a database (such as EF Core, Dapper, or a raw connection), you may want to measure it under realistic conditions. This includes using real query plans, real serialization, parameterized inputs, and actual dependency injection (DI) lifetimes. To do this, your benchmark requires a scoped `DbContext` per method, support for multiple input sizes, and a way to group related benchmarks.

## Complete example

The following `Program.cs` example demonstrates how to target an ASP.NET Core service using harness mode. This setup uses attribute-based discovery, scoped DI to provide a fresh `DbContext` for each `[Benchmark]` method, parameterized cases to sweep input sizes, and categories to organize the suite.

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>().WithScopedServices(BuildServices)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddDbContext<BenchDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

public sealed class OrderBenchmarks(BenchDbContext db)
{
    [Benchmark]
    [BenchmarkCategory("Read")]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1_000)]
    public int ListRecentOrders(int limit)
        => db.Orders.OrderByDescending(o => o.Id).Take(limit).Count();

    [Benchmark]
    [BenchmarkCategory("Write")]
    public int InsertOrder()
    {
        db.Orders.Add(new Order());
        return db.SaveChanges();
    }
}
```

## What's happening

- **`UseScopedDependencyInjection<T>(BuildServices)`**: This method performs three actions: it discovers the assembly for `T`, configures instances to be resolved from a container built by your factory, and creates a fresh DI scope per `[Benchmark]` method. The engine disposes of the scope during per-method teardown, ensuring that any `IDisposable` or `IAsyncDisposable` services (such as `DbContext` or `HttpClient`) are cleaned up. For more information, see [Dependency Injection](../features/dependency-injection.md).

- **Using a factory instead of a provider**: You must pass a factory rather than a pre-built `IServiceProvider`. Because a container is live code containing singletons and open connections, it cannot cross a process boundary; attempting to pass a built container results in a compile error. A factory serves as a recipe that the worker process executes in its own process to build its own container. This ensures that the benchmark does not measure the "warmth" of a container created in the host process. The factory can capture values like connection strings, but it must not return the container itself.

- **`AddDbContext` and `WithScopedServices`**: This combination provides each benchmark method with a fresh `DbContext`. With the default `PerMethod` lifetime, one method cannot warm the entity cache for another. For a full overview, see the [lifetime and disposal table](../features/dependency-injection.md#lifetime-and-disposal-semantics).

- **`[Arguments(...)]`**: This attribute expands a single method into multiple benchmarks, one for each case. The display name includes the parameter, such as `ListRecentOrders(limit=10)`. The engine groups significance testing by parameter set, meaning benchmarks with `limit=100` are compared against each other rather than against benchmarks with `limit=1_000`. For more information, see [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md).

- **`[BenchmarkCategory(...)]`**: This attribute tags benchmarks for easier filtering. For example, you can run only the read path using `dotnet run -- --include-category Read`, or exclude writes using `dotnet run -- --exclude-category Write`. For more information, see [Categories](../features/categories.md).

> [!WARNING] Shared state breaks statistical independence
> Pairing `WithScopedServices` with `[InstanceLifetime(InstanceLifetime.PerClass)]` causes a single instance and `DbContext` to be shared by every `[Benchmark]` method in the class. This allows the cache to warm across methods, linking the timings of one method to another and violating the independence assumption of the significance test. To prevent this, the engine resolves the lifetime to `PerMethod` (a fresh instance and scope per method) and attaches a warning to the results. To maintain `PerClass` lifetime, implement `IStateReset` or add `[SharedState]` if the carry-over is the subject of your measurement. The **NB0011 analyzer** also reports this combination at build time. For more information, see [State isolation](../features/state-isolation.md).

## Run the benchmark

Execute the following commands from the benchmark project directory:

```bash
# Run all benchmarks
dotnet run -c Release

# Run only the read benchmarks for the two larger sizes
dotnet run -c Release -- --include-category Read --filter "*limit=100*" --filter "*limit=1_000*"

# Perform a smoke test (runs the body once without warmup or measurement)
dotnet run -c Release -- --dry-run
dotnet run -c Release -- --samples 1 --warmup-samples 0

# Export results to JSON for a CI dashboard
dotnet run -c Release -- --reporter json --output ./results
```

The harness is **isolated by default**. Each benchmark class runs in its own freshly spawned worker process, ensuring that JIT, GC, and thread-pool state from one class cannot bias another. For more information, see [Isolated runs](../features/isolated-runs.md).

## Read the results

The console reporter prints one comparison table per class, grouped by parameter set. Pay attention to the following columns:

- **Median**: The middle timing. Use this to observe scaling across parameter values.
- **Ratio**: The speed relative to the baseline. A value of `0.75x` indicates the implementation is 25% faster, while `2.0x` indicates it is twice as slow.
- **Sig**: A **✓** indicates the difference from the baseline is statistically significant (p < 0.05); an **✗** indicates the measurements are too noisy to determine a result.
- **Magnitude**: The size of the difference (Negligible, Small, Medium, or Large). A **✓** with a Negligible magnitude is real but likely too small to be meaningful.
- **Alloc/op**: The mean heap allocation per operation. Because EF Core query materialization is often allocation-heavy, this column provides an actionable signal for optimization.

For a full explanation of every column, indicator, and warning, see [Reading Your Results](../getting-started/reading-your-results.md).

## Next steps

For more information, see the following pages:

- [Harness mode: BenchmarkHarness](../usage-modes/harness-mode.md) - Full attribute reference, including `[GlobalSetup]`, `[SampleSetup]`, `[Isolation(Isolation.Required)]`, and `[Runtimes]`.
- [Dependency Injection](../features/dependency-injection.md) - Details on scoped vs. root providers, multiple assemblies, non-Microsoft containers, and the `WithInstanceFactory` method.
- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - Using `[ArgumentsSource]` for generated or file-backed inputs.
- [State isolation](../features/state-isolation.md) - Using `IStateReset` for classes that intentionally share state.
- [Analyzers](../reference/analyzers.md) - The NB0001-NB0014 Roslyn diagnostics, including NB0011.
- [Performance gates in your test suite](./performance-gates.md) - How to fail a pull request on regression instead of printing a table.
