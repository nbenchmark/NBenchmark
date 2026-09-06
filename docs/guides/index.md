---
title: Guides
description: Real-world workflow recipes that combine NBenchmark features to solve common benchmarking tasks end-to-end.
order: 3
---

# Guides

The [Features](../features/) section documents individual capabilities. However, real-world benchmarks typically combine several features to achieve a goal. For example, a parameterized EF Core benchmark requires harness mode, dependency injection, and parameter cases. Similarly, a CI regression gate requires isolation, environment control, and a threshold check.

These guides are **workflow-first**. Each guide begins with a concrete goal, such as "I refactored a hot path; is it really faster?" or "I need to fail the build when a benchmark regresses," and assembles the relevant features into a complete, runnable example. For brevity, inline snippets omit `using` statements. For more depth, each guide links to the corresponding feature pages.

## Recipes

### [Benchmarking ASP.NET Core services](./aspnet-core-services.md)
Benchmark an EF Core query or ASP.NET service end-to-end. This guide covers harness mode, scoped dependency injection, parameterized cases, and categories. It also discusses the shared-state pitfall (using `PerClass` lifetime with a scoped `DbContext`) and how the NB0011 analyzer detects it.

### [Tuning for CI/CD pipelines](./ci-cd-pipelines.md)
Get reliable numbers on a noisy shared runner and fail the build upon regression. This guide combines isolated runs, environment control (CPU affinity, process priority, and dedicated-host guidance), the `--max-regression-percent` gate, and `--launch-count` to ensure an honest signal on contested hosts. It includes a minimal GitHub Actions snippet.

### [Comparing a refactor side-by-side](./refactor-comparison.md)
Determine if a change to a hot path actually improved performance. This guide uses suite mode with a baseline, the Sig and Magnitude columns, and the practical-significance gate. It also covers cross-class significance when old and new implementations reside in separate classes.

### [Parameter sweeps across input sizes](./parameter-sweeps.md)
Analyze how an algorithm scales across different input sizes. This guide uses parameterized suite mode (`WithParameter`) and parameterized harness mode (`[Arguments]` / `[ArgumentsSource]`), and explains how to read scaling trends in the output table.

### [Cross-runtime comparison](./cross-runtime.md)
Verify if your code benefits from .NET 10 compared to .NET 8. This guide covers multi-runtime support in suite mode (`WithRuntimes`) and harness mode (`--runtimes` / `[Runtimes]`), `<TargetFrameworks>` project setup, always-worker isolation, and significance grouped within each runtime.

### [Performance gates in your test suite](./performance-gates.md)
Fail pull requests on performance regressions within your existing xUnit, NUnit, or MSTest test suite without needing a separate benchmark project or CI step. This guide covers `[PerformanceFact]`, `[Performance]`, `[PerformanceTestMethod]`, `PerformanceAssert.Run`, and the difference between absolute and relative thresholds.

### [Custom statistics](./custom-statistics.md)
Implement your own logic if the built-in IQR/MAD outlier trimming or rank-based significance tests do not fit your domain (such as latency SLOs or fixed physical thresholds). Learn how to swap in a custom `IOutlierDetector` and `ISignificanceTest`.

### [Tuning recipes](./tuning-recipes.md)
Find configuration recipes for five common scenarios: noisy CI runners, fast feedback during development, publication-grade precision, pure CPU measurement, and debugging non-reproducible results.

## How to use these guides

Each guide is self-contained and provides a runnable example following this pattern:

1. **Scenario**: The goal you want to achieve.
2. **Complete example**: A copy-pasteable configuration body.
3. **What's happening**: Brief callouts on feature interactions with links to feature pages for more depth.
4. **Run it**: The `dotnet run` or CLI invocations required to execute the benchmark.
5. **Read the results**: A plain-English explanation of the output, linking to [Reading Your Results](../getting-started/reading-your-results.md).
6. **When to go deeper**: Links to relevant feature and statistics pages.

If you are new to NBenchmark, start with the [Quick start](../getting-started/quick-start.md) and [Key concepts](../getting-started/key-concepts.md) before using these guides.

## See also

For more information, see the following sections:

- [Features](../features/) - A per-feature reference for every capability used in these guides.
- [Usage modes](../usage-modes/) - The four ways to run benchmarks (Single, Suite, Harness, and Global Tool).
- [Configuration](../reference/configuration.md) - The full `MeasurementOptions` reference.
- [Statistics](../statistics/) - The mathematical methodology behind the measurements.
