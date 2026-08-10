---
title: Dependency Injection
description: Use Microsoft.Extensions.DependencyInjection (or any container) to give benchmark classes constructor dependencies.
order: 7
---

# Dependency Injection

By default, `BenchmarkHarness` instantiates benchmark classes with `Activator.CreateInstance`, which means the class must have a public parameterless constructor. The `NBenchmark.DependencyInjection` companion package lifts that constraint: it resolves benchmark classes from an `IServiceProvider`, so constructor dependencies are injected automatically.

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

await BenchmarkHarness.Create(args)
    .UseDependencyInjection<OrderBenchmarks>(BuildServices)   // one call: discovery + DI
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// A static factory, not a built container. The worker runs this in its own process, so the run
// stays isolated - see "Lifetime and disposal semantics" below.
static IServiceProvider BuildServices() => new ServiceCollection()
    .AddSingleton<IOrderRepository, SqlOrderRepository>()
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

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
| --- | --- |
| `UseDependencyInjection<T>(sp)` | The common case. Discovers `T`'s assembly and resolves from the root provider. One line. |
| `UseScopedDependencyInjection<T>(BuildServices)` | Like above but creates a fresh DI scope per instance, disposing it after teardown. Good for `DbContext`, EF Core, and any other scoped service. **Isolated** - the worker builds its own container and its own scopes. |
| `WithServiceProvider(sp)` | You already called `AddFromAssembly` yourself (perhaps with multiple assemblies) and want to plug in the root provider. |
| `WithScopedServiceProvider(BuildServices)` | Same as above but with a fresh scope per instance. **Isolated.** |
| `WithScopedServiceProvider(sp)` | Takes a live container, which no worker can reproduce - so the run is **refused** and fails. Pass the factory instead. |

