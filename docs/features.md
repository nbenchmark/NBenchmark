# state-isolation.md

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


---

# environment-control.md

---
title: Environment control
description: Pin benchmarks to CPU cores, raise process priority, and detect noisy hosts to reduce measurement noise at its source.
order: 8
---

# Environment control

NBenchmark's [outlier trimming](../statistics/outliers.md) and [bimodal warning](../statistics/outliers.md#bimodal-distribution-warning) react to measurement noise after the fact - they discard or flag samples that look like OS interference. **Environment control** is the proactive counterpart: it reduces noise at the source before the timer starts.

Three opt-in host controls are available. All default to off, all are restored when the run completes, and none are required for the zero-ceremony "just run my benchmark" path.

## CPU affinity

Pin the benchmark process to specific logical CPU cores to eliminate inter-core migration noise. When the OS scheduler moves a benchmark thread between cores, the cold L1/L2 cache on the new core inflates a handful of samples; pinning keeps the thread on one core so the cache stays warm.

```csharp
// Suite / Harness fluent API
new BenchmarkSuite("MySuite")
    .WithHardwareAffinity(2, 3)
    .Add(...)
    .RunAsync();

await BenchmarkHarness.Create(args)
    .WithHardwareAffinity(2, 3)
    .RunAsync();
```

```bash
# CLI
dotnet run -- --cpu-affinity 2,3
```

Core indices are zero-based and logical (as reported by the OS). The prior affinity mask is restored when the run completes.

**Choosing cores:** core 0 is often used by the OS for driver interrupt handling on Linux and Windows; avoid it for single-core pinning. A small group away from core 0 (e.g. `2,3` on an 8-core host) is the typical sweet spot for single-threaded benchmarks: it avoids the OS core and gives the scheduler room to honour affinity without starving the benchmark.

**Platform support:** processor affinity is applied on Linux and Windows. On macOS the BCL does not expose the `setaffinity` syscall, so the flag is accepted but skipped with a warning. Pin to a Linux or Windows host for affinity-pinned CI gates.

## Process priority

Request a higher process priority to reduce preemption by unrelated OS work. On a busy host, normal-priority benchmark threads compete with every other process for CPU time; each preemption adds a multi-millisecond stall to a sample that has nothing to do with your code.

```csharp
new BenchmarkSuite("MySuite")
    .WithProcessPriority(ProcessPriorityClass.High)
    .Add(...)
    .RunAsync();
```

```bash
dotnet run -- --priority high
```

`high` is the recommended value for dedicated benchmark hosts. `realtime` can starve the OS and is discouraged.

A refused elevation (common on locked-down CI runners that disallow priority changes) is surfaced as a console warning, not an error - the run still proceeds at whatever priority the host allows. The prior priority is restored when the run completes.

## Dedicated-host guidance

A non-fatal pre-run probe that warns when the host looks like a shared or otherwise noisy benchmark environment. Enable it on CI runners and dev laptops to surface hidden noise sources before you trust a comparison.

```bash
dotnet run -- --dedicated-host-guidance
```

The probe checks for:

- **Low CPU core count** (< 4 logical cores) - typical of shared-tenant CI runners. Inflates noise and makes baseline comparisons unreliable.
- **macOS** - frequency scaling and thermal throttling are not directly observable from managed code. The probe suggests running on wall power and preferring a dedicated Linux or Windows host for CI gates.
- **Priority not raised on a suitable host** (>= 4 cores, no `--priority` set) - the probe actively suggests `--priority high` (or `WithProcessPriority`) to reduce preemption.

The run still proceeds regardless of what the probe finds - this is guidance, not a gate.

```csharp
new BenchmarkSuite("MySuite")
    .WithDedicatedHostGuidance()
    .Add(...)
    .RunAsync();
```

## Build-configuration guidance (always on)

Separate from the three host controls above, NBenchmark emits a one-time warning when:

- The entry assembly is built in `Debug` configuration.
- A debugger is attached.

Those conditions can make timings non-production-representative (for example, reduced inlining/tiering behavior), so the warning is enabled by default in single, suite, and harness modes.

When measuring Debug behavior is intentional, suppress it with either of these knobs:

```csharp
new BenchmarkSuite("MySuite")
    .WithSuppressBuildConfigurationWarning()
    .Add(...)
    .RunAsync();

await BenchmarkHarness.Create(args)
    .WithSuppressBuildConfigurationWarning()
    .RunAsync();
```

```bash
NBENCHMARK_SUPPRESS_DEBUG_WARNING=1 dotnet run -- --filter MyBenchmarks.*
```

## Combining the controls

The three controls are independent and compose. For a dedicated benchmark host running a CI regression gate:

```bash
dotnet run -- --cpu-affinity 2,3 --priority high --dedicated-host-guidance
```

In code:

```csharp
var options = new MeasurementOptions
{
    Environment = new EnvironmentOptions
    {
        CpuAffinity = [2, 3],
        ProcessPriority = ProcessPriorityClass.High,
        DedicatedHostGuidance = true,
    },
};
```

The fluent methods layer on top of each other, so you can chain them:

```csharp
new BenchmarkSuite("MySuite")
    .WithProcessPriority(ProcessPriorityClass.High)
    .WithHardwareAffinity(2, 3)
    .WithDedicatedHostGuidance()
    .Add(...)
    .RunAsync();
```

## Isolated-process propagation

In [Harness mode](../usage-modes/harness-mode.md) the host runs each discovered class in a child process by default. Environment controls are propagated to those children via the isolated-run request, so each child pins itself to the same cores and priority as the parent - the clean-room CLR runs under the same hardware constraints as the parent's in-process benchmarks.

A `[BenchmarkPlan]` suite builds itself inside the worker, so it derives the same `MeasurementOptions` (including `Environment`) there and applies them itself. No extra wiring is needed.

See [Isolated runs](./isolated-runs.md) for the full isolation model.

## What this is not

Environment control reduces noise; it does not eliminate it. The [adaptive measurement loop](../statistics/measurement.md) and [outlier trimming](../statistics/outliers.md) still run and still matter - they handle the residual noise that makes it through even a pinned, elevated process. Think of environment control as raising the floor on measurement quality, not as a replacement for the statistical machinery.

For a discussion of why benchmarking on a noisy host is fundamentally hard, see the [Troubleshooting guide](../troubleshooting.md).

## See also

