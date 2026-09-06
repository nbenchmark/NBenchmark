---
title: Test integration
description: Enforce performance thresholds inside an existing xUnit, NUnit, or MSTest test suite.
order: 7
---

# Test integration

NBenchmark's integration packages connect the engine to your existing toolchain. These integrations allow you to enforce performance thresholds directly inside your test suite, removing the need for a separate benchmark project or CI step.

## Test framework packages

| Package | Framework |
|---|---|
| `NBenchmark.Integration.xUnit` | xUnit v2 |
| `NBenchmark.Integration.NUnit` | NUnit 3 / 4 |
| `NBenchmark.Integration.MSTest` | MSTest v2 / v3 |

These packages are agnostic to your test runner and integrate with whichever framework you use. All three packages depend on `NBenchmark` (the core package) and a shared `NBenchmark.Integration.Abstractions` package, which is included automatically.

## Usage patterns

### Attribute pattern

Replace the standard test attribute on a method to make the entire method body a benchmark. You set thresholds as named arguments on the attribute.

```csharp
// xUnit
[PerformanceFact(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);

// NUnit
[Performance(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);

// MSTest
[PerformanceTestMethod(MaxMeanNs = 500_000)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

If the measured mean exceeds 500,000 ns (500 $\mu$s), the test fails with a message describing the violation.

### Assert pattern for NUnit and MSTest

Call `PerformanceAssert.Run` from inside any test to run a benchmark inline. If a violation occurs, the test fails immediately. Use this pattern to measure a specific part of a larger test.

```csharp
// NUnit
[Test]
public void Repository_Query_Is_Fast_Enough()
{
    var repo = new OrderRepository(connection);

    PerformanceAssert.Run(
        () => repo.GetRecentOrders(limit: 100),
        new PerformanceAssertionOptions { MaxMeanNs = 2_000_000 });
}

// MSTest
[TestMethod]
public void Repository_Query_Is_Fast_Enough()
{
    var repo = new OrderRepository(connection);

    PerformanceAssert.Run(
        () => repo.GetRecentOrders(limit: 100),
        new PerformanceAssertionOptions { MaxMeanNs = 2_000_000 });
}
```

## Relative regression checks

Regression checks compare your benchmark against a reference point measured during the same run. This ensures that a fast developer machine and a slow CI runner produce a similar ratio, eliminating the need for stored baseline files or complex CI workflows.

NBenchmark measures both sides in worker processes whenever possible. The ratio gate is only enforced when isolation is achieved; if two measurements share a test host, they also share the host's JIT tiering state, which prevents the ratio from being accurate. For more information, see [ratio gate enforcement](../guides/performance-gates.md#ratio-gate-enforcement), `[AllowInProcessGate]`, and `RequireIsolation`.

### Calibration mode

When you set `MaxSlowdownRatio` without specifying a `ReferenceMethod`, NBenchmark runs a built-in CPU-bound calibration benchmark alongside your method. For CPU-bound work, the ratio between your method and the calibration remains stable across different hardware because both scale with machine speed.

For allocation-heavy or I/O-bound benchmarks, the ratio to a CPU calibration loop is less stable. In these cases, use a `ReferenceMethod` to compare against a method with a similar resource profile, or use absolute thresholds with `MaxAbsoluteThresholdTolerance`.

NBenchmark measures the calibration in the same environment as the benchmark method. If your test runs in an isolated worker, the worker measures the calibration as well, ensuring both sides of the ratio share the same runtime configuration. If the worker cannot produce one, the gate falls back to a host-measured calibration and adds a note to the test output.

```csharp
// xUnit
[PerformanceFact(MaxSlowdownRatio = 5.0)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);

// NUnit
[Performance(MaxSlowdownRatio = 5.0)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);

