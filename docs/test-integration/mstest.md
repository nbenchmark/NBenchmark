---
title: "Test integration: MSTest"
description: Run NBenchmark benchmarks as MSTest tests using [PerformanceTestMethod] or PerformanceAssert.
order: 3
---

# MSTest integration

`NBenchmark.Integration.MSTest` lets you enforce performance thresholds on MSTest tests. There are two ways to use it:

- **`[PerformanceTestMethod]` attribute** - the entire test method is run as a benchmark.
- **`PerformanceAssert`** - benchmark a specific piece of code inline from any test.

## Installation

```bash
dotnet add package NBenchmark.Integration.MSTest
```

This automatically pulls in `NBenchmark` and `NBenchmark.Integration.Abstractions`.

## [PerformanceTestMethod]

Add `[PerformanceTestMethod]` in place of `[TestMethod]`. The method body is run as a benchmark and the result is checked against the configured thresholds. If a threshold is exceeded the test fails.

```csharp
using NBenchmark.Integration.MSTest;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class SerializationTests
{
    [PerformanceTestMethod(MaxMeanNs = 500_000)]
    public void Serialize_Is_Fast_Enough()
    {
        JsonSerializer.Serialize(new MyDto { Id = 1, Name = "test" });
    }
}
```

### Async tests

```csharp
[PerformanceTestMethod(MaxMeanNs = 2_000_000)]
public async Task FetchFromCache_Is_Fast_Enough()
{
    await _cache.GetAsync("key");
}
```

Both `Task` and `Task<T>` return types are supported.

### Data-driven tests

`[PerformanceTestMethod]` works with MSTest's data-driven attributes:

```csharp
[PerformanceTestMethod(MaxMeanNs = 1_000_000)]
[DataRow(10)]
[DataRow(100)]
[DataRow(1_000)]
public void Sort_Scales_Reasonably(int size)
{
    var data = Enumerable.Range(0, size).Reverse().ToArray();
    Array.Sort(data);
}
```

Each data row is benchmarked independently and reported as a separate test case.

> [!NOTE]
> Thresholds apply to every data row. If you need different limits per row, split into separate methods or use `PerformanceAssert` (see below) for per-row control.

## Threshold properties

See the [thresholds reference](./index.md#thresholds-reference) for the complete list. All properties are `init`-only.

```csharp
[PerformanceTestMethod(
    MaxMeanNs         = 100_000,   // fail if mean > 100 us
    MaxP95Ns          = 300_000,   // fail if P95  > 300 us
    MaxAllocatedBytes = 4096,      // fail if mean allocs > 4 KiB per op
    MaxSlowdownRatio  = 5.0,      // fail if >5x the calibration benchmark
    ReferenceMethod   = nameof(ReferenceImpl),  // compare against this method instead of calibration
    Iterations        = 300,
    WarmupIterations  = 30,
    OutlierMode       = OutlierMode.IqrFence,
    ConfidenceLevel   = 0.99)]
public void CriticalPath() { /* ... */ }

private static void ReferenceImpl() { /* ... */ }
```

## PerformanceAssert

Use `PerformanceAssert` when you want to benchmark a specific piece of code inside an existing test, rather than benchmarking the entire test method.

### Synchronous

```csharp
[TestMethod]
public void Repository_Query_Is_Fast_Enough()
{
    var repo = new OrderRepository(connection);

    var result = PerformanceAssert.Run(
        () => repo.GetRecentOrders(limit: 100),
        new PerformanceAssertionOptions { MaxMeanNs = 2_000_000 },
        name: "GetRecentOrders");

    // result is a BenchmarkResult - inspect it further if needed
    Assert.IsTrue(result.Mean < 3_000_000);
}
```

### Async

```csharp
[TestMethod]
public async Task Cache_Lookup_Is_Fast_Enough()
{
    await PerformanceAssert.RunAsync(
        async () => await _cache.GetAsync("key"),
        new PerformanceAssertionOptions { MaxMeanNs = 500_000 });
}
```

### Validate an existing BenchmarkResult

If you already have a `BenchmarkResult` from `Benchmark.Run`, call `PerformanceAssert.Validate` to assert against it:

```csharp
[TestMethod]
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

`PerformanceAssertionOptions` exposes the same properties as the `[PerformanceTestMethod]` attribute. All properties are optional; omitting them disables the corresponding check.

```csharp
new PerformanceAssertionOptions
{
    MaxMeanNs          = 100_000,
    MaxP95Ns           = 300_000,
    MaxAllocatedBytes  = 4096,
    MaxSlowdownRatio   = 5.0,      // calibration mode (assert pattern does not support ReferenceMethod)
    Iterations         = 300,
    WarmupIterations   = 30,
    MeasureAllocations = true,
    OutlierMode        = OutlierMode.IqrFence,
    ConfidenceLevel    = 0.99,
}
```

## Failure output

When a threshold is violated the test fails with a `PerformanceAssertException` (which extends `AssertFailedException`). The message lists every violated threshold:

```
Performance thresholds exceeded for 'GetRecentOrders':
  - Mean 2,341,289.50 ns exceeds maximum 2,000,000.00 ns (excess: 341,289.50 ns)
```
