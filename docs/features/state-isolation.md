---
title: State Isolation
description: Keep PerClass benchmark instances clean between methods with IStateReset, and understand the auto-isolation fallback for factory-resolved classes.
order: 8
---

# State isolation across benchmark methods

When a benchmark class uses `[InstanceLifetime(InstanceLifetime.PerClass)]`, a single instance is shared across every `[Benchmark]` method in the class. This is useful when construction is expensive (a database connection, a large in-memory dataset) and you want to amortise that cost across multiple methods. The tradeoff is **state contamination**: method A can leave cached state behind that method B observes, so method B's timings depend on method A running first. That violates the statistical-independence assumption of the significance test and produces false-confidence p-values.

NBenchmark offers two mechanisms to keep PerClass sharing safe: an explicit reset contract (`IStateReset`) and an automatic isolation fallback for classes that do not opt in.

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

### No-op IStateReset

A no-op implementation is valid and declares that the shared state is intentionally carried across methods:

```csharp
public Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
```

This opts the class out of the auto-isolation fallback (below) and silences the auto-upgrade warning. The general PerClass independence warning can still be emitted unless it is explicitly suppressed in `MeasurementOptions`. Use a no-op reset only when the shared state is truly immutable or when cross-method coupling is intentional.

## Auto-isolation fallback

When a PerClass class is resolved via a factory (`WithInstanceFactory`, `WithServiceProvider`, or `WithScopedServiceProvider`) and does **not** implement `IStateReset`, the host automatically upgrades the isolation decision from PerClass to PerBenchmark. Each method runs in its own clean child process, preserving statistical independence at the cost of a process launch per method. The affected results carry a warning:

> Class 'OrderBenchmarks' uses InstanceLifetime.PerClass with a factory-resolved instance and does not implement IStateReset; upgrading to per-benchmark isolated process to preserve statistical independence. Implement IStateReset on the class to allow in-process PerClass execution.

This protects against silent false confidence: without the fallback, a scoped service (e.g. `DbContext`) shared across methods would warm the cache that the next method reads, producing dependent timings and invalid significance results. The fallback trades wall-clock cost for measurement cleanliness.

### Opting out of the fallback

Three ways to keep PerClass in-process execution:

1. **Implement `IStateReset`** - the recommended path. The engine resets state between methods and the class stays in-process.
2. **Add `[InProcess]` to a method** - explicit per-method intent wins. That method stays in-process regardless of the fallback rule.
3. **Pass `--in-process` globally** - the global flag wins over the fallback for every benchmark in the run.

### What does not trigger the fallback

- **PerClass without a factory** (parameterless constructor): the fallback only fires when `_instanceFactory` is set. A parameterless-ctor PerClass class that does not implement `IStateReset` keeps the runtime soft warning and stays PerClass - it is a rare misuse case already flagged by the NB0013 analyzer and `ApplyPerClassIndependenceWarning`.
- **PerMethod** (the default): no shared instance, no contamination risk.
- **`IStateReset` implemented**: the reset contract is in place, so in-process PerClass is safe.

## Runtime warning

Even when the fallback does not fire (e.g. parameterless-ctor PerClass, or `IStateReset` implemented but state still shared), a runtime warning is attached to every result from a PerClass suite with more than one `[Benchmark]` method:

> Class 'OrderBenchmarks' uses InstanceLifetime.PerClass with 2 [Benchmark] methods. Sharing a single instance across methods can cause the second method to observe cached state from the first, violating the statistical-independence assumption of the significance test. To preserve independence: implement IStateReset on the class (the engine will call it between methods), or add [IsolatedProcess] to run each method in a clean process. Set SuppressPerClassIndependenceWarning to true on MeasurementOptions only if sharing is intentional.

Suppress with `SuppressPerClassIndependenceWarning = true` on `MeasurementOptions` when sharing is intentional. The no-op `IStateReset` is usually a better choice because it documents the intent in code rather than in a configuration flag.

## Compile-time diagnostic (NB0011)

The `PerClassWithScopedServiceAnalyzer` (NB0011) flags at compile time when a PerClass class injects a constructor parameter that looks like a scoped service (any non-primitive, non-ambient reference type). The diagnostic is a suppressible warning. A code fix provider offers two fixes:

1. **Use InstanceLifetime.PerMethod** - change the attribute to `[InstanceLifetime(InstanceLifetime.PerMethod)]`, giving each method a fresh instance.
2. **Implement IStateReset** - add `IStateReset` to the class and generate a `ResetAsync` stub (available when the `NBenchmark.Lifecycle.IStateReset` type is resolvable in the compilation).

See the [analyzers reference](../reference/analyzers.md#nb0011---perclass-lifetime-with-scoped-service) for the full NB0011 description and suppression guidance.

## See also

- [Dependency injection](./dependency-injection.md) - `WithScopedServiceProvider`, `WithServiceProvider`, and the PerClass sharing warning.
- [Analyzers reference](../reference/analyzers.md) - NB0011 (PerClass with scoped service) and NB0013 (PerClass with mutable field).
- [Configuration reference](../reference/configuration.md) - `SuppressPerClassIndependenceWarning` and `MeasurementOptions`.