- [Configuration: Environment](../reference/configuration.md#environment) - the `EnvironmentOptions` record reference
- [CLI Reference](../reference/cli.md) - `--cpu-affinity`, `--priority`, `--dedicated-host-guidance`
- [Measurement](../statistics/measurement.md) - the adaptive loop that runs under these controls
- [Outlier Trimming](../statistics/outliers.md) - the reactive noise handling this complements


---

# multiple-launches.md

---
title: "Multiple launches"
description: Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.
order: 6
---

# Multiple launches

By default each benchmark runs once. Use multiple launches to run each benchmark `N` times as independent launches. Each launch includes its own warmup and GC cycle, so variance across launches reflects real run-to-run differences (process state, ASLR, scheduler placement), not just intra-run noise.

The primary result fields (median, mean, percentiles, etc.) come from the **best** (lowest median) launch. Cross-launch statistics (mean, stddev, median, CI across per-launch medians) are computed and displayed in a "Launch Aggregation" table below the main results when `LaunchCount > 1`.

Use multiple launches when single-run noise is a concern and you want to understand how stable the measurement is at the launch level.

## Suite mode: `WithLaunchCount`

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort(data))
    .Add("array", () => Array.Sort(data))
    .WithBaseline("bubble")
    .WithLaunchCount(5)             // 5 independent launches per benchmark
    .WithIterations(100)
    .WithWarmup(10)
    .RunAsync();
```

## Harness mode: `--launch-count` CLI flag

```bash
dotnet run -- --launch-count 5
```

Or in code via `WithOptions`:

```csharp
BenchmarkHarness.Create(args)
    .WithOptions(new MeasurementOptions { LaunchCount = 5 })
    .RunAsync();
```

## Per-method attribute override

Each `[Benchmark]` can specify its own launch count via the `LaunchCount` property (Harness mode):

```csharp
// 3 independent launches for this method only
[Benchmark(LaunchCount = 3)]
public int NoisyMethod() => Compute();

// Default launch count (1) - no aggregation
[Benchmark]
public int StableMethod() => Compute();
```

The per-method override is overridden by `--launch-count` if both are present. This matters when you want a single method to get extra launches without affecting the rest:

```csharp
public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    // This method runs 5 launches on its own;
    // Baseline and Fast keep the default (1).
    [Benchmark(LaunchCount = 5)]
    public int NoisyWork() => ExpensiveJob();

    [Benchmark]
    public int Fast() => QuickJob();
}
```

## Dry-run interaction

When `--dry-run` (Iterations=0, WarmupIterations=0) is combined with `LaunchCount > 1`, exactly one dry launch is performed. Extra launches would not add information since dry runs skip the body.

## Isolation interaction

In isolated mode (the Harness mode default), the parent spawns N child processes per isolated group. The child process is unaware of the launch count; the parent orchestrates the repeats. Per-method attribute overrides are respected: the parent uses the maximum launch count across all benchmarks in the group so that every benchmark receives at least the launches it requested.

In Suite mode the suite repeats in a fresh worker process per launch. The worker is unaware of the launch count; the coordinator orchestrates the repeats, which is what makes the spread between them a run-to-run reproducibility estimate.

## Example

```bash
# Run each benchmark 3 times and show the launch aggregation table
dotnet run -- --launch-count 3

# With a single benchmark getting extra attention via attribute:
dotnet run -- --filter MyBenchmarks.NoisyWork
```

The "Launch Aggregation" table shows cross-launch mean, standard deviation, median, and 95% confidence interval for each benchmark that ran multiple launches. Only benchmarks with `LaunchCount > 1` appear in this table.

## See also

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Harness mode](../usage-modes/harness-mode.md) - attribute-based discovery and CLI
- [Isolated runs](./isolated-runs.md) - how launches interact with process isolation
- [CLI reference](../reference/cli.md) - all `BenchmarkHarness` flags


---

# dependency-injection.md

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

var services = new ServiceCollection()
    .AddSingleton<IOrderRepository, SqlOrderRepository>()
    .AddTransient<OrderBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHarness.Create(args)
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
| --- | --- |
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

await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .AddFromAssembly<InventoryBenchmarks>()
    .UseScopedDependencyInjection<OrderBenchmarks>(services)
    .RunAsync();
```

## Lifetime and disposal semantics

The DI integration matches how `BenchmarkHarness` manages benchmark instances: **a fresh instance per `[Benchmark]` method**. This is the same lifetime the host uses for plain parameterless classes, so DI users get a one-to-one mapping between methods and instances.

| Method | Instance lifetime | Scope lifetime |
| --- | --- | --- |
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

await BenchmarkHarness.Create(args)
    .AddFromAssembly<OrderBenchmarks>()
    .UseScopedDependencyInjection<OrderBenchmarks>(services)
    .RunAsync();
```

`UseScopedDependencyInjection` is `WithScopedServiceProvider` under the hood. With `PerMethod`, each `[Benchmark]` method gets a fresh `MyDbContext` - no shared state, no cache contamination between methods.

> **Warning: shared state breaks statistical independence.** If you pair `WithScopedServiceProvider` with `[InstanceLifetime(InstanceLifetime.PerClass)]`, all `[Benchmark]` methods in the class share one instance. A scoped service like `DbContext` caches entities and queries in memory, so method A can warm the cache that method B reads. Method B's timings become artificially linked to method A running first, which violates the independence assumption of the Mann-Whitney U test used for significance. The NB0011 analyzer warns on this combination at compile time, but the warning is soft and can scroll past unnoticed in CI. See the [state isolation guide](./state-isolation.md) for the `IStateReset` contract and the auto-isolation fallback that enforce independence at runtime, and the [NB0011 reference](../reference/analyzers.md#nb0011---perclass-lifetime-with-scoped-service) for suppression guidance if sharing state is intentional.

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

The package is built around the `IServiceProvider` interface from the BCL, so any container that exposes one is supported. For Autofac, DryIoc, SimpleInjector, Lamar, etc., build your container, get its `IServiceProvider`, and pass it in:

```csharp
var container = new ContainerBuilder()
    .RegisterType<SqlOrderRepository>().As<IOrderRepository>()
    .Build();

await BenchmarkHarness.Create(args)
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


---

# multi-runtime.md

---
title: "Multi-runtime comparison"
description: Run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare results side-by-side.
order: 5
---

# Multi-runtime comparison

NBenchmark can run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare the results side-by-side. This is available in Suite mode (`WithRuntimes`), Harness mode (`--runtimes` CLI flag), and Harness mode via the `[Runtimes]` attribute.

## Project setup

The project must target all the runtimes you want to compare in its `.csproj` file:

```xml
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
```

## Suite mode: `WithRuntimes`

Pass `RuntimeMoniker` values to `WithRuntimes`:

```csharp
var results = await new BenchmarkSuite("string-concat")
    .Add("concat", () => "a" + "b" + "c")
    .Add("interpolate", () => $"a {"b"} {"c"}")
    .WithBaseline("concat")
    .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)
    .WithWarmup(3)
    .WithIterations(50)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## Harness mode: `--runtimes` CLI flag

Pass the runtimes on the command line. Both short (`net8`) and full (`net8.0`) forms are accepted:

```bash
dotnet run -- --runtimes net8,net9,net10
dotnet run -- --runtimes net8.0,net10.0
dotnet run -- --runtimes net8,net9 --iterations 500 --reporter markdown --output ./results
```

When `--runtimes` is specified, the coordinator builds the project for each target framework via `dotnet build -f <tfm>`, measures the benchmarks in **that build's own worker process**, and aggregates the results. A worker is framework-dependent, so only the net8.0 worker can load a net8.0 build - the build targets already deploy the right one beside each build's assemblies, which makes worker selection a lookup rather than a guess.

## Harness mode: `[Runtimes]` attribute

Instead of passing `--runtimes` on the CLI, you can declare the runtimes on the benchmark class itself:

```csharp
using NBenchmark.Attributes;

