---
title: "Test integration: NUnit"
description: Run NBenchmark benchmarks as NUnit tests using the [Performance] attribute or PerformanceAssert.
order: 2
---

# NUnit integration

`NBenchmark.Integration.NUnit` allows you to enforce performance thresholds on NUnit tests. You can use it in two ways:

- **`[Performance]` attribute**: Runs the entire test method as a benchmark.
- **`PerformanceAssert`**: Benchmarks a specific piece of code inline from any test.

## Installation

```bash
dotnet add package NBenchmark.Integration.NUnit
```

This command automatically installs `NBenchmark` and `NBenchmark.Integration.Abstractions`.

## [Performance] attribute

Add `[Performance]` to any test method in place of (or in addition to) `[Test]`. NBenchmark runs the method body as a benchmark and checks the result against the configured thresholds. If a threshold is exceeded, the test fails.

```csharp
using NBenchmark.Integration.NUnit;
using NUnit.Framework;

[TestFixture]
public class SerializationTests
{
    [Performance(MaxMeanNs = 500_000)]
    public void Serialize_Is_Fast_Enough()
    {
        JsonSerializer.Serialize(new MyDto { Id = 1, Name = "test" });
    }
}
```

### Async tests

`[Performance]` supports `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` return types:

```csharp
[Performance(MaxMeanNs = 2_000_000)]
public async Task FetchFromCache_Is_Fast_Enough()
{
    await _cache.GetAsync("key");
}
```

### Parameterized tests

`[Performance]` works with NUnit's built-in parameterization attributes:

```csharp
[Performance(MaxMeanNs = 1_000_000)]
[TestCase(10)]
[TestCase(100)]
[TestCase(1_000)]
public void Sort_Scales_Reasonably(int size)
{
    var data = Enumerable.Range(0, size).Reverse().ToArray();
    Array.Sort(data);
}
```

NBenchmark benchmarks each test case independently and reports it as a separate NUnit test.

> [!NOTE]
> Thresholds apply to every test case. If you require different limits per case, split the test into separate methods or use `PerformanceAssert` for per-case control.

## Threshold properties

For a complete list, see the [thresholds reference](./index.md#thresholds-reference). All properties are `init`-only.

```csharp
[Performance(
    MaxMeanNs         = 100_000,   // fail if mean > 100 us
    MaxP95Ns          = 300_000,   // fail if P95  > 300 us
    MaxAllocatedBytes = 4096,      // fail if mean allocs > 4 KiB per op
    MaxSlowdownRatio  = 5.0,      // fail if >5x the calibration benchmark
    ReferenceMethod   = nameof(ReferenceImpl),  // compare against this method instead of calibration
    LaunchCount       = 3,         // measure the pair in 3 workers, for a paired ratio interval
    Samples        = 300,
    WarmupSamples  = 30,
    OutlierMode       = OutlierMode.IqrFence,
    ConfidenceLevel   = 0.99)]
public void CriticalPath() { /* ... */ }

private static void ReferenceImpl() { /* ... */ }
```

## PerformanceAssert

Use `PerformanceAssert` to benchmark a specific piece of code inside an existing test rather than benchmarking the entire test method.

### Synchronous benchmarks

```csharp
[Test]
public void Repository_Query_Is_Fast_Enough()
{
    var repo = new OrderRepository(connection);

    var result = PerformanceAssert.Run(
        () => repo.GetRecentOrders(limit: 100),
        new PerformanceAssertionOptions { MaxMeanNs = 2_000_000 },
        name: "GetRecentOrders");

    // result is a BenchmarkResult; you can inspect it further if needed
    Assert.That(result.MeanNs, Is.LessThan(3_000_000));
}
```

### Async benchmarks

```csharp
[Test]
public async Task Cache_Lookup_Is_Fast_Enough()
{
    await PerformanceAssert.RunAsync(
        async () => await _cache.GetAsync("key"),
        new PerformanceAssertionOptions { MaxMeanNs = 500_000 });
}
```

### Validating an existing BenchmarkResult

If you already have a `BenchmarkResult` from `Benchmark.Run`, call `PerformanceAssert.Validate` to assert against it:

```csharp
[Test]
public void Manually_Measured_Code_Meets_Threshold()
{
    var result = Benchmark.Run(() => DoWork(), name: "DoWork");

    PerformanceAssert.Validate(result, new PerformanceAssertionOptions
    {
        MaxMeanNs = 100_000,
        MaxP95Ns  = 200_000,
    });
}
```

### PerformanceAssertionOptions reference

`PerformanceAssertionOptions` exposes the same properties as the `[Performance]` attribute. All properties are optional; omitting a property disables the corresponding check.

```csharp
new PerformanceAssertionOptions
{
    MaxMeanNs         = 100_000,
    MaxP95Ns          = 300_000,
    MaxAllocatedBytes = 4096,
    MaxSlowdownRatio  = 5.0,       // calibration mode (assert pattern does not support ReferenceMethod)
    Samples        = 300,
    WarmupSamples  = 30,
    MeasureAllocations = true,
    OutlierMode       = OutlierMode.IqrFence,
    ConfidenceLevel   = 0.99,
}
```

## Failure output

When a threshold is violated, the test fails with an NUnit assertion failure. The error message lists every violated threshold:

```text
Performance thresholds exceeded for 'GetRecentOrders':
  - MeanNs 2,341,289.50 ns exceeds maximum 2,000,000.00 ns (excess: 341,289.50 ns)
```
