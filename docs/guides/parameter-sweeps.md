---
title: Parameter sweeps across input sizes
description: See how an algorithm scales across input sizes with parameterized Suite mode and Harness mode, and read the scaling trend in the output.
order: 4
---

# Parameter sweeps across input sizes

## Scenario

You have an algorithm - a sort, a parse, a query, a hash - and you want to see how it scales across input sizes. A single number at `n=1000` doesn't tell you whether it's O(n log n) or O(n²); a sweep across `n = 10, 100, 1000, 10000` does. Parameterized benchmarks run the same method body across multiple input values and produce one benchmark entry per parameter combination, so the scaling trend is visible in a single table.

## Complete example

### Suite mode - `WithParameter`

```csharp
var results = await new BenchmarkSuite("sorting")
    .WithParameter("size", 10, 100, 1_000, 10_000)
    .Add("sort", (int size) =>
    {
        var arr = Enumerable.Range(0, size).Reverse().ToArray();
        Array.Sort(arr);
    })
    .WithRunOrder(RunOrder.Declaration)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

This produces four benchmarks: `sort(size=10)`, `sort(size=100)`, `sort(size=1_000)`, `sort(size=10_000)`.

### Harness mode - `[BenchmarkCase]` and `[BenchmarkCases]`

For a small literal list:

```csharp
public class SortingBenchmarks
{
    [BenchmarkCase(10)]
    [BenchmarkCase(100)]
    [BenchmarkCase(1_000)]
    [BenchmarkCase(10_000)]
    [Benchmark]
    public void Sort(int n)
    {
        var arr = Enumerable.Range(0, n).Reverse().ToArray();
        Array.Sort(arr);
    }
}
```

For a generated sweep (powers of ten, file-backed inputs, or any source method):

```csharp
public class SortingBenchmarks
{
    [BenchmarkCases(nameof(SortCases))]
    [Benchmark]
    public void Sort(int n)
    {
        var arr = Enumerable.Range(0, n).Reverse().ToArray();
        Array.Sort(arr);
    }

    public static IEnumerable<(int N)> SortCases()
    {
        for (var p = 1; p <= 7; p++)
            yield return ((int)Math.Pow(10, p));
    }
}
```

### Comparing algorithms across the same sweep

The real power is sweeping two or more algorithms across the same inputs and reading the scaling trend against the baseline:

```csharp
var results = await new BenchmarkSuite("search")
    .WithParameter("size", 10, 100, 1_000, 10_000)
    .Add("linear", (int size) => LinearSearch(size))
    .Add("binary", (int size) => BinarySearch(size))
    .WithBaseline("linear")
    .WithRunOrder(RunOrder.Declaration)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

## What's happening

- **`WithParameter("size", ...)`** (Suite) / **`[BenchmarkCase(...)]`** / **`[BenchmarkCases(nameof(Source))]`** (Harness) - the three ways to declare a sweep. Each value (or Cartesian product across multiple parameters) becomes a separate benchmark entry with a distinct display name. See [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) and [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md).

- **Significance is grouped by parameter set.** The `size=100` benchmarks are compared against each other, not against `size=10_000`. A parameterized suite with `N` parameter combinations and `M` benchmark methods produces `N` separate significance comparisons, each over `M` benchmarks - rather than one flat comparison over `N * M` results. This keeps each comparison apples-to-apples.

- **Run order.** `RunOrder.Declaration` runs the sweep in size order, which makes the scaling trend easy to read in the output. `RunOrder.Random` (the default) shuffles within each parameter group, which guards against systematic bias but scrambles the trend. For a sweep you're reading visually, declaration order is usually clearer.

- **Baselines expand across parameters.** `WithBaseline("linear")` marks every `linear(size=...)` variant as a baseline. Each parameter group gets its own `linear(size=N)` baseline, and the `binary(size=N)` row is compared against it.