[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)]
public class StringBenchmarks
{
    [Benchmark]
    public string Concat() => "a" + "b" + "c";
}
```

```bash
# No --runtimes flag needed - the attribute drives the build
dotnet run --project samples/MultiRuntimeHarness
```

### How `--runtimes` and `[Runtimes]` interact

When `--runtimes` is passed on the CLI, the CLI list wins and `[Runtimes]` is ignored. When multiple classes declare `[Runtimes]`, the host uses the union of all declared lists (preserving declaration order, deduplicating). A class filtered out by `--filter` does not contribute its runtimes.

| `--runtimes` flag | `[Runtimes]` attribute | Runtimes used |
|-------------------|------------------------|---------------|
| absent            | absent                 | none (single-runtime) |
| absent            | present on >= 1 class  | union of all declared lists |
| present           | absent or present      | CLI list; attribute ignored |

## How it works

`WithRuntimes` and `--runtimes` always isolate: each runtime is measured in a freshly spawned worker, so JIT, GC and thread-pool state from one runtime cannot bias another. `--runtimes` overrides `--in-process`, because a comparison across runtimes measured in one process would not be a comparison across runtimes at all.

In **Suite mode**, multi-runtime needs a `[BenchmarkPlan]` factory rather than an inline suite:

```csharp
await BenchmarkSuite.RunPlanAsync(BuildSuite);

[BenchmarkPlan]
static BenchmarkSuite BuildSuite() =>
    new BenchmarkSuite("comparison")
        .Add("concat", () => Concat())
        .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net10);
```

Measuring another target framework means measuring a *different build* of your code, and an inline suite's bodies are located by metadata token - a number that only means anything inside the build that produced it. A factory is found by name, which is stable across builds, so each runtime's worker constructs the suite from that runtime's own assemblies. An inline suite with `WithRuntimes` says so rather than measuring the wrong thing.

Harness mode needs no change: it already addresses benchmark classes by name.

The console and markdown reporters add a "Runtime" column when results span multiple runtimes. Significance testing is performed within each runtime (net8 results are compared against the net8 baseline, not the net10 one). The first runtime in the list is the implicit baseline for ratio calculations.

## Samples

- [MultiRuntimeSuite sample](../samples.md#multiruntimesuite---suite-mode-multi-runtime) - Suite mode multi-runtime
- [MultiRuntimeHost sample](../samples.md#multiruntimehost---harness-mode-multi-runtime) - Harness mode multi-runtime

## See also

- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Harness mode](../usage-modes/harness-mode.md) - attribute-based discovery and CLI
- [Isolated runs](./isolated-runs.md) - the underlying process isolation model
- [CLI reference](../reference/cli.md) - all `BenchmarkHarness` flags


---

# isolated-runs.md

---
title: "Isolated Runs (Advanced)"
description: Run benchmarks in clean child processes to avoid runtime cross-contamination.
order: 4
---

# Isolated Runs (Advanced)

Process isolation runs benchmarks in a freshly spawned child process so their measurements are not biased by runtime state - JIT warmup, heap and GC pressure, or thread-pool and process-level state - left behind by earlier work in the same process.

How you reach for isolation depends on the mode:

| Mode | Isolation | Granularity |
| --- | --- | --- |
| **Single** (`Benchmark.Run` / `RunAsync`) | **On by default** | One worker per call |
| **Suite** (`BenchmarkSuite`) | **On by default** | The whole suite runs in one worker |
| **Harness** (`BenchmarkHarness`) | **On by default** | Per class, with per-benchmark and opt-out controls |

Isolation is useful when you want to reduce contamination from:

- prior JIT warmup from earlier benchmarks
- heap and GC pressure left by unrelated work
- thread-pool and process-level runtime state

## Single mode

Single mode isolates by default. The call signature is unchanged - including the synchronous return - and the body is measured in a worker started under the configured runtime profile:

```csharp
using NBenchmark;

// Measured in a worker, under the steady-state runtime profile.
var result = Benchmark.Run(() => Fibonacci(20), name: "fib");
```

This matters more here than anywhere else, because a lambda measured in whatever process happens to be running inherits that process's JIT tiering. The same body, measured both ways in the same program:

| round | isolated | in-process | ratio |
| --- | --- | --- | --- |
| 0 | 329 ns | 7,009 ns | 21.3x |
| 1 | 320 ns | 6,733 ns | 21.0x |
| 2 | 322 ns | 329 ns | 1.0x |

The in-process column is not noisy - it is *wrong*, by a factor of 21, until the JIT happens to promote the body to tier 1 on the third attempt. Nothing in the reported confidence interval hints at that. The isolated column is the same number every time.

### When the body cannot be isolated

A body that **captures state from its enclosing scope** cannot be measured in a worker, because the captured values live only in this process:

```csharp
var iterations = 1000;

