---
title: Dependency Injection
description: Use Microsoft.Extensions.DependencyInjection (or any container) to give benchmark classes constructor dependencies.
order: 7
---

# Dependency injection

By default, `BenchmarkHarness` instantiates benchmark classes using `Activator.CreateInstance`, which requires the class to have a public parameterless constructor. The `NBenchmark.DependencyInjection` companion package removes this constraint by resolving benchmark classes from an `IServiceProvider`, allowing constructor dependencies to be injected automatically.

## Installation

Install the following packages:

```bash
dotnet add package NBenchmark.DependencyInjection
dotnet add package Microsoft.Extensions.DependencyInjection   # Optional: required for concrete DI implementation
```

The companion package adds `Microsoft.Extensions.DependencyInjection.Abstractions`. You only need the `Microsoft.Extensions.DependencyInjection` reference if you want to use the `ServiceCollection` and `BuildServiceProvider` API directly. Any container that exposes an `IServiceProvider` works.

## Minimal example

```csharp
using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark;
using NBenchmark.Reporters.Console;
using NBenchmark.DependencyInjection;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>().WithServices(BuildServices)   // Handles discovery and DI
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Use a static factory instead of a built container. The worker runs this in its own
// process to maintain isolation - see "Lifetime and disposal semantics" below.
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
    public int Count() => 1_247;   // Mock DB call
}

public sealed class OrderBenchmarks(IOrderRepository repository)
{
    [Benchmark]
    public int CountOrders() => repository.Count();
}
```

`UseDependencyInjection<T>` is shorthand for `AddFromAssembly<T>().WithServices(BuildServices)`. It discovers the assembly containing `T`, configures the host to resolve benchmark instances from a container built by the factory, and runs the benchmarks.

## Extension methods

Choose the granularity that matches your needs. All four methods take a factory. The engine does not provide an overload that takes a built container directly (see the note below).

| Method | When to use it |
| --- | --- |
| `UseDependencyInjection<T>(BuildServices)` | Common case. Discovers `T`'s assembly and resolves from the root provider. |
| `UseScopedDependencyInjection<T>(BuildServices)` | Similar to above, but creates a fresh DI scope per instance and disposes it after teardown. This is ideal for `DbContext`, EF Core, and other scoped services. It is isolated because the worker builds its own container and scopes. |
| `WithServices(BuildServices)` | Use this if you've already called `AddFromAssembly` (perhaps with multiple assemblies) and want to plug in the root provider. It is isolated. |
| `WithScopedServices(BuildServices)` | Similar to above, but provides a fresh scope per instance. It is isolated. |

> [!NOTE]
> These methods take a `Func<IServiceProvider>` rather than a built `IServiceProvider`. A container is live code that holds singletons and open connections that cannot cross a process boundary. Passing a built container would be a compile error because it would break process isolation.

Example: multiple assemblies with scoped lifetime:

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .AddFromAssembly<InventoryBenchmarks>()
    .AddFromAssembly<OrderBenchmarks>().WithScopedServices(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddSingleton<IClock, SystemClock>()
    .AddDbContext<MyDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .AddTransient<InventoryBenchmarks>()
    .BuildServiceProvider();
```

## Lifetime and disposal semantics

The DI integration follows the `BenchmarkHarness` instance management: **one fresh instance per `[Benchmark]` method**. This matches the lifetime used for plain parameterless classes, providing a one-to-one mapping between methods and instances.

| Method | Instance lifetime | Scope lifetime | Isolated? |
| --- | --- | --- | --- |
| `WithServices(BuildServices)` | One fresh instance per `[Benchmark]` method, resolved from the root container. | None. | Yes - the worker builds its own container. |
| `WithScopedServices(BuildServices)` | One fresh instance per `[Benchmark]` method. | One fresh scope per instance, disposed in per-method teardown. | Yes - the worker builds its own container and scopes. |
| `WithServices(BuildServices)` + `[InstanceLifetime(PerClass)]` | Resolved from the root container and reused across all `[Benchmark]` methods. | None. | Yes. |
| `WithScopedServices(BuildServices)` + `[InstanceLifetime(PerClass)]` | Resolved from a fresh scope. The scope is disposed after the suite's teardown runs, cleaning up any `IDisposable` or `IAsyncDisposable` services (such as `DbContext`). | One scope per class instance, disposed in the `finally` block. | Yes. |

> [!IMPORTANT]
> Every configuration above is isolated because the worker executes `BuildServices` in its own process to build its own container. This is why you must pass a factory rather than a built `IServiceProvider`. A container holds live state (singletons, connections) that cannot be transferred across the process boundary, so handing one over would cost the run its isolation before anything ran - worth roughly 3.3x on bodies of provably identical cost. Passing a factory ensures the worker creates its own live state locally; it is not a style choice, it is the whole mechanism.

The host does not automatically dispose the benchmark instance when a service provider is configured, as the scope's disposal handles it. This prevents double-disposal of `IDisposable` benchmarks resolved from a scope.

### Example: EF Core with per-method instances

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .AddFromAssembly<OrderBenchmarks>().WithScopedServices(BuildServices)
    .RunAsync();

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddDbContext<MyDbContext>(opts => opts.UseInMemoryDatabase("benchmarks"))
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();
```

`WithScopedServices` uses `WithScopedServices` internally. With `PerMethod` lifetime, each `[Benchmark]` method receives a fresh `MyDbContext`, preventing shared state or cache contamination between methods.

> **Shared state breaks statistical independence.** If you pair `WithScopedServices` with `[InstanceLifetime(InstanceLifetime.PerClass)]`, one instance and one scope serve every `[Benchmark]` method in the class. A scoped service like `DbContext` caches entities and queries; therefore, method A could warm the cache that method B reads. This links the timings of the two methods, violating the independence assumption of the Mann-Whitney U test used for significance. Consequently, the engine resolves this combination to `PerMethod`, and the results include a warning. You can implement `IStateReset` to keep `PerClass` and reset state between methods, or use `[SharedState]` to declare the carry-over as deliberate. The NB0011 analyzer reports this combination at compile time. For more information, see the [state isolation guide](./state-isolation.md) and the [NB0011 reference](../reference/analyzers.md#nb0011---perclass-lifetime-with-scoped-service).

## Constructor injection

The engine supports both primary constructors (C# 12+) and traditional constructors:

**Primary constructors:**
```csharp
public sealed class MyBenchmarks(IRepository repo, ILogger<MyBenchmarks> logger)
{
    [Benchmark]
    public int Read() => repo.GetCount();
}
```

**Traditional constructors:**
```csharp
public sealed class MyBenchmarks
{
    private readonly IRepository _repo;
    public MyBenchmarks(IRepository repo) => _repo = repo;

    [Benchmark]
    public int Read() => _repo.GetCount();
}
```

The container resolves all constructor parameters from registered services. If a service is missing, the harness logs an error and skips the suite rather than crashing.

## Using a non-Microsoft container

The package uses the BCL `IServiceProvider` interface, so any container that exposes one is supported (e.g., Autofac, DryIoc, SimpleInjector, Lamar). Build the container inside a static factory so the worker can rebuild it:

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>().WithServices(BuildServices)
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

If you don't use a DI container but still need a non-parameterless constructor, use the `WithInstanceFactory` extension point in the core library:

```csharp
host.WithInstanceFactory(type =>
{
    var ctor = type.GetConstructors().Single();
    var args = ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray();
    return ctor.Invoke(args);
});
```

This is the internal mechanism used by the `NBenchmark.DependencyInjection` package. Under `PerMethod` lifetime, the factory is called once per `[Benchmark]` method. To share one instance across all methods in a class, add `[InstanceLifetime(InstanceLifetime.PerClass)]`.

## Single mode and Suite mode

DI integration only affects **Harness mode** (`BenchmarkHarness`), where classes are discovered reflectively. Single mode (`Benchmark.Run`) and Suite mode (`BenchmarkSuite`) take lambdas directly, so dependencies are captured in the closure:

```csharp
// Single mode - dependencies captured in the closure
var result = Benchmark.Run(() => repository.GetCount());

// Suite mode - dependencies captured in the closure
await new BenchmarkSuite("repo")
    .Add("count", () => repository.GetCount())
    .Add("list",  () => repository.ListAll())
    .RunAsync();
```

## Troubleshooting

**Runtime error: "Could not instantiate MyBenchmarks - the type must have a public parameterless constructor"**

This error occurs when `Activator.CreateInstance` cannot construct your benchmark class. Use one of these remedies:

1. **Add a parameterless constructor** to the benchmark class if it has no real dependencies.
2. **Install `NBenchmark.Analyzers`** to detect this at compile time (NB0001).
3. **Use `WithServices` or `WithInstanceFactory`** on `BenchmarkHarness` to resolve instances from your DI container. Wrap your container build logic in a static factory:

   ```csharp
   await BenchmarkHarness.Create(args)
       .AddFromAssembly<MyBenchmarks>()
       .WithServices(BuildServices)
       .RunAsync();

   static IServiceProvider BuildServices() => services; // Use your container build logic here
   ```

   `WithServices` is a core-library method. For scoped lifetimes (such as EF Core's `DbContext`), install `NBenchmark.DependencyInjection` and use `WithScopedServices` or `UseScopedDependencyInjection<T>`.

## See also

For more information, see the following pages:

- [Harness mode: BenchmarkHarness](../usage-modes/harness-mode.md) - Full reference for harness mode.
- [Samples](../samples.md) - See the `samples/DependencyInjection/` project for a complete working example.
- [FAQ](../faq.md#my-benchmark-class-needs-dependencies-how-do-i-inject-them) - Common questions about dependency injection.
