---
title: Performance gates in your test suite
description: Fail PRs on performance regression inside your existing xUnit, NUnit, or MSTest test suite - no separate benchmark project or CI step required.
order: 6
---

# Performance gates in your test suite

## Scenario

You already run a unit test suite in CI. You don't want a separate benchmark project, a separate CI step, or a separate `--threshold-pct` invocation - you want a perf regression to fail a test the same way any other assertion failure fails a test, visible in the same test report and the same PR check.

NBenchmark's test-integration packages attach to xUnit, NUnit, and MSTest and run the benchmark inline as part of the test method. Thresholds can be absolute (a hard SLA: "this method must complete in under 500 µs") or relative (a regression gate: "this method must not be more than 5x slower than a calibration benchmark"), and relative thresholds are hardware-independent because the comparison runs in the same test session.

## Complete example

### Absolute threshold (SLA-style)

```csharp
[PerformanceFact(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

If the measured mean exceeds 500,000 ns (500 µs), the test fails with a message describing the violation.

### Relative threshold (regression gate, zero config)

```csharp
[PerformanceFact(MaxSlowdownRatio = 5.0)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

Without a `ReferenceMethod`, the test runs a built-in CPU-bound calibration benchmark alongside your method. The ratio between your method and the calibration is stable across hardware for CPU-bound work - both scale with machine speed. The test fails only when the slowdown is **both** statistically significant (p < 0.05) **and** practically meaningful (ratio exceeds `MaxSlowdownRatio`). A significant-but-small slowdown passes (noise); a large-but-noisy slowdown passes (not enough evidence).

### Relative threshold with a reference method (compare two implementations)

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse))]
public void OptimisedParse() => OptimisedParser.Parse(Payload);

private static void NaiveParse() => NaiveParser.Parse(Payload);
```

Both methods run in the same test session; the candidate must not exceed 1.2x the reference. The reference can be private and runs with the same measurement options as the candidate (same iterations, warmup, outlier mode), so the comparison is apples-to-apples.

### NUnit / MSTest equivalents

```csharp
// NUnit
[Performance(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);

// MSTest
[PerformanceTestMethod(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

### Assert pattern (measure one part of a larger test)

```csharp
[Test]
public void Repository_Query_Is_Fast_Enough()
{
    var repo = new OrderRepository(connection);

    PerformanceAssert.Run(
        () => repo.GetRecentOrders(limit: 100),
        new PerformanceAssertionOptions { MaxMeanNs = 2_000_000 });
}
```

`PerformanceAssert.Run` is available in NUnit and MSTest and supports calibration mode only (no `ReferenceMethod`).

## What's happening

- **Attribute pattern** - replace the test attribute on a test method. The entire method body becomes the benchmark. Thresholds are set as named arguments on the attribute. Available in all three frameworks.

- **Assert pattern** - call `PerformanceAssert.Run` from inside any test. The benchmark runs inline and violations fail the test immediately. Useful when you want to measure just one part of a larger test. Available in NUnit and MSTest.

- **Absolute thresholds** (`MaxMeanNs`, `MaxP95Ns`, `MaxAllocatedBytes`) - hard SLAs. Susceptible to shared-runner noise; prefer `MaxSlowdownRatio` for regression gates. Set `MaxAbsoluteThresholdTolerance` to relax absolute thresholds when a shared runner or high-jitter host is detected (e.g. `1.25` for 25% relaxation).

- **Relative thresholds** (`MaxSlowdownRatio`) - regression gates. Hardware-independent because the comparison runs in the same test session. A fast dev machine and a slow CI runner produce the same ratio. No stored files, no environment mismatch, no CI workflow setup. The test fails only when the slowdown is both statistically significant and exceeds the ratio.

- **Statistical gating** - the test fails only when the slowdown is **both** statistically significant (Mann-Whitney U p-value below the significance level) **and** practically meaningful (ratio exceeds `MaxSlowdownRatio`). A significant-but-small slowdown passes; a large-but-noisy slowdown passes. This mirrors the [practical-significance gate](../statistics/significance.md#practical-significance-gate) in the suite / harness flow.

> [!TIP] Absolute vs. relative - which to use?
> Use **absolute** thresholds only when you have a hard SLA ("parse must complete in under 500 µs"). Use **relative** thresholds for regression gates ("this PR must not regress the parser"). Relative thresholds are robust to the host running them; absolute thresholds are not. A practical tuning workflow for `MaxSlowdownRatio` is to start loose (e.g. `10.0`) and tighten based on several runs in your CI environment.

## Run it

The test runs as part of your normal test suite - no special invocation needed:

```bash
dotnet test
dotnet test --filter "FullyQualifiedName~ParseJson"
```

To pin the measurement (e.g. for reproducibility in CI), set `Iterations` and `WarmupIterations` on the attribute:

```csharp
[PerformanceFact(MaxSlowdownRatio = 5.0, Iterations = 200, WarmupIterations = 20)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

## Read the results

A passing test reports normally. A failing test prints the violation:

```text
PerformanceAssert: mean 612,345 ns exceeded MaxMeanNs 500,000 ns
  median: 598,210 ns  mean: 612,345 ns  p95: 720,000 ns
  alloc/op: 1,024 B
```

For a relative-threshold failure:

```text
PerformanceAssert: slowdown ratio 6.2x exceeded MaxSlowdownRatio 5.0
  candidate median: 612,345 ns  reference median: 98,762 ns
  p = 0.0003 (significant)  Cliff's delta = 0.92 (large)
```

The `p` and Cliff's delta values tell you whether the slowdown is real and how large. See [Reading Your Results](../output/reading-your-results.md) for every column the underlying benchmark reports.

## How this differs from `--threshold-pct`

| | Test-integration packages | Harness `--threshold-pct` |
| --- | --- | --- |
| Lives in | Your existing test suite | A dedicated benchmark project |
| Trigger | `dotnet test` | `dotnet run -- --threshold-pct 10` |
| Comparison | Your method vs. a calibration / `ReferenceMethod` (same session) | Each benchmark vs. the suite baseline (same session) |
| Hardware-independent | Yes (relative thresholds) | No (absolute medians) |
| Exit code | Test failure | `Environment.ExitCode = 1` |
| Best for | "Don't regress this hot path" | "Don't regress any benchmark in the suite" |

Both are valid; they serve different scopes. The test-integration packages are per-method and live where your tests live; `--threshold-pct` is per-suite and lives where your benchmarks live. See [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) for the `--threshold-pct` path.

## When to go deeper

- [Test integration](../test-integration/index.md) - the full threshold reference, the attribute vs. assert patterns, calibration vs. `ReferenceMethod`, and `MaxAbsoluteThresholdTolerance` for shared runners.
- [xUnit integration](../test-integration/xunit.md) / [NUnit integration](../test-integration/nunit.md) / [MSTest integration](../test-integration/mstest.md) - per-framework setup and examples.
- [Significance Testing](../statistics/significance.md) - how the Mann-Whitney U test and Cliff's delta underpin the statistical gating.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - the `--threshold-pct` alternative and the noise-reduction stack that applies to both.
- [Configuration](../reference/configuration.md) - the underlying `MeasurementOptions` that the integration attributes expose.
