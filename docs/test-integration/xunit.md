---
title: "Test integration: xUnit"
description: Run NBenchmark benchmarks as xUnit tests using PerformanceFact and PerformanceTheory attributes.
order: 1
---

# xUnit integration

`NBenchmark.Integration.xUnit` lets you enforce performance thresholds on xUnit tests. Replace `[Fact]` with `[PerformanceFact]` or `[Theory]` with `[PerformanceTheory]` and set threshold properties as named arguments. If any threshold is exceeded the test fails.

## Installation

```bash
dotnet add package NBenchmark.Integration.xUnit
```

This automatically pulls in `NBenchmark` and `NBenchmark.Integration.Abstractions`.

## Quick start

```csharp
using NBenchmark.Integration.xUnit;

public class SerializationTests
{
    [PerformanceFact(MaxMeanNs = 500_000)]
    public void Serialize_Is_Fast_Enough()
    {
        JsonSerializer.Serialize(new MyDto { Id = 1, Name = "test" });
    }
}
```

`PerformanceFact` discovers this test through xUnit's extensibility API. The method body is run as a benchmark (warmup + measured iterations) and the measured mean is compared to `MaxMeanNs`. If the mean exceeds 500 µs the test fails.

## [PerformanceFact]

`PerformanceFact` extends `FactAttribute`. Every `[Fact]` property (`DisplayName`, `Skip`, etc.) continues to work. Add any threshold properties as named arguments.

```csharp
[PerformanceFact(
    MaxMeanNs = 100_000,
    MaxP95Ns  = 250_000,
    MaxAllocatedBytes = 1024,
    Iterations = 500,
    WarmupIterations = 50)]
public void ProcessMessage()
{
    MessageProcessor.Process(SampleMessage);
}
```

### Async tests

```csharp
[PerformanceFact(MaxMeanNs = 2_000_000)]
public async Task FetchFromCache_Is_Fast_Enough()
{
    await _cache.GetAsync("key");
}
```

Both `Task` and `ValueTask` return types are supported.

## [PerformanceTheory]

`PerformanceTheory` extends `TheoryAttribute`. Use it together with any standard xUnit data source (`[InlineData]`, `[MemberData]`, `[ClassData]`). The benchmark runs once per data row and each row is reported as a separate test case.

```csharp
[PerformanceTheory(MaxMeanNs = 1_000_000)]
[InlineData(10)]
[InlineData(100)]
[InlineData(1_000)]
public void Sort_Scales_Reasonably(int size)
{
    var data = Enumerable.Range(0, size).Reverse().ToArray();
    Array.Sort(data);
}
```

> [!NOTE]
> Thresholds apply to every data row. If you need different limits per row, split the test into separate `[PerformanceFact]` methods.

## Threshold properties

See the [thresholds reference](./index.md#thresholds-reference) for the complete list. All properties are `init`-only.

```csharp
[PerformanceFact(
    MaxMeanNs        = 100_000,    // fail if mean > 100 us
    MaxP95Ns         = 300_000,    // fail if P95  > 300 us
    MaxAllocatedBytes = 4096,      // fail if mean allocs > 4 KiB per op
    MaxSlowdownRatio = 5.0,       // fail if >5x the calibration benchmark
    ReferenceMethod  = nameof(ReferenceImpl),  // compare against this method instead of calibration
    Iterations       = 300,
    WarmupIterations = 30,
    OutlierMode      = OutlierMode.IqrFence,
    ConfidenceLevel  = 0.99)]
public void CriticalPath() { /* ... */ }

private static void ReferenceImpl() { /* ... */ }
```

## Failure output

When a threshold is violated the test fails with a `PerformanceAssertException`. The message lists every violated threshold:

```
Performance thresholds exceeded for 'CriticalPath':
  - Mean 612,847.23 ns exceeds maximum 500,000.00 ns (excess: 112,847.23 ns)
  - P95 1,204,312.00 ns exceeds maximum 1,000,000.00 ns (excess: 204,312.00 ns)
```

## Inline assertions from a plain [Fact]

If you need to benchmark only part of a test and you want to stay within a regular `[Fact]`, use `Benchmark.Run` from the core package and inspect the result:

```csharp
using NBenchmark;
using NBenchmark.Integration.Abstractions;
using Xunit;

[Fact]
public void Critical_Section_Is_Fast()
{
    // setup ...

    var result = Benchmark.Run(() => CriticalSection());

    var violations = BenchmarkAssert.Validate(result, new PerformanceThresholds
    {
        MaxMeanNs = 200_000,
    });

    Assert.Empty(violations);
}
```

`BenchmarkAssert.Validate` is in `NBenchmark.Integration.Abstractions`, which is already a transitive dependency of `NBenchmark.Integration.xUnit`.
