---
title: Dependency Injection
description: Use Microsoft.Extensions.DependencyInjection (or any container) to give benchmark classes constructor dependencies.
order: 4
---

# Dependency Injection

By default, `BenchmarkHost` instantiates benchmark classes with `Activator.CreateInstance`, which means the class must have a public parameterless constructor. The `NBenchmark.DependencyInjection` companion package lifts that constraint: it resolves benchmark classes from an `IServiceProvider`, so constructor dependencies are injected automatically.

## Install

```bash
dotnet add package NBenchmark.DependencyInjection
dotnet add package Microsoft.Extensions.DependencyInjection   # if you also want the concrete DI implementation
```

The companion package only adds `Microsoft.Extensions.DependencyInjection.Abstractions`. The `Microsoft.Extensions.DependencyInjection` reference is only required if you want to use the `ServiceCollection` / `BuildServiceProvider` API directly - any container that exposes an `IServiceProvider` works.

## Minimal example

```csharp
using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;
using NBenchmark.DependencyInjection;

var services = new ServiceCollection()
    .AddSingleton<IOrderRepository, SqlOrderRepository>()
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHost.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(services)   // one call: discovery + DI
    .WithReporter(new ConsoleReporter())
    .RunAsync();

public interface IOrderRepository
{
    int Count();
}

public sealed class SqlOrderRepository : IOrderRepository
{
    public int Count() => 1_247;   // pretend this hits a real DB
}

public sealed class OrderBenchmarks(IOrderRepository repository)
{
    [Benchmark]
    public int CountOrders() => repository.Count();
}
```

`UseDependencyInjection<T>` is shorthand for `AddFromAssembly<T>().WithServiceProvider(services)`. It discovers the assembly containing `T`, configures the host to resolve benchmark instances from the supplied service provider, and runs.

## The four extension methods

Pick the granularity that matches your needs:

| Method | When to use it |
|---|---|
| `UseDependencyInjection<T>(sp)` | The common case. Discovers `T`'s assembly and resolves from the root provider. One line. |
| `UseScopedDependencyInjection<T>(sp)` | Like above but creates a fresh DI scope per suite, disposing it after teardown. Good for `DbContext`, EF Core, and any other scoped service. |
| `WithServiceProvider(sp)` | You already called `AddFromAssembly` yourself (perhaps with multiple assemblies) and want to plug in the root provider. |
| `WithScopedServiceProvider(sp)` | Same as above but with a fresh scope per suite. |

Example: multiple assemblies, scoped lifetime:

```csharp
var services = new ServiceCollection()
    .AddSingleton<IClock, SystemClock>()
    .AddDbContext<MyDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .AddTransient<InventoryBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHost.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .AddFromAssembly<InventoryBenchmarks>()
    .UseScopedDependencyInjection<OrderBenchmarks>(services)
    .RunAsync();
```

## Lifetime and disposal semantics

The DI integration matches how `BenchmarkHost` manages benchmark instances: **a fresh instance per `[Benchmark]` method**. This is the same lifetime the host uses for plain parameterless classes, so DI users get a one-to-one mapping between methods and instances.

| Method | Instance lifetime | Scope lifetime |
|---|---|---|
| `WithServiceProvider` | One fresh instance per `[Benchmark]` method, resolved from the root provider. | None. The root provider lives as long as your application. |
| `WithScopedServiceProvider` | One fresh instance per `[Benchmark]` method. | One fresh scope per method, disposed in per-method teardown. |
| `WithServiceProvider` + `[InstanceLifetime(PerClass)]` | Resolved from the root provider. Re-used across all `[Benchmark]` methods. | None. |
| `WithScopedServiceProvider` + `[InstanceLifetime(PerClass)]` | Resolved from a fresh scope. The scope is disposed **after** the suite's teardown runs, so any `IDisposable` / `IAsyncDisposable` services (e.g. `DbContext`) are cleaned up. | One scope per suite. Disposed in the `finally` block. |

