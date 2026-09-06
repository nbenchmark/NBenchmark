---
title: Parameter sweeps across input sizes
description: See how an algorithm scales across input sizes with parameterized Suite mode and Harness mode, and read the scaling trend in the output.
order: 4
---

# Parameter sweeps across input sizes

## Scenario

If you have an algorithm - such as a sort, a parse, a query, or a hash - you may want to see how it scales across different input sizes. A single measurement at `n=1000` does not indicate whether the complexity is O(n log n) or O(n²). However, a sweep across `n = 10, 100, 1000, 10000` reveals the scaling trend. Parameterized benchmarks run the same method body across multiple input values and produce one benchmark entry per parameter combination, allowing you to see the trend in a single table.

## Complete example

### Suite mode: `WithParameter`

Use the `WithParameter` method to define the input sizes:

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

This configuration produces four benchmarks: `sort(size=10)`, `sort(size=100)`, `sort(size=1_000)`, and `sort(size=10_000)`.

### Harness mode: `[Arguments]` and `[ArgumentsSource]`

For a small list of literal values, use the `[Arguments]` attribute:

```csharp
public class SortingBenchmarks
{
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1_000)]
    [Arguments(10_000)]
    [Benchmark]
    public void Sort(int n)
    {
        var arr = Enumerable.Range(0, n).Reverse().ToArray();
        Array.Sort(arr);
    }
}
```

For a generated sweep (such as powers of ten or file-backed inputs), use the `[ArgumentsSource]` attribute:

```csharp
public class SortingBenchmarks
{
    [ArgumentsSource(nameof(SortCases))]
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

### Comparing algorithms across a sweep

You can sweep multiple algorithms across the same inputs to compare their scaling trends against a baseline:

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

- **Declaring a sweep**: You can declare a sweep using `WithParameter("size", ...)` in suite mode, or `[Arguments(...)]` / `[ArgumentsSource(nameof(Source))]` in harness mode. Each value (or Cartesian product of multiple parameters) becomes a separate benchmark entry with a unique display name. For more information, see [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) and [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md).

- **Grouped significance**: Significance is grouped by parameter set. For example, benchmarks with `size=100` are compared against each other, not against those with `size=10_000`. A parameterized suite with *N* parameter combinations and *M* benchmark methods produces *N* separate significance comparisons, each over *M* benchmarks. This ensures that comparisons remain apples-to-apples.

- **Run order**: Use `RunOrder.Declaration` to run the sweep in size order, which makes the scaling trend easier to read in the output. `RunOrder.Random` (the default) shuffles benchmarks within each parameter group to guard against systematic bias, but it scrambles the visual trend.

- **Baseline expansion**: Using `WithBaseline("linear")` marks every `linear(size=...)` variant as a baseline. Each parameter group receives its own `linear(size=N)` baseline, and the `binary(size=N)` row is compared against it.

> [!NOTE] Single-method sweeps rank against the fastest point
> When you sweep a single method across parameter values without a second algorithm for comparison, every parameter group contains only one benchmark. In this case, the engine ranks every row against the fastest point. The `Ratio` column reports each point's scaling factor (with the fastest point as the `baseline`), and the `Sig` and `Mag` columns remain `-` because the engine does not test different workloads against one another.

## Run the benchmark

Execute the following commands based on your needs:

```bash
# Suite mode: Run the full sweep
dotnet run -c Release

# Harness mode: Filter to a subset of sizes by display name
dotnet run -c Release -- --filter "*n=1000*" --filter "*n=10000*"

# Pin the run for reproducible results across CI and local environments
dotnet run -c Release -- --samples 200 --warmup-samples 20 --order declaration
```

## Read the results

A single-method sweep produces a scaling table where the `Ratio` column provides the most useful signal:

```text
SortingBenchmarks
Benchmark    | n     | Median    | Mean      | Ops/s      | Ratio               | Sig | Mag | Alloc/op
-------------+-------+-----------+-----------+------------+---------------------+-----+-----+---------
Sort         |    10 |   31.2 ns |   32.7 ns | 30,567,164 | baseline            |  -  |  -  |    24 B
Sort         |   100 |  117.2 ns |  132.2 ns |  7,565,906 | 3.76x               |  -  |  -  |    24 B
Sort         |  1000 |    1.40 µs|   1.27 µs |    789,515 | 44.87x              |  -  |  -  | 4,048 B
Sort         | 10000 |   18.7 µs |  18.9 µs  |     53,476 | 599.0x              |  -  |  -  | 48,024 B
```

Interpret the trend as follows:
- **Median scaling**: The median scales roughly linearly (e.g., 10 → 100 is 3.76x, 100 → 1000 is 11.9x). This indicates a complexity closer to O(n log n) than O(n²), though with a super-linear constant.
- **Linear allocation growth**: The `Alloc/op` tracks the input size, which is expected when using `Enumerable.Range(...).ToArray()`.
- **Ratio against the fastest point**: The `baseline` is `n=10`; every other row shows how many times slower the method is compared to that baseline.

A two-algorithm sweep produces a comparison table grouped by parameter set, where each group has its own baseline and significance verdict:

```text
search
Benchmark | size | Median   | Mean     | Ops/s      | Ratio             | Sig | Mag    | Alloc/op
----------+------+----------+----------+------------+-------------------+-----+--------+---------
linear    |   10 |  90.0 ns |  91.2 ns | 11,111,111 | baseline          |  -  |  -      |    32 B
binary    |   10 |  85.0 ns |  86.1 ns | 11,764,706 | 0.94x             |  ✗  | neg    |    32 B
linear    |  100 | 250.0 ns | 252.1 ns |  4,000,000 | baseline          |  -  |  -      |    32 B
binary    |  100 | 110.0 ns | 112.4 ns |  9,090,909 | 0.44x             |  ✓  | large  |    32 B
```

In this example, `binary(size=100)` is 2.27x faster than `linear(size=100)` with a `large` effect and `✓` significance, while `binary(size=10)` is barely different and not significant. The scaling trend reveals where the algorithm choice becomes meaningful.

For a full explanation of indicators and warnings, see [Reading Your Results](../getting-started/reading-your-results.md).

## Next steps

For more information, see the following pages:

- [Parameterized benchmarks: Suite mode](../features/parameterized-suite.md) - Details on `WithParameter`, supported parameter types, and significance grouping.
- [Parameterized benchmarks: Harness mode](../features/parameterized-harness.md) - Details on `[Arguments]` vs. `[ArgumentsSource]` and filtering by display name.
- [Suite mode: Run order](../usage-modes/suite-mode.md#run-order) - Why declaration order is clearer for sweeps.
- [Configuration: Samples](../reference/configuration.md#samples) - Pinning the run for reproducible sweeps.
- [Reading Your Results](../getting-started/reading-your-results.md) - The full column reference and the behavior of the `Ratio` column.
