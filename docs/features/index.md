---
title: Features
description: Advanced cross-cutting NBenchmark capabilities - isolated runs, parameterized benchmarks, categories, multi-runtime comparison, multiple launches, and dependency injection.
order: 4
---

# Features

These pages cover capabilities that apply across the usage modes. Most are opt-in features for finer control over measurement, filtering, or runtime environments; isolated runs are the exception - that one is on by default, and the page explains what it does and how to opt out. For the engineering internals behind these pages, see [Deep dives](../deep-dives/).

## [Isolated runs](./isolated-runs.md)

Every mode measures in a clean worker process by default, because JIT tiering and GC flavour are fixed at process start and can only be chosen for a process that has not begun. `WithIsolation(false)` opts a suite back into the host process.

## [Categories](./categories.md)

Tag benchmarks with `[BenchmarkCategory]` and include or exclude groups from a run via CLI flags or the programmatic `WithCategoryFilter` API.

## [Parameterized benchmarks: Suite mode](./parameterized-suite.md)

Run a benchmark body across multiple input values using `WithParameter` and typed `Add` lambdas. Each parameter combination produces a separate benchmark entry.

## [Parameterized benchmarks: Harness mode](./parameterized-harness.md)

Run a benchmark body across multiple input values using the `[BenchmarkCase]` and `[BenchmarkCases]` attributes. Includes a comparison with the suite-mode API.

## [Multi-runtime comparison](./multi-runtime.md)

Run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare results side-by-side. Available in Suite mode (`WithRuntimes`), Harness mode (`--runtimes` CLI flag), and Harness mode via the `[Runtimes]` attribute.

## [Multiple launches](./multiple-launches.md)

Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.

## [Dependency injection](./dependency-injection.md)

Use `Microsoft.Extensions.DependencyInjection` (or any container that exposes an `IServiceProvider`) to give benchmark classes constructor dependencies. Harness mode only.

## [Environment control](./environment-control.md)

Pin benchmarks to CPU cores, raise process priority, place the measuring thread, and detect noisy hosts to reduce measurement noise at its source. Thread control is on by default; the rest are opt-in.

## [State isolation](./state-isolation.md)

Keep `InstanceLifetime.PerClass` statistically valid with `IStateReset` or automatic per-benchmark isolation fallback when shared state would contaminate timing.

## See also

- [Deep dives](../deep-dives/) - the engineering internals: worker protocol, state transfer, the measurement engine
- [Guides](../guides/) - workflow-first recipes that combine these features to solve real benchmarking tasks (ASP.NET services, CI/CD tuning, refactors, parameter sweeps, cross-runtime, test-suite gates, custom statistics)
- [Usage modes](../usage-modes/) - the four ways to run benchmarks
- [Output](../output/index.md) - reporters and output control
- [Configuration](../reference/configuration.md) - configuration and CLI flags