// Captures `iterations`, so this is measured in-process and labelled.
var result = Benchmark.Run(() => Work(iterations));
```

NBenchmark measures it here, prints the reason once, and stamps `IsolationStatus.InProcessCapturedState` on the result. It never reconstructs the captured state: doing so was tried, and it did not fail loudly - it returned a plausible number for the wrong value. To isolate a body like this, remove the capture (use a constant, or move the state into a benchmark class field).

The analyzer package reports this at compile time as [NB0014](../reference/analyzers.md#nb0014---capturing-body-cannot-be-isolated), naming the symbols captured - which is more precise than the runtime can be, since by then they are fields on a compiler-generated class. It is informational rather than a warning, because capturing is the idiomatic way to benchmark over prepared data.

A few shapes are worth knowing because they do not read the way they lower:

| Body | Isolated? | Why |
| --- | --- | --- |
| `() => 43` | yes | Nothing to carry. Roslyn still emits it as an instance method on a cached singleton, so a `Target is null` test would get this wrong. |
| `static () => 43` | yes | Same as above - `static` documents the intent, it does not change the lowering. |
| `() => Work(local)` | no | Captures `local`. |
| `() => Work(_field)` | no | Captures `this` - naming an instance member without a receiver carries the whole object. |
| `() => Work(StaticField)` | yes | A static needs no receiver. |
| `widget.Compute` | no | A method group over a live object; the receiver is state this process owns. |
| `() => 43` beside `() => local` | yes | A non-capturing lambda keeps its isolation even when a sibling in the same scope captures. |

### Measuring this process on purpose

`RunInProcess` is not a fallback - it is the correct choice when the current process *is* the subject: cold-start and first-call cost, or a body that must observe host state such as a warm cache or an open connection. It measures here silently, with no warning, and stamps `IsolationStatus.InProcessRequested`:

```csharp
// Deliberate: measuring first-call cost, where disabling tiering would measure the wrong thing.
var cold = Benchmark.RunInProcess(() => ColdStartSensitivePath(), name: "cold path");
```

`Benchmark.Warmup()` optionally starts a worker in the background so the first measured call does not pay the roughly 70 ms launch.

## Suite mode

An ordinary suite isolates with no ceremony. Nothing about how you write it changes:

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort())
    .Add("array", () => ArraySort())
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

Each body is a non-capturing lambda, so NBenchmark addresses the compiled method behind each one and measures the whole suite in a worker. `WithIsolation(false)` opts back into the host process, deliberately and without a warning.

### Why one worker for the whole suite

All of a suite's benchmarks share a single worker, so every ratio between them is a **paired, within-process** comparison - the worker's CPU frequency, thermal state and address-space layout cancel out of the ratio rather than adding to it.

Measuring each benchmark in its own process sounds safer, but the measurements say otherwise. On four benchmarks of provably identical cost:

| Configuration | Spread across runs | Largest fabricated difference |
| --- | --- | --- |
| in-process | 3.27x | 2.80x |
| **isolated per child, host runtime configuration** | **3.10x** | **3.06x** |
| isolated, `steady-state` runtime configuration | 1.03x | 1.03x |

Per-benchmark isolation buys the middle row. Sibling contamination was never the dominant error - uncontrolled JIT tiering was, and that is a *per-process* setting which is identical whether one worker runs one benchmark or five. Splitting them would convert every ratio into a between-process contrast, inflating its variance for no accuracy gain.

Residual order effects are handled instead by randomizing run order per replicate (`WithRunOrder(RunOrder.Random)`, reproducible via `WithSeed`), and `WithLaunchCount(n)` measures the suite in *n* separate workers to estimate run-to-run reproducibility.

If a specific benchmark genuinely pollutes its siblings - one that permanently fills a static cache, say - put it in its own suite. Harness mode's `[IsolatedProcess]` gives per-benchmark isolation when you need it named at the benchmark level.

### When a suite cannot be isolated on its own

Some suites hold things a worker cannot be handed and must instead **build for itself**. NBenchmark says which benchmark and why, then measures in the host process and labels the results:

- a body that **captures a local** - the captured value exists only in your process
- **suite setup/teardown**, or per-iteration setup/teardown - live delegates that would otherwise run on the wrong side of the boundary, preparing state the benchmarks never see
- **parameterized** benchmarks, which close over their parameter values
- a custom `IOutlierDetector` / `ISignificanceTest` that cannot be rebuilt from a type name

For those, move the suite into a static factory and hand the method group to `RunPlanAsync`. The worker invokes *your factory* in its own process, so all of that is constructed there rather than described to it - nothing has to be serializable:

```csharp
using NBenchmark.Attributes;

await BenchmarkSuite.RunPlanAsync(BuildSuite);

[BenchmarkPlan]
static BenchmarkSuite BuildSuite()
{
    var payload = new byte[4096];

    return new BenchmarkSuite("hashing")
        .Add("hash", () => Hash(payload))
        .WithSuiteSetup(() => Random.Shared.NextBytes(payload));
}
```

The factory must be `static` and capture nothing itself, so a worker can locate it by metadata token. `RunPlansAsync(typeof(Plans))` runs every `[BenchmarkPlan]` on a type, each in its own worker. A method marked `[BenchmarkPlan]` but shaped wrongly throws rather than being skipped - a silently skipped suite gives its author nothing to go on.

## Harness mode

Harness mode is **isolated by default**: each benchmark class runs in its own clean child process. You usually don't configure anything - `BenchmarkHarness.Create(args)...RunAsync()` already isolates per class.

```csharp
using NBenchmark.Attributes;

public sealed class StartupBenchmarks
{
    [Benchmark]
    public int ColdPath() => RunColdSensitiveWork();   // isolated per class by default
}
```

You can tune the granularity:

- **`[IsolatedProcess]`** on a method (finest granularity) gives that one benchmark its own dedicated child process, isolated even from siblings in the same class.
- **`[InProcess]`** on a method (or class) opts that benchmark back into the host process.
- **`--in-process`** on the command line, or **`WithIsolation(false)`** in code, disables isolation for the whole run.

```csharp
public sealed class MixedBenchmarks
{
    [Benchmark]
    public int Default() => Work();              // shares one per-class child

    [Benchmark]
    [IsolatedProcess]
    public int OwnProcess() => ColdWork();        // its own dedicated child