// MSTest
[PerformanceTestMethod(MaxSlowdownRatio = 5.0)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

The test fails when the slowdown is both statistically significant and practically meaningful (i.e., the ratio exceeds `MaxSlowdownRatio`). Whether a result is "real" depends on whether the test uses replicates - see [replicates and the paired ratio](#replicates-and-the-paired-ratio). With the default single launch, NBenchmark uses a Mann-Whitney U p-value below the significance level. A significant but small slowdown passes as noise, and a large but noisy slowdown passes due to insufficient evidence.

If a slowdown breaches the gate, the failure output includes ratio and significance details (`ratio`, `p`, and Cliff's delta). To tune this, start with a loose value (such as `MaxSlowdownRatio = 10.0`) and tighten it based on several runs in your CI environment.

### ReferenceMethod mode

To compare two implementations, set `ReferenceMethod` to the name of your baseline implementation. The candidate must not exceed `MaxSlowdownRatio` relative to the reference. NBenchmark measures both in matching worker processes when isolation is available.

```csharp
// xUnit
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse))]
public void OptimizedParse() => OptimizedParser.Parse(Payload);

private static void NaiveParse() => NaiveParser.Parse(Payload);
```

The reference method can be private. It uses the same measurement options as the candidate (samples, warmup, outlier mode, and confidence level) to ensure an apples-to-apples comparison. For example, if the candidate uses `Samples = 300`, the reference also runs 300 samples. The total wall-clock cost for the test is the combined duration of the candidate and the reference.

The candidate and its reference are measured co-resident in one worker process per replicate. This ensures their ratio excludes the worker's core draw, thermal state, and address-space layout. This pairing is why the pair is handled as a single measurement request.

`ReferenceMethod` is only available in the attribute pattern; the assert pattern (`PerformanceAssert.Run`) only supports calibration mode.

For more information on how the comparison is performed, see [statistics: significance testing](../statistics/significance.md).

## Replicates and the paired ratio

Because a ratio gate can fail a build, you need to know if a result is reproducible rather than just whether it exceeds a threshold. You use the `LaunchCount` property to verify reproducibility.

```csharp
[PerformanceFact(
    MaxSlowdownRatio = 1.2,
    ReferenceMethod = nameof(NaiveParse),
    LaunchCount = 3)]
public void OptimizedParse() => OptimizedParser.Parse(Payload);
```

This configuration measures the pair in **three separate worker processes**. Each worker produces its own candidate/reference ratio, and NBenchmark combines them on a log scale into a geometric mean with a confidence interval - the same estimator used by the engine's `Ratio CI` column. For more details, see [ratios](../statistics/ratios.md).

The following table shows how the gate changes when an interval exists:

| Metric | `LaunchCount = 1` (default) | `LaunchCount >= 2` |
|---|---|---|
| Ratio gated on | Candidate mean / reference mean | Geometric mean of per-launch ratios |
| "Is the difference real?" | Mann-Whitney U on pooled samples | Does the interval exclude `1.00x`? |
| Worker launches | One | One per replicate (pair shares each) |
| Test output | mean, P95, allocations, samples | Above, plus a `Launches:` line with the run-to-run spread |

Although NBenchmark still computes and reports the p-value for `LaunchCount >= 2`, the gate no longer relies on it. Pooling samples across launches increases statistical power without improving reproducibility; a difference far below the run-to-run noise can appear overwhelmingly significant. A gate must survive the interval over per-replicate ratios. For more information, see [ratios: when sig and the ratio interval disagree](../statistics/ratios.md#when-sig-and-the-ratio-interval-disagree).

Consider these two factors before setting `LaunchCount`:
- **Resource cost.** `LaunchCount = 3` uses three worker launches per test instead of one. `[PerformanceTheory]` tests spend these launches per test case. Only increase this value for comparisons that decide a build. Two is the minimum for an interval; three is generally sufficient.
- **Noise handling.** When a point estimate exceeds the gate but the interval spans `1.00x`, the gate is **not** enforced. This prevents tests from failing due to noise when the run cannot distinguish between the two bodies. The test output explicitly notes this. Increasing `LaunchCount` narrows the interval; if it remains wide, the difference is smaller than the machine's run-to-run variation.

A failure at `LaunchCount = 1` includes a note stating that the ratio is a point estimate with no interval. This is the limit of what a single-launch measurement can support.

`LaunchCount` also applies to calibration mode. Each replicate's worker measures the calibration standard after its own benchmark work, pairing the medians by launch exactly as it does for a reference method.

## Thresholds reference

All three integration packages share the same threshold properties. Each numeric threshold is disabled by `IPerformanceThresholds.Unset` (`-1`), which is also its default, so omitting a property leaves the check off. Sample counts use `IPerformanceThresholds.AutoSampleCount` (`0`) instead - "as many as it takes" is zero explicit samples rather than minus one.

The sentinels exist because these thresholds live on attributes, and an attribute argument cannot be a `Nullable<T>`. Name them rather than writing the numbers:

```csharp
[PerformanceFact(MaxMeanNs = 200_000)]                       // gate on the mean
[PerformanceFact(MaxMeanNs = IPerformanceThresholds.Unset)]  // explicitly no gate
```

The `PerformanceThresholds` option bag used by `BenchmarkAssert.Validate` is an ordinary object rather than attribute metadata, so it uses `double?` and `long?` and needs no sentinel.

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxMeanNs` | `double` | `Unset` (-1, disabled) | Maximum allowed mean execution time in nanoseconds. |
| `MaxP95Ns` | `double` | `Unset` (-1, disabled) | Maximum allowed 95th-percentile execution time in nanoseconds. Requires P95 to be in `MeasurementOptions.ReportedPercentiles`. |
| `MaxAllocatedBytes` | `long` | `UnsetBytes` (-1, disabled) | Maximum allowed mean allocated bytes per operation. Implicitly enables `MeasureAllocations`. |
| `MaxSlowdownRatio` | `double` | `Unset` (-1, disabled) | Maximum allowed slowdown relative to a calibration benchmark or `ReferenceMethod`. Set to a positive value to enable regression checking (e.g., `5.0` = 5$\times$ the calibration time). The test fails only when the slowdown is both statistically significant and exceeds this ratio. |
| `ReferenceMethod` | `string?` | null | Name of a method on the same class to use as the reference. When null, calibration mode runs. |
| `Samples` | `int` | `AutoSampleCount` (0) | Number of measured samples. `0` uses the framework default. |
| `WarmupSamples` | `int` | `AutoSampleCount` (0) | Number of warmup samples. `0` uses the framework default. |
| `MeasureAllocations` | `bool` | false | Enable allocation tracking. Automatically enabled when `MaxAllocatedBytes` is set. |
| `RequireIsolation` | `bool` | **true** | Fails the test if the measurement occurs in the test host rather than a worker process. Opt out with `[AllowInProcessGate]` on the method, class, or assembly. This is settable only via `PerformanceAssert` options. |
| `LaunchCount` | `int` | 1 | Number of worker processes to measure this test in. Two or more enable the paired per-replicate estimate with a confidence interval. |
| `OutlierMode` | `OutlierMode` | `IqrFence` | Outlier removal strategy applied before statistics are computed. |
| `ConfidenceLevel` | `double` | 0.95 | Confidence level for the margin-of-error calculation. |
| `MaxAbsoluteThresholdTolerance` | `double` | 1.0 | Multiplier applied to absolute thresholds when a shared runner or high-jitter host is detected. For example, `1.25` provides a 25% relaxation. |

For a full explanation of each option, see [configuration](../reference/configuration.md).

## SLA-style hard limits

Absolute thresholds (`MaxMeanNs`, `MaxP95Ns`, `MaxAllocatedBytes`) are susceptible to noise from shared CI runners. Use `MaxSlowdownRatio` (calibration or `ReferenceMethod`) for regression gates and reserve absolute thresholds for hard SLAs.

To relax absolute thresholds for jitter on shared CI runners, set `MaxAbsoluteThresholdTolerance`:

```csharp
[PerformanceFact(
    MaxMeanNs = 500_000,
    MaxAbsoluteThresholdTolerance = 1.25)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

If NBenchmark detects a shared runner or high-jitter host, the effective threshold becomes $500,000 \times 1.25 = 625,000$ ns. On a dedicated host, the original 500,000 ns threshold applies.

## Per-framework reference

- [xUnit integration](./xunit.md)
- [NUnit integration](./nunit.md)
- [MSTest integration](./mstest.md)
