---
title: Configuration
description: All MeasurementOptions settings with their defaults and valid ranges.
order: 3
---

# Configuration

All measurement settings are controlled by `MeasurementOptions`. The defaults are sensible for most benchmarks - only change what you have a reason to change.

## Using MeasurementOptions

### With Benchmark (Quick mode)

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
};

var result = Benchmark.Run(() => MyMethod(), options: options);
```

### With BenchmarkSuite (Suite mode)

Use the fluent `With*` methods - they each update a single option:

```csharp
await new BenchmarkSuite("name")
    .WithIterations(500)
    .WithWarmup(50)
    .WithAllocations()
    .WithOutlierMode(OutlierMode.IqrFence)
    .WithConfidenceLevel(0.99)
    .RunAsync();
```

### With BenchmarkHost (Host mode)

Call `WithOptions` or use CLI flags. CLI flags always take priority over `WithOptions`:

```csharp
BenchmarkHost.Create(args)
    .WithOptions(new MeasurementOptions { Iterations = 500 })
    ...
```

```bash
dotnet run -- --iterations 500 --warmup 50
```

## Options reference

### Iterations

```csharp
Iterations = 200   // default
```

The number of measured iterations per benchmark. Valid range: `0` to `100 000`.

A value of `0` (combined with `WarmupIterations = 0`) is the dry-run signal: the body is not invoked and no measurements are taken. See [CLI Reference: `--dry-run`](./cli-reference.md#--dry-run).

More iterations produce a tighter confidence interval (smaller Error column) at the cost of a longer run time. The default of 200 is a good balance for most benchmarks.

::: tip
If you see a large Error (margin of error) relative to the mean, try increasing iterations to `500` or `1000`.
:::

### WarmupIterations

```csharp
WarmupIterations = 25   // default
```

The number of warmup iterations before measurement begins. Valid range: `0` to `10 000`.

Warmup lets the JIT compiler optimise your code and brings data into CPU caches. See [Key Concepts: Warmup](./getting-started/key-concepts.md#warmup) for more detail.

CLI flag: `--warmup <n>`

### ForceGcBeforeEachIteration

```csharp
ForceGcBeforeEachIteration = true   // default
```

When `true`, a gen-0 GC collection is triggered before each measured iteration. This keeps allocation side-effects from previous iterations out of your measurement.

Disable it if your benchmark intentionally tests GC pressure or allocation-heavy paths where the cumulative effect is the point.

### MeasureAllocations

```csharp
MeasureAllocations = false   // default
```

When `true`, NBenchmark samples `GC.GetAllocatedBytesForCurrentThread` around each iteration and reports the mean bytes allocated per operation in the **Alloc/op** column (with a process-wide fallback for async thread hops).

BenchmarkSuite fluent method: `.WithAllocations()`

::: info
Allocation tracking adds a small overhead to each iteration and may slightly affect timing measurements.
:::

### OutlierMode

```csharp
OutlierMode = OutlierMode.RemoveTop5Percent   // default
```

Controls which samples are discarded before statistics are computed.

| Value | Behaviour |
|---|---|
| `OutlierMode.None` | No samples are removed. |
| `OutlierMode.RemoveTop5Percent` | The slowest 5% of samples are removed. **(default)** |
| `OutlierMode.RemoveTopAndBottom5Percent` | The slowest and fastest 5% are removed. |
| `OutlierMode.IqrFence` | Samples beyond 1.5× the [IQR (inter-quartile range)](https://en.wikipedia.org/wiki/Interquartile_range) are removed. |

With 200 iterations and `RemoveTop5Percent`, the 10 noisiest measurements are discarded. This guards against OS scheduling spikes and thermal throttling without discarding too much data.

BenchmarkSuite fluent method: `.WithOutlierMode(mode)`

### ConfidenceLevel

```csharp
ConfidenceLevel = 0.95   // default
```

The confidence level for the margin of error reported in the Error column. Must be strictly between `0` and `1`.

| Value | Meaning |
|---|---|
| `0.90` | 90% confidence - narrower interval, less conservative |
| `0.95` | 95% confidence - the standard choice **(default)** |
| `0.99` | 99% confidence - wider interval, more conservative |

A higher confidence level produces a wider (larger) Error value. Use `0.99` when a result will be used to make an important decision and you want to be very conservative.

BenchmarkSuite fluent method: `.WithConfidenceLevel(0.99)`  
CLI flag: `--confidence 0.99`

### EnableSignificance

```csharp
EnableSignificance = true   // default
```

When `true` and there are two or more benchmarks, NBenchmark runs a [Mann-Whitney U test](https://en.wikipedia.org/wiki/Mann%E2%80%93Whitney_U_test) to determine whether the difference in medians is statistically significant (p < 0.05).

Disable it if you don't need significance testing and want to reduce overhead:

```csharp
.WithSignificance(false)
```

### ForceGcBetweenBenchmarks

```csharp
ForceGcBetweenBenchmarks = true   // default
```

When `true`, a full gen-2 GC collection runs between benchmarks to clean up any heap allocations from the previous benchmark before the next one begins.

## Applying options per-method (Host mode)

In Host mode, the `[Benchmark]` attribute accepts per-method overrides that take priority over the host-level options:

```csharp
// This method uses 1000 iterations regardless of the host setting.
[Benchmark(Iterations = 1000, WarmupIterations = 100)]
public void MyExpensiveBenchmark() => SlowOperation();
```

## Valid ranges summary

| Option | Type | Default | Valid range |
|---|---|---|---|
| `Iterations` | `int` | `200` | `0` – `100 000` |
| `WarmupIterations` | `int` | `25` | `0` – `10 000` |
| `ConfidenceLevel` | `double` | `0.95` | `>0` and `<1` |
| `ForceGcBeforeEachIteration` | `bool` | `true` | - |
| `MeasureAllocations` | `bool` | `false` | - |
| `OutlierMode` | `enum` | `RemoveTop5Percent` | See above |
| `EnableSignificance` | `bool` | `true` | - |
| `ForceGcBetweenBenchmarks` | `bool` | `true` | - |

Values outside the valid range throw `ArgumentOutOfRangeException`.

---

**Still having issues?** See the [Troubleshooting guide](./troubleshooting.md) for symptom-to-configuration mappings for common measurement problems.
