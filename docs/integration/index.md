---
title: Integration
description: Integrate NBenchmark with other tools and frameworks - test runners, CI pipelines, and more.
order: 5
---

# Integration

NBenchmark's integration packages connect it to the rest of your toolchain. The current integrations focus on test frameworks, letting you enforce performance thresholds directly inside your existing test suite - no separate benchmark project or CI step required.

## Test framework packages

| Package | Framework |
|---|---|
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

If the measured mean exceeds `500_000 ns` (500 µs), the test fails with a message describing the violation.

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

## Thresholds reference

All three packages share the same set of threshold properties. A threshold of `-1` (double) or `-1` (long) means the check is disabled. Omitting a property is equivalent to `-1`.

| Property | Type | Default | Description |
|---|---|---|---|
| `MaxMeanNs` | `double` | -1 (disabled) | Maximum allowed mean execution time in nanoseconds. |
| `MaxP95Ns` | `double` | -1 (disabled) | Maximum allowed 95th-percentile execution time in nanoseconds. |
| `MaxAllocatedBytes` | `long` | -1 (disabled) | Maximum allowed mean allocated bytes per operation. Implicitly enables `MeasureAllocations`. |
| `BaselinePath` | `string?` | null | Path to a JSON baseline file. Fails if the benchmark regresses beyond `MaxSlowdownRatio`. |
| `MaxSlowdownRatio` | `double` | 1.2 | Maximum allowed slowdown relative to the baseline (1.2 = 20% regression). |
| `Iterations` | `int` | 0 (use default) | Override the number of measured iterations. `0` uses the framework default (200). |
| `WarmupIterations` | `int` | 0 (use default) | Override the number of warmup iterations. `0` uses the framework default (25). |
| `MeasureAllocations` | `bool` | false | Enable allocation tracking. Automatically enabled when `MaxAllocatedBytes` is set. |
| `OutlierMode` | `OutlierMode` | `IqrFence` | Outlier removal strategy applied before statistics are computed. |
| `ConfidenceLevel` | `double` | 0.95 | Confidence level for the margin-of-error calculation. |

See [Configuration](../reference/configuration.md) for a full explanation of each option.

## Baseline regression checks

Any integration package can compare the current run against a stored baseline. Save a baseline JSON file from a known-good run using `Benchmark.Run(...).ToJsonAsync(...)`, then reference it via `BaselinePath`.

```csharp
// xUnit - attribute pattern
[PerformanceFact(
    BaselinePath = "baselines/parse-json.json",
    MaxSlowdownRatio = 1.1)]   // fail if more than 10% slower
public void ParseJson() => JsonSerializer.Deserialize<MyDto>(Payload);
```

The test fails if:

- The baseline file is missing.
- The benchmark name is not found in the file.
- The measured median exceeds `baseline.median × MaxSlowdownRatio`.

See [Statistics: Significance Testing](../statistics/significance.md) for how the comparison is performed.

## Per-framework reference

- [xUnit integration](./xunit.md)
- [NUnit integration](./nunit.md)
- [MSTest integration](./mstest.md)
