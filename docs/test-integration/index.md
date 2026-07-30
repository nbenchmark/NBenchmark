---
title: Test integration
description: Enforce performance thresholds inside an existing xUnit, NUnit, or MSTest test suite.
order: 7
---

# Test integration

NBenchmark's integration packages connect it to the rest of your toolchain. The current integrations focus on test frameworks, letting you enforce performance thresholds directly inside your existing test suite - no separate benchmark project or CI step required.

## Test framework packages

| Package | Framework |
| --- | --- |
| `NBenchmark.Integration.xUnit` | xUnit v2 |
| `NBenchmark.Integration.NUnit` | NUnit 3 / 4 |
| `NBenchmark.Integration.MSTest` | MSTest v2 / v3 |

Each package has **no opinion on your test runner** - it slots into whichever framework you already use. All three packages depend on `NBenchmark` (the core package) and on a shared `NBenchmark.Integration.Abstractions` package that is pulled in automatically.

## Two usage patterns

### Attribute pattern

Replace the test attribute on a test method. The entire method body becomes the benchmark. Thresholds are set as named arguments on the attribute.

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

If the measured mean exceeds `500_000 ns` (500 us), the test fails with a message describing the violation.

### Assert pattern (NUnit and MSTest)

Call `PerformanceAssert.Run` from inside any test. The benchmark runs inline and violations fail the test immediately. This is useful when you want to measure just one part of a larger test.

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

Regression checks compare your benchmark against a reference point measured in the same run, so a fast developer machine and a slow CI runner produce a similar ratio. No stored files, no environment mismatch, no CI workflow setup.

