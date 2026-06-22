---
title: Features
description: Advanced cross-cutting NBenchmark capabilities - parameterized benchmarks, categories, isolated runs, multi-runtime comparison, multiple launches, and dependency injection.
order: 3
---

# Features

These pages cover advanced capabilities that apply across the usage modes. They are opt-in features for experienced benchmarkers who need finer control over measurement, filtering, isolation, or runtime environments.

## [Parameterized benchmarks: Suite mode](./parameterized-suite.md)

Run a benchmark body across multiple input values using `WithParameter` and typed `Add` lambdas. Each parameter combination produces a separate benchmark entry.

## [Parameterized benchmarks: Host mode](./parameterized-host.md)

Run a benchmark body across multiple input values using the `[BenchmarkCase]` and `[BenchmarkCases]` attributes. Includes a comparison with the suite-mode API.

## [Categories](./categories.md)

Tag benchmarks with `[BenchmarkCategory]` and include or exclude groups from a run via CLI flags or the programmatic `WithCategoryFilter` API.

## [Isolated runs](./isolated-runs.md)

Run Suite and Host benchmarks in clean child processes when runtime state contamination matters more than raw execution speed. Host mode is isolated by default; suites opt in with `WithIsolation()`.

## [Multi-runtime comparison](./multi-runtime.md)

Run the same benchmarks across multiple .NET runtimes (net8.0, net9.0, net10.0) and compare results side-by-side. Available in Suite mode (`WithRuntimes`), Host mode (`--runtimes` CLI flag), and Host mode via the `[Runtimes]` attribute.

## [Multiple launches](./multiple-launches.md)

Run each benchmark N times as independent launches to measure run-to-run variance and produce cross-launch aggregation statistics.

## [Dependency injection](./dependency-injection.md)

Use `Microsoft.Extensions.DependencyInjection` (or any container that exposes an `IServiceProvider`) to give benchmark classes constructor dependencies. Host mode only.

## See also

- [Usage modes](../usage-modes/) - the four ways to run benchmarks
- [Output](../output/index.md) - reporters and output control
- [Reference](../reference/configuration.md) - configuration and CLI flags