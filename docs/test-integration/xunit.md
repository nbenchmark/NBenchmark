---
title: "Test integration: xUnit"
description: Run NBenchmark benchmarks as xUnit tests using PerformanceFact and PerformanceTheory attributes.
order: 1
---

# xUnit integration

`NBenchmark.Integration.xUnit` allows you to enforce performance thresholds on xUnit tests. To use it, replace `[Fact]` with `[PerformanceFact]` or `[Theory]` with `[PerformanceTheory]` and set threshold properties as named arguments. If any threshold is exceeded, the test fails.

## Installation

```bash
dotnet add package NBenchmark.Integration.xUnit
```

This command automatically installs `NBenchmark` and `NBenchmark.Integration.Abstractions`.

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

`PerformanceFact` discovers this test through the xUnit extensibility API. NBenchmark runs the method body as a benchmark (including warmup and measured samples) and compares the measured mean to `MaxMeanNs`. If the mean exceeds 500 $\mu$s, the test fails.

## [PerformanceFact]

`PerformanceFact` extends `FactAttribute`. All standard `[Fact]` properties - such as `DisplayName` and `Skip` - continue to work. You can add any threshold properties as named arguments.

```csharp
[PerformanceFact(
    MaxMeanNs = 100_000,
    MaxP95Ns  = 250_000,
    MaxAllocatedBytes = 1024,
    Samples = 500,
    WarmupSamples = 50)]
public void ProcessMessage()
{
    MessageProcessor.Process(SampleMessage);
}
```

### Async tests

`PerformanceFact` supports both `Task` and `ValueTask` return types:

```csharp
[PerformanceFact(MaxMeanNs = 2_000_000)]
public async Task FetchFromCache_Is_Fast_Enough()
{
    await _cache.GetAsync("key");
}
```

## [PerformanceTheory]

`PerformanceTheory` extends `TheoryAttribute`. Use it with any standard xUnit data source, such as `[InlineData]`, `[MemberData]`, or `[ClassData]`. NBenchmark runs the benchmark once per data row and reports each row as a separate test case.

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
> Thresholds apply to every data row. If you require different limits per row, split the test into separate `[PerformanceFact]` methods.
>
> `LaunchCount` is also spent per data row. For example, a theory with eight rows and `LaunchCount = 3` launches 24 workers.

## Threshold properties

For a complete list, see the [thresholds reference](./index.md#thresholds-reference). All properties are `init`-only.

```csharp
[PerformanceFact(
    MaxMeanNs        = 100_000,    // fail if mean > 100 us
    MaxP95Ns         = 300_000,    // fail if P95  > 300 us
    MaxAllocatedBytes = 4096,      // fail if mean allocs > 4 KiB per op
    MaxSlowdownRatio = 5.0,       // fail if >5x the calibration benchmark
    ReferenceMethod  = nameof(ReferenceImpl),  // compare against this method instead of calibration
    LaunchCount      = 3,          // measure the pair in 3 workers, for a paired ratio interval
    Samples       = 300,
    WarmupSamples = 30,
    OutlierMode      = OutlierMode.IqrFence,
    ConfidenceLevel  = 0.99)]
public void CriticalPath() { /* ... */ }

private static void ReferenceImpl() { /* ... */ }
```

## Failure output

When a threshold is violated, the test fails with a `PerformanceAssertException`. The error message lists every violated threshold:

```text
Performance thresholds exceeded for 'CriticalPath':
  - MeanNs 612,847.23 ns exceeds maximum 500,000.00 ns (excess: 112,847.23 ns)
  - P95 1,204,312.00 ns exceeds maximum 1,000,000.00 ns (excess: 204,312.00 ns)
```

## Inline assertions from a plain [Fact]

To benchmark only a part of a test while remaining within a regular `[Fact]`, use `Benchmark.Run` from the core package and inspect the result:

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

`BenchmarkAssert.Validate` is provided by `NBenchmark.Integration.Abstractions`, which is a transitive dependency of `NBenchmark.Integration.xUnit`.
