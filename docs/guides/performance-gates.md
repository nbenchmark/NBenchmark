---
title: Performance gates in your test suite
description: Fail PRs on performance regression inside your existing xUnit, NUnit, or MSTest test suite - no separate benchmark project or CI step required.
order: 6
---

# Performance gates in your test suite

## Scenario

If you already run a unit test suite in CI, you may want to detect performance regressions without creating a separate benchmark project, a separate CI step, or a separate `--threshold-pct` invocation. Instead, you can make a performance regression fail a test the same way any other assertion failure does, making it visible in your standard test reports and pull request checks.

NBenchmark provides test-integration packages for xUnit, NUnit, and MSTest that run benchmarks as part of a test method. You can set thresholds as either absolute (a hard SLA, such as "this method must complete in under 500 µs") or relative (a regression gate, such as "this method must not be more than 5x slower than a reference"). Relative thresholds are generally preferred because they absorb changes in machine speed, as both the candidate and reference scale together.

## Complete example

### Absolute threshold (SLA-style)

Use an absolute threshold when you have a hard performance requirement:

```csharp
[PerformanceFact(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

If the measured mean exceeds 500,000 ns (500 µs), the test fails with a message describing the violation.

### Relative threshold (regression gate, zero config)

Use a relative threshold to detect regressions without needing a reference method:

```csharp
[PerformanceFact(MaxSlowdownRatio = 5.0)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

Without a `ReferenceMethod`, the test runs a built-in CPU-bound calibration benchmark alongside your method. The ratio between your method and the calibration is stable across hardware for CPU-bound work. The test fails only when the slowdown is **both** statistically significant (p < 0.05) **and** practically meaningful (the ratio exceeds `MaxSlowdownRatio`). A significant-but-small slowdown or a large-but-noisy slowdown will pass.

The calibration is measured in the same environment as your method. If the test is measured in an isolated worker, the worker performs the calibration in the same process and under the same runtime configuration. If the worker cannot produce a calibration, it falls back to the host's calibration and notes this in the test output.

### Relative threshold with a reference method

Compare two specific implementations to ensure an optimized version meets a target:

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse))]
public void OptimizedParse() => OptimizedParser.Parse(Payload);