Example: multiple assemblies, scoped lifetime:

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .AddFromAssembly<InventoryBenchmarks>()
    .UseScopedDependencyInjection<OrderBenchmarks>(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddSingleton<IClock, SystemClock>()
    .AddDbContext<MyDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .AddTransient<InventoryBenchmarks>()
    .BuildServiceProvider();
```

## Lifetime and disposal semantics

The DI integration matches how `BenchmarkHarness` manages benchmark instances: **a fresh instance per `[Benchmark]` method**. This is the same lifetime the host uses for plain parameterless classes, so DI users get a one-to-one mapping between methods and instances.

| Method | Instance lifetime | Scope lifetime | Isolated? |
| --- | --- | --- | --- |
| `WithServiceProvider(BuildServices)` | One fresh instance per `[Benchmark]` method, resolved from the root container. | None. | Yes - the worker builds its own container. |
| `WithScopedServiceProvider(BuildServices)` | One fresh instance per `[Benchmark]` method. | One fresh scope per instance, disposed in per-method teardown. | Yes - the worker builds its own container and its own scopes. |
| `WithServiceProvider(BuildServices)` + `[InstanceLifetime(PerClass)]` | Resolved from the root container. Re-used across all `[Benchmark]` methods. | None. | Yes. |
| `WithScopedServiceProvider(BuildServices)` + `[InstanceLifetime(PerClass)]` | Resolved from a fresh scope. The scope is disposed **after** the suite's teardown runs, so any `IDisposable` / `IAsyncDisposable` services (e.g. `DbContext`) are cleaned up. | One scope per class instance. Disposed in the `finally` block. | Yes. |
| Either, given a built `IServiceProvider` instead of a factory | As above. | As above. | **No.** A live container cannot cross a process boundary, so the run is measured here and stamped `host`. |

> [!IMPORTANT]
> Pass the factory, not the container. Every row above is isolated only because the worker can run
> `BuildServices` itself. Handing over a built `IServiceProvider` is the single most common reason a
> DI-backed run silently loses its isolation - and on bodies of provably identical cost, the
> configuration difference between an isolated worker and this process is worth roughly 3.3x.

The host **does not** auto-dispose the benchmark instance when a service provider is configured - the scope's disposal already handles that. This avoids double-disposal of `IDisposable` benchmarks that come from a scope.

### Worked example: EF Core with per-method instances

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .UseScopedDependencyInjection<OrderBenchmarks>(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddDbContext<MyDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();
```

`UseScopedDependencyInjection` is `WithScopedServiceProvider` under the hood. With `PerMethod`, each `[Benchmark]` method gets a fresh `MyDbContext` - no shared state, no cache contamination between methods.

> **Shared state breaks statistical independence, so this combination is resolved rather than warned about.** If you pair `WithScopedServiceProvider` with `[InstanceLifetime(InstanceLifetime.PerClass)]`, one instance and one scope would serve every `[Benchmark]` method in the class - and a scoped service like `DbContext` caches entities and queries in memory, so method A would warm the cache that method B reads. Method B's timings would become artificially linked to method A running first, violating the independence assumption of the Mann-Whitney U test used for significance. The lifetime therefore resolves to `PerMethod`: each method gets its own instance and its own scope, and the results carry a warning saying so. Implement `IStateReset` to keep `PerClass` and reset between methods, or add `[SharedState]` to declare the carry-over deliberate. The NB0011 analyzer reports the same combination at compile time. See the [state isolation guide](./state-isolation.md) and the [NB0011 reference](../reference/analyzers.md#nb0011---perclass-lifetime-with-scoped-service).

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

The container resolves all constructor parameters from registered services. If a service is missing, the harness logs an error and skips the suite rather than crashing the run.

## Using a non-Microsoft container

The package is built around the `IServiceProvider` interface from the BCL, so any container that exposes one is supported. For Autofac, DryIoc, SimpleInjector, Lamar, etc., build the container inside a static factory and pass that, so the worker can rebuild it:

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

## A note on Single mode and Suite mode

The DI integration only affects **Harness mode** (`BenchmarkHarness`), where classes are discovered reflectively and instantiated. Single mode (`Benchmark.Run`) and Suite mode (`BenchmarkSuite`) take lambdas directly, so dependencies are captured in the closure - no DI package needed:

```csharp
// Single mode - dependencies captured in the closure
var result = Benchmark.Run(() => repository.GetCount());

// Suite mode - same closure trick
await new BenchmarkSuite("repo")
    .Add("count", () => repository.GetCount())
    .Add("list",  () => repository.ListAll())
    .RunAsync();
```

## Troubleshooting

**Runtime error: "Could not instantiate MyBenchmarks - the type must have a public parameterless constructor"**

This error fires when `Activator.CreateInstance` cannot construct your benchmark class because it has no parameterless constructor. Three remedies:

1. **Add a parameterless constructor** to the benchmark class. This is the simplest fix if the class has no real dependencies.
2. **Install `NBenchmark.Analyzers`** for compile-time detection (NB0001). The analyzer catches the missing constructor before you run, saving a debug cycle.
3. **Use `WithServiceProvider` or `WithInstanceFactory`** on `BenchmarkHarness` to resolve instances from your DI container. If you already have an `IServiceProvider`:

   ```csharp
    await BenchmarkHarness.Create(args)
        .AddFromAssembly<MyBenchmarks>()
        .WithServiceProvider(services)
        .RunAsync();
   ```

   `WithServiceProvider` is a core-library method (no extra package needed). For scoped lifetime (e.g. EF Core's `DbContext`), install `NBenchmark.DependencyInjection` and use `WithScopedServiceProvider` or `UseScopedDependencyInjection<T>` instead.

## Next steps

- [Harness mode: BenchmarkHarness](../usage-modes/harness-mode.md) - full reference for the harness mode
- [Samples](../samples.md) - see the `samples/DependencyInjection/` project for a complete working example
- [FAQ](../faq.md#my-benchmark-class-needs-dependencies-how-do-i-inject-them) - common questions