Both sides are measured in worker processes when they can be, and the ratio gate is only enforced when they were - two measurements sharing a test host also share its JIT tiering state, which does not cancel between them. See [when a ratio gate is enforced](../guides/performance-gates.md#when-a-ratio-gate-is-enforced), `[AllowInProcessGate]`, and `RequireIsolation`.

### Calibration mode (zero config)

When you set `MaxSlowdownRatio` without `ReferenceMethod`, the test runs a built-in CPU-bound calibration benchmark alongside your method. The ratio between your method and the calibration is stable across hardware for CPU-bound work - both scale with machine speed. For allocation-heavy or I/O-bound benchmarks, the ratio to a CPU calibration loop is less stable across hardware; use `ReferenceMethod` to compare against a method with a similar resource profile, or use absolute thresholds with `MaxAbsoluteThresholdTolerance`.

The calibration is measured in the same place as the method it divides into. When your test runs in an isolated worker the worker measures the calibration too, so both sides of the ratio share a runtime configuration - the worker's tiering and ReadyToRun settings differ from the test host's, and that difference alone can move the number by ~3x. When the worker cannot produce one, the gate falls back to a host-measured calibration and adds a note to the test output saying the ratio spans two configurations.

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

The test fails when the slowdown is **both** real **and** practically meaningful (ratio exceeds `MaxSlowdownRatio`). What counts as "real" depends on whether the test asked for replicates - see [Replicates and the paired ratio](#replicates-and-the-paired-ratio). With the default single launch it is a Mann-Whitney U p-value below the significance level. A significant-but-small slowdown passes (noise); a large-but-noisy slowdown passes (not enough evidence).

Failure output includes ratio and significance details (`ratio`, `p`, and Cliff's delta) when a slowdown breaches the gate. A practical tuning workflow is to start with a loose value (for example `MaxSlowdownRatio = 10.0`) and tighten it based on several runs in your CI environment.

### ReferenceMethod mode (compare two implementations)

When you have two implementations to compare, point `ReferenceMethod` at the baseline implementation. The candidate must not exceed `MaxSlowdownRatio` relative to the reference; both are measured the same way, in matching worker processes when they isolate.

```csharp
// xUnit
[PerformanceFact(MaxSlowdownRatio = 1.2, ReferenceMethod = nameof(NaiveParse))]
public void OptimisedParse() => OptimisedParser.Parse(Payload);

private static void NaiveParse() => NaiveParser.Parse(Payload);
```

The reference method can be private. It runs with the same measurement options as the candidate (iterations, warmup, outlier mode, confidence level). This keeps the comparison apples-to-apples: if the candidate uses `Iterations = 300`, the reference also runs 300 iterations. Wall-clock cost for the test is therefore candidate + reference durations.

**Both run in the same worker process.** The candidate and its reference are measured co-resident, one worker per replicate, so their ratio has that worker's core draw, thermal state and address-space layout divided out of it rather than left in the numerator and denominator independently. This is the same rule the engine applies to a comparison group, and it is why the pair is a single measurement request rather than two.

`ReferenceMethod` is only available in the attribute pattern. The assert pattern (`PerformanceAssert.Run`) supports calibration mode only.

See [Statistics: Significance Testing](../statistics/significance.md) for how the comparison is performed.

## Replicates and the paired ratio

A ratio gate decides a build, so the question that matters is not "is this number above the threshold" but "would it still be tomorrow". A single measurement cannot answer it. `LaunchCount` is how you ask.

```csharp
[PerformanceFact(
    MaxSlowdownRatio = 1.2,
    ReferenceMethod = nameof(NaiveParse),
    LaunchCount = 3)]
public void OptimisedParse() => OptimisedParser.Parse(Payload);
```

That measures the pair in **three separate worker processes**. Each one produces its own candidate/reference ratio, and the three are combined on the log scale into a geometric mean with a confidence interval - the same estimator the engine's `Ratio CI` column uses. See [Ratios](../statistics/ratios.md).

What changes when the interval exists:

| | `LaunchCount = 1` (default) | `LaunchCount >= 2` |
| --- | --- | --- |
| ratio gated on | candidate mean / reference mean | geometric mean of the per-launch ratios |
| "is the difference real?" | Mann-Whitney U on pooled samples | does the interval exclude `1.00x`? |
| worker launches | 1 | one per replicate - the pair shares each |
| test output | mean, P95, allocations, iterations | the above plus a `Launches:` line with the run-to-run spread |

The p-value is still computed and reported at `LaunchCount >= 2`, but it is no longer what the gate turns on. Pooling samples across launches multiplies statistical power without improving reproducibility, so a difference far below the run-to-run noise can read as overwhelmingly significant - on NBenchmark's own calibration sample, four bodies of provably identical cost, that combination marks one significantly slower than another routinely. The interval over per-replicate ratios is the run-to-run spread, and that is the quantity a gate must survive.

Two consequences worth knowing before you set it:

- **It costs launches.** `LaunchCount = 3` spends three worker launches on that test instead of one, and a `[PerformanceTheory]` spends them per test case. Raise it on the comparisons that decide a build, not across the suite. Two is the smallest value that produces an interval; three is enough for the interval to be worth reading.
- **A noisy comparison stops failing.** When the point estimate is past the gate but the interval spans `1.00x`, the gate is **not** enforced - the run cannot distinguish the two bodies, and failing on that is failing on noise. The test output says so explicitly rather than passing in silence. Raising `LaunchCount` narrows the interval; if it stays wide, the difference is smaller than your machine's run-to-run variation and no threshold can honestly detect it.

Conversely, a failure at `LaunchCount = 1` now carries a note saying the ratio is a point estimate with no interval behind it. That is not a defect in the gate; it is the most the single-launch measurement supports.

`LaunchCount` applies to calibration mode too: each replicate's worker measures the calibration standard after its own benchmark work, so those medians pair by launch exactly as a reference method's do.

## Thresholds reference

All three packages share the same set of threshold properties. A threshold of `-1` (double) or `-1` (long) means the check is disabled. Omitting a property is equivalent to `-1`.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `MaxMeanNs` | `double` | -1 (disabled) | Maximum allowed mean execution time in nanoseconds. |
| `MaxP95Ns` | `double` | -1 (disabled) | Maximum allowed 95th-percentile execution time in nanoseconds. Requires P95 to be in `MeasurementOptions.ReportedPercentiles` (the default set includes `0.95`). If P95 was not computed, a clear error message guides you to check the configuration. |
| `MaxAllocatedBytes` | `long` | -1 (disabled) | Maximum allowed mean allocated bytes per operation. Implicitly enables `MeasureAllocations`. |
| `MaxSlowdownRatio` | `double` | 0 (disabled) | Maximum allowed slowdown relative to a calibration benchmark or `ReferenceMethod`. Set to a positive value to enable regression checking (e.g. `5.0` = 5x the calibration time). The test fails only when the slowdown is both statistically significant and exceeds this ratio. |
| `ReferenceMethod` | `string?` | null | Name of a method on the same class to use as the reference for ratio comparison. When null, calibration mode runs (built-in CPU-bound benchmark). |
| `Iterations` | `int` | 0 (use default) | Override the number of measured samples. `0` uses the framework default (auto-resolved). |
| `WarmupIterations` | `int` | 0 (use default) | Override the number of warmup samples. `0` uses the framework default (auto-detected). |
| `MeasureAllocations` | `bool` | false | Enable allocation tracking. Automatically enabled when `MaxAllocatedBytes` is set. |
| `RequireIsolation` | `bool` | false | Fail the test when the measurement was taken in the test host rather than a worker process. Use it on gates that matter: isolation can be lost quietly when a fixture argument is added or a worker fails to deploy, and the gate would otherwise keep passing against a number measured somewhere you did not choose. |
| `LaunchCount` | `int` | 1 | Worker processes to measure this test in. Two or more turn the ratio gate into a paired per-replicate estimate with a confidence interval, at the cost of a worker launch each. See [Replicates and the paired ratio](#replicates-and-the-paired-ratio). |
| `OutlierMode` | `OutlierMode` | `IqrFence` | Outlier removal strategy applied before statistics are computed. |
| `ConfidenceLevel` | `double` | 0.95 | Confidence level for the margin-of-error calculation. |
| `MaxAbsoluteThresholdTolerance` | `double` | 1.0 | Multiplier applied to absolute thresholds (`MaxMeanNs`, `MaxP95Ns`, `MaxAllocatedBytes`) when a shared runner or high-jitter host is detected. Set to e.g. `1.25` for 25% relaxation on shared CI runners. |

See [Configuration](../reference/configuration.md) for a full explanation of each option.

## SLA-style hard limits

Absolute thresholds (`MaxMeanNs`, `MaxP95Ns`, `MaxAllocatedBytes`) are susceptible to shared-runner noise in CI. Prefer `MaxSlowdownRatio` (calibration or `ReferenceMethod`) for regression gates; use absolute thresholds only when you have a hard SLA.

On shared CI runners, set `MaxAbsoluteThresholdTolerance` to relax absolute thresholds for jitter:

```csharp
[PerformanceFact(
    MaxMeanNs = 500_000,
    MaxAbsoluteThresholdTolerance = 1.25)]
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

When a shared runner or high-jitter host is detected, the effective threshold becomes `500_000 x 1.25 = 625_000 ns`. On a dedicated host, the original `500_000 ns` threshold applies.

## Per-framework reference

- [xUnit integration](./xunit.md)
- [NUnit integration](./nunit.md)
- [MSTest integration](./mstest.md)
