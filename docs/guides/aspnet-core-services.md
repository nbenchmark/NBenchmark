---
title: Benchmarking ASP.NET Core services
description: Benchmark an EF Core query or ASP.NET service end-to-end with Harness mode, scoped dependency injection, parameterized cases, and categories.
order: 1
---

# Benchmarking ASP.NET Core services

## Scenario

You have a service or repository method that hits a database (EF Core, Dapper, or a raw connection) and you want to measure it under realistic conditions: real query plans, real serialization, parameterized inputs, and real DI lifetimes. The benchmark needs a scoped `DbContext` per method, multiple input sizes, and a way to group related benchmarks together.

## Complete example

This is a complete `Program.cs` for a dedicated benchmark project that targets a real ASP.NET Core service. It uses Harness mode for attribute-based discovery, scoped DI so each `[Benchmark]` method gets a fresh `DbContext`, parameterized cases to sweep input sizes, and categories to keep the suite navigable.

```csharp
var services = new ServiceCollection()
    .AddDbContext<BenchDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHarness.Create(args)
    .UseScopedDependencyInjection<OrderBenchmarks>(services)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

public sealed class OrderBenchmarks(BenchDbContext db)
{
    [Benchmark]
    [BenchmarkCategory("Read")]
    [BenchmarkCase(10)]
    [BenchmarkCase(100)]
    [BenchmarkCase(1_000)]
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

- **`UseScopedDependencyInjection<T>(sp)`** does three things in one call: discovers `T`'s assembly, configures the host to resolve benchmark instances from the supplied service provider, and creates a fresh DI scope per `[Benchmark]` method. The scope is disposed in per-method teardown, so any `IDisposable` / `IAsyncDisposable` services (`DbContext`, `HttpClient`, etc.) are cleaned up. See [Dependency Injection](../features/dependency-injection.md).

- **`AddDbContext` + `UseScopedDependencyInjection`** gives each benchmark method a fresh `DbContext`. With `PerMethod` lifetime (the default), method A cannot warm the entity cache that method B reads. See the [lifetime and disposal table](../features/dependency-injection.md#lifetime-and-disposal-semantics) for the full matrix.

- **`[BenchmarkCase(...)]`** expands the method into one benchmark per case. The display name carries the parameter: `ListRecentOrders(limit=10)`, `ListRecentOrders(limit=100)`, `ListRecentOrders(limit=1_000)`. Significance is grouped by parameter set, so the `limit=100` benchmarks are compared against each other, not against `limit=1_000`. See [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md).

- **`[BenchmarkCategory(...)]`** tags benchmarks for filtering. Run only the read path with `dotnet run -- --category Read`, or exclude writes with `dotnet run -- --exclude-category Write`. See [Categories](../features/categories.md).

> [!WARNING] Shared state breaks statistical independence
> If you pair `UseScopedDependencyInjection` with `[InstanceLifetime(InstanceLifetime.PerClass)]`, all `[Benchmark]` methods in the class share one instance and one `DbContext`. The cache warms across methods, method B's timings become linked to method A running first, and the significance test's independence assumption is violated. The **NB0011 analyzer** warns on this combination at build time. See [State isolation](../features/state-isolation.md) for the `IStateReset` contract and the auto-isolation fallback that enforce independence at runtime.

## Run it

From the benchmark project directory:

```bash
# Everything
dotnet run -c Release

# Just the read benchmarks, at the two larger sizes
dotnet run -c Release -- --category Read --filter "*limit=100*" --filter "*limit=1_000*"

# Smoke test: run the body once, no warmup, no measurement
dotnet run -c Release -- --dry-run
dotnet run -c Release -- --iterations 1 --warmup 0

# Publish JSON for the CI dashboard
dotnet run -c Release -- --reporter json --output ./results
```

The harness is **isolated by default**: each benchmark class runs in its own freshly spawned worker, so JIT, GC, and thread-pool state from one class cannot bias another. See [Isolated runs](../features/isolated-runs.md).

## Read the results

The console reporter prints one comparison table per class, grouped by parameter set. The columns you care about:

- **Median** - the middle timing. Compare this across the parameter values to see scaling.
- **Ratio** - speed relative to the baseline. `0.75x` = 25% faster; `2.0x` = twice as slow.
- **Sig** - **✓** means the difference from the baseline is statistically real (p < 0.05); **✗** means the measurements are too noisy to tell.
- **Magnitude** - how large the difference is (Negligible / Small / Medium / Large). A ✓ with a Negligible magnitude is real but too small to act on.
- **Alloc/op** - mean heap allocation per operation. EF Core query materialization is allocation-heavy; this column is often the most actionable signal.

See [Reading Your Results](../output/reading-your-results.md) for every column, indicator, and warning.

## When to go deeper

- [Harness mode: BenchmarkHarness](../usage-modes/harness-mode.md) - the full attribute reference (`[BenchmarkSetup]`, `[BenchmarkIterationSetup]`, `[IsolatedProcess]`, `[Runtimes]`, etc.).
- [Dependency Injection](../features/dependency-injection.md) - scoped vs. root provider, multiple assemblies, non-Microsoft containers, the `WithInstanceFactory` escape hatch.
- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - `[BenchmarkCases]` for generated or file-backed inputs, named-tuple display names.
- [State isolation](../features/state-isolation.md) - `IStateReset` for `PerClass` classes that share state intentionally.
- [Analyzers](../reference/analyzers.md) - the NB0001-NB0014 Roslyn diagnostics, including NB0011 (PerClass + scoped service).
- [Performance gates in your test suite](./performance-gates.md) - if you want this comparison to fail a PR on regression instead of just printing a table.
