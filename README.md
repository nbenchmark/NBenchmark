# NBenchmark

A lightweight, async-native .NET benchmarking library. No configuration files, no separate compilation step — add a reference and start measuring.

> **Pre-release.** The API may change between versions without notice.

---

## Table of contents

- [Installation](#installation)
- [Quickstart](#quickstart)
- [Understanding the output](#understanding-the-output)
- [Usage tiers](#usage-tiers)
  - [Tier 1 — Bench (single measurement)](#tier-1--bench-single-measurement)
  - [Tier 2 — BenchmarkSuite (compare multiple)](#tier-2--benchmarksuite-compare-multiple)
  - [Tier 3 — BenchmarkHost (attribute-based, CLI-driven)](#tier-3--benchmarkhost-attribute-based-cli-driven)
- [Configuration](#configuration)
- [Reporters](#reporters)
- [CLI reference](#cli-reference)
- [How it works](#how-it-works)

---

## Installation

NBenchmark ships as two packages:

| Package | When to use |
|---|---|
| `NBenchmark` | The zero-dependency core. All measurement, statistics, and file reporters live here. |
| `NBenchmark.Console` | Adds a rich terminal table via [Spectre.Console](https://spectreconsole.net/). Add this to get pretty output. |

```
dotnet add package NBenchmark
dotnet add package NBenchmark.Console   # optional, for the console table
```

---

## Quickstart

The fastest path from zero to a number:

```csharp
using NBenchmark;
using NBenchmark.Console;

var result = Bench.Time(() =>
{
    // code you want to measure
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
```

Output:

```
  Benchmark: 8.30 µs median
    Mean: 8.47 µs, P95: 9.50 µs
    StdDev: 564.2 ns
    95% CI: 8.31 µs … 8.63 µs (±160 ns)
```

---

## Understanding the output

When you use a reporter that produces a table (e.g. `ConsoleReporter` or `MarkdownReporter`), each row looks like this:

| Benchmark | Median | Mean | Error | StdDev | P95 | P99 | Ratio | Sig | Alloc/op |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| linq | 2.10 µs | 2.15 µs | ±80 ns | 560 ns | 3.50 µs | 4.20 µs | 0.75x | ✓ | 1.2 KB |
| bubble _(baseline)_ | 2.80 µs | 2.85 µs | ±95 ns | 665 ns | 4.00 µs | 5.10 µs | 1.00x | — | — |

**What each column means:**

- **Median** — The middle value when all measurements are sorted. The most reliable single number for "how fast is this?" because it ignores a few very slow or very fast outliers.
- **Mean** — The arithmetic average. Close to the median in well-behaved benchmarks; further away when there is high variance.
- **Error** — The margin of error on the mean at the stated confidence level (default 95%). Think of it as "the mean is probably somewhere in this ± range". Smaller is better.
- **StdDev** — How spread out the measurements are. A large StdDev relative to the mean means your code's timing is unpredictable (cache misses, OS scheduling, etc.).
- **P95 / P99** — 95th and 99th percentile. "In 95% of runs, this completed within P95." Useful for understanding worst-case behaviour.
- **Ratio** — Speed relative to the baseline. `0.75x` means 25% faster; `2.0x` means twice as slow.
- **Sig** ✓ — Whether the difference from the baseline is **statistically significant**. A ✓ means it is very unlikely to be random noise (Mann-Whitney U, p < 0.05). No ✓ means you cannot confidently say the difference is real.
- **Alloc/op** — Mean heap bytes allocated per iteration. Only shown when memory measurement is enabled.

_Error = ±95% confidence interval half-width on the mean._

---

## Usage tiers

### Tier 1 — Bench (single measurement)

Use `Bench.Time` anywhere in your code for a quick one-off measurement. No class attributes, no project setup.

```csharp
// Synchronous
var result = Bench.Time(() => MyMethod());

// Return a value (prevents the compiler from optimising the call away)
var result = Bench.Time(() => ComputeSomething());

// Async
var result = await Bench.TimeAsync(async () => await MyMethodAsync());
```

Access the raw numbers directly or call `.Print()`:

```csharp
Console.WriteLine($"Median: {result.Median} ns");
Console.WriteLine($"95% CI: {result.ConfidenceIntervalLower} … {result.ConfidenceIntervalUpper} ns");
result.Print();            // plain-text summary to stdout
await result.ToMarkdownAsync("results.md");
```

**Custom options:**

```csharp
var options = new MeasurementOptions
{
    Iterations = 500,
    WarmupIterations = 50,
    MeasureAllocations = true,
    ConfidenceLevel = 0.99,
};

var result = Bench.Time(() => MyMethod(), options: options);
```

---

### Tier 2 — BenchmarkSuite (compare multiple)

`BenchmarkSuite` lets you benchmark several implementations side-by-side and get a comparison table. Wire up reporters for output.

```csharp
using NBenchmark;
using NBenchmark.Console;

var results = await new BenchmarkSuite("sorting")
    .Add("bubble", () =>
    {
        var arr = Enumerable.Range(0, 100).Reverse().ToArray();
        Array.Sort(arr);
    })
    .Add("linq", () =>
    {
        _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray();
    })
    .WithBaseline("bubble")
    .WithIterations(200)
    .WithWarmup(25)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

**Async benchmarks** work the same way:

```csharp
suite.Add("fetch", async () => await httpClient.GetStringAsync(url));
```

**Per-benchmark setup and teardown:**

```csharp
suite.Add(
    name: "database-query",
    action: async () => await db.QueryAsync("SELECT 1"),
    setup: () => db.Open(),
    teardown: () => db.Close()
);
```

**Suite-level setup and teardown** run once before and after all benchmarks:

```csharp
suite
    .WithSuiteSetup(() => server.Start())
    .WithSuiteTeardown(() => server.Stop());
```

**All fluent options:**

```csharp
new BenchmarkSuite("name")
    .WithIterations(200)          // measured iterations (default: 200)
    .WithWarmup(25)               // warmup iterations (default: 25)
    .WithBaseline("name")         // which benchmark is the 1.00x baseline
    .WithMemory()                 // enable allocation tracking
    .WithOutlierMode(OutlierMode.RemoveTop5Percent)   // default
    .WithConfidenceLevel(0.99)    // CI level on the mean (default: 0.95)
    .WithSignificance(false)      // disable Mann-Whitney U test
    .WithRunOrder(RunOrder.Declaration)   // default: Random
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results.md"))
    .WithProgress(new ConsoleBenchmarkProgress())
    .WithSuiteSetup(() => { })
    .WithSuiteTeardown(() => { })
```

---

### Tier 3 — BenchmarkHost (attribute-based, CLI-driven)

For benchmarking as part of a dedicated console project — similar to BenchmarkDotNet's style. Decorate methods with `[Benchmark]`, point the host at your assembly, and control everything from the command line.

```csharp
// Program.cs
using NBenchmark;
using NBenchmark.Console;
using NBenchmark.Attributes;

await BenchmarkHost.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Anywhere in the project
public class MyBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    [Benchmark]
    public int Compute() => SomeExpensiveMethod();

    [Benchmark]
    public async Task FetchData() => await httpClient.GetAsync("/api/data");
}
```

**Parameterised benchmarks** run once per argument set:

```csharp
public class ParseBenchmarks
{
    [BenchmarkArguments(100)]
    [BenchmarkArguments(10_000)]
    [BenchmarkArguments(1_000_000)]
    [Benchmark]
    public void Parse(int input) => _ = input.ToString();
}
```

**Lifecycle attributes** for setup/teardown:

```csharp
public class DatabaseBenchmarks
{
    private DbConnection _conn = null!;

    [BenchmarkSetup]
    public void Setup() => _conn = new DbConnection(connectionString);

    [BenchmarkTeardown]
    public void Teardown() => _conn.Dispose();

    [BenchmarkIterationSetup]
    public void BeforeEach() => _conn.BeginTransaction();

    [BenchmarkIterationTeardown]
    public void AfterEach() => _conn.RollbackTransaction();

    [Benchmark]
    public void Query() => _conn.Execute("SELECT 1");
}
```

---

## Configuration

All defaults are production-ready for most cases. Override only what you need.

| Option | Default | Notes |
|---|---|---|
| `Iterations` | `200` | Measured iterations. Higher values give tighter error bounds. |
| `WarmupIterations` | `25` | Runs before measurement to let the JIT and CPU caches settle. |
| `ForceGcBeforeEachIteration` | `true` | Runs GC before each measurement to reduce allocation noise. |
| `MeasureAllocations` | `false` | Tracks mean heap bytes per iteration. Adds a small overhead. |
| `OutlierMode` | `RemoveTop5Percent` | Trims the noisiest results. Options: `None`, `RemoveTop1Percent`, `RemoveTop5Percent`, `RemoveTop10Percent`. |
| `ConfidenceLevel` | `0.95` | Confidence level for the error/CI column. `0.99` gives a wider (more conservative) interval. |
| `EnableSignificance` | `true` | Whether to run the Mann-Whitney U test to check if differences are real. |

---

## Reporters

Reporters consume the finished results and produce output. Pass one or more to `.WithReporter(...)`.

| Reporter | Package | Output |
|---|---|---|
| `ConsoleReporter` | `NBenchmark.Console` | Rich table in the terminal using Spectre.Console |
| `MarkdownReporter` | `NBenchmark` | `.md` file with a results table |
| `CsvReporter` | `NBenchmark` | `.csv` file with all statistics including CI bounds |
| `JsonReporter` | `NBenchmark` | `.json` file |

You can stack multiple reporters:

```csharp
suite
    .WithReporter(new ConsoleReporter())
    .WithReporter(new MarkdownReporter("results/sorting.md"))
    .WithReporter(new CsvReporter("results/sorting.csv"))
```

The `Bench` tier also supports one-liner file output:

```csharp
await result.ToMarkdownAsync("results.md");
await result.ToCsvAsync("results.csv");
await result.ToJsonAsync("results/");
```

---

## CLI reference

When using `BenchmarkHost`, you can control a run entirely from the command line without recompiling.

```
dotnet run                              # run all discovered benchmarks
dotnet run -- --filter String*          # glob filter over "ClassName.MethodName"
dotnet run -- --iterations 500
dotnet run -- --warmup 50
dotnet run -- --confidence 0.99         # widen the CI (default: 0.95)
dotnet run -- --reporter markdown       # save a .md file
dotnet run -- --reporter csv            # save a .csv file
dotnet run -- --reporter json           # save a .json file
dotnet run -- --output ./results        # directory for file reporters
dotnet run -- --list                    # list benchmarks without running them
dotnet run -- --dry-run                 # invoke each benchmark once to check for errors
dotnet run -- --order declaration       # run in declaration order instead of random
dotnet run -- --seed 42                 # reproducible random order
dotnet run -- --help
```

---

## How it works

Understanding what NBenchmark does under the hood helps you interpret the numbers.

**Warmup** — Before any measurements are taken, each benchmark runs for `WarmupIterations` (default 25). This gives the .NET JIT time to compile and optimise your code, and lets CPU caches warm up. Without warmup, the first few measurements would be artificially slow.

**Garbage collection** — By default, GC is forced before each iteration. This reduces noise caused by previous iterations leaving objects on the heap.

**Outlier trimming** — Even with GC and warmup, occasional OS scheduling pauses or context switches can spike a measurement. The default `RemoveTop5Percent` mode discards the slowest 5% of samples before computing statistics, so a single unlucky measurement doesn't skew your results.

**Confidence interval** — The _Error_ column shows how precisely the mean is estimated. It is computed using Student's t-distribution from the measured samples. With the default 200 iterations and 95% confidence level, a small Error value means the mean is well-established; a large one suggests high variance and you may want more iterations.

**Statistical significance** — When comparing two or more benchmarks, NBenchmark uses the **Mann-Whitney U test** (a non-parametric rank test) to decide whether a difference in medians is real or just noise. This test makes no assumption that your timings follow a bell curve. A ✓ in the _Sig_ column means p < 0.05 — the difference would occur by chance less than 5% of the time.

**Allocation tracking** — When enabled, `GC.GetAllocatedBytesForCurrentThread()` is sampled around each iteration to measure heap allocations. Useful for spotting unexpected boxing or LINQ allocations.