> [!NOTE] Single-method sweeps rank against the fastest point
> When a single method is swept across parameter values (no second algorithm to compare), every parameter group holds just one benchmark, so there's no within-group comparison. The table instead ranks every row against the fastest point: the `Ratio` column reports each point's scaling factor (the fastest point is the `baseline`), and `Sig` / `Mag` stay `-`, because the engine does not test different workloads against one another. This makes scaling trends easy to read.

## Run it

```bash
# Suite mode - the full sweep
dotnet run -c Release

# Harness mode - filter to a subset of sizes by display name
dotnet run -c Release -- --filter "*n=1000*" --filter "*n=10000*"

# Pin the run so the sweep is reproducible across CI and local
dotnet run -c Release -- --iterations 200 --warmup 20 --order declaration
```

## Read the results

A single-method sweep produces a scaling table where the `Ratio` column is the most useful signal:

```text
SortingBenchmarks
Benchmark    | n     | Median    | Mean      | Ops/s      | Ratio               | Sig | Mag | Alloc/op
-------------+-------+-----------+-----------+------------+---------------------+-----+-----+---------
Sort         |    10 |   31.2 ns |   32.7 ns | 30,567,164 | baseline            |  -  |  -  |    24 B
Sort         |   100 |  117.2 ns |  132.2 ns |  7,565,906 | 3.76x               |  -  |  -  |    24 B
Sort         |  1000 |    1.40 µs|   1.27 µs |    789,515 | 44.87x              |  -  |  -  | 4,048 B
Sort         | 10000 |   18.7 µs |  18.9 µs  |     53,476 | 599.0x              |  -  |  -  | 48,024 B
```

Reading the trend:

- **Median × 10 per 10× input** - the median scales roughly linearly (10 → 100 is 3.76x, 100 → 1000 is 11.9x, 1000 → 10000 is 13.4x). Closer to O(n log n) than O(n²) (which would be 10x per step) but with a super-linear constant.
- **Alloc/op growing linearly with `n`** - the allocation tracks the input size, which is expected for `Enumerable.Range(...).ToArray()`.
- **Ratio against the fastest point** - the `baseline` is `n=10`; every other row shows how many times slower it is than that.

A two-algorithm sweep produces a comparison table grouped by parameter set, where each group has its own baseline and its own significance verdict:

```text
search
Benchmark | size | Median   | Mean     | Ops/s      | Ratio             | Sig | Mag    | Alloc/op
----------+------+----------+----------+------------+-------------------+-----+--------+---------
linear    |   10 |  90.0 ns |  91.2 ns | 11,111,111 | baseline          |  -  |  -      |    32 B
binary    |   10 |  85.0 ns |  86.1 ns | 11,764,706 | 0.94x             |  ✗  | neg    |    32 B
linear    |  100 | 250.0 ns | 252.1 ns |  4,000,000 | baseline          |  -  |  -      |    32 B
binary    |  100 | 110.0 ns | 112.4 ns |  9,090,909 | 0.44x             |  ✓  | large  |    32 B
```

Here `binary(size=100)` is 2.27x faster than `linear(size=100)` with a `large` effect and `✓` significance, while `binary(size=10)` is barely different and not significant. The scaling trend tells you where the algorithm choice starts to matter.

See [Reading Your Results](../getting-started/reading-your-results.md) for every column, indicator, and warning.

## When to go deeper

- [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) - `WithParameter` for up to 3 parameters, mixed parameterized and plain benchmarks, supported parameter types, baselines, and significance grouping.
- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - `[BenchmarkCase]` vs. `[BenchmarkCases]`, named-tuple display names, `--filter` by display name, the Suite vs. Harness comparison table.
- [Suite mode: Run order](../usage-modes/suite-mode.md#run-order) - why declaration order is usually clearer for sweeps, and why randomization is the default for comparisons.
- [Configuration: Iterations](../reference/configuration.md#iterations) - pinning the run for reproducible sweeps across CI and local.
- [Reading Your Results](../getting-started/reading-your-results.md) - the full column reference, including how the `Ratio` column behaves for single-method vs. multi-method sweeps.