    [Benchmark]
    [InProcess]
    public int InHost() => HostObservableWork();  // runs in the host process
}
```

When isolation resolves to a mix, NBenchmark runs the in-process benchmarks in the host, the per-class benchmarks together in one child, and each `[IsolatedProcess]` benchmark in its own child.

See [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) for the full attribute reference.

### How the child works

Harness mode measures in a dedicated worker process, `nbworker`, which ships inside the NBenchmark package and is copied next to your application at build time. The coordinator - the process you started - plans the work, aggregates statistics and renders reports, but never measures.

A worker loads the assembly declaring your benchmarks into its own load context, runs the same attribute discovery the host would, measures with the same engine, and streams results back over a private pipe. Three consequences are worth knowing:

- **Your `Main` does not re-run.** Earlier versions re-executed the entry assembly for every child, so a program with *M* isolated suites did *M²* work and any side effect in `Main` - a file write, an HTTP call, database seeding - happened once per child. A worker is given an assembly and a class name instead.
- **Progress is live.** Warmup and measurement phases stream from the worker into your own `IBenchmarkProgress` and `IMeasurementObserver` as they happen. Per-*sample* observer events stop at the process boundary, because forwarding thousands of them would add measurable time to the run; the full raw samples still arrive with each result.
- **Results and their samples arrive together.** There is no side table to look them up in, which is what previously allowed every isolated result to lose its samples and silently disable significance testing.

If the worker is missing - an incomplete restore, or `NBenchmarkDeployWorker=false` - benchmarks are measured in the host process, the reason is printed, and the results are stamped `host`. Set `NBENCHMARK_WORKER_PATH` to point at a specific `nbworker.dll` if you need to override discovery.

### What cannot be isolated

A worker does not re-run your entry point, so anything NBenchmark holds as *live code in the coordinator* has no counterpart there. These fall back to in-process measurement, with the reason printed and the results stamped `host`:

- **Instance factories and service providers** (`WithInstanceFactory`, `WithServiceProvider`). A worker can construct a type, but it cannot reproduce a factory that exists only in your process. Building the type directly instead would measure a differently-configured object while reporting it as though nothing had changed.
- **Benchmarks declared in an assembly with no file on disk** - a single-file or in-memory build.
- **Custom `IOutlierDetector` / `ISignificanceTest` instances that cannot be rebuilt from a type name** - one constructed with arguments, for example. Strategies with a parameterless constructor travel fine.

The rule throughout is to refuse rather than guess. Reconstructing captured state was tried and did not fail loudly: it returned plausible, *wrong* numbers - a body over a captured `5` measured as though it were `1`, with no error and a tight confidence interval.

## Checking isolation rather than trusting it

Two flags make the claim verifiable on your own code:

- **`--strict-isolation`** fails the run if any benchmark was measured in the host process, naming each one and its remedy. Use it wherever a pipeline gates on benchmark numbers: a benchmark that quietly fell back - a build agent without the worker deployed, or a body that captures state - cannot be compared against a baseline measured under a different runtime configuration.
- **`--verify-isolation`** measures everything a second time in the host process and prints the per-benchmark difference, so you can see what your own numbers would have been. It reports a ratio per benchmark rather than an aggregate, because the finding is that host measurement is *unpredictable* rather than uniformly wrong.

See [CLI reference](../reference/cli.md#--strict-isolation) for both.

## Why isolation actually matters

The intuitive case for isolation is that a benchmark should not inherit JIT, GC or thread-pool state left behind by its siblings. That is true, but it is not the main reason, and measuring it shows why.

Four benchmarks with provably identical cost, measured repeatedly:

| Configuration | Spread across runs | Largest fabricated difference |
| --- | --- | --- |
| in-process | 3.27x | 2.80x |
| isolated, host runtime configuration | 3.10x | 3.06x |
| **isolated, `steady-state` runtime configuration** | **1.03x** | **1.03x** |

Isolation on its own barely helped. What fixed it was disabling tiered compilation - and **that can only be done to a process that has not started yet**, because the runtime reads the setting once at startup and never again.

So the process boundary is not the remedy. It is the *delivery mechanism* for the [runtime profile](../reference/cli.md#--runtime-profile-profile), which is the remedy. Isolation without it produces numbers that are reproducible and wrong, reported with a tight confidence interval - which is worse than being obviously noisy, because it invites trust.

This is also why an in-process benchmark can never be as trustworthy as an isolated one, no matter how many samples it collects: it is stuck with whatever configuration its host process was started with. NBenchmark reports that rather than hiding it - in-process results are stamped `host`, and results measured under different runtime configurations are never compared against each other.

## Important behavior notes

- Isolation adds overhead: one worker launch per group. A worker costs roughly 70 ms to start and complete its handshake. Against the per-benchmark wall-clock floor of about 600 ms (`MinWarmupTime` plus `MinMeasurementTime`), either is a small tax for a comparison group of any size.
- **Do not rely on `--in-process` for anything comparative.** On four benchmarks with provably identical cost, repeated in-process runs spanned 3.27x and fabricated a 2.80x difference between two of them, while reporting a tight confidence interval on each. The same benchmarks measured in workers under the default runtime profile spanned 1.03x. See `plans/out-of-process-pivot.md` for the measurements and the reason.
- **`LaunchCount` is a replicate count, and each replicate is a fresh process.** In Harness mode, `--launch-count 3` measures the group in three separate workers, each with its own shuffle order derived from the session seed. That is what gives a run-to-run reproducibility estimate rather than three repetitions inside one process - and reproducibility, not within-process precision, is what a regression gate should read.
- Run-order randomization is honoured everywhere, and each replicate derives a distinct order from the session seed, so run order is a randomized nuisance factor rather than a fixed confound.
- `--dry-run` (equivalent to `--iterations 0 --warmup 0`) always runs in-process - no child is spawned.
- `RunPlanAsync` suites need no configuration transfer at all: the worker runs your factory, so custom detectors, significance tests and lifecycle delegates are constructed there rather than described to it. Harness workers receive the resolved configuration directly and rebuild custom strategies from their type names.
- A child that never returns is killed, along with its whole process tree, once it exceeds a wall-clock ceiling derived from the tuning budget (`MaxTuningTime` and `CapGraceFactor`, plus warmup and process-start allowances). The affected benchmarks are reported as errored, naming the timeout, rather than hanging the run. Raise `--max-tuning-time` if the work is genuinely that slow.
- A worker cannot outlive the run that started it. It blocks reading its inbound pipe, so if the coordinator exits for any reason - a clean finish, a Ctrl-C, a crash, an IDE stop button - the read ends and the worker exits on its own, measured at 7 ms. Nothing supervises it, which matters because the supervisor would be the process most likely to have died.

## Related

- See [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) for `[IsolatedProcess]` and `[InProcess]` on attribute-discovered benchmarks.
- See [Suite mode](../usage-modes/suite-mode.md) for the full `BenchmarkSuite` fluent API.
- See [Samples](../samples.md) for a runnable isolated-runs sample project.


---

# index.md

---
title: Features
description: Advanced cross-cutting NBenchmark capabilities - parameterized benchmarks, categories, isolated runs, multi-runtime comparison, multiple launches, and dependency injection.
order: 3
---

# Features

These pages cover advanced capabilities that apply across the usage modes. They are opt-in features for experienced benchmarkers who need finer control over measurement, filtering, isolation, or runtime environments.

## [Categories](./categories.md)

Tag benchmarks with `[BenchmarkCategory]` and include or exclude groups from a run via CLI flags or the programmatic `WithCategoryFilter` API.

## [Parameterized benchmarks: Suite mode](./parameterized-suite.md)

Run a benchmark body across multiple input values using `WithParameter` and typed `Add` lambdas. Each parameter combination produces a separate benchmark entry.

## [Parameterized benchmarks: Harness mode](./parameterized-harness.md)

Run a benchmark body across multiple input values using the `[BenchmarkCase]` and `[BenchmarkCases]` attributes. Includes a comparison with the suite-mode API.

## [Isolated runs](./isolated-runs.md)

Every mode measures in a clean worker process by default, because JIT tiering and GC flavour are fixed at process start and can only be chosen for a process that has not begun. `WithIsolation(false)` opts a suite back into the host process.

## [Multi-runtime comparison](./multi-runtime.md)

Run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare results side-by-side. Available in Suite mode (`WithRuntimes`), Harness mode (`--runtimes` CLI flag), and Harness mode via the `[Runtimes]` attribute.

## [Multiple launches](./multiple-launches.md)

Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.

## [Environment control](./environment-control.md)

Pin benchmarks to CPU cores, raise process priority, and detect noisy hosts to reduce measurement noise at its source. Opt-in controls that complement the statistical noise handling.

## [Dependency injection](./dependency-injection.md)

Use `Microsoft.Extensions.DependencyInjection` (or any container that exposes an `IServiceProvider`) to give benchmark classes constructor dependencies. Harness mode only.

## [State isolation](./state-isolation.md)

Keep `InstanceLifetime.PerClass` statistically valid with `IStateReset` or automatic per-benchmark isolation fallback when shared state would contaminate timing.

## See also

- [Guides](../guides/) - workflow-first recipes that combine these features to solve real benchmarking tasks (ASP.NET services, CI/CD tuning, refactors, parameter sweeps, cross-runtime, test-suite gates, custom statistics)
- [Usage modes](../usage-modes/) - the four ways to run benchmarks
- [Output](../output/index.md) - reporters and output control
- [Configuration](../reference/configuration.md) - configuration and CLI flags


---

# parameterized-suite.md

---
title: "Parameterized benchmarks: Suite mode"
description: Run a benchmark body across multiple input values using WithParameter and typed Add lambdas in BenchmarkSuite.
order: 2
---

# Parameterized benchmarks: Suite mode

Parameterized benchmarks run the same method body across multiple input values, producing one benchmark entry per parameter combination. This is useful for comparing algorithms at different scales, testing multiple configurations, or sweeping a parameter space.

In Suite mode, parameterized benchmarks use `WithParameter` plus a typed `Add` lambda.

## `WithParameter`

Pass a name and values to `WithParameter`, then use a typed lambda in `Add`:

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("sorting")
    .WithParameter("size", 10, 100, 1000)
    .Add("sort", (int size) =>
    {
        var arr = Enumerable.Range(0, size).Reverse().ToArray();
        Array.Sort(arr);
    })
    .WithRunOrder(RunOrder.Declaration)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Produces three benchmarks:
//   sort(size=10)
//   sort(size=100)
//   sort(size=1000)
```