private static void NaiveParse() => NaiveParser.Parse(Payload);
```

The candidate must not exceed 1.2x the reference. The reference can be private and runs with the same measurement options (iterations, warmup, and outlier mode) as the candidate. When both are isolated, they run in the same worker process, which removes the worker's core draw and memory layout from the ratio.

At the default `LaunchCount = 1`, this produces a single quotient. For higher reliability in CI, add replicates:

```csharp
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse), LaunchCount = 3)]
public void OptimizedParse() => OptimizedParser.Parse(Payload);
```

With three launches, each worker measures the pair and produces its own ratio. The gate then applies the threshold to the combined estimate and fails only when the interval excludes `1.00x`. This ensures that a failure indicates a real slowdown rather than a difference between two runs of the same code. For more information, see [replicates and the paired ratio](../test-integration/index.md#replicates-and-the-paired-ratio).

### NUnit and MSTest equivalents

The same functionality is available in NUnit and MSTest:

```csharp
// NUnit
[Performance(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);

// MSTest
[PerformanceTestMethod(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

### Assert pattern

Use `PerformanceAssert.Run` to measure a specific part of a larger test. This is available in NUnit and MSTest:

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

`PerformanceAssert.Run` supports calibration mode only (it does not support `ReferenceMethod`).

## What's happening

- **Attribute pattern**: Replace the standard test attribute on a method. The entire method body becomes the benchmark, and thresholds are set as named arguments. This is available in xUnit, NUnit, and MSTest.
- **Assert pattern**: Call `PerformanceAssert.Run` from inside any test. The benchmark runs inline, and violations fail the test immediately. This is available in NUnit and MSTest.
- **Absolute thresholds** (`MaxMeanNs`, `MaxP95Ns`, `MaxAllocatedBytes`): These act as hard SLAs. Because they are susceptible to shared-runner noise, prefer `MaxSlowdownRatio` for regression gates. You can use `MaxAbsoluteThresholdTolerance` to relax absolute thresholds when a shared runner or high-jitter host is detected (e.g., `1.25` for a 25% relaxation).
- **Relative thresholds** (`MaxSlowdownRatio`): These act as regression gates. By comparing two bodies measured in the same session, the engine cancels out the speed of the machine. A quick development box and a slow CI runner will agree on the ratio. Start with a loose ratio (e.g., `10.0`) and tighten it based on observed CI runs. The test fails only when the slowdown is both statistically significant and exceeds the ratio.
- **Statistical gating**: This mirrors the [practical-significance gate](../statistics/significance.md#practical-significance-gate) used in suite and harness modes. A test fails only when the slowdown is both real and practically meaningful.

The definition of "real" depends on `LaunchCount`. With one launch, it is based on a Mann-Whitney U p-value. With two or more launches, it is based on whether the paired ratio interval excludes `1.00x`, which is a stronger claim about reproducibility.

## Measurement location

Performance tests are measured in a **worker process**, not in the test host. This isolation is necessary because JIT tiering, dynamic PGO, and GC flavor are fixed when a process starts.

The worker builds your test class, which requires the class to be constructible without arguments. If a class cannot be isolated, NBenchmark runs it in the test host and reports the reason.

| Situation | Measurement Location | Reported As |
| --- | --- | --- |
| Plain test class with simple or no arguments | Worker | `Isolated` |
| Static test class or method | Worker | `Isolated` |
| `IClassFixture`, `ITestOutputHelper`, or constructor injection | Test host | `InProcessLiveFixture` |
| Argument is an object graph or mock | Test host | `InProcessLiveFixture` |
| No worker deployed | Test host | `InProcessNoWorker` |

### Ratio gate enforcement

| Candidate | Reference | Ratio Gate Status |
| --- | --- | --- |
| Worker | Worker | **Enforced.** With `LaunchCount >= 2`, it fails only when the paired interval excludes `1.00x`. |
| Test host | Test host | Not enforced. Add `[AllowInProcessGate]` to enforce it. |
| Worker | Test host (or vice versa) | **Never enforced.** |

Ratios between two measurements in the same test host may report the host's state rather than the code's performance. Ratios spanning a process boundary are dominated by the difference between runtime configurations. To fix this, make both sides isolatable by moving injected state into the method.

### `[AllowInProcessGate]`

Use `[AllowInProcessGate]` on a method, class, or assembly when a test cannot be isolated, but a noisy ratio is still useful.

```csharp
[AllowInProcessGate]
public class ParserTests : IClassFixture<ParserFixture>
{
    [PerformanceFact(MaxSlowdownRatio = 1.5, ReferenceMethod = nameof(Naive))]
    public void Optimized() => _fixture.Parser.Parse(Payload);
}
```

The gate then runs on host measurements, and the result includes a note stating so. Treat marginal outcomes as inconclusive.

### Isolation requirements

By default, a performance gate fails if the measurement was not taken in a worker process. This prevents labeled-but-passing tests from hiding the fact that isolation was lost (e.g., due to a fixture argument or a deployment failure on a build agent). `[AllowInProcessGate]` waives both the isolation requirement and the ratio-gate restriction.

Simple values (such as `int`, `string`, `bool`, `enum`, `decimal`, `DateTime`, and `Guid`) reach the worker intact, so `[InlineData]` and `[DataRow]` cases isolate normally. Object arguments are refused because the engine cannot guarantee a correct reconstruction.

> [!TIP] Absolute vs. relative thresholds
> Use **absolute** thresholds only for hard SLAs. Use **relative** thresholds for regression gates, as they tolerate changes in machine hardware. Start `MaxSlowdownRatio` loosely (e.g., `10.0`) and tighten it based on several runs in your CI environment.

## Run the tests

Performance tests run as part of your normal test suite:

```bash
dotnet test
dotnet test --filter "FullyQualifiedName~ParseJson"
```

To ensure reproducibility in CI, set `Iterations` and `WarmupIterations` on the attribute:

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

The `p` and Cliff's delta values indicate whether the slowdown is real and how large it is. For more information, see [Reading Your Results](../getting-started/reading-your-results.md).

## Comparison with `--threshold-pct`

| Feature | Test-integration packages | Harness `--threshold-pct` |
| --- | --- | --- |
| Location | Existing test suite | Dedicated benchmark project |
| Trigger | `dotnet test` | `dotnet run -- --threshold-pct 10` |
| Comparison | Method vs. calibration / `ReferenceMethod` | Benchmark vs. suite baseline |
| Hardware Portability | Yes (relative thresholds) | No (absolute medians) |
| Outcome | Test failure | `Environment.ExitCode = 1` |
| Best Use Case | "Don't regress this hot path" | "Don't regress any benchmark in the suite" |

The test-integration packages are per-method and reside with your tests; `--threshold-pct` is per-suite and resides with your benchmarks. For more information on the `--threshold-pct` approach, see [Tuning for CI/CD pipelines](./ci-cd-pipelines.md).

## Next steps

For more information, see the following pages:

- [Test integration](../test-integration/index.md) - Full threshold reference and the `MaxAbsoluteThresholdTolerance` setting.
- [xUnit integration](../test-integration/xunit.md) / [NUnit integration](../test-integration/nunit.md) / [MSTest integration](../test-integration/mstest.md) - Per-framework setup.
- [Significance Testing](../statistics/significance.md) - How Mann-Whitney U and Cliff's delta underpin the statistical gating.
- [Tuning for CI/CD pipelines](./ci-cd-pipelines.md) - The noise-reduction stack and the `--threshold-pct` alternative.
- [Configuration](../reference/configuration.md) - Underlying `MeasurementOptions` exposed by the attributes.
