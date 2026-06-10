---
title: "Integration: NUnit"
description: Run NBenchmark benchmarks as NUnit tests using the [Performance] attribute or PerformanceAssert.
order: 2
---

# NUnit integration

`NBenchmark.Integration.NUnit` lets you enforce performance thresholds on NUnit tests. There are two ways to use it:

- **`[Performance]` attribute** - the entire test method is run as a benchmark.
- **`PerformanceAssert`** - benchmark a specific piece of code inline from any test.

## Installation

```bash
dotnet add package NBenchmark.Integration.NUnit
```

This automatically pulls in `NBenchmark` and `NBenchmark.Integration.Abstractions`.

## [Performance] attribute

Add `[Performance]` to any test method in place of (or in addition to) `[Test]`. The method body is run as a benchmark and the result is checked against the configured thresholds. If a threshold is exceeded the test fails.

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

```csharp
[Performance(MaxMeanNs = 2_000_000)]
public async Task FetchFromCache_Is_Fast_Enough()
{
    await _cache.GetAsync("key");
}
```

`Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` return types are all supported.

### Parameterised tests

`[Performance]` works together with NUnit's built-in parameterisation attributes:

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

Each test case is benchmarked independently and reported as a separate NUnit test.

> [!NOTE]
> Thresholds apply to every test case. If you need different limits per case, split into separate methods or use `PerformanceAssert` (see below) for per-case control.

## Threshold properties

See the [thresholds reference](./index.md#thresholds-reference) for the complete list. All properties are `init`-only.

```csharp
[Performance(
    MaxMeanNs         = 100_000,   // fail if mean > 100 µs
    MaxP95Ns          = 300_000,   // fail if P95  > 300 µs
    MaxAllocatedBytes = 4096,      // fail if mean allocs > 4 KiB per op
    BaselinePath      = "baselines/my-benchmark.json",
    MaxSlowdownRatio  = 1.15,      // fail if >15% slower than baseline
    Iterations        = 300,
    WarmupIterations  = 30,
    OutlierMode       = OutlierMode.IqrFence,
    ConfidenceLevel   = 0.99)]
public void CriticalPath() { /* ... */ }
```

## PerformanceAssert

Use `PerformanceAssert` when you want to benchmark a specific piece of code inside an existing test, rather than benchmarking the entire test method.

### Synchronous

```csharp
[Test]
public void Repository_Query_Is_Fast_Enough()
{
    var repo = new OrderRepository(connection);

    var result = PerformanceAssert.Run(
        () => repo.GetRecentOrders(limit: 100),
        new PerformanceAssertionOptions { MaxMeanNs = 2_000_000 },
        name: "GetRecentOrders");

    // result is a BenchmarkResult - inspect it further if needed
    Assert.That(result.Mean, Is.LessThan(3_000_000));
}
```

### Async

```csharp
[Test]
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

`PerformanceAssertionOptions` exposes the same properties as the `[Performance]` attribute. All properties are optional; omitting them disables the corresponding check.

```csharp
new PerformanceAssertionOptions
{
    MaxMeanNs         = 100_000,
    MaxP95Ns          = 300_000,
    MaxAllocatedBytes = 4096,
    BaselinePath      = "baselines/my-benchmark.json",
    MaxSlowdownRatio  = 1.1,
    Iterations        = 300,
    WarmupIterations  = 30,
    MeasureAllocations = true,
    OutlierMode       = OutlierMode.IqrFence,
    ConfidenceLevel   = 0.99,
}
```

## Failure output

When a threshold is violated the test fails with an NUnit assertion failure. The message lists every violated threshold:

```
Performance thresholds exceeded for 'GetRecentOrders':
  - Mean 2,341,289.50 ns exceeds maximum 2,000,000.00 ns (excess: 341,289.50 ns)
```