The `Add` lambda accepts one parameter whose type matches the `WithParameter` type argument. Return a value to prevent dead-code elimination:

```csharp
suite.Add("hash", (int size) => ComputeHash(size));
```

## Multiple parameters

Call `WithParameter` once per parameter. The suite generates the Cartesian product of all values:

```csharp
var results = await new BenchmarkSuite("matrix")
    .WithParameter("rows", 10, 100)
    .WithParameter("cols", 5, 50)
    .Add("allocate", (int rows, int cols) => new int[rows, cols])
    .WithRunOrder(RunOrder.Declaration)
    .RunAsync();

// Produces four benchmarks:
//   allocate(rows=10, cols=5)
//   allocate(rows=10, cols=50)
//   allocate(rows=100, cols=5)
//   allocate(rows=100, cols=50)
```

Multi-parameter `Add` overloads accept up to three lambda parameters:

```csharp
suite.Add("work", (int a, int b) => a + b);
suite.Add("work", (int a, int b, int c) => a + b + c);
```

Async and value-returning overloads follow the same pattern as non-parameterized benchmarks:

```csharp
suite.Add("async", async (int size) => await FetchAsync(size));
suite.Add("compute", (int size) => ComputeHash(size));
```

Per-benchmark setup and teardown are also supported on parameterized overloads:

```csharp
suite.Add("db", (int poolSize) => QueryDb(poolSize),
    setup: () => OpenConnection(),
    teardown: () => CloseConnection());
```

## Mixed parameterized and non-parameterized benchmarks

A suite can contain both plain `Add` calls and parameterized `Add` calls. Plain benchmarks run once; parameterized benchmarks expand per parameter combination. All benchmarks share the same `MeasurementOptions`:

```csharp
var results = await new BenchmarkSuite("mixed")
    .Add("plain", () => DoWork())
    .WithParameter("size", 10, 100)
    .Add("param", (int size) => DoWork(size))
    .WithRunOrder(RunOrder.Declaration)
    .RunAsync();

// Produces three benchmarks:
//   plain
//   param(size=10)
//   param(size=100)
```

## Supported parameter types

`WithParameter` accepts primitives, enums, strings, and `null`:

```csharp
suite.WithParameter("value", (string?)null, "hello");
suite.WithParameter("mode", FileMode.Read, FileMode.Write);
suite.WithParameter("count", 1, 10, 100);
```

The following types are supported: `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `char`, `string`, and any `enum`. Passing an unsupported type (e.g. a custom class) throws `ArgumentException`.

## Baselines with parameters

`WithBaseline` uses the **original** benchmark name (before expansion), and the baseline flag applies to every expanded variant:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("linear", (int size) => LinearSearch(size))
    .Add("binary", (int size) => BinarySearch(size))
    .WithBaseline("linear")
    .RunAsync();

// "linear(size=10)" and "linear(size=100)" are both baselines.
// Significance is computed separately within each parameter group.
```

## Significance with parameters

Significance testing groups results by parameter set. Each group is compared independently, so results for `size=10` are only compared against other `size=10` benchmarks, not against `size=100` benchmarks. Non-parameterized benchmarks in the same suite form a single group.

This means a parameterized suite with `N` parameter combinations and `M` benchmark methods produces `N` separate significance comparisons, each over `M` benchmarks - rather than one flat comparison over `N * M` results.

## Categories with parameters

Use `categories` on parameterized `Add` overloads, then filter with `WithCategoryFilter`:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("linear", (int size) => LinearSearch(size), categories: ["Brute"])
    .Add("binary", (int size) => BinarySearch(size), categories: ["Smart"])
    .WithCategoryFilter(include: ["Smart"])
    .RunAsync();

// Only runs "binary(size=10)" and "binary(size=100)"
```

Categories on parameterized benchmarks work identically to categories on non-parameterized benchmarks. See [Categories](./categories.md) for the full filtering model.

## Unique names after expansion

Each expanded name must be unique. Duplicate parameter values produce duplicate names and throw `ArgumentException` at run time:

```csharp
// This throws ArgumentException: "sort(size=10)" appears twice
suite.WithParameter("size", 10, 10)
     .Add("sort", (int size) => Sort(size));
```

## Run order with parameters

When `RunOrder.Random` is used with parameterized benchmarks, the suite shuffles benchmarks **within each parameter group** while keeping groups together. This ensures that results for the same parameter set stay comparable. Non-parameterized benchmarks are shuffled independently by `SuiteRunner` as usual. `RunOrder.Declaration` preserves the expansion order.

```csharp
// Random order shuffles within each parameter group:
await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("linear", (int size) => LinearSearch(size))
    .Add("binary", (int size) => BinarySearch(size))
    .WithRunOrder(RunOrder.Random)  // shuffles within each size group
    .RunAsync();
