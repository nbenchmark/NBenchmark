---
title: Instance Lifetime Resolution
description: How long a benchmark instance lives, who decides, and why the answer travels with the run.
order: 3
---

# Instance Lifetime Resolution

The [State isolation](../features/state-isolation.md) page describes the user-facing contract: `IStateReset`, `[SharedState]`, and the automatic fallback for container-resolved classes. This page is the engineering underneath: who decides how long an instance lives, why the decision is made exactly once, and how it travels to the process that measures.

## The two questions

`Engine/InstanceIndependence` answers **how long a benchmark instance lives**, from facts about the class: its declared lifetime, whether it resets itself, whether it carries `[SharedState]`, and whether its instances come from a container. A PerClass class whose instances are container- or factory-resolved and which declares neither resolves to `PerMethod`, with the reason attached to its rows.

This is decided in the same function as **which process measures it**, and the second answer swallows the first: the global in-process guard returns before the lifetime rule can run, so the rule is unreachable for every run that cannot isolate, and its own condition is true only for harnesses with no instance source - meaning it fires exactly where dependence is impossible and never where a container is handing out scoped `DbContext`s. Two questions, two functions: `InstanceIndependence.ResolveLifetime` and `BenchmarkHarness.ResolveGranularity`.

## The answer travels with the run

The resolved lifetime travels on the wire as `RunGroupPayload.InstanceLifetimeOverride`, which beats both the class attribute and `DefaultInstanceLifetime` in the worker. It cannot ride on the default, because the attribute beats a default - a class carrying `[InstanceLifetime(PerClass)]` would go on sharing its instance however the coordinator had resolved it.

The rule is evaluated once, on the coordinator, for the reason `DiscoveredGroupExecutor` is one implementation: a rule evaluated twice can disagree with itself, and an isolated and an in-process number differing by instance lifetime differ for a reason unrelated to the process boundary.

The dependence warning is raised from `DiscoveredGroupExecutor` and the coordinator's in-process path both, so a shared instance says so wherever it was measured - not only on the coordinator's in-process path, which a default Harness run never takes.

## Launches build their own

With `LaunchCount > 1` every launch constructs a new instance and re-runs `[BenchmarkSetup]`, on every path. `LaunchAggregator` derives the reported `StandardError`/`MarginOfError` from the between-launch spread, and launches sharing one object, one scope and one setup are not independent measurements of anything - the field documented a reproducibility figure and carried the opposite. It also settles the reset question at the launch boundary by construction: there is no carried state there for `IStateReset` to be asked to clean.

## Instance sources

`Workers/InstanceSource` is how a measuring process obtains the instances a benchmark class's methods are invoked on. Four kinds: `Constructed` (the type's own constructor - the default, and the only one needing no recipe), `ServiceProvider`, `ScopedServiceProvider`, and `InstanceFactory`. The source carries three things together - the kind, the addressable recipe a worker can follow, and the host-side resolver for the in-process path - and that grouping is the point. The kind and the resolver travel as one unit, so "this is scoped" and "this factory is addressable" are expressible together; without the grouping, a `Func<Type, InstanceHandle>` says "cannot isolate" and a `Func<IServiceProvider>` says the opposite, with nothing tying them together, and every DI API except one specific overload loses the run its isolation regardless of how the factory is written.

The kind travels on the wire because the worker cannot infer it: `ServiceProvider` and `ScopedServiceProvider` are both `Func<IServiceProvider>` and differ only in what the worker does afterwards. Resolving a scoped registration off the root container throws under `ValidateScopes` and, without it, silently shares one instance across every benchmark method - which is exactly the dependence the significance test assumes is absent.

`NBenchmark.Worker/ServiceScopes` creates the per-instance scope by reflecting over `IServiceScopeFactory`, resolved through the **target's** load context rather than the worker's. Neither core NBenchmark nor `nbworker` references `Microsoft.Extensions.DependencyInjection.Abstractions` - that is why the DI integration is a separate package - and referencing it to call one extension method would put it in every worker's graph, including the majority of runs that use no container. The types are certainly reachable from the target: a run only gets here because the user called `WithScopedServiceProvider`.

Host-side resolvers are **deferred**. The coordinator's container is only wanted if this process ends up measuring, and on an isolated run it never does, so building one at configuration time opened a database and constructed an EF model in a process with no benchmark in it.

## See also

- [State isolation](../features/state-isolation.md) - the user-facing contract
- [Dependency injection](../features/dependency-injection.md) - the DI surface this resolution applies to
- [Multiple launches](../features/multiple-launches.md) - why each launch builds its own instance
