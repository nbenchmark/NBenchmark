---
title: Instance Lifetime Resolution
description: How long a benchmark instance lives, who decides, and why the answer travels with the run.
order: 3
---

# Instance lifetime resolution

The [State isolation](../features/state-isolation.md) page describes the user-facing contract: `IStateReset`, `[SharedState]`, and the automatic fallback for container-resolved classes. This page describes the engineering internals: how the engine determines instance lifetime, why that decision is made once, and how it's passed to the measuring process.

## How the engine resolves lifetime

`Engine/InstanceIndependence` determines the benchmark instance lifetime based on several factors: the declared lifetime, whether the class implements `IStateReset`, whether it uses `[SharedState]`, and whether instances are provided by a container. If a class is marked as `PerClass` but its instances are resolved by a container or factory, and it doesn't declare a specific lifetime, the engine resolves it to `PerMethod` and attaches the reason to the results.

The engine determines the lifetime and the measuring process in the same logic path. The global in-process guard returns before the lifetime rule executes, making the rule unreachable for runs that cannot isolate. The rule only triggers for harnesses without an instance source, meaning it executes where dependence is impossible and not when a container provides scoped objects, such as a `DbContext`. This logic is split between `InstanceIndependence.ResolveLifetime` and `BenchmarkHarness.ResolveGranularity`.

## Propagating the lifetime decision

The engine transmits the resolved lifetime as `RunGroupPayload.InstanceLifetimeOverride`. This value takes precedence over both the class attribute and the `DefaultInstanceLifetime` in the worker. Using a default value wouldn't work because a class attribute overrides the default; for example, a class with `[InstanceLifetime(PerClass)]` would continue sharing its instance regardless of how the coordinator resolved it.

The coordinator evaluates the rule once because `DiscoveredGroupExecutor` is a single implementation. Evaluating the rule twice could lead to inconsistent results. If an isolated run and an in-process run differ due to instance lifetime, the difference is unrelated to the process boundary.

Both `DiscoveredGroupExecutor` and the coordinator's in-process path can raise dependence warnings. This ensures that shared instances are flagged regardless of where they were measured, including paths that a default harness run doesn't use.

## Independence across multiple launches

When `LaunchCount` is greater than 1, every launch constructs a new instance and re-runs `[BenchmarkSetup]` on every path. `LaunchAggregator` calculates the `StandardError` or `MarginOfError` based on the spread between launches. Launches that share a single object, scope, or setup are not independent measurements of anything - the field documented a reproducibility figure and carried the opposite. This approach also resolves the state reset question at the launch boundary: because each launch starts fresh, there is no carried state for `IStateReset` to clean.

## Instance sources and providers

`Workers/InstanceSource` determines how a measuring process obtains instances for a benchmark class. The engine supports four types: `Constructed` (the default, using the type's own constructor), `ServiceProvider`, `ScopedServiceProvider`, and `InstanceFactory`. The source groups the type, the addressable recipe for the worker, and the host-side resolver for in-process paths. This grouping allows the engine to express both the scope and the addressability of the factory. Without this, different DI APIs (like `Func<Type, InstanceHandle>` versus `Func<IServiceProvider>`) would provide conflicting isolation signals, and every DI API except one specific overload - `WithServices(Func<IServiceProvider>)` - loses the run its isolation regardless of how the factory is written.

The engine transmits the source type because the worker cannot infer it. Both `ServiceProvider` and `ScopedServiceProvider` use `Func<IServiceProvider>` and differ only in the worker's subsequent actions. Resolving a scoped registration from the root container throws an exception when `ValidateScopes` is enabled. Without this validation, the engine would silently share one instance across every benchmark method, creating the exact dependence that the significance test assumes is absent.

`NBenchmark.Worker/ServiceScopes` creates the per-instance scope by reflecting over `IServiceScopeFactory`. It resolves this through the target's load context instead of the worker's. Core NBenchmark and `nbworker` do not reference `Microsoft.Extensions.DependencyInjection.Abstractions` to avoid adding unnecessary dependencies to the worker graph for runs that don't use a container; this is why DI integration is provided as a separate package. These types are reachable because the user explicitly called `WithScopedServices`.

The engine defers host-side resolvers. The coordinator only needs its container if it performs the measurement. Since isolated runs never do this, building the container at configuration time would unnecessarily open databases or construct EF models in a process that doesn't run any benchmarks.

## See also

For more information, see the following pages:

- [State isolation](../features/state-isolation.md) - The user-facing contract
- [Dependency injection](../features/dependency-injection.md) - The DI surface this resolution applies to
- [Multiple launches](../features/multiple-launches.md) - Why each launch builds its own instance
