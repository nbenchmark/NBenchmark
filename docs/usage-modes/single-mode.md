---
title: "Single mode: Benchmark"
description: Measure a single piece of code with one call using Benchmark.Run or Benchmark.RunAsync.
order: 1
---

# Single mode: Benchmark

`Benchmark` is the entry point for one-off measurements. It requires no class structure, attributes, or project setup beyond adding the NuGet reference. Use it whenever you need a quick, reliable measurement.

## Basic usage

```csharp
using NBenchmark;

var result = Benchmark.Run(() =>
{
    // code to measure
    for (int i = 0; i < 1000; i++) { }
});
```

`Benchmark.Run` warms up the code until the timings plateau and collects measured samples until the confidence interval is sufficiently tight. It then trims outliers using the IQR fence rule and returns a `BenchmarkResult`.

## Overloads

### Synchronous benchmarks

```csharp
// Action - for code with no return value
var result = Benchmark.Run(() => DoWork());

// Func<T> - returns a value so the runner can prevent dead-code elimination
var result = Benchmark.Run(() => Fibonacci(20));
```

### Async benchmarks

```csharp
// Func<Task>
var result = await Benchmark.RunAsync(async () => await FetchDataAsync());

// Func<Task<T>>
var result = await Benchmark.RunAsync(async () => await ComputeAsync(input));
```

### Prepared state

If you are benchmarking code that uses data that must be built once, pass the preparation as its own delegate. The `prepare` delegate runs once before warmup in the worker on an isolated run, and the body receives the result:

```csharp
var result = Benchmark.Run(
    prepare: () => BuildData(),
    body:    d => Sort(d));
```

This pattern is also available as `RunAsync`, for a `Task`-returning body.

Optional `setup:` and `teardown:` hooks receive the state and run outside the timed window - per iteration, before and after the body. This allows you to reset state for a body that mutates its data between iterations:

```csharp
var result = Benchmark.Run(
    prepare: () => BuildData(),
    body:    d => Sort(d),
    setup:   d => Shuffle(d));
```

A `prepare` delegate can capture its own locals. For example, `prepare: () => BuildData(size)` sends the `size` and builds the data in the worker. If you prefer to name the input explicitly, use the parameterized overload:

```csharp
var result = Benchmark.Run(
    prepare:         (int size) => BuildData(size),
    prepareArgument: 100_000,
    body:            d => Sort(d));
```

### Raw samples

`BenchmarkResult.RawSamples` carries every measured sample, in sample order, before outlier
trimming:

```csharp
var result = Benchmark.Run(() => DoWork());
IReadOnlyList<double> rawSamples = result.RawSamples;   // nanoseconds per op
```

`result.TrimmedOrdinals` indexes into it, so you can see which samples the outlier detector
removed.

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

For a full list of options, see [Configuration](../reference/configuration.md).

## Naming the benchmark

Use the `name` parameter to set the label used in output and file reporters:

```csharp
var result = Benchmark.Run(() => MyMethod(), name: "MyMethod with 1000-item input");
```

## Displaying results

### Plain text (core package)

Calling `Print()` defaults to `ReportDetail.Simple`:

```csharp
result.Print();
```

The output is similar to the following:

```text
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 342.1 ns       Ops/s: 2.87 Mops/s
  │  Alloc/op: 0 B
  │
  │  Measured in an isolated worker under 'steady-state'.
  │
  └─────────────────────────────────────────────────
```

Pass a detail level to see the full distribution:

```csharp
result.Print(ReportDetail.Standard);
```

The output is similar to the following:

```text
  ┌─ Benchmark ─────────────────────────────────────
  │
  │  Median: 342.1 ns       Mean: 348.7 ns
  │  Ops/s:  2.87 Mops/s    Median ops/s: 2.92 Mops/s
  │  P95: 361.2 ns  P99: 378.5 ns  P99.9: 380.0 ns
  │  StdDev: 8.3 ns         CV:   2.38%
  │  Error:  ±3.1 ns (0.89% of Mean)
  │  CI:     [345.6 ns … 351.8 ns] (95%)
  │  Alloc/op: 0 B
  │
  │  Measured in an isolated worker under 'steady-state'.
  │
  └─────────────────────────────────────────────────
```

`ReportDetail.Advanced` adds sample counts, quartiles, fences, skewness, kurtosis, MAD, and an allocation breakdown.

### Rich console table (NBenchmark.Reporters.Console)

```csharp
using NBenchmark.Reporters.Console;

await result.PrintAsync();
```

This method runs the result through `ConsoleReporter` and renders a Spectre.Console table.

### File reporters

```csharp
await result.ToMarkdownAsync("results.md");
await result.ToCsvAsync("results.csv");
await result.ToJsonAsync("results/");   // output directory
```

## Accessing result fields directly

`BenchmarkResult` is a plain record. You can access any field directly:

```csharp
Console.WriteLine($"Median:  {result.Median} ns");
Console.WriteLine($"Mean:    {result.Mean} ns");
Console.WriteLine($"Ops/s:   {result.OperationsPerSecond}");
Console.WriteLine($"P95:     {result.GetPercentile(0.95)} ns");
Console.WriteLine($"StdDev:  {result.StandardDeviation} ns");
Console.WriteLine($"Error:   ±{result.MarginOfError} ns ({result.ConfidenceLevel * 100:0}% CI)");
Console.WriteLine($"CI:      {result.ConfidenceIntervalLower} … {result.ConfidenceIntervalUpper} ns");

if (result.MeanAllocatedBytes.HasValue)
    Console.WriteLine($"Alloc:   {result.MeanAllocatedBytes.Value} bytes/op");
```

## Where measurements occur

By default, `Benchmark.Run` measures the body in a dedicated worker process. This ensures the result is reproducible; measuring the same body in the host process can read more than 20x wrong until the JIT promotes the method.

Ordinary captured data - such as an `int`, a `string`, an `int[]`, or a record containing these - is sent to the worker by value. Most bodies isolate without requiring a rewrite. For state that the worker should build rather than receive, use the `prepare:` / `body:` overload.

Use `Benchmark.RunInProcess` to measure in the current process. This is the correct choice when the process itself is the subject, such as measuring cold-start costs or observing host state like a warm cache or an open connection:

```csharp
var cold = Benchmark.RunInProcess(() => ColdStartSensitivePath(), name: "cold path");
```

You can optionally call `Benchmark.Warmup()` to start a worker in the background so your first measured call does not incur the launch delay (approximately 70 ms).

For more information, see [Isolated runs](../features/isolated-runs.md).

## What Benchmark does not do

- **Compare benchmarks**: Use [Suite mode: BenchmarkSuite](./suite-mode.md) for A/B comparisons.
- **Run significance testing** between multiple results: Significance testing requires paired raw samples and is handled by `BenchmarkSuite` and `BenchmarkHarness`.

## Next steps

- [Suite mode: BenchmarkSuite](./suite-mode.md) - Compare two or more implementations
- [Configuration](../reference/configuration.md) - Full options reference
- [Reporters](../output/index.md) - Save results to files