```

## Process isolation

Isolated runs always execute in declaration order, regardless of `WithRunOrder`. See [Isolated Runs](./isolated-runs.md) for the full model.

## Reading the report

Console and Markdown reporters consolidate a parameterized benchmark into a **single comparison table** - one table for the whole suite. Each parameter becomes its own column, and the `Benchmark` column shows the base method name without its parameter suffix:

```text
search benchmarks
Benchmark | size | Median   | Mean     | Ops/s      | Ratio             | Sig | Mag   | Alloc/op
----------+------+----------+----------+------------+-------------------+-----+-------+---------
binary    |   10 |  90.0 ns |  91.2 ns | 11,111,111 | ████ baseline    |  -  |  -    |    32 B
linear    |   10 | 108.0 ns | 109.4 ns |  9,259,259 | █████ 1.20x      |  ✓  | large |    24 B
binary    |  100 | 250.0 ns | 252.1 ns |  4,000,000 | ███████ baseline  |  -  |  -    |    32 B
linear    |  100 | 300.0 ns | 305.7 ns |  3,333,333 | █████████ 1.20x   |  ✓  | large |    24 B
```

Rows are grouped by parameter set in expansion order and sorted by median within each group. To leave room for the parameter columns, parametric tables use the compact labels `Ratio`, `Sig` and `Mag`. When a parameter group holds competing benchmarks, the baseline, ratio, significance (`Sig`) and effect magnitude are computed independently **per parameter group**, so every comparison stays within a single parameter combination.

When a single method is swept across parameter values, every parameter group holds just one benchmark, so there is no within-group comparison. The table instead ranks every row against its fastest point: the `Ratio` column reports each point's scaling factor (the fastest point is the `baseline`), while `Sig` and `Mag` stay `-`, because the engine does not test different workloads against one another. This makes scaling trends easy to read:

```text
LinearSearch benchmarks
Benchmark    | count | Median   | Mean     | Ops/s      | Ratio               | Sig | Mag | Alloc/op
-------------+-------+----------+----------+------------+---------------------+-----+-----+---------
LinearSearch |    10 |  31.2 ns |  32.7 ns | 30,567,164 | █ baseline          |  -  |  -  |     24 B
LinearSearch |   100 | 117.2 ns | 132.2 ns |  7,565,906 | ███ 3.76x           |  -  |  -  |     24 B
LinearSearch |  1000 |  1.40 µs |  1.27 µs |    789,515 | ████████████ 44.87x |  -  |  -  |  4,048 B
```

In a mixed suite, a non-parameterized benchmark shows `-` in every parameter column; when no parameter group has a within-group comparison, it joins the table-wide ranking against the fastest row.

CSV and JSON reporters keep one record per result, each carrying its full `ParameterSet`, for machine consumption.

## Accessing results

Each expanded benchmark produces its own `BenchmarkResult` with the `ParameterSet` property set:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100)
    .Add("binary", (int size) => BinarySearch(size))
    .RunAsync();

foreach (var r in results)
{
    Console.WriteLine($"{r.Name}: {r.Median:F0} ns");
    // r.ParameterSet[0].Name  -> "size"
    // r.ParameterSet[0].Value -> 10 or 100
}
```

## Next steps

- [Parameterized benchmarks: Harness mode](./parameterized-harness.md) - the `[BenchmarkCase]` / `[BenchmarkCases]` attribute API
- [Suite mode](../usage-modes/suite-mode.md) - the full fluent API
- [Categories](./categories.md) - tag and filter benchmarks
- [Configuration](../reference/configuration.md) - all measurement options


---

# categories.md

---
title: Categories
description: Tag and filter benchmarks by category.
order: 1
---

# Categories

NBenchmark supports tagging benchmarks with categories and then including or excluding them from a run. This is useful for grouping benchmarks by subsystem, speed, or CI tier.

## Tagging benchmarks

Use `[BenchmarkCategory]` on methods, classes, or both. The attribute is repeatable.

```csharp
using NBenchmark.Attributes;

[BenchmarkCategory("String")]
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Fast")]
    public string Concat() => "hello" + " " + "world";

    [Benchmark]
    [BenchmarkCategory("Fast")]
    public string Interpolate() => $"hello {"world"}";

    [Benchmark]
    [BenchmarkCategory("Slow")]
    public string ManyConcat()
    {
        var s = "";
        for (var i = 0; i < 100; i++)
            s += (char)('a' + i % 26);
        return s;
    }
}
```

Class-level categories are unioned with method-level categories, so `ManyConcat` is tagged with both `String` and `Slow`. Inherited class-level categories are also applied to derived classes.

## CLI filtering

| Flag | Description |
| --- | --- |
| `--category <name>` | Include benchmarks tagged with this category. Repeatable (OR). |
| `--exclude-category <name>` | Exclude benchmarks tagged with this category. Repeatable (OR). |

```bash
# Run all String benchmarks
dotnet run -- --category String

# Run fast string benchmarks only
dotnet run -- --category String --exclude-category Slow

# Run benchmarks tagged String OR Memory
dotnet run -- --category String --category Memory

# Combine with the glob filter
dotnet run -- --category String --filter StringBenchmarks.Con*
```

Untagged benchmarks are excluded when any `--category` flag is present.

## Programmatic filtering

In Harness mode, use `WithCategoryFilter`:

```csharp
await BenchmarkHarness.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithCategoryFilter(include: ["String"], exclude: ["Slow"])
    .RunAsync();
```

In Suite mode, use `WithCategories` and `WithCategoryFilter`:

```csharp
var results = await new BenchmarkSuite("string")
    .Add("concat", () => "a" + "b", categories: ["Fast"])
    .Add("interpolate", () => $"a { "b" }", categories: ["Fast"])
    .Add("manyConcat", () => string.Concat(Enumerable.Range(0, 100)))
    .WithCategoryFilter(include: ["Fast"])
    .RunAsync();
```

`WithCategoryFilter` composes with CLI flags: each include source must match independently, while exclude lists are unioned. This lets you set a default include list in code and still narrow it from the command line.

## Categories in reports

- **JSON** always emits a `categories` array on every `BenchmarkResult`.
- **Markdown**, **CSV**, and **Console** reporters show a `Categories` column in **advanced** detail only.
- `--list` prints categories next to each benchmark when any are present.

```bash
dotnet run -- --list
dotnet run -- --reporter markdown --detail advanced --output ./results
```


---

# parameterized-harness.md

---
title: "Parameterized benchmarks: Harness mode"
description: Run a benchmark body across multiple input values using BenchmarkCase and BenchmarkCases attributes in BenchmarkHarness.
order: 3
---

# Parameterized benchmarks: Harness mode

Parameterized benchmarks run the same method body across multiple input values, producing one benchmark entry per parameter combination. This is useful for comparing algorithms at different scales, testing multiple configurations, or sweeping a parameter space.

In Harness mode, parameterized benchmarks use the `[BenchmarkCase]` and `[BenchmarkCases]` attributes. The method must accept parameters matching the argument types.

## `[BenchmarkCase]` - inline literal cases

Apply the attribute multiple times, once per argument set:

```csharp
using NBenchmark.Attributes;

public class SortingBenchmarks
{
    [BenchmarkCase(10)]
    [BenchmarkCase(1_000)]
    [BenchmarkCase(100_000)]
    [Benchmark]
    public void Sort(int n)
    {
        var arr = Enumerable.Range(0, n).Reverse().ToArray();
        Array.Sort(arr);
    }
}
```

Each case becomes a separate benchmark entry named `Sort(n=10)`, `Sort(n=1000)`, `Sort(n=100000)`. Multi-parameter methods use method-parameter names in the display name:

```csharp
[BenchmarkCase(100, "asc")]
[BenchmarkCase(100, "desc")]
[BenchmarkCase(10_000, "asc")]
[BenchmarkCase(10_000, "desc")]
[Benchmark]
public void Sort(int count, string order)
{
    var data = order == "desc"
        ? Enumerable.Range(0, count).Reverse().ToArray()
        : Enumerable.Range(0, count).ToArray();
    Array.Sort(data);
}
// Names: Sort(count=100, order=asc), Sort(count=100, order=desc), Sort(count=10000, order=asc), Sort(count=10000, order=desc)
```

