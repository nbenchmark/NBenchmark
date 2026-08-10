---
title: "Single mode: Benchmark"
description: Measure a single piece of code with one call using Benchmark.Run or Benchmark.RunAsync.
order: 1
---

# Single mode: Benchmark

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

`Benchmark.Run` warms up until the timings plateau, collects measured samples until the confidence interval is tight enough, trims outliers using the IQR fence rule, and returns a `BenchmarkResult`.

## Isolation

`Benchmark.Run` measures the body in a dedicated worker process by default, not in the process that called it. The same body measured both ways can differ by more than 20× until the host's JIT happens to promote it, and nothing in the reported confidence interval hints at that. A worker starts under a controlled runtime profile, so the number reflects the body rather than whatever the host was doing beforehand.

A body that **captures a local** is the one shape that cannot cross that boundary: the captured value exists in this process and nowhere else, and a fabricated replacement returns plausible, silently wrong numbers rather than throwing. Such a body is measured in this process, stamped `IsolationStatus.InProcessCapturedState`, and reported with the reason. To isolate it, split the preparation into its own delegate so both halves capture nothing and the worker builds the data itself:

```csharp
var data = BuildData();

// Captures `data` -> measured in this process and labelled
var result = Benchmark.Run(() => Sort(data));

// Both halves capture nothing -> measured in a worker
var result = Benchmark.Run(
    prepare: () => BuildData(),
    body:    d => Sort(d));
```

`Benchmark.RunInProcess` is the opposite choice - not a fallback, but the correct one when the current process *is* the subject: cold-start and first-call cost, or a body that must observe host state such as a warm cache or an open connection. It measures here deliberately, with no warning, and stamps `IsolationStatus.InProcessRequested`:

```csharp
var cold = Benchmark.RunInProcess(() => ColdStartSensitivePath(), name: "cold path");
```

`Benchmark.Warmup()` optionally starts a worker in the background so the first measured call does not pay the roughly 70 ms launch.

See [Isolated runs](../features/isolated-runs.md) for the full mechanism, the `Iso` column, and what else cannot cross.

## Overloads

### Synchronous

```csharp
// Action - for code with no return value
var result = Benchmark.Run(() => DoWork());

// Func<T> - returns a value so the runner can prevent dead-code elimination
var result = Benchmark.Run(() => Fibonacci(20));
```

### Async

```csharp
// Func<Task>
var result = await Benchmark.RunAsync(async () => await FetchDataAsync());

// Func<Task<T>>
var result = await Benchmark.RunAsync(async () => await ComputeAsync(input));
```

### Prepared state

For a benchmark over data that must be built once, pass the preparation as its own delegate. `prepare` runs once before warmup - in the worker, on an isolated run - and the body receives its result:

```csharp
var result = Benchmark.Run(
    prepare: () => BuildData(),
    body:    d => Sort(d));
```

The same shape is available as `RunAsync` (for a `Task`-returning body) and as `RunRaw` / `RunRawAsync` (to keep the raw sample array). Optional `setup:` and `teardown:` hooks receive the state and run outside the timed window - per iteration, before and after the body - so a body that mutates its state can be reset between iterations:

```csharp
var result = Benchmark.Run(
    prepare: () => BuildData(),
    body:    d => Sort(d),
    setup:   d => Shuffle(d));
```

A `prepare` delegate that itself captures a local is measured in this process for the same reason a body is, and stamped `IsolationStatus.InProcessCapturedState`. Pass the captured value as an argument instead: `Run(prepare: (int size) => Build(size), prepareArgument: 100_000, body: d => Sort(d))`.

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

See [Configuration](../reference/configuration.md) for the full list of options.

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
  └─────────────────────────────────────────────────
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
Console.WriteLine($"Ops/s:   {result.OperationsPerSecond}");
Console.WriteLine($"P95:     {result.GetPercentile(0.95)} ns");
Console.WriteLine($"StdDev:  {result.StandardDeviation} ns");
Console.WriteLine($"Error:   ±{result.MarginOfError} ns ({result.ConfidenceLevel * 100:0}% CI)");
Console.WriteLine($"CI:      {result.ConfidenceIntervalLower} … {result.ConfidenceIntervalUpper} ns");

if (result.MeanAllocatedBytes.HasValue)
    Console.WriteLine($"Alloc:   {result.MeanAllocatedBytes.Value} bytes/op");
```

## What Benchmark does not do

- **It does not compare benchmarks.** Use [BenchmarkSuite](./suite-mode.md) for A/B comparisons.
- **It does not run significance testing** between multiple results. Significance testing requires paired raw samples and is handled by `BenchmarkSuite` and `BenchmarkHarness`.

## Next steps

- [Suite mode: BenchmarkSuite](./suite-mode.md) - compare two or more implementations
- [Configuration](../reference/configuration.md) - full options reference
- [Reporters](../output/index.md) - save results to files
