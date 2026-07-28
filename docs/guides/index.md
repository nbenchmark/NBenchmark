---
title: Guides
description: Real-world workflow recipes that combine NBenchmark features to solve common benchmarking tasks end-to-end.
order: 4
---

# Guides

The [Features](../features/) section documents each capability on its own. Real benchmarks rarely use one feature at a time - a parameterized EF Core benchmark needs Harness mode, dependency injection, and parameter cases together; a CI regression gate needs isolation, environment control, and the threshold check together.

These guides are **workflow-first**. Each one starts from a concrete goal you might actually have - "I refactored a hot path, is it really faster?" or "I need to fail the build when a benchmark regresses" - and assembles the relevant features into a complete, copy-pasteable example. Inline snippets omit `using` statements for brevity; each guide links out to the feature pages for depth.

## Recipes

### [Benchmarking ASP.NET Core services](./aspnet-core-services.md)

Benchmark an EF Core query or ASP.NET service end-to-end: Harness mode, scoped dependency injection, parameterized cases, and categories. Includes the shared-state pitfall (`PerClass` lifetime with a scoped `DbContext`) and how the NB0011 analyzer catches it.

### [Tuning for CI/CD pipelines](./ci-cd-pipelines.md)

Get clean numbers on a noisy shared runner and fail the build on regression. Combines isolated runs, environment control (CPU affinity, process priority, dedicated-host guidance), the `--threshold-pct` gate, and `--launch-count` as the honest signal on a contested host. Includes a minimal GitHub Actions snippet.

### [Comparing a refactor side-by-side](./refactor-comparison.md)

"I changed a hot path - is it really faster?" Suite mode with a baseline, the Sig and Magnitude columns, the practical-significance gate, and cross-class significance when the old and new implementations live in separate classes.

### [Parameter sweeps across input sizes](./parameter-sweeps.md)

See how an algorithm scales across input sizes. Parameterized Suite mode (`WithParameter`) and parameterized Harness mode (`[BenchmarkCase]` / `[BenchmarkCases]`), plus how to read the scaling trend in the output table.

### [Cross-runtime comparison](./cross-runtime.md)

Verify your code benefits from net10 vs net8. Multi-runtime in Suite mode (`WithRuntimes`) and Harness mode (`--runtimes` / `[Runtimes]`), the `<TargetFrameworks>` project setup, always-child-process isolation, and significance grouped within each runtime.

### [Performance gates in your test suite](./performance-gates.md)

Fail PRs on performance regression inside your existing xUnit, NUnit, or MSTest test suite - no separate benchmark project or CI step required. Covers `[PerformanceFact]` / `[Performance]` / `[PerformanceTestMethod]`, `PerformanceAssert.Run`, absolute vs. relative thresholds, and how this differs from the Harness `--threshold-pct` gate.

### [Custom statistics](./custom-statistics.md)

The built-in IQR/MAD outlier trimming or rank-based significance tests don't fit your domain (latency SLOs, fixed physical thresholds, bootstrap comparison). Swap in a custom `IOutlierDetector` and a custom `ISignificanceTest`.

## How to use these guides

Each guide is self-contained and produces a runnable example. The pattern is:

1. **Scenario** - the goal you arrived with.
2. **Complete example** - the configuration body, copy-pasteable.
3. **What's happening** - brief callouts on the feature interactions, linking to the feature pages for depth.
4. **Run it** - the `dotnet run` / CLI invocations.
5. **Read the results** - plain-English, linking to [Reading Your Results](../output/reading-your-results.md).
6. **When to go deeper** - links into the relevant feature and statistics pages.

If you're new to NBenchmark, start with the [Quick Start](../getting-started/quick-start.md) and [Key Concepts](../getting-started/key-concepts.md), then come back here once you have a specific goal in mind.

## See also

- [Features](../features/) - per-feature reference for every capability used in these guides.
- [Usage modes](../usage-modes/) - the four ways to run benchmarks (Single, Suite, Harness, Global Tool).
- [Configuration](../reference/configuration.md) - the full `MeasurementOptions` reference.
- [Statistics](../statistics/) - the mathematical methodology behind the numbers.
