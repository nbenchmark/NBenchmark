---
title: State Isolation
description: Keep PerClass benchmark instances clean between methods with IStateReset, declare deliberate sharing with [SharedState], and understand how the lifetime is resolved for container-resolved classes.
order: 9
---

# State isolation across benchmark methods

**The symptom:** Your second benchmark reads a cache the first one filled, making it appear faster than it is. The result then depends on the order in which the benchmarks ran.

This happens when a benchmark class uses `[InstanceLifetime(InstanceLifetime.PerClass)]`, which shares a single instance across every `[Benchmark]` method in the class. Sharing is useful when construction is expensive (such as a database connection or a large in-memory dataset) and you want to amortize that cost. However, this means methods are no longer independent measurements, which violates the assumption the significance test relies on. This can lead to confident-looking p-values that are incorrect.

The engine provides three ways to keep `PerClass` sharing honest: an explicit reset contract (`IStateReset`), an explicit declaration that the carry-over is deliberate (`[SharedState]`), and - for classes whose instances come from a container - an automatic resolution to a fresh instance per method when neither is present.

## `IStateReset`: explicit reset between methods

Implement `IStateReset` on the benchmark class to declare how its shared state is cleared between methods. The engine calls `ResetAsync` after one method completes (and after its inter-benchmark GC) and before the next method's warmup starts. For *N* methods, the engine makes *N-1* calls.

```csharp
using NBenchmark;
using NBenchmark.Lifecycle;

[InstanceLifetime(InstanceLifetime.PerClass)]
public class OrderBenchmarks : IStateReset
{
    private readonly DbContext _db;

    public OrderBenchmarks(DbContext db) => _db = db;

    [Benchmark] public int QueryCached() => _db.Orders.Count();
    [Benchmark] public int QueryFresh() => _db.Orders.AsNoTracking().Count();

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        _db.ChangeTracker.Clear();
        return Task.CompletedTask;
    }
}
```

The class owns its reset semantics and handles whatever it holds: a `DbContext` clears its change tracker, a cache drops its entries, or a counter resets to zero. The engine checks `typeof(IStateReset).IsAssignableFrom(suite.Type)` at dispatch time before instantiation, so there is no runtime introspection cost.

### Empty `IStateReset` implementations

`IStateReset` indicates that the class resets between methods, making `PerClass` safe. An empty method body claims to reset state without actually doing so:

```csharp
// Wrong. NB0011 reports this.
public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
```

The engine only detects if the interface is present; it cannot read the method body. Consequently, an empty implementation maintains the `PerClass` lifetime while resetting nothing. Analyzer NB0011 reports empty `ResetAsync` methods for this reason. If the carry-over is deliberate, use `[SharedState]` instead.

## `[SharedState]`: deliberate carry-over

Some benchmarks specifically measure warm state, such as the second call into a populated cache. Declare this using `[SharedState]`:

```csharp
[InstanceLifetime(InstanceLifetime.PerClass)]
[SharedState]
public class CacheBenchmarks
{
    [Benchmark] public int ColdishRead() => _cache.Get("k");
    [Benchmark] public int WarmRead() => _cache.Get("k");
}
```

This maintains the `PerClass` lifetime, suppresses the independence warning, and suppresses NB0011 for that class. It does not eliminate the dependence; a significance test over two methods that share an instance still compares samples that are not independent. The attribute simply documents that you are aware of the dependence.

## Lifetime resolution for container-resolved classes

If a `PerClass` class's instances are provided by a factory or service container (via `WithInstanceFactory`, `WithServices`, `WithScopedServices`, or `WithScopedServices`) and the class implements neither `IStateReset` nor `[SharedState]`, the engine resolves the lifetime to **PerMethod**. Each `[Benchmark]` method receives a fresh instance and, under scoped DI, its own `IServiceScope`. The affected results include a warning:

> Class 'OrderBenchmarks' declares InstanceLifetime.PerClass and its instances come from a factory or service container, so one instance - and, under scoped DI, one scope and everything it holds - would be shared by every [Benchmark] method. It is measured with a fresh instance per method instead, because the significance test assumes the methods are independent. Implement IStateReset to keep PerClass and reset between methods, or add [SharedState] to declare that the carry-over is deliberate.

This rule prevents issues such as a scoped `DbContext` sharing a change tracker across methods, which would produce dependent timings and an invalid significance verdict.

**This resolution is independent of where the benchmark is measured.** It applies to isolated workers, in-process runs, `--in-process` runs, and cross-runtime runs. The lifetime is a property of the class, not the process.

### Conditions that maintain `PerClass`

The engine maintains the `PerClass` lifetime if:
- **`IStateReset` is implemented**: The reset contract is in place, and the shared instance is cleaned between methods.
- **`[SharedState]` is used**: The sharing is declared as deliberate.
- **A parameterless constructor is used**: Nothing was injected, so there is no scope to share. The class keeps `PerClass` and receives a runtime warning. `[SharedState]` silences this warning.

## Runtime warnings

Whenever a class runs as `PerClass` with more than one `[Benchmark]` method and has declared neither `IStateReset` nor `[SharedState]`, the engine attaches a warning to every result:

> Class 'OrderBenchmarks' uses InstanceLifetime.PerClass with 2 [Benchmark] methods. Sharing a single instance across methods can cause the second method to observe cached state from the first, violating the statistical-independence assumption of the significance test. To preserve independence: implement IStateReset on the class (the engine will call it between methods), or use InstanceLifetime.PerMethod. If the carry-over is deliberate, say so with [SharedState] - or add BenchmarkWarnings.PerClassIndependence to MeasurementOptions.SuppressedWarnings to silence it for the whole run.

Using `[SharedState]` is preferred over `BenchmarkWarnings.PerClassIndependence` because it documents the intent on the class itself.

## Launches

When `LaunchCount` is greater than 1, each launch builds a **new instance** and re-runs `[GlobalSetup]` on every path. This ensures that the reported standard error and margin of error reflect between-launch reproducibility rather than readings of a single warmed object. Consequently, `IStateReset` is not called at launch boundaries - since there is no carried state - but only in the gaps between methods within a single launch.

## Compile-time diagnostics (NB0011)

Analyzer **NB0011** detects this issue at build time when a `PerClass` class with two or more `[Benchmark]` methods injects something that looks like a scoped service. It offers two code fixes: switch to `PerMethod` or implement `IStateReset`. It also flags empty `ResetAsync` methods.

For more information, see the [analyzers reference](../reference/analyzers.md#nb0011---perclass-lifetime-with-scoped-service).

## See also

For more information, see the following pages:

- [Dependency injection](./dependency-injection.md) - `WithScopedServices`, `WithServices`, and the `PerClass` sharing warning.
- [Analyzers reference](../reference/analyzers.md) - NB0011 (PerClass with scoped service) and NB0013 (PerClass with mutable field).
- [Configuration reference](../reference/configuration.md) - `SuppressedWarnings` and `MeasurementOptions`.
- [Deep dive: Instance lifetime resolution](../deep-dives/instance-lifetime-resolution.md) - How the engine determines how long an instance lives and how that decision is propagated.