## `[BenchmarkCases]` - programmatic case sources

For generated values, file-backed inputs, or large parameter sweeps, reference a source method that yields named value tuples:

```csharp
[BenchmarkCases(nameof(SortCases))]
[Benchmark]
public void Sort(int count, string order)
{
    var data = order == "desc"
        ? Enumerable.Range(0, count).Reverse().ToArray()
        : Enumerable.Range(0, count).ToArray();
    Array.Sort(data);
}

public static IEnumerable<(int Count, string Order)> SortCases()
{
    yield return (10, "asc");
    yield return (10, "desc");
    yield return (1_000, "asc");
    yield return (1_000, "desc");
}
```

When the tuple elements are named (e.g. `(int Count, string Order)`), the display name uses those names: `Sort(Count=10, Order=asc)`. Unnamed tuples fall back to the method's own parameter names: `Sort(count=10, order=asc)`.

The source method can be `static` or instance, `public` or `non-public`. A static source is recommended since instance sources receive a bare `Activator.CreateInstance` result at discovery time.

## Choosing between the two

| Use case | Attribute |
| --- | --- |
| Small literal list (2-5 values) | `[BenchmarkCase]` |
| Generated values, file/database-backed inputs, parameter sweeps, large lists | `[BenchmarkCases]` |
| Named display names for readability in reports | `[BenchmarkCases]` with named tuples |

The two attributes are mutually exclusive on a method. Use one or the other.

## Baselines in harness mode

When `[Benchmark(Baseline = true)]` is applied to a parameterized method, **all** expanded cases from that method are marked as baseline:

```csharp
[BenchmarkCase(10)]
[BenchmarkCase(100)]
[Benchmark(Baseline = true)]
public void LinearSearch(int size) => Search(size);

[BenchmarkCase(10)]
[BenchmarkCase(100)]
[Benchmark]
public void BinarySearch(int size) => Search(size);
```

## Significance in harness mode

Harness mode computes significance **per class**. When a class has parameterized results, comparisons are grouped by `ParameterSet`, so each parameter combination is tested independently. Non-parameterized results in the same class form their own group.

## Harness mode filtering

Use `--filter` on the CLI to select specific cases by display name:

```bash
dotnet run -- --filter "Sort*100*"   # runs Sort(n=100) and Sort(n=100000)
```

## Reading the report

Console and Markdown reporters consolidate a parameterized benchmark into a **single comparison table** - one table per class in harness mode. Each parameter becomes its own column, and the `Benchmark` column shows the base method name without its parameter suffix:

```text
search benchmarks
Benchmark | size | Median   | Mean     | Ops/s      | Ratio             | Sig | Mag   | Alloc/op
----------+------+----------+----------+------------+-------------------+-----+-------+---------
binary    |   10 |  90.0 ns |  91.2 ns | 11,111,111 | ████ baseline    |  -  |  -    |    32 B
linear    |   10 | 108.0 ns | 109.4 ns |  9,259,259 | █████ 1.20x      |  ✓  | large |    24 B
binary    |  100 | 250.0 ns | 252.1 ns |  4,000,000 | ███████ baseline  |  -  |  -    |    32 B
linear    |  100 | 300.0 ns | 305.7 ns |  3,333,333 | █████████ 1.20x   |  ✓  | large |    24 B
```

Rows are grouped by parameter set in expansion order and sorted by median within each group. To leave room for the parameter columns, parametric tables use the compact labels `Ratio`, `Sig` and `Mag`. When a parameter group holds competing benchmarks, the baseline, ratio, significance (`Sig`) and effect magnitude are computed independently **per parameter group**, so every comparison stays within a single parameter combination.

When a single method is swept across parameter values, every parameter group holds just one benchmark, so there is no within-group comparison. The table instead ranks every row against its fastest point: the `Ratio` column reports each point's scaling factor (the fastest point is the `baseline`), while `Sig` and `Mag` stay `-`, because the engine does not test different workloads against one another. This makes scaling trends easy to read:

```text
LinearSearch benchmarks
Benchmark    | count | Median   | Mean     | Ops/s      | Ratio               | Sig | Mag | Alloc/op
-------------+-------+----------+----------+------------+---------------------+-----+-----+---------
LinearSearch |    10 |  31.2 ns |  32.7 ns | 30,567,164 | █ baseline          |  -  |  -  |     24 B
LinearSearch |   100 | 117.2 ns | 132.2 ns |  7,565,906 | ███ 3.76x           |  -  |  -  |     24 B
LinearSearch |  1000 |  1.40 µs |  1.27 µs |    789,515 | ████████████ 44.87x |  -  |  -  |  4,048 B
```

In a mixed class, a non-parameterized benchmark shows `-` in every parameter column; when no parameter group has a within-group comparison, it joins the table-wide ranking against the fastest row.

CSV and JSON reporters keep one record per result, each carrying its full `ParameterSet`, for machine consumption.

## Accessing results

Each case is a separate `BenchmarkResult` with the display name in the `Name` property and structured values in `ParameterSet`:

```csharp
var results = await BenchmarkHarness.Create(args)
    .AddFromAssembly<SortingBenchmarks>()
    .RunAsync();

foreach (var r in results)
{
    Console.WriteLine($"{r.Name}: {r.Median:F0} ns");
    // Names like "Sort(n=10)", "Sort(n=1000)", "Sort(n=100000)"
    // r.ParameterSet carries the parsed parameter names and values.
}
```

## Suite vs. Harness mode comparison

| Feature | Suite (`WithParameter`) | Harness (`[BenchmarkCase]` / `[BenchmarkCases]`) |
| --- | --- | --- |
| Declaration | Fluent lambda + `WithParameter` call | Attribute on method |
| Parameter types | Primitives, enums, strings, null | Any type matching method signature |
| Multi-parameter | `WithParameter<T1, T2>` / `WithParameter<T1, T2, T3>` | Method parameter names or named tuples |
| Display name | `sort(size=10)` | `Sort(n=10)` or `Sort(count=10, order=asc)` |
| Significance | Per-parameter-group | Per-parameter-group within each class |
| Per-case baseline | All expanded variants share baseline flag | All expanded variants share baseline flag |
| Result metadata | `ParameterSet` property on `BenchmarkResult` | `ParameterSet` property on `BenchmarkResult` |
| CLI filtering | N/A (programmatic only) | `--filter` by display name |

## Next steps

- [Parameterized benchmarks: Suite mode](./parameterized-suite.md) - the `WithParameter` fluent API
- [Harness mode](../usage-modes/harness-mode.md) - attribute-based discovery and CLI
- [Categories](./categories.md) - tag and filter benchmarks
- [Configuration](../reference/configuration.md) - all measurement options


---

