---
title: State Isolation
description: Keep PerClass benchmark instances clean between methods with IStateReset, declare deliberate sharing with [SharedState], and understand how the lifetime is resolved for container-resolved classes.
order: 8
---

# State isolation across benchmark methods

When a benchmark class uses `[InstanceLifetime(InstanceLifetime.PerClass)]`, a single instance is shared across every `[Benchmark]` method in the class. This is useful when construction is expensive (a database connection, a large in-memory dataset) and you want to amortise that cost across multiple methods. The tradeoff is **state contamination**: method A can leave cached state behind that method B observes, so method B's timings depend on method A running first. That violates the statistical-independence assumption of the significance test and produces false-confidence p-values.

Three things keep PerClass sharing honest: an explicit reset contract (`IStateReset`), an explicit declaration that the carry-over is deliberate (`[SharedState]`), and - for a class whose instances come from a container - an automatic resolution to a fresh instance per method when neither is present.

## IStateReset - explicit reset between methods

Implement `IStateReset` on the benchmark class to declare how its shared state is cleared between methods. The engine calls `ResetAsync` after one method completes (and after its inter-benchmark GC) and before the next method's warmup starts - N-1 calls for N methods.

```csharp
using NBenchmark.Attributes;
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

The class owns its reset semantics and fans the reset out to whatever it holds - a `DbContext` clears its change tracker, a cache drops its entries, a counter resets to zero. The engine checks `typeof(IStateReset).IsAssignableFrom(suite.Type)` at dispatch time (before instantiation), so the check is a pure reflection fact with no runtime introspection cost.

### A no-op IStateReset is not a way to declare sharing

`IStateReset` means one thing: *I reset between methods, so PerClass is safe*. An empty body claims that and does not do it:

```csharp
// Wrong. NB0011 reports this.
public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
```

The engine can only see that the interface is *present* - it cannot read a method body - so an empty implementation used to silence both the warning and the lifetime resolution while changing nothing about the shared state. If the carry-over is deliberate, say so with `[SharedState]` below, which claims nothing a body could contradict. Analyzer NB0011 reports an empty `ResetAsync` for exactly this reason.

## `[SharedState]` - the carry-over is the point

Some benchmarks are *about* the warm state: measuring the second call into a populated cache is a legitimate thing to want. Declare it:

```csharp
[InstanceLifetime(InstanceLifetime.PerClass)]
[SharedState]
public class CacheBenchmarks
{
    [Benchmark] public int ColdishRead() => _cache.Get("k");
    [Benchmark] public int WarmRead() => _cache.Get("k");
}
```

This keeps the PerClass lifetime, suppresses the independence warning, and suppresses NB0011 for that class. It does not make the dependence go away - a significance test over two methods that share an instance is still comparing samples that are not independent. It says you know.

## Lifetime resolution for container-resolved classes

When a PerClass class's instances come from a factory or a service container - `WithInstanceFactory`, `WithServiceProvider`, `WithScopedServiceProvider`, `UseScopedDependencyInjection` - and the class implements neither `IStateReset` nor `[SharedState]`, the lifetime resolves to **PerMethod**: each `[Benchmark]` method gets a fresh instance and, under scoped DI, its own `IServiceScope`. The affected results carry a warning:

> Class 'OrderBenchmarks' declares InstanceLifetime.PerClass and its instances come from a factory or service container, so one instance - and, under scoped DI, one scope and everything it holds - would be shared by every [Benchmark] method. It is measured with a fresh instance per method instead, because the significance test assumes the methods are independent. Implement IStateReset to keep PerClass and reset between methods, or add [SharedState] to declare that the carry-over is deliberate.

This is the case the rule exists for: a scoped `DbContext` shared across methods warms the change tracker that the next method reads, producing dependent timings and a significance verdict computed on an assumption that does not hold.

**The resolution is independent of where the benchmark is measured.** It applies to an isolated worker, an in-process run, `--in-process`, and a cross-runtime run alike - the lifetime is a fact about the class, not about which process holds it. (It was previously entangled with the isolation decision, which meant `--in-process` and any refusal to isolate skipped it entirely.)

### What keeps PerClass

- **`IStateReset`** - the reset contract is in place, so the shared instance is cleaned between methods.
- **`[SharedState]`** - the sharing is declared deliberate.
- **A parameterless constructor** - nothing was injected, so there is no scope to share. The class keeps PerClass and gets the runtime warning below; `[SharedState]` silences it.

## Runtime warning

Whenever a class actually runs PerClass with more than one `[Benchmark]` method and has declared neither `IStateReset` nor `[SharedState]`, a warning is attached to every result from it - from the isolated worker and the in-process path alike:

> Class 'OrderBenchmarks' uses InstanceLifetime.PerClass with 2 [Benchmark] methods. Sharing a single instance across methods can cause the second method to observe cached state from the first, violating the statistical-independence assumption of the significance test. To preserve independence: implement IStateReset on the class (the engine will call it between methods), or use InstanceLifetime.PerMethod. If the carry-over is deliberate, say so with [SharedState] - or set SuppressPerClassIndependenceWarning on MeasurementOptions to silence it for the whole run.

`[SharedState]` is the better answer than `SuppressPerClassIndependenceWarning`, because it documents the intent on the class rather than turning the warning off for every class in the run.

## Launches

With `LaunchCount > 1` each launch builds a **new instance** and re-runs `[BenchmarkSetup]`, on every path. That is what makes the reported standard error and margin of error a between-launch reproducibility figure rather than three readings of the same warmed object. `IStateReset` is therefore not called at a launch boundary - there is no carried state left to reset - only in the gaps between methods within a launch.

## Compile-time diagnostic (NB0011)

The `PerClassWithScopedServiceAnalyzer` (NB0011) flags at compile time when a PerClass class with two or more `[Benchmark]` methods injects a constructor parameter that looks like a scoped service (any non-primitive, non-ambient reference type). PerClass is recognised from the class attribute *or* from a `WithInstanceLifetime(InstanceLifetime.PerClass)` call elsewhere in the same compilation. The diagnostic is a suppressible warning, and it also fires when `IStateReset` is implemented with an empty body. A code fix provider offers two fixes:

1. **Use InstanceLifetime.PerMethod** - change the attribute to `[InstanceLifetime(InstanceLifetime.PerMethod)]`, giving each method a fresh instance.
2. **Implement IStateReset** - add `IStateReset` to the class and generate a `ResetAsync` stub (available when the `NBenchmark.Lifecycle.IStateReset` type is resolvable in the compilation). The generated body is a `TODO` and a `throw`, not a completed task: a body that compiles away quietly would silence the diagnostic without resetting anything.

See the [analyzers reference](../reference/analyzers.md#nb0011---perclass-lifetime-with-scoped-service) for the full NB0011 description and suppression guidance.

## See also

- [Dependency injection](./dependency-injection.md) - `WithScopedServiceProvider`, `WithServiceProvider`, and the PerClass sharing warning.
- [Analyzers reference](../reference/analyzers.md) - NB0011 (PerClass with scoped service) and NB0013 (PerClass with mutable field).
- [Configuration reference](../reference/configuration.md) - `SuppressPerClassIndependenceWarning` and `MeasurementOptions`.
