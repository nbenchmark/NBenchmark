---
title: Features
description: Advanced cross-cutting NBenchmark capabilities - isolated runs, parameterized benchmarks, categories, multi-runtime comparison, multiple launches, and dependency injection.
order: 4
---

# Features

These pages cover capabilities that apply across all usage modes. Most are opt-in features for finer control over measurement, filtering, or runtime environments. Isolated runs are the exception: this feature is enabled by default, and the corresponding page explains how it works and how to opt out. For the engineering internals, see [Deep dives](../deep-dives/).

## Feature overview

- **[Isolated runs](./isolated-runs.md)** - Every mode measures in a clean worker process by default because JIT tiering and GC flavor are fixed at process start. Use `WithIsolation(Isolation.Off)` to opt a suite back into the host process.
- **[Categories](./categories.md)** - Tag benchmarks with `[BenchmarkCategory]` to include or exclude groups from a run via CLI flags or the `WithCategoryFilter` API.
- **[Parameterized benchmarks: Suite mode](./parameterized-suite.md)** - Run a benchmark body across multiple input values using `WithParameter` and typed `Add` lambdas. Each parameter combination produces a separate benchmark entry.
- **[Parameterized benchmarks: Harness mode](./parameterized-harness.md)** - Run a benchmark body across multiple input values using the `[BenchmarkCase]` and `[BenchmarkCases]` attributes.
- **[Multi-runtime comparison](./multi-runtime.md)** - Run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare results side-by-side. This is available in Suite mode (`WithRuntimes`), Harness mode (`--runtimes` CLI flag), and Harness mode via the `[Runtimes]` attribute.
- **[Multiple launches](./multiple-launches.md)** - Run each benchmark multiple times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.
- **[Dependency injection](./dependency-injection.md)** - Use `Microsoft.Extensions.DependencyInjection` (or any container that exposes an `IServiceProvider`) to provide constructor dependencies for benchmark classes. This is available in Harness mode only.
- **[Environment control](./environment-control.md)** - Pin benchmarks to CPU cores, raise process priority, place the measuring thread, and detect noisy hosts to reduce measurement noise. Thread control is enabled by default; other settings are opt-in.
- **[State isolation](./state-isolation.md)** - Keep `InstanceLifetime.PerClass` statistically valid using `IStateReset` or automatic per-benchmark isolation fallback when shared state would contaminate timing.

## See also

For more information, see the following pages:

- [Deep dives](../deep-dives/) - The engineering internals, including the worker protocol, state transfer, and the measurement engine.
- [Guides](../guides/) - Workflow recipes that combine these features for tasks such as ASP.NET services, CI/CD tuning, and cross-runtime comparisons.
- [Usage modes](../usage-modes/) - The four ways to run benchmarks.
- [Output](../output/index.md) - Reporters and output control.
- [Configuration](../reference/configuration.md) - Configuration and CLI flags.
