---
title: "Quick mode: Benchmark"
description: Measure a single piece of code with one call using Benchmark.Run or Benchmark.RunAsync.
order: 1
---

# Quick mode: Benchmark

`Benchmark` is the entry point for one-off measurements. It requires no class structure, no attributes, and no project setup beyond adding the NuGet reference. Use it anywhere you want a quick, reliable number.

## Basic usage

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    // code to measure
    for (int i = 0; i < 1000; i++) { }
});
```

`Benchmark.Run` runs 25 warmup iterations, then 200 measured iterations, trims the top 5% of [outliers](https://en.wikipedia.org/wiki/Outlier), and returns a `BenchmarkResult`.

## Overloads

### Synchronous

```csharp
// Action - for code with no return value
var result = Benchmark.Run(() => DoWork());

// Func<T> - for code that returns a value
// The result is consumed by a sink to prevent the compiler optimising the call away.
var result = Benchmark.Run(() => ComputeHash(data));
```

### Async

```csharp
// Func<Task>
var result = await Benchmark.RunAsync(async () => await FetchDataAsync());

// Func<Task<T>>
var result = await Benchmark.RunAsync(async () => await ComputeAsync(input));
```

### Raw outcome

`Benchmark.RunRaw` returns a `MeasurementOutcome` which includes both the `BenchmarkResult` and the raw per-iteration sample array. Use this if you need the underlying data.

```csharp
var outcome = Benchmark.RunRaw(() => DoWork());
double[] rawSamples = outcome.RawSamples;     // nanoseconds, before outlier trimming
BenchmarkResult result = outcome.Result;
```

## Custom options

Pass a `MeasurementOptions` instance to override the defaults:

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
    MeasureAllocations = true,
    ConfidenceLevel = 0.99,
};

var result = Benchmark.Run(() => MyMethod(), options: options);
```

See [Configuration](../configuration.md) for the full list of options.

## Naming the benchmark

The `name` parameter sets the label used in output and file reporters:

```csharp
var result = Benchmark.Run(() => MyMethod(), name: "MyMethod with 1000-item input");
```

## Displaying results

### Plain text (core package)

```csharp
result.Print();
```

Output:

```
  MyMethod: 1.20 µs median
    Mean: 1.24 µs, P95: 2.00 µs
    StdDev: 360 ns
    95% CI: 1.19 µs … 1.29 µs (±50 ns)
```

### Rich console table (NBenchmark.Reporters.Console)

```csharp
using NBenchmark.Reporters.Console;

await result.PrintAsync();
```

This runs the result through `ConsoleReporter` and renders a Spectre.Console table.

### File reporters

```csharp
await result.ToMarkdownAsync("results.md");
await result.ToCsvAsync("results.csv");
await result.ToJsonAsync("results/");   // output directory
```

## Accessing result fields directly

`BenchmarkResult` is a plain record - access any field directly:

```csharp
Console.WriteLine($"Median:  {result.Median} ns");
Console.WriteLine($"Mean:    {result.Mean} ns");
Console.WriteLine($"P95:     {result.P95} ns");
Console.WriteLine($"StdDev:  {result.StandardDeviation} ns");
Console.WriteLine($"Error:   ±{result.MarginOfError} ns ({result.ConfidenceLevel * 100:0}% CI)");
Console.WriteLine($"CI:      {result.ConfidenceIntervalLower} … {result.ConfidenceIntervalUpper} ns");

if (result.MeanAllocatedBytes.HasValue)
    Console.WriteLine($"Alloc:   {result.MeanAllocatedBytes.Value} bytes/op");
```

## What Benchmark does not do

- **It does not compare benchmarks.** Use [BenchmarkSuite](./suite-mode.md) for A/B comparisons.
- **It does not run significance testing** between multiple results. Significance testing requires paired raw samples and is handled by `BenchmarkSuite` and `BenchmarkHost`.

## Next steps

- [Suite mode: BenchmarkSuite](./suite-mode.md) - compare two or more implementations
- [Configuration](../configuration.md) - full options reference
- [Reporters](../reporters/) - save results to files