The host **does not** auto-dispose the benchmark instance when a service provider is configured - the scope's disposal already handles that. This avoids double-disposal of `IDisposable` benchmarks that come from a scope.

### Worked example: EF Core with per-method instances

```csharp
var services = new ServiceCollection()
    .AddDbContext<MyDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHost.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .UseScopedDependencyInjection<OrderBenchmarks>(services)
    .RunAsync();
```

`UseScopedDependencyInjection` is `WithScopedServiceProvider` under the hood. With `PerMethod`, each `[Benchmark]` method gets a fresh `MyDbContext` - no shared state, no cache contamination between methods.

> **Warning: shared state breaks statistical independence.** If you pair `WithScopedServiceProvider` with `[InstanceLifetime(InstanceLifetime.PerClass)]`, all `[Benchmark]` methods in the class share one instance. A scoped service like `DbContext` caches entities and queries in memory, so method A can warm the cache that method B reads. Method B's timings become artificially linked to method A running first, which violates the independence assumption of the Mann-Whitney U test used for significance. The NB0011 analyzer warns on this combination at compile time, but the warning is soft and can scroll past unnoticed in CI. See the [NB0011 reference](../reference/analyzers.md#nb0011---perclass-lifetime-with-scoped-service) for suppression guidance if sharing state is intentional.

## Constructor injection

Primary constructors (C# 12+) work out of the box:

```csharp
public sealed class MyBenchmarks(IRepository repo, ILogger<MyBenchmarks> logger)
{
    [Benchmark]
    public int Read() => repo.GetCount();
}
```

Traditional constructors work too:

```csharp
public sealed class MyBenchmarks
{
    private readonly IRepository _repo;
    public MyBenchmarks(IRepository repo) => _repo = repo;

    [Benchmark]
    public int Read() => _repo.GetCount();
}
```

The container resolves all constructor parameters from registered services. If a service is missing, the host logs an error and skips the suite rather than crashing the run.

## Using a non-Microsoft container

The package is built around the `IServiceProvider` interface from the BCL, so any container that exposes one is supported. For Autofac, DryIoc, SimpleInjector, Lamar, etc., build your container, get its `IServiceProvider`, and pass it in:

```csharp
var container = new ContainerBuilder()
    .RegisterType<SqlOrderRepository>().As<IOrderRepository>()
    .Build();

await BenchmarkHost.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(container.Resolve<IServiceProvider>())
    .RunAsync();
```

## Escape hatch: `WithInstanceFactory`

If you don't use any DI container but still need a non-parameterless constructor, the underlying extension point is public on the core library:

```csharp
host.WithInstanceFactory(type =>
{
    var ctor = type.GetConstructors().Single();
    var args = ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray();
    return ctor.Invoke(args);
});
```

This is what the `NBenchmark.DependencyInjection` package does internally. Under `PerMethod`, the factory is called once per `[Benchmark]` method and the returned instance is used for that one method only. If you need one instance shared across all benchmark methods in a class, add `[InstanceLifetime(InstanceLifetime.PerClass)]`.

## A note on Quick mode and Suite mode

The DI integration only affects **Host mode** (`BenchmarkHost`), where classes are discovered reflectively and instantiated. Quick mode (`Benchmark.Run`) and Suite mode (`BenchmarkSuite`) take lambdas directly, so dependencies are captured in the closure - no DI package needed:

```csharp
// Quick mode - dependencies captured in the closure
var result = Benchmark.Run(() => repository.GetCount());

// Suite mode - same closure trick
await new BenchmarkSuite("repo")
    .Add("count", () => repository.GetCount())
    .Add("list",  () => repository.ListAll())
    .RunAsync();
```

## Next steps

- [Host mode: BenchmarkHost](./host-mode.md) - full reference for the host mode
- [Samples](../samples.md) - see the `samples/DependencyInjection/` project for a complete working example
- [FAQ](../faq.md#my-benchmark-class-needs-dependencies-how-do-i-inject-them) - common questions
