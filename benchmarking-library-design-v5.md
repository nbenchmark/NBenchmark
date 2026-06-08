# .NET Benchmarking Library - Design Document v5

> A developer-first benchmarking library for modern .NET. Simpler than BenchmarkDotNet,
> powerful enough for real work.

---

## Table of Contents

1. [Motivation & Philosophy](#1-motivation--philosophy)
2. [API Design](#2-api-design)
3. [Project Architecture](#3-project-architecture)
4. [Core Engine](#4-core-engine)
5. [Statistical Analysis](#5-statistical-analysis)
6. [Reporters](#6-reporters)
7. [Host & Discovery](#7-host--discovery)
8. [Source Generator (Optional)](#8-source-generator-optional)
9. [Phase 2 - Web UI](#9-phase-2--web-ui)
10. [Build Order & Milestones](#10-build-order--milestones)
11. [Name Ideas](#11-name-ideas)

---

## 1. Motivation & Philosophy

BenchmarkDotNet is a remarkable piece of engineering. It is also genuinely too complicated for the
majority of benchmarking tasks:

- It compiles and launches child processes to isolate the JIT
- Results are only available after a multi-stage pipeline completes
- Configuration requires attributes, base classes, or separate config files
- Async benchmarks, parameterised benchmarks, and setup/teardown all have their own ceremony

The goal of this library is a **different trade-off**: accept slightly less rigour in exchange for
dramatically better developer experience. In-process, fast, zero-config by default, with opt-in
precision for when you need it.

### Design Principles

- **One line to start.** The simplest useful case must be a single expression.
- **No out-of-process execution by default.** Run in the same process, no compilation step.
- **Async-native.** `async`/`await` is a first-class citizen, not an afterthought.
- **Modern .NET only.** Target net8.0+. No .NET Framework baggage, use the best modern APIs.
- **Sensible defaults.** Warmup, GC collection, outlier trimming, allocation tracking, benchmark
  order randomisation, and significance testing work without configuration.
- **Layered complexity.** Simple things are simple; complicated things are possible.
- **Beautiful output.** A great terminal experience is part of the product.
- **Comparison-first design.** When you benchmark more than one thing, the output tells you
  which is faster and whether the difference is real.

### What We Trade Away

| BenchmarkDotNet | This Library |
|---|---|
| Out-of-process isolation (eliminates JIT cross-contamination) | In-process (simpler, slightly less rigorous; mitigated by benchmark order randomisation) |
| Full statistical model (confidence intervals, error bounds) | Practical stats (median, P95, P99, std dev) with Mann-Whitney U significance testing |
| Multi-runtime / multi-framework targeting | Single runtime per run |
| Diagnosers (ETW, perf counters) | Allocation tracking via GC APIs |
| Param-level result matrix | Single-argument parameterised benchmarks (post-v1) |

Document this trade-off clearly in the README. It is not a bug; it is the design.

---

## 2. API Design

The library exposes three tiers. All tiers share the same measurement engine and produce the same
`BenchmarkResult` type.

### Tier 1 - One-Liner

For measuring a single thing right now. Zero configuration.

```csharp
// Sync - returns BenchmarkResult directly, no async wrapper
var result = Benchmark.Run(() => MyMethod());
result.Print();

// Async
var result = await Benchmark.RunAsync(async () => await httpClient.GetAsync("/health"));
result.Print();

// With return value (prevents dead-code elimination automatically)
var result = Benchmark.Run(() => JsonSerializer.Serialize(myObject));
result.Print();

// Raw samples for custom analysis
var outcome = Benchmark.MeasureRaw(() => MyMethod());
// outcome.RawSamples, outcome.Result
```

`Benchmark.Run` and `Benchmark.RunAsync` accept `Func<T>`, `Func<Task>`, and `Func<Task<T>>` overloads.
Return values are consumed via a no-inline sink so the compiler cannot eliminate the call.
`Benchmark.MeasureRaw` returns the full `MeasurementOutcome` including raw samples for downstream
significance testing or custom analysis.

### Tier 2 - Fluent Suite

For comparing multiple implementations against each other.

```csharp
var buffer = new List<int>();
Action myLambda = () => { /* benchmarked logic */ };

await new BenchmarkSuite("String Formatting")
    .Add("Concat",        () => string.Concat("hello", " ", "world"))
    .Add("Interpolate",   () => $"hello world")
    .Add("StringBuilder", () => new StringBuilder()
                                    .Append("hello").Append(' ').Append("world")
                                    .ToString())
    .Add("WithReset", action: myLambda,
                     setup: () => buffer.Clear(),
                     teardown: () => { })
    .WithBaseline("Concat")          // mark one as the baseline for ratio comparison
    .WithWarmup(iterations: 50)
    .WithIterations(500)
    .WithMemory()                    // track bytes allocated per operation
    .WithOutlierMode(OutlierMode.RemoveTop5Percent)
    .WithoutConsoleOutput()           // suppress console, write only Markdown
    .WithReporter(new MarkdownReporter())
    .RunAsync();
```

Output is printed to the console automatically; opt-in reporters (JSON, Markdown) also write files.

Significance annotations (`✓` / `~`) appear automatically when a suite has 2+ benchmarks.
Disable via `.WithSignificance(false)`.

> **Note:** Tier 2 supports per-benchmark iteration setup/teardown via optional
> `setup`/`teardown` parameters on `.Add()` (see example above). These are passed to the
> engine and run outside the timed region - same semantics as Tier 3 attributes. Tier 3
> additionally supports suite-level `[BenchmarkSetup]`/`[BenchmarkTeardown]` and
> `[BenchmarkIterationSetup]`/`[BenchmarkIterationTeardown]` attributes.

### Tier 3 - Benchmark Host

For a dedicated benchmark project. Discovers benchmark classes by reflection and runs them as a
structured suite with CLI support.

```csharp
// Program.cs - the entire file
var results = await BenchmarkHost.Create(args)
    .AddFromAssembly<MyBenchmarks>()   // discover all [Benchmark] methods in the assembly
    .WithWebUI(port: 5050)             // optional (Phase 2)
    .RunAsync();
```

Benchmark classes are plain classes - no base class required:

```csharp
public class StringBenchmarks
{
    private readonly string _source = "hello world";

    [BenchmarkSetup]
    public void Setup()
    {
        // Runs once before the suite, not timed
    }

    [BenchmarkIterationSetup]
    public void IterationSetup()
    {
        // Runs before each timed iteration (not included in timing)
    }

    [BenchmarkIterationTeardown]
    public void IterationTeardown()
    {
        // Runs after each timed iteration (not included in timing)
    }

    [Benchmark(Baseline = true, Description = "String.Contains with StringComparison")]
    public bool ContainsOrdinal() =>
        _source.Contains("world", StringComparison.Ordinal);

    [Benchmark(Description = "String.Contains without StringComparison")]
    public bool ContainsDefault() =>
        _source.Contains("world");

    [BenchmarkTeardown]
    public void Teardown()
    {
        // Runs once after the suite
    }
}
```

### Parameterised Benchmarks (post-v1)

```csharp
public class SerializationBenchmarks
{
    [Benchmark]
    [BenchmarkArguments(100)]
    [BenchmarkArguments(1000)]
    [BenchmarkArguments(10000)]
    public string Serialize(int size) =>
        JsonSerializer.Serialize(new byte[size]);
}
```

Each `[BenchmarkArguments]` attribute produces a separate benchmark entry in the results,
named `Serialize(size=100)`, `Serialize(size=1000)`, etc.

### CLI Arguments (Tier 3)

```
myapp.exe                          # run all discovered benchmarks
myapp.exe --filter String*         # run suites matching glob
myapp.exe --filter *.Contains*     # run individual methods matching glob
myapp.exe --iterations 1000        # override iterations
myapp.exe --warmup 100             # override warmup
myapp.exe --reporter json          # set reporter
myapp.exe --output ./results       # set output directory
myapp.exe --list                   # list discovered benchmarks without running
myapp.exe --dry-run                 # invoke each benchmark once (skip measurement)
myapp.exe --order declaration      # preserve declaration order (default: random)
myapp.exe --threshold-pct 5        # [NOT YET IMPLEMENTED] will reject and exit 1; placeholder for v1.0
myapp.exe --help | -h              # show help text
```

---

## 3. Project Architecture

```
NBenchmark/
├── src/
│   ├── NBenchmark.Core/                # Pure measurement engine - zero dependencies
│   │   ├── Engine/
│   │   │   ├── MeasurementEngine.cs
│   │   │   ├── WarmupStrategy.cs
│   │   │   ├── GcControl.cs
│   │   │   └── ResultSink.cs
│   │   ├── Stats/
│   │   │   ├── StatsSummary.cs
│   │   │   ├── Percentile.cs
│   │   │   └── MannWhitneyU.cs
│   │   └── Models/
│   │       ├── BenchmarkResult.cs
│   │       ├── BenchmarkFormatter.cs
│   │       ├── IBenchmarkProgress.cs    # interface + NullBenchmarkProgress - zero deps
│   │       ├── MeasurementOutcome.cs
│   │       ├── MeasurementOptions.cs
│   │       ├── OutlierMode.cs
│   │       └── RunOrder.cs
│   │
│   ├── NBenchmark/                     # Public API, reporters, host - depends on Core
│   │   ├── Benchmark.cs               # Static entry point (Tier 1)
│   │   ├── BenchmarkSuite.cs      # Fluent builder (Tier 2)
│   │   ├── BenchmarkHost.cs       # Host + discovery (Tier 3)
│   │   ├── Attributes/
│   │   │   ├── BenchmarkAttribute.cs
│   │   │   ├── BenchmarkSetupAttribute.cs
│   │   │   ├── BenchmarkTeardownAttribute.cs
│   │   │   ├── BenchmarkIterationSetupAttribute.cs
│   │   │   ├── BenchmarkIterationTeardownAttribute.cs
│   │   │   ├── BenchmarkArgumentsAttribute.cs
│   │   │   └── BenchmarkClassAttribute.cs     # Source-generator only (Milestone 4)
│   │   ├── Reporters/
│   │   │   ├── IReporter.cs
│   │   │   ├── ConsoleReporter.cs             # Default, uses Spectre.Console
│   │   │   ├── ConsoleBenchmarkProgress.cs    # Live progress, uses Spectre.Console
│   │   │   ├── JsonReporter.cs
│   │   │   ├── MarkdownReporter.cs
│   │   │   ├── CsvReporter.cs
│   │   │   └── PathValidation.cs              # Shared ValidateOutputPath utility
│   │   └── Discovery/
│   │       ├── BenchmarkDiscoverer.cs
│   │       └── BenchmarkDefinition.cs

(Output capped at 50 KB. Showing lines 1-50. Use offset=2751 to continue.)

│   │
│   └── NBenchmark.Web/                 # Phase 2: embedded web UI
│       ├── BenchmarkWebHost.cs
│       ├── Api/
│       │   ├── BenchmarkEndpoints.cs
│       │   └── ResultsStore.cs
│       └── wwwroot/               # Embedded SPA
│           └── index.html
│
├── samples/
│   ├── Quick/                     # Tier 1 examples
│   ├── Suite/                     # Tier 2 examples
│   ├── Host/                      # Tier 3 examples
│   └── Web/                       # Phase 2 Web UI examples
│
└── tests/
    ├── NBenchmark.Core.Tests/
    └── NBenchmark.Tests/
```

### Package Dependencies

| Package | Used In | Purpose |
|---|---|---|
| `Spectre.Console` | `NBenchmark` | Rich terminal output |
| `System.Text.Json` | `NBenchmark` | JSON reporter |
| `Microsoft.AspNetCore` | `NBenchmark.Web` | Web UI host |

`NBenchmark.Core` has **zero NuGet dependencies** - only BCL APIs. This keeps it embeddable
anywhere including NativeAOT projects.

**Note on `System.CommandLine`:** This library was considered but ultimately rejected.
Microsoft has placed it in maintenance mode with no active development. The CLI argument parser
is hand-rolled (~50 lines) to avoid a dependency on effectively dead infrastructure.

---

## 4. Core Engine

### 4.1 Models

```csharp
// MeasurementOptions.cs
namespace NBenchmark.Core;

public record MeasurementOptions
{
    public static readonly MeasurementOptions Default = new();

    /// <summary>Minimum allowed iterations (prevent accidental 0-length arrays).</summary>
    public const int MinIterations = 1;
    /// <summary>Maximum allowed iterations (prevent massive allocation).</summary>
    public const int MaxIterations = 100_000;
    /// <summary>Maximum allowed warmup iterations.</summary>
    public const int MaxWarmupIterations = 10_000;

    /// <summary>Number of iterations to run before measurement begins.</summary>
    /// <remarks>
    /// Minimum is 1, not 0: zero warmup is explicitly disallowed because the
    /// measurement engine combines warmup + measurement in a single call and
    /// the first few measured iterations are almost always dominated by JIT
    /// compilation, tiered compilation, and tier-0 code paths. Skipping warmup
    /// would silently inflate the reported median for sub-microsecond code.
    /// The CLI <c>--warmup</c> flag and the <c>WarmupIterations</c> property
    /// both reject 0. The engine's own loop would naturally allow 0, so this
    /// is a property-level guard against accidental mis-measurement.
    /// </remarks>
    public int WarmupIterations
    {
        get => _warmupIterations;
        init => _warmupIterations = value is >= 1 and <= MaxWarmupIterations
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"WarmupIterations must be between 1 and {MaxWarmupIterations}");
    }
    private readonly int _warmupIterations = 25;

    /// <summary>Number of measured iterations.</summary>
    public int Iterations
    {
        get => _iterations;
        init => _iterations = value is >= 1 and <= MaxIterations
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Iterations must be between {MinIterations} and {MaxIterations}");
    }
    private readonly int _iterations = 200;

    /// <summary>Force a Gen0 GC collection before each measured iteration.</summary>
    /// <remarks>
    /// This trades steady-state realism for between-iteration isolation.
    /// For sub-microsecond benchmarks, the collection itself can exceed the
    /// measurement - disable for nanosecond-scale measurements.
    /// Default: true (sensible for ms-scale benchmarks; honest in docs).
    /// </remarks>
    public bool ForceGcBeforeEachIteration { get; init; } = true;

    /// <summary>Collect allocation data using GC.GetTotalAllocatedBytes().</summary>
    public bool MeasureAllocations { get; init; } = false;

    /// <summary>How to handle statistical outliers before computing summary stats.</summary>
    public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;

    /// <summary>Enable significance testing for multi-benchmark suites.</summary>
    public bool EnableSignificance { get; init; } = true;

    /// <summary>
    /// Force a full Gen2 GC (compacting, with finalizer wait) between each benchmark
    /// in a suite. Prevents cross-benchmark allocation pollution. Default: true.
    /// </summary>
    public bool ForceGcBetweenBenchmarks { get; init; } = true;
}
```

```csharp
// OutlierMode.cs
namespace NBenchmark.Core;

public enum OutlierMode
{
    /// <summary>Keep all measurements.</summary>
    None,

    /// <summary>Remove the top 5% of measurements (slowest outliers).</summary>
    RemoveTop5Percent,

    /// <summary>Remove the top and bottom 5% of measurements.</summary>
    RemoveTop5PercentAndBottom5Percent,

    /// <summary>Use the IQR fence method (1.5 × IQR from Q1/Q3).</summary>
    IqrFence,
}
```

```csharp
// RunOrder.cs
namespace NBenchmark.Core;

public enum RunOrder
{
    /// <summary>Randomise benchmark execution order to reduce cross-contamination.</summary>
    Random,

    /// <summary>Preserve declaration order (useful for debugging or when ordering matters).</summary>
    Declaration,
}
```

```csharp
// MeasurementOutcome.cs
namespace NBenchmark.Core;

/// <summary>
/// The result of a single measurement run, containing both the summary
/// (post-trimmed stats) and the raw samples (pre-outlier-removal).
/// Raw samples are preserved for downstream significance testing.
/// </summary>
/// <remarks>
/// <see cref="RawSamples"/> is sorted in ascending order by the measurement engine
/// (the same sort used for outlier removal and percentile computation). Consumers
/// should not assume measurement-order is preserved.
/// </remarks>
public sealed class MeasurementOutcome
{
    public required BenchmarkResult Result { get; init; }
    public required double[] RawSamples { get; init; }
}

/// <summary>
/// Progress hooks called during benchmark execution.
/// Implement to observe live progress (console output, CI streaming, Web UI SSE).
/// </summary>
public interface IBenchmarkProgress
{
    /// <summary>Called when a suite run begins (useful for Web UI / SSE initialization).</summary>
    Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total);

    /// <summary>Called before warmup begins for a benchmark.</summary>
    Task OnWarmupStarting(string name, int totalWarmupIterations);
    
    /// <summary>Called after warmup completes for a benchmark.</summary>
    /// <remarks>
    /// Because <see cref="MeasurementEngine"/> combines warmup and measurement in a single
    /// invocation, this hook is called after the entire benchmark (warmup + measurement)
    /// completes, not between warmup and measurement. The name reflects the semantic event
    /// ("warmup stage is done") rather than its precise timing.
    /// </remarks>
    Task OnWarmupCompleted(string name);

    /// <summary>Called before a benchmark's measured iterations begin.</summary>
    Task OnBenchmarkStarting(string name, int index, int total);

    /// <summary>Called after a benchmark completes (including errored).</summary>
    Task OnBenchmarkCompleted(BenchmarkResult result);

    /// <summary>Called when an entire suite finishes.</summary>
    Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results);
}

/// <summary>
/// Default no-op implementation for consumers that only need partial hook coverage.
/// </summary>
public class NullBenchmarkProgress : IBenchmarkProgress
{
    public static readonly NullBenchmarkProgress Instance = new();
    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) => Task.CompletedTask;
    public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;
    public Task OnWarmupCompleted(string name) => Task.CompletedTask;
    public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;
    public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;
    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) => Task.CompletedTask;
}

/// <summary>
/// Console-based progress reporter. Prints "[1/10] Concat - running (200 iterations)..."
/// lines during execution. Used by BenchmarkSuite and BenchmarkHost by default.
/// </summary>
public class ConsoleBenchmarkProgress : IBenchmarkProgress
{
    private int _suiteTotal;
    private string? _suiteOptions;
    private string? _currentName;

    public ConsoleBenchmarkProgress(int iterations, int warmupIterations)
    {
        _suiteOptions = $"{warmupIterations} warmup / {iterations} measured";
    }

    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
    {
        AnsiConsole.MarkupLine($"[bold]Starting {total} benchmark(s)...[/]");
        return Task.CompletedTask;
    }

    public Task OnWarmupStarting(string name, int totalWarmupIterations)
    {
        _currentName = name;
        AnsiConsole.MarkupLine($"  [{EscapeMarkup(name)}] warming up ({totalWarmupIterations} iterations)...");
        return Task.CompletedTask;
    }

    public Task OnWarmupCompleted(string name)
    {
        return Task.CompletedTask;
    }

    public Task OnBenchmarkStarting(string name, int index, int total)
    {
        _suiteTotal = total;
        AnsiConsole.MarkupLine($"  [grey][[{index}/{total}][/] {EscapeMarkup(name)} - running ({_suiteOptions})...");
        return Task.CompletedTask;
    }

    public Task OnBenchmarkCompleted(BenchmarkResult result)
    {
        if (result.Errored)
            AnsiConsole.MarkupLine($"[red]  Error: {EscapeMarkup(result.ErrorMessage)}[/]");

        return Task.CompletedTask;
    }

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
    {
        AnsiConsole.MarkupLine($"  Completed {results.Count} benchmark(s).");
        return Task.CompletedTask;
    }

    private static string EscapeMarkup(string? text) =>
        text?.Replace("[", "[[").Replace("]", "]]") ?? "";
}
```

```csharp
// BenchmarkResult.cs
namespace NBenchmark.Core;

// Required properties are kept to the core measurement (Name + timing) and the
// success/failure flag (Errored). Everything else has a sensible default, so
// construction sites (tests, factories, the errored-result factory) only need
// to specify what's meaningful for that result. The engine still sets every
// field when producing a successful result.
public record BenchmarkResult
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    // Timing - all values in nanoseconds. Required because they ARE the result.
    public required double Mean { get; init; }
    public required double Median { get; init; }
    public required double P95 { get; init; }
    public required double P99 { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required double StandardDeviation { get; init; }

    // Allocations (null if not measured)
    public long? MeanAllocatedBytes { get; init; }

    // Significance (null if not computed - single benchmark or disabled)
    public double? PValue { get; init; }
    public bool? IsSignificant { get; init; }

    // Error handling
    public bool Errored { get; init; }
    public string? ErrorMessage { get; init; }

    // Meta
    public int MeasuredIterations { get; init; }
    public int WarmupIterations { get; init; }
    public DateTimeOffset RunAt { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan TotalDuration { get; init; } = TimeSpan.Zero;
    public bool IsBaseline { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;

    /// <summary>
    /// Factory for producing an errored result without repeating the full
    /// construction boilerplate at every error site. All optional fields
    /// default to match the values of an errored result (zero/null/default).
    /// </summary>
    public static BenchmarkResult Errored(string name, string errorMessage,
        string? description = null, bool isBaseline = false,
        OutlierMode outlierMode = OutlierMode.RemoveTop5Percent) => new()
    {
        Name        = name,
        Description = description,
        // Timing fields stay at their required values; consumers should not
        // read timing from an Errored result, but zero is the honest default.
        Mean = 0, Median = 0, P95 = 0, P99 = 0,
        Min  = 0, Max  = 0, StandardDeviation = 0,
        Errored      = true,
        ErrorMessage = errorMessage,
        IsBaseline   = isBaseline,
        OutlierMode  = outlierMode,
    };
}

/// <summary>
/// Shared formatting helpers for nanoseconds and bytes.
/// Used by all reporters to prevent formatter drift.
/// </summary>
public static class BenchmarkFormatter
{
    public static string FormatNs(double ns) => ns switch
    {
        < 1_000              => $"{ns:F1} ns",
        < 1_000_000          => $"{ns / 1_000:F2} µs",
        < 1_000_000_000      => $"{ns / 1_000_000:F2} ms",
        _                    => $"{ns / 1_000_000_000:F2} s",
    };

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
        _                    => $"{bytes / (1024.0 * 1024):F1} MB",
    };
}
```

### 4.2 The Measurement Engine

This is the heart of the library. It handles warmup, timing, GC control, allocation
measurement, and raw sample collection.

```csharp
// MeasurementEngine.cs
namespace NBenchmark.Core.Engine;

using System.Diagnostics;
using System.Runtime.CompilerServices;

/// <summary>
/// Pure measurement engine. Zero dependencies, static API.
/// </summary>
/// <remarks>
/// Static by design: the engine has no per-instance state (the timing arrays
/// are local to each call) and every consumer - Tier 1 <c>Benchmark.Run</c>, Tier 2
/// <c>BenchmarkSuite</c>, Tier 3 <c>BenchmarkHost</c> - would use the same
/// implementation. An instance-based design would add parameter noise and a
/// parallel-test surface area that the engine does not need. If future
/// extensibility demands it (e.g. an alternative engine for distributed timing),
/// the static methods can be re-introduced as a thin facade over an interface.
/// </remarks>
public static class MeasurementEngine
{
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    // --- Sync hot path (used by sync overload) ---
    // Avoids async state machine, Task.CompletedTask allocation, and
    // TaskAwaiter overhead for synchronous benchmarks. Critical for
    // sub-microsecond benchmarks where ~100ns per-iteration async noise
    // would inflate measurements by 2-3x.

    public static MeasurementOutcome MeasureSync(
        string name,
        Action action,
        MeasurementOptions? options = null,
        string? description = null,
        bool isBaseline = false,
        Action? iterationSetup = null,
        Action? iterationTeardown = null,
        CancellationToken cancellationToken = default)
    {
        options ??= MeasurementOptions.Default;
        var totalTimer = Stopwatch.StartNew();

        // --- warmup (sync) ---
        for (var i = 0; i < options.WarmupIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterationSetup?.Invoke();
            action();
            iterationTeardown?.Invoke();
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // --- sample collection (sync) ---
        var timings     = new double[options.Iterations];
        long[]? allocations = options.MeasureAllocations ? new long[options.Iterations] : null;

        for (var i = 0; i < options.Iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            iterationSetup?.Invoke();

            long allocBefore = 0;
            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes(precise: false);

            var timestamp = Stopwatch.GetTimestamp();
            action();

            // Capture allocation delta BEFORE teardown (consistent with timing)
            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes(precise: false);
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            iterationTeardown?.Invoke();

            timings[i] = Stopwatch.GetElapsedTime(timestamp).TotalNanoseconds;
        }

        totalTimer.Stop();

        // --- stats ---
        var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
        var stats   = StatsSummary.Compute(trimmed);

        long? meanAllocs = allocations is not null
            ? (long)allocations.Average()
            : null;

        return new MeasurementOutcome
        {
            RawSamples = timings,
            Result = new BenchmarkResult
            {
                Name               = name,
                Description        = description,
                Mean               = stats.Mean, Median = stats.Median,
                P95                = stats.P95, P99 = stats.P99,
                Min                = stats.Min, Max = stats.Max,
                StandardDeviation  = stats.StandardDeviation,
                MeanAllocatedBytes = meanAllocs,
                PValue             = null, IsSignificant = null,
                Errored            = false, ErrorMessage = null,
                MeasuredIterations = trimmed.Length,
                WarmupIterations   = options.WarmupIterations,
                RunAt              = DateTimeOffset.UtcNow,
                TotalDuration      = totalTimer.Elapsed,
                IsBaseline         = isBaseline,
                OutlierMode        = options.OutlierMode,
            }
        };
    }

    // --- Async hot path (for async benchmarks only) ---

    public static async Task<MeasurementOutcome> MeasureAsync(
        string name,
        Func<Task> action,
        MeasurementOptions? options = null,
        string? description = null,
        bool isBaseline = false,
        Action? iterationSetup = null,      // runs before each timed iteration, not timed
        Action? iterationTeardown = null,   // runs after each timed iteration, not timed
        CancellationToken cancellationToken = default)
    {
        options ??= MeasurementOptions.Default;

        var totalTimer = Stopwatch.StartNew();

        await RunWarmupAsync(action, iterationSetup, iterationTeardown, options.WarmupIterations, cancellationToken);

        var (timings, allocations) = await CollectSamplesAsync(action, iterationSetup, iterationTeardown, options, cancellationToken);

        totalTimer.Stop();

        var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
        var stats = StatsSummary.Compute(trimmed);

        long? meanAllocs = allocations is not null
            ? (long)allocations.Average()
            : null;

        return new MeasurementOutcome
        {
            RawSamples = timings, // pre-outlier-removal, for significance testing
            Result = new BenchmarkResult
            {
                Name               = name,
                Description        = description,
                Mean               = stats.Mean,
                Median             = stats.Median,
                P95                = stats.P95,
                P99                = stats.P99,
                Min                = stats.Min,
                Max                = stats.Max,
                StandardDeviation  = stats.StandardDeviation,
                MeanAllocatedBytes = meanAllocs,
                PValue             = null,  // set by the caller (suite/host) after pairwise comparison
                IsSignificant      = null,  // set by the caller
                Errored            = false,
                ErrorMessage       = null,
                MeasuredIterations = trimmed.Length,
                WarmupIterations   = options.WarmupIterations,
                RunAt              = DateTimeOffset.UtcNow,
                TotalDuration      = totalTimer.Elapsed,
                IsBaseline         = isBaseline,
                OutlierMode        = options.OutlierMode,
            }
        };
    }

    /// <summary>
    /// Sync wrapper - delegates to MeasureSync for zero-async-overhead measurement.
    /// Wraps the MeasurementOutcome in Task.FromResult for API consistency with the
    /// async overload that returns Task<MeasurementOutcome>.
    /// </summary>
    public static Task<MeasurementOutcome> MeasureAsync(
        string name,
        Action action,
        MeasurementOptions? options = null,
        string? description = null,
        bool isBaseline = false,
        Action? iterationSetup = null,
        Action? iterationTeardown = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = MeasureSync(name, action, options, description, isBaseline,
            iterationSetup, iterationTeardown, cancellationToken);
        return Task.FromResult(outcome);
    }
    // -------------------------------------------------------------------------
    // Warmup
    // -------------------------------------------------------------------------

    private static async Task RunWarmupAsync(
        Func<Task> action,
        Action? iterationSetup,
        Action? iterationTeardown,
        int iterations,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterationSetup?.Invoke();
            await action();
            iterationTeardown?.Invoke();
        }

        // Encourage the JIT to settle and GC to collect warmup allocations
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // -------------------------------------------------------------------------
    // Sample collection
    // -------------------------------------------------------------------------

    private static async Task<(double[] Timings, long[]? Allocations)> CollectSamplesAsync(
        Func<Task> action,
        Action? iterationSetup,
        Action? iterationTeardown,
        MeasurementOptions options,
        CancellationToken cancellationToken)
    {
        var timings     = new double[options.Iterations];
        long[]? allocations = options.MeasureAllocations ? new long[options.Iterations] : null;

        for (var i = 0; i < options.Iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            iterationSetup?.Invoke();  // NOT timed

            long allocBefore = 0;
            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes(precise: false);

            var timestamp = Stopwatch.GetTimestamp();
            await action();

            // Capture allocation delta BEFORE teardown (consistent with timing)
            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes(precise: false);
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            iterationTeardown?.Invoke();  // NOT timed, NOT allocation-tracked

            timings[i] = Stopwatch.GetElapsedTime(timestamp).TotalNanoseconds;
        }

        return (timings, allocations);
    }

    // -------------------------------------------------------------------------
    // GC helpers
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGen0Collection()
    {
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
    }

    // -------------------------------------------------------------------------
    // Outlier removal - always returns a sorted array so downstream stats
    // computation can reuse the sort rather than sorting again.

    private static double[] ApplyOutlierMode(double[] timings, OutlierMode mode)
    {
        return mode switch
        {
            OutlierMode.None => SortAndReturn(timings),
            OutlierMode.RemoveTop5Percent => RemoveTopPercent(timings, 0.05),
            OutlierMode.RemoveTop5PercentAndBottom5Percent => RemoveBothPercent(timings, 0.05),
            OutlierMode.IqrFence => RemoveIqrOutliers(timings),
            _ => timings,
        };
    }

    private static double[] SortAndReturn(double[] values)
    {
        Array.Sort(values);
        return values;
    }

    private static double[] RemoveTopPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var keep = (int)Math.Floor(values.Length * (1.0 - fraction));
        return values[..keep];
    }

    private static double[] RemoveBothPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var trimEach = (int)Math.Floor(values.Length * fraction);
        return values[trimEach..(values.Length - trimEach)];
    }

    private static double[] RemoveIqrOutliers(double[] values)
    {
        Array.Sort(values);
        var q1    = Percentile.Compute(values, 0.25);
        var q3    = Percentile.Compute(values, 0.75);
        var iqr   = q3 - q1;
        var lower = q1 - 1.5 * iqr;
        var upper = q3 + 1.5 * iqr;
        var filtered = values.Where(v => v >= lower && v <= upper).ToArray();

        // If IQR fence removes all values (e.g., very small sample with wide IQR),
        // fall back to the sorted array to avoid returning empty stats.
        return filtered.Length > 0 ? filtered : values;
    }
}
```

### 4.3 Dead-Code Elimination Sink

The compiler and JIT may eliminate calls to methods whose return values are unused. The sink
below prevents this without introducing measurable overhead.

For reference types, the object-assignment pattern is used (`_hole = value as object`).
For value types, `Volatile.Write` is used to prevent boxing noise. The `NoInlining` attribute
ensures the JIT cannot peer through to eliminate the calls from the caller's perspective.

```csharp
// ResultSink.cs
namespace NBenchmark.Core.Engine;

using System.Runtime.CompilerServices;

/// <summary>
/// Consumes return values to prevent the JIT from dead-code-eliminating
/// the benchmarked method. The NoInlining attribute is critical here.
/// </summary>
/// <remarks>
/// The library's internal hot paths (Benchmark.Run&lt;T&gt;, BenchmarkSuite.Add&lt;T&gt;)
/// always use the generic <see cref="Consume{T}(T)"/> overload, which boxes value types.
/// The specialised overloads (int, long, double, bool) use Volatile.Write to avoid boxing
/// noise and are available for users who call ResultSink directly in custom runners.
/// Boxing overhead (~2 ns) is negligible compared to the default 200-iteration measurement
/// window, which is why the internal paths do not add type-checking branching.
/// </remarks>
public static class ResultSink
{
    private static volatile object? _hole;
    private static volatile int _holeInt;
    private static volatile long _holeLong;
    private static volatile double _holeDouble;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume<T>(T value)
    {
        // For reference types: store as object to prevent DCE.
        // For value types: JIT boxes, which introduces negligible overhead.
        // Most benchmarks are on the µs scale, so boxing noise is insignificant.
        _hole = value as object;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(int value)
    {
        // Volatile.Write prevents the JIT from eliminating the store.
        // The method itself is NoInlining, so the caller cannot see through it.
        System.Threading.Volatile.Write(ref _holeInt, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(long value)
    {
        System.Threading.Volatile.Write(ref _holeLong, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(double value)
    {
        System.Threading.Volatile.Write(ref _holeDouble, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(bool value)
    {
        // Always write to an int field - never skip the store, so the JIT cannot
        // distinguish between true/false paths and eliminate the call.
        Volatile.Write(ref _holeInt, value ? 1 : 0);
    }
}
```

When the runner calls a benchmark method that returns a value, it automatically wraps the
call: `ResultSink.Consume(await action())`. Users never need to know this exists.

### 4.4 Allocation Tracking for Async Code

**Important caveat:** `GC.GetTotalAllocatedBytes(precise: false)` measures process-wide
allocations. For async benchmarks where continuations may run on different threads, this
is the correct approach - `GC.GetAllocatedBytesForCurrentThread()` would undercount any
allocations that occur after an `await` resumption on a different thread.

The trade-off is that `GetTotalAllocatedBytes` also includes allocations from concurrent
work (other threads, system activity). The library documents this limitation: allocation
numbers are reliable for single-threaded or lightly-loaded processes, but may include
background noise in heavily concurrent scenarios. In practice, for the typical dedicated
benchmark console application, this noise is negligible.

---

## 5. Statistical Analysis

### 5.1 Percentile Computation

Uses the nearest-rank method, which works correctly for small sample sizes.

```csharp
// Percentile.cs
namespace NBenchmark.Core.Stats;

public static class Percentile
{
    /// <summary>
    /// Computes the p-th percentile of a sorted array using the nearest-rank method.
    /// </summary>
    /// <param name="sorted">Values sorted ascending.</param>
    /// <param name="p">Percentile as a fraction [0, 1].</param>
    public static double Compute(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }
}
```

### 5.2 Stats Summary

```csharp
// StatsSummary.cs
namespace NBenchmark.Core.Stats;

public sealed class StatsSummary
{
    public double Mean { get; init; }
    public double Median { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double StandardDeviation { get; init; }

    /// <param name="samples">Pre-sorted array (the engine provides a sorted copy).</param>
    public static StatsSummary Compute(double[] samples)
    {
        if (samples.Length == 0)
            return new StatsSummary();

        // Compute mean first (pre-sorted samples - Min/Max free).
        var mean = samples.Average();

        // Two-pass variance: subtract the mean in a second pass to avoid
        // catastrophic cancellation in (sumSq / n) - (mean * mean) when
        // the mean is large relative to the standard deviation. For typical
        // nanosecond-scale benchmarks (~10^3 to 10^9 ns) and `double` precision
        // (~15 significant digits), this remains well-conditioned, but the
        // two-pass form costs one extra subtraction per sample and is robust
        // for arbitrarily large means.
        var sumSq = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var d = samples[i] - mean;
            sumSq += d * d;
        }
        // Population variance (/n). With 200+ samples the difference from
        // sample variance (/n-1) is negligible. We don't build confidence
        // intervals from this value, so /n is the simpler and acceptable choice.
        var variance = sumSq / samples.Length;

        return new StatsSummary
        {
            Mean              = mean,
            Median            = Percentile.Compute(samples, 0.50),
            P95               = Percentile.Compute(samples, 0.95),
            P99               = Percentile.Compute(samples, 0.99),
            Min               = samples[0],
            Max               = samples[^1],
            StandardDeviation = Math.Sqrt(variance),
        };
    }
}
```

### 5.3 Significance Testing - Mann-Whitney U

Timing distributions are typically right-skewed (OS scheduling jitter, GC pauses, thermal
effects), which violates the normality assumption of parametric tests like Welch's t-test.
The Mann-Whitney U test is non-parametric - it makes no distribution assumption - and it
answers exactly the right question: "are these two sets of measurements from meaningfully
different distributions?"

The implementation below follows the standard two-tailed Mann-Whitney U test with tie
correction. At ~60 lines with zero dependencies, it fits naturally in `NBenchmark.Core`.

**Note:** The normal approximation used below is accurate for n ≥ 20 per group. With the
default 200 iterations, both groups are well above this threshold. For very small samples
(e.g., parameterised benchmarks with low iteration counts), the p-value may be approximate.

```csharp
// MannWhitneyU.cs
namespace NBenchmark.Core.Stats;

public static class MannWhitneyU
{
    /// <summary>
    /// Performs a two-tailed Mann-Whitney U test on two independent samples.
    /// </summary>
    /// <param name="sampleA">First sample (e.g., baseline benchmark timings).</param>
    /// <param name="sampleB">Second sample (e.g., candidate benchmark timings).</param>
    /// <returns>The two-tailed p-value.</returns>
    public static double Test(double[] sampleA, double[] sampleB)
    {
        var n1 = sampleA.Length;
        var n2 = sampleB.Length;

        if (n1 == 0 || n2 == 0) return 1.0;

        // Combine and rank all values, handling ties by assigning mean rank.
        var combined = new (double Value, int Group)[n1 + n2];
        for (var i = 0; i < n1; i++) combined[i]     = (sampleA[i], 0);
        for (var i = 0; i < n2; i++) combined[n1 + i] = (sampleB[i], 1);

        Array.Sort(combined, (a, b) => a.Value.CompareTo(b.Value));

        var ranks = new double[n1 + n2];
        var j     = 0;
        while (j < combined.Length)
        {
            var k = j + 1;
            while (k < combined.Length && combined[k].Value == combined[j].Value)
                k++;

            var rankCount = k - j;
            // Mean rank for tied group: (j+1 + k) / 2.0
            var meanRank  = (j + k + 1) / 2.0;
            for (var t = j; t < k; t++)
                ranks[t] = meanRank;

            j = k;
        }

        // Sum ranks for sample A (group 0).
        double R1 = 0;
        for (var i = 0; i < combined.Length; i++)
            if (combined[i].Group == 0)
                R1 += ranks[i];

        var U1 = R1 - (double)n1 * (n1 + 1) / 2.0;
        var U2 = (double)n1 * n2 - U1;
        var U  = Math.Min(U1, U2);   // two-tailed: use smaller U

        var mu    = (double)n1 * n2 / 2.0;
        var total = n1 + n2;

        // Tie correction: sum over tied groups of (t^3 - t)
        var tieCorrection = 0.0;
        j = 0;
        while (j < combined.Length)
        {
            var k = j + 1;
            while (k < combined.Length && combined[k].Value == combined[j].Value)
                k++;

            var t = k - j;
            if (t > 1)
                tieCorrection += t * t * t - t;

            j = k;
        }

        var sigma = Math.Sqrt(
            ((double)n1 * n2 / (total * (total - 1))) *
            ((total * total * total - total) / 12.0 - tieCorrection / 12.0)
        );

        if (sigma == 0) return 1.0;  // all values identical

        var z = (U - mu) / sigma;

        // Normal approximation for p-value (two-tailed).
        return 2.0 * (1.0 - NormalCdf(Math.Abs(z)));
    }

    /// <summary>
    /// Approximation of the standard normal cumulative distribution function.
    /// Abromowitz and Stegun approximation; accurate to ~1e-7.
    /// </summary>
    private static double NormalCdf(double x)
    {
        const double a1 =  0.254829592;
        const double a2 = -0.284496736;
        const double a3 =  1.421413741;
        const double a4 = -1.453152027;
        const double a5 =  1.061405429;
        const double p  =  0.3275911;

        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x) / Math.Sqrt(2.0);

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}
```

#### Significance Integration

The suite and host compute significance automatically when running 2+ benchmarks with
significance enabled. The logic:

1. Identify the baseline benchmark (explicitly marked or fastest by median).
2. For each non-baseline benchmark, run a two-tailed Mann-Whitney U test against the
   baseline's raw samples.
3. Annotate the result with the p-value and a significance flag.

The `[Baseline]` attribute makes the comparison target explicit. Without it, the fastest
benchmark (by median) is used as the implicit baseline.

### 5.4 Display Format

In the console reporter, significance is shown adjacent to the ratio column:

```
  Baseline:    ContainsOrdinal      45.2 ns  (baseline)      48 B
  Comparison:  ContainsDefault      48.1 ns  1.06x  p=0.03 ✓   48 B
  Comparison:  RegexMatch          120.3 ns  2.66x  p<0.01 ✓  256 B
```

- `✓` - p < 0.05 (statistically significant difference)
- `~` - p ≥ 0.05 (difference may be noise)

For single-benchmark runs (Tier 1), no significance output appears - there is nothing to
compare against.

The p-value column can be hidden via `.WithSignificance(false)` on the builder.

---

## 6. Reporters

All reporters implement the same interface:

```csharp
// IReporter.cs
namespace NBenchmark.Reporters;

public interface IReporter
{
    Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default);
}
```

### 6.1 Console Reporter

Uses Spectre.Console for coloured output, a bar chart, and significance annotations when
comparing multiple benchmarks.

```csharp
// ConsoleReporter.cs
namespace NBenchmark.Reporters;

using Spectre.Console;

public sealed class ConsoleReporter : IReporter
{
    public Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No results to display.[/]");
            return Task.CompletedTask;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Benchmark Results[/]");

        var multiBenchmark = results.Count > 1;
        var successful = results.Where(r => !r.Errored).ToList();

        // Guard against all benchmarks erroring - no baseline, no significance.
        if (successful.Count == 0)
        {
            foreach (var result in results)
                AnsiConsole.MarkupLine($"[red][Error] {EscapeMarkup(result.Name)}: {EscapeMarkup(result.ErrorMessage)}[/]");
            return Task.CompletedTask;
        }

        // Use the first SUCCESSFUL result for the header. If results[0] is errored,
        // the WarmupIterations/MeasuredIterations are 0 (BenchmarkResult.Errored
        // factory) and would render as "0 warmup / 0 measured" - misleading. The
        // all-errored case is guarded above (early return before this point).
        var headerSource = successful[0];
        AnsiConsole.MarkupLine($"[grey]Run at {headerSource.RunAt:yyyy-MM-dd HH:mm:ss} UTC - "
                             + $"{headerSource.WarmupIterations} warmup / "
                             + $"{headerSource.MeasuredIterations} measured[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Benchmark")
            .AddColumn(new TableColumn("Median").Centered())
            .AddColumn(new TableColumn("Mean").Centered())
            .AddColumn(new TableColumn("P95").Centered())
            .AddColumn(new TableColumn("P99").Centered())
            .AddColumn(new TableColumn("StdDev").Centered())
            .AddColumn(new TableColumn("Ratio").Centered())
            .AddColumn(new TableColumn("Alloc/op").Centered());

        var hasDescriptions = results.Any(r => !string.IsNullOrEmpty(r.Description));
        if (hasDescriptions)
            table.AddColumn("Description");

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                    ?? successful.MinBy(r => r.Median)!;
        var totalDuration = results.Aggregate(TimeSpan.Zero, (a, r) => a + r.TotalDuration);

        foreach (var result in results.OrderBy(r => r.Median))
        {
            if (result.Errored)
            {
                var errorCols = new List<string>
                {
                    $"[red][Error] {EscapeMarkup(result.Name)}[/]",
                    "[red]-[/]", "[red]-[/]", "[red]-[/]",
                    "[red]-[/]", "[red]-[/]", "[red]-[/]", "[red]-[/]"
                };
                if (hasDescriptions)
                    errorCols.Add("[red]-[/]");
                table.AddRow(errorCols.ToArray());
                AnsiConsole.MarkupLine($"[red]  Error: {EscapeMarkup(result.ErrorMessage)}[/]");
                continue;
            }

            // Guard against divide-by-zero when baseline median is 0 (sub-ns operations).
            var ratio = baseline.Median == 0 ? double.NaN : result.Median / baseline.Median;
            var ratioCol = double.IsNaN(ratio)
                ? "[grey]N/A[/]"
                : result.IsBaseline
                    ? "[grey]1.00x[/]"
                    : ratio <= 1.05 ? $"[green]{ratio:F2}x[/]"
                    : ratio <= 1.5  ? $"[yellow]{ratio:F2}x[/]"
                    :                 $"[red]{ratio:F2}x[/]";

            var significanceCol = "";
            if (multiBenchmark && !result.IsBaseline && result.IsSignificant.HasValue)
            {
                significanceCol = result.IsSignificant.Value
                    ? " [green]✓[/]"
                    : " [grey]~[/]";
            }

            var safeName = EscapeMarkup(result.Name);
            var nameCol = result.IsBaseline
                ? $"[bold]{safeName}[/] [grey](baseline)[/]"
                : ratio <= 1.05 ? $"[green]{safeName}[/]"
                : ratio <= 1.5  ? $"[yellow]{safeName}[/]"
                :                 $"[red]{safeName}[/]";

            var rowCols = new List<string>
            {
                $"{nameCol}{significanceCol}",
                BenchmarkFormatter.FormatNs(result.Median),
                BenchmarkFormatter.FormatNs(result.Mean),
                BenchmarkFormatter.FormatNs(result.P95),
                BenchmarkFormatter.FormatNs(result.P99),
                BenchmarkFormatter.FormatNs(result.StandardDeviation),
                ratioCol,
                result.MeanAllocatedBytes.HasValue
                    ? BenchmarkFormatter.FormatBytes(result.MeanAllocatedBytes.Value)
                    : "[grey]-[/]"
            };
            if (hasDescriptions)
                rowCols.Add(string.IsNullOrEmpty(result.Description) ? "" : EscapeMarkup(result.Description));
            table.AddRow(rowCols.ToArray());
        }

        AnsiConsole.Write(table);

        // Bar chart - exclude errored results
        if (successful.Count > 1)
        {
            AnsiConsole.WriteLine();
            var chart = new BarChart()
                .Width(60)
                .Label("[bold]Median (ns)[/]")
                .CenterLabel();

            foreach (var result in successful.OrderBy(r => r.Median))
                chart.AddItem(result.Name, Math.Round(result.Median, 1), Color.SteelBlue1);

            AnsiConsole.Write(chart);
        }

        // Summary footer
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[grey]Ran {results.Count} benchmark(s) in {totalDuration.TotalSeconds:F1}s - "
            + $"Significance: Mann-Whitney U (p < 0.05) - "
            + $"Outliers: {FormatOutlierMode(results.FirstOrDefault()?.OutlierMode ?? OutlierMode.RemoveTop5Percent)}[/]");

        AnsiConsole.WriteLine();
        return Task.CompletedTask;
    }

    // No FormatNs/FormatBytes here - use shared BenchmarkFormatter

    private static string FormatOutlierMode(OutlierMode mode) => mode switch
    {
        OutlierMode.None => "none",
        OutlierMode.RemoveTop5Percent => "top 5%",
        OutlierMode.RemoveTop5PercentAndBottom5Percent => "top & bottom 5%",
        OutlierMode.IqrFence => "IQR fence (1.5×)",
        _ => "auto",
    };

    private static string EscapeMarkup(string? text) =>
        text?.Replace("[", "[[").Replace("]", "]]") ?? "";
}
```

**Example console output:**

```
Benchmark Results
Run at 2025-06-01 09:32:14 UTC - 25 warmup / 200 measured

╭───────────────────┬──────────┬──────────┬──────────┬──────────┬─────────┬────────┬──────────╮
│ Benchmark         │  Median  │   Mean   │   P95    │   P99    │  StdDev │  Ratio │ Alloc/op │
├───────────────────┼──────────┼──────────┼──────────┼──────────┼─────────┼────────┼──────────┤
│ Interpolate       │ 39.8 ns  │ 41.2 ns  │ 48.1 ns  │ 59.3 ns  │  4.2 ns │ 1.00x  │    48 B  │
│ Concat       ✓    │ 42.1 ns  │ 43.8 ns  │ 50.4 ns  │ 62.0 ns  │  4.9 ns │ 1.06x  │    48 B  │
│ StringBuilder ✓   │ 112.4 ns │ 115.9 ns │ 128.6 ns │ 141.2 ns │ 11.3 ns │ 2.82x  │   128 B  │
╰───────────────────┴──────────┴──────────┴──────────┴──────────┴─────────┴────────┴──────────╯

  Median (ns)
  Interpolate   ████████████████████░░░░░░░░░░░░░░░░░░░░░░░  39.8
  Concat        █████████████████████░░░░░░░░░░░░░░░░░░░░░░  42.1
  StringBuilder ████████████████████████████████████████████ 112.4

  Ran 3 benchmarks in 0.4s - Significance: Mann-Whitney U (p < 0.05) - Outliers: top 5%
```

### 6.2 Markdown Reporter

Outputs a GitHub-flavoured markdown table, ideal for pasting into PRs. Includes ratio
and significance columns.

```csharp
// MarkdownReporter.cs
namespace NBenchmark.Reporters;

public sealed class MarkdownReporter : IReporter
{
    private readonly string _outputPath;

    public MarkdownReporter(string outputPath = "benchmark-results.md")
    {
        // Timestamp the filename to avoid clobbering previous runs (consistent with JsonReporter).
        if (outputPath == "benchmark-results.md")
            outputPath = $"benchmark-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md";
        _outputPath = ValidateOutputPath(outputPath);
    }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Benchmark Results");
        sb.AppendLine();

        // Guard against all-errored results: a successful baseline is required
        // for ratio computation and for the header info. Matches the
        // ConsoleReporter's all-errored early-return behaviour.
        var successful = results.Where(r => !r.Errored).ToList();
        if (successful.Count == 0)
        {
            sb.AppendLine("_All benchmarks errored - no results to display._");
            await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
            return;
        }

        // Use the first SUCCESSFUL result for the header (results[0] may be
        // errored, in which case WarmupIterations/MeasuredIterations are 0).
        var headerSource = successful[0];
        sb.AppendLine($"_Run at {headerSource.RunAt:yyyy-MM-dd HH:mm:ss} UTC - "
                    + $"{headerSource.WarmupIterations} warmup / "
                    + $"{headerSource.MeasuredIterations} measured_");
        sb.AppendLine();

        var multiBenchmark = results.Count > 1;
        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                    ?? successful.MinBy(r => r.Median)!;

        sb.AppendLine("| Benchmark | Median | Mean | P95 | P99 | StdDev | Ratio | Sig | Alloc/op |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var result in results.OrderBy(r => r.Median))
        {
            var ratio = result.Median / baseline.Median;
            var sig = !multiBenchmark || result.IsBaseline || !result.IsSignificant.HasValue
                ? "-"
                : result.IsSignificant.Value ? "✓" : "~";

            sb.AppendLine(
                $"| {result.Name} " +
                $"| {BenchmarkFormatter.FormatNs(result.Median)} " +
                $"| {BenchmarkFormatter.FormatNs(result.Mean)} " +
                $"| {BenchmarkFormatter.FormatNs(result.P95)} " +
                $"| {BenchmarkFormatter.FormatNs(result.P99)} " +
                $"| {BenchmarkFormatter.FormatNs(result.StandardDeviation)} " +
                $"| {ratio:F2}x " +
                $"| {sig} " +
                $"| {(result.MeanAllocatedBytes.HasValue ? BenchmarkFormatter.FormatBytes(result.MeanAllocatedBytes.Value) : "-")} |"
            );
        }

        await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
    }

}
```

### 6.3 JSON Reporter

Writes structured JSON, usable by CI pipelines and the Phase 2 web UI. Includes p-values
and baseline flag in the output.

```csharp
// JsonReporter.cs
namespace NBenchmark.Reporters;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class JsonReporter : IReporter
{
    private readonly string _outputDirectory;

    public JsonReporter(string outputDirectory = ".")
    {
        _outputDirectory = ValidateOutputPath(outputDirectory);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // NOTE: Do NOT use WhenWritingNull - downstream consumers (CI tooling, web UI)
        // may rely on key presence for nullable fields like PValue, IsSignificant, etc.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static int _jsonFileCounter;

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        // Include milliseconds and a monotonic counter to prevent collisions when
        // multiple runs happen in the same second (e.g., CI matrix builds).
        var counter = Interlocked.Increment(ref _jsonFileCounter);
        var fileName = $"benchmarks-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{counter:D3}.json";
        var filePath = Path.Combine(_outputDirectory, fileName);

        var envelope = new ResultEnvelope
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Results = results,
        };

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, envelope, Options, cancellationToken);
    }

    private sealed class ResultEnvelope
    {
        public DateTimeOffset GeneratedAt { get; init; }
        public IReadOnlyList<BenchmarkResult> Results { get; init; } = [];
    }
}
```

> **Note:** Reporters that write files do not emit `Console.WriteLine` messages. The host
> or suite orchestrates user-facing output. Reporters are purely format-and-write.

### 6.4 CSV Reporter

```csharp
// CsvReporter.cs
namespace NBenchmark.Reporters;

public sealed class CsvReporter : IReporter
{
    private readonly string _outputPath;

    public CsvReporter(string outputPath = "benchmark-results.csv")
    {
        _outputPath = ValidateOutputPath(outputPath);
    }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Name,Median,Mean,P95,P99,StdDev,Ratio,Significant,AllocPerOp");

        var multiBenchmark = results.Count > 1;
        var baseline = results.FirstOrDefault(r => r.IsBaseline)
                    ?? results.MinBy(r => r.Median)!;

        foreach (var result in results.OrderBy(r => r.Median))
        {
            var ratio = result.Median / baseline.Median;
            var sig = !multiBenchmark || result.IsBaseline || !result.IsSignificant.HasValue
                ? ""
                : result.IsSignificant.Value ? "true" : "false";

            // Quoting convention: string columns are quoted (and embedded quotes
            // are escaped by doubling). Numeric columns are unquoted. This
            // matches RFC 4180 and keeps the file easy to read.
            var safeName = result.Name.Replace("\"", "\"\""); // CSV escaping
            var safeSig  = sig.Replace("\"", "\"\"");
            sb.AppendLine(
                $"\"{safeName}\"," +
                $"{result.Median:F1}," +
                $"{result.Mean:F1}," +
                $"{result.P95:F1}," +
                $"{result.P99:F1}," +
                $"{result.StandardDeviation:F1}," +
                $"{ratio:F2}," +
                $"\"{safeSig}\"," +
                $"{result.MeanAllocatedBytes?.ToString() ?? "null"}"
            );
        }

        await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
    }
}

---

## 7. Host & Discovery

### 7.1 Benchmark Attributes

```csharp
// BenchmarkAttribute.cs
namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkAttribute : Attribute
{
    public string? Description { get; set; }
    public bool Baseline { get; set; }
    public int? Iterations { get; set; }        // null = use host default
    public int? WarmupIterations { get; set; }  // null = use host default
}

// BenchmarkSetupAttribute.cs
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkSetupAttribute : Attribute;

// BenchmarkTeardownAttribute.cs
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkTeardownAttribute : Attribute;

// BenchmarkIterationSetupAttribute.cs
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkIterationSetupAttribute : Attribute;

// BenchmarkIterationTeardownAttribute.cs
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkIterationTeardownAttribute : Attribute;

// BenchmarkArgumentsAttribute.cs (post-v1)
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class BenchmarkArgumentsAttribute : Attribute
{
    public object[] Arguments { get; }
    public BenchmarkArgumentsAttribute(params object[] arguments)
    {
        Arguments = arguments;
    }
}
```

### 7.2 Discovery

```csharp
// BenchmarkDiscoverer.cs
namespace NBenchmark.Discovery;

using System.Reflection;

public sealed class BenchmarkDiscoverer
{
    public IReadOnlyList<BenchmarkSuiteDefinition> Discover(Assembly assembly)
    {
        var suites = new List<BenchmarkSuiteDefinition>();

        var types = assembly.GetTypes()
            .Where(t => !t.IsAbstract
                     && t.GetMethods().Any(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null));

        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                           .Cast<MethodInfo>()
                           .Concat(type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                           .ToArray();

            // Hoist lifecycle method lookups out of the per-benchmark Select
            // (these don't depend on the benchmark method itself).
            var setupMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkSetupAttribute>() is not null);
            var teardownMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkTeardownAttribute>() is not null);
            var iterSetupMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkIterationSetupAttribute>() is not null);
            var iterTeardownMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkIterationTeardownAttribute>() is not null);

            // Build cached lifecycle delegates once per type (not per benchmark).
            Action<object>? setupDel = setupMethod is not null
                ? (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), setupMethod)
                : null;
            Action<object>? teardownDel = teardownMethod is not null
                ? (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), teardownMethod)
                : null;
            Action<object>? iterSetupDel = iterSetupMethod is not null
                ? (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), iterSetupMethod)
                : null;
            Action<object>? iterTeardownDel = iterTeardownMethod is not null
                ? (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), iterTeardownMethod)
                : null;

            var benchmarks = methods
                .Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null)
                .Select(m =>
                {
                    // Build cached delegates once at discovery time to avoid
                    // MethodInfo.Invoke overhead (~700 ns-1 µs) on every iteration.
                    // For Task<T> methods, also cache the Result property to avoid
                    // per-iteration reflection for dead-code elimination prevention.
                    var isAsync = typeof(Task).IsAssignableFrom(m.ReturnType);
                    var returnsVoid = m.ReturnType == typeof(void);
                    Func<object, object?>? syncDelegate = null;
                    Func<object, Task>? asyncDelegate = null;
                    Func<Task, object?>? resultExtractor = null;

                    if (isAsync)
                    {
                        asyncDelegate = (Func<object, Task>)Delegate.CreateDelegate(
                            typeof(Func<object, Task>), m);

                        // Cache the Result property for Task<T> to avoid reflection per iteration.
                        if (m.ReturnType.IsGenericType)
                        {
                            var resultProp = m.ReturnType.GetProperty("Result")!;
                            resultExtractor = task => resultProp.GetValue(task);
                        }
                    }
                    else if (returnsVoid)
                    {
                        // void methods cannot be cast to Func<object, object?> - wrap to return null.
                        var action = (Action<object>)Delegate.CreateDelegate(
                            typeof(Action<object>), m);
                        syncDelegate = instance => { action(instance); return null; };
                    }
                    else
                    {
                        syncDelegate = (Func<object, object?>)Delegate.CreateDelegate(
                            typeof(Func<object, object?>), m);
                    }

                    return new BenchmarkMethodDefinition(
                        Method: m,
                        Attribute: m.GetCustomAttribute<BenchmarkAttribute>()!
                    )
                    {
                        SyncDelegate = syncDelegate,
                        AsyncDelegate = asyncDelegate,
                        ResultExtractor = resultExtractor,
                        IterationSetupDelegate = iterSetupDel,
                        IterationTeardownDelegate = iterTeardownDel,
                    };
                })
                .ToList();

            if (benchmarks.Count == 0) continue;

            suites.Add(new BenchmarkSuiteDefinition(
                Type: type,
                Benchmarks: benchmarks,
                SetupDelegate: setupDel,
                TeardownDelegate: teardownDel
            ));
        }

        return suites;
    }
}

public sealed record BenchmarkSuiteDefinition(
    Type Type,
    IReadOnlyList<BenchmarkMethodDefinition> Benchmarks,
    /// <summary>Cached delegate for [BenchmarkSetup] method on the suite (avoids MethodInfo.Invoke).</summary>
    Action<object>? SetupDelegate = null,
    /// <summary>Cached delegate for [BenchmarkTeardown] method on the suite (avoids MethodInfo.Invoke).</summary>
    Action<object>? TeardownDelegate = null
);

public sealed record BenchmarkMethodDefinition(
    MethodInfo Method,
    BenchmarkAttribute Attribute
)
{
    /// <summary>Cached delegate for sync methods (avoid MethodInfo.Invoke overhead per iteration).
    /// For void methods, wraps as Action<object> returning null.</summary>
    public Func<object, object?>? SyncDelegate { get; init; }
    /// <summary>Cached delegate for async methods returning Task.</summary>
    public Func<object, Task>? AsyncDelegate { get; init; }
    /// <summary>Cached Result property extractor for Task&lt;T&gt; methods (avoids per-iteration reflection).</summary>
    public Func<Task, object?>? ResultExtractor { get; init; }
    /// <summary>Cached delegate for [BenchmarkIterationSetup] method (avoids MethodInfo.Invoke).</summary>
    public Action<object>? IterationSetupDelegate { get; init; }
    /// <summary>Cached delegate for [BenchmarkIterationTeardown] method (avoids MethodInfo.Invoke).</summary>
    public Action<object>? IterationTeardownDelegate { get; init; }
}
```

**Changes from v1 design:**

- `internal` types are now discovered (removed `IsPublic` filter).
- `BenchmarkMethodDefinition` now carries `ResultExtractor` for `Task<T>` (avoids per-iteration reflection).
- `BenchmarkMethodDefinition` now carries cached iteration setup/teardown delegates (avoids `MethodInfo.Invoke`).
- `BenchmarkSuiteDefinition` now carries cached suite-level setup/teardown delegates.
- `void`-returning benchmark methods are handled via `Action<object>` delegate (avoiding `Func<object, object?>` cast failure).
- Lifecycle `MethodInfo` references have been removed in favour of cached delegates on the suite/method definitions.

### 7.3 Benchmark Host

```csharp
// BenchmarkHost.cs
namespace NBenchmark;

using System.Reflection;

public sealed class BenchmarkHost
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<IReporter> _reporters = [new ConsoleReporter()];
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private RunOrder _runOrder = RunOrder.Random;
    private string? _filter;
    private string? _outputDir;
    private bool _listOnly;
    private bool _dryRun;
    private bool _showHelp;
    private bool _thresholdRejected;
    private int? _seed;

    private BenchmarkHost() { }

    public static BenchmarkHost Create(string[] args)
    {
        var host = new BenchmarkHost();
        host.ParseArgs(args);
        return host;
    }

    public BenchmarkHost AddFromAssembly<T>()
    {
        _assemblies.Add(typeof(T).Assembly);
        return this;
    }

    public BenchmarkHost AddFromAssembly(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    public BenchmarkHost WithReporter(IReporter reporter)
    {
        _reporters.Add(reporter);
        return this;
    }

    public BenchmarkHost WithOptions(MeasurementOptions options)
    {
        _options = options;
        return this;
    }

    public BenchmarkHost WithRunOrder(RunOrder order)
    { _runOrder = order; return this; }

    public BenchmarkHost WithProgress(IBenchmarkProgress progress)
    { _progress = progress; return this; }

    /// <summary>Remove the default console reporter (keeping progress output).</summary>
    public BenchmarkHost WithoutConsoleReporter()
    { _reporters.RemoveAll(r => r is ConsoleReporter); return this; }

    /// <summary>Remove the default console reporter AND progress output.</summary>
    public BenchmarkHost WithoutConsoleOutput()
    { _reporters.RemoveAll(r => r is ConsoleReporter); _progress = NullBenchmarkProgress.Instance; return this; }

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_showHelp) { PrintHelp(); return Array.Empty<BenchmarkResult>(); }

        // Emit timer resolution so users know their measurement precision.
        // Sub-100-ns benchmarks become noise on low-resolution virtualized timers.
        Console.WriteLine($"Timer resolution: {Stopwatch.Frequency:N0} ticks/s "
                        + $"({1_000_000_000.0 / Stopwatch.Frequency:F2} ns per tick)");
        Console.WriteLine();

        var discoverer = new BenchmarkDiscoverer();
        var allSuites  = _assemblies.SelectMany(discoverer.Discover).ToList();

        if (allSuites.Count == 0)
        {
            Console.WriteLine("No benchmark classes found. Decorate methods with [Benchmark].");
            return Array.Empty<BenchmarkResult>();
        }

        var filtered = FilterSuites(allSuites);

        if (_listOnly)
        {
            foreach (var suite in filtered)
            {
                Console.WriteLine($"── {suite.Type.Name} ──");
                foreach (var b in suite.Benchmarks)
                    Console.WriteLine($"    {b.Method.Name}"
                        + (b.Attribute.Description is not null ? $" - {b.Attribute.Description}" : ""));
            }
            return Array.Empty<BenchmarkResult>();
        }

        // Default to console progress unless explicitly overridden.
        if (_progress is NullBenchmarkProgress)
            _progress = new ConsoleBenchmarkProgress(_options.Iterations, _options.WarmupIterations);

        var allResults = new List<BenchmarkResult>();
        var rawSamples = new Dictionary<string, double[]>();

        var totalBenchmarks = filtered.Sum(s => s.Benchmarks.Count);
        var runningIndex = 0;

        await _progress.OnSuiteStarting(
            filtered.SelectMany(s => s.Benchmarks.Select(b => $"{s.Type.Name}.{b.Method.Name}")).ToList(),
            totalBenchmarks);

        foreach (var suite in filtered)
        {
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(suite.Type);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[Error] Could not instantiate {suite.Type.Name} - "
                    + "the type must have a public parameterless constructor, or be internal with "
                    + "a public constructor and InternalsVisibleTo. "
                    + $"Details: {ex.Message}");
                continue;
            }

            // Suite-level setup - wrapped in try/catch so a failing setup
            // marks the entire suite as errored rather than crashing the host.
            // Per-benchmark iteration setup/teardown delegates are on each BenchmarkMethodDefinition.
            try
            {
                suite.SetupDelegate?.Invoke(instance);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[Error] Setup failed for {suite.Type.Name}: {ex.Message}");
                // Mark all benchmarks in this suite as errored.
                foreach (var b in suite.Benchmarks)
                {
                    var name = $"{suite.Type.Name}.{b.Method.Name}";
                    allResults.Add(BenchmarkResult.Errored(name,
                        $"Suite setup failed: {ex.Message}", b.Attribute.Description,
                        b.Attribute.Baseline, _options.OutlierMode));
                }
                continue;
            }

            try
            {
                var ordered = _runOrder == RunOrder.Random
                    ? ShuffleBenchmarks(suite.Benchmarks.ToList(), _seed ?? Random.Shared.Next())
                    : suite.Benchmarks;

                foreach (var benchmark in ordered)
                {
                    var benchmarkName = $"{suite.Type.Name}.{benchmark.Method.Name}";
                    runningIndex++;

                    await _progress.OnWarmupStarting(benchmarkName, _options.WarmupIterations);
                    await _progress.OnBenchmarkStarting(benchmarkName, runningIndex, totalBenchmarks);

                    BenchmarkResult result;

                    try
                    {
                        var options = benchmark.Attribute.Iterations.HasValue
                            ? _options with { Iterations = benchmark.Attribute.Iterations.Value }
                            : _options;

                        // Use cached delegates built at discovery time (not MethodInfo.Invoke)
                        Func<Task> action;
                        if (benchmark.AsyncDelegate is not null)
                        {
                            // async benchmark - use cached ResultExtractor (avoids per-iteration reflection)
                            var asyncDel = benchmark.AsyncDelegate;
                            var resultExtractor = benchmark.ResultExtractor;
                            action = async () =>
                            {
                                var task = asyncDel(instance);
                                await task;

                                if (resultExtractor is not null)
                                {
                                    var resultValue = resultExtractor(task);
                                    if (resultValue is not null)
                                        ResultSink.Consume(resultValue);
                                }
                            };
                        }
                        else
                        {
                            // sync benchmark - use cached delegate for zero-reflection overhead
                            var syncDel = benchmark.SyncDelegate!;
                            action = () =>
                            {
                                var r = syncDel(instance);
                                if (r is not null) ResultSink.Consume(r);
                                return Task.CompletedTask;
                            };
                        }

                        // Use cached lifecycle delegates (not MethodInfo.Invoke)
                        Action? iterSetup = benchmark.IterationSetupDelegate is not null
                            ? () => benchmark.IterationSetupDelegate(instance)
                            : null;
                        Action? iterTeardown = benchmark.IterationTeardownDelegate is not null
                            ? () => benchmark.IterationTeardownDelegate(instance)
                            : null;

                        if (_dryRun)
                        {
                            await action();
                            result = new BenchmarkResult
                            {
                                Name               = benchmarkName,
                                Description        = benchmark.Attribute.Description,
                                Mean               = 0, Median = 0, P95 = 0, P99 = 0,
                                Min                = 0, Max = 0, StandardDeviation = 0,
                                MeanAllocatedBytes = null,
                                PValue             = null, IsSignificant = null,
                                Errored            = false, ErrorMessage = null,
                                MeasuredIterations = 0, WarmupIterations = 0,
                                RunAt              = DateTimeOffset.UtcNow,
                                TotalDuration      = TimeSpan.Zero,
                                IsBaseline         = benchmark.Attribute.Baseline,
                                OutlierMode        = _options.OutlierMode,
                            };
                        }
                        else
                        {
                            var outcome = await MeasurementEngine.MeasureAsync(
                                name: benchmarkName,
                                action: action,
                                options: options,
                                description: benchmark.Attribute.Description,
                                isBaseline: benchmark.Attribute.Baseline,
                                iterationSetup: iterSetup,
                                iterationTeardown: iterTeardown,
                                cancellationToken: cancellationToken
                            );
                            result = outcome.Result;
                            rawSamples[benchmarkName] = outcome.RawSamples;
                        }

                        await _progress.OnWarmupCompleted(benchmarkName);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (TargetInvocationException tiex)
                    {
                        var inner = tiex.InnerException ?? tiex;
                        result = BenchmarkResult.Errored(benchmarkName, inner.ToString(),
                            benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                            _options.OutlierMode);
                    }
                    catch (Exception ex)
                    {
                        result = BenchmarkResult.Errored(benchmarkName, ex.ToString(),
                            benchmark.Attribute.Description, benchmark.Attribute.Baseline,
                            _options.OutlierMode);
                    }

                    allResults.Add(result);
                    await _progress.OnBenchmarkCompleted(result);

                    if (_options.ForceGcBetweenBenchmarks)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                        GC.WaitForPendingFinalizers();
                    }
                }
            }
            finally
            {
                // Suite-level teardown - best-effort, never crashes the host.
                try
                {
                    suite.TeardownDelegate?.Invoke(instance);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[Warning] Teardown failed for {suite.Type.Name}: {ex.Message}");
                }
            }
        }

        await _progress.OnSuiteCompleted(allResults);

        // Compute significance for multi-benchmark suites
        if (_options.EnableSignificance && allResults.Count(r => !r.Errored) > 1)
            ComputeSignificance(allResults, rawSamples);

        if (!string.IsNullOrEmpty(_outputDir))
        {
            // TODO (Milestone 3): repoint MarkdownReporter / JsonReporter / CsvReporter
            // to paths within _outputDir.
        }

        foreach (var reporter in _reporters)
            await reporter.ReportAsync(allResults, cancellationToken);

        // Apply --threshold-pct rejection flag here, AFTER all reporters have run,
        // so a later reporter cannot overwrite Environment.ExitCode.
        if (_thresholdRejected)
            Environment.ExitCode = 1;

        return allResults;
    }

    /// <summary>
    /// Computes Mann-Whitney U significance for each non-baseline benchmark
    /// against the designated baseline. If no baseline is explicitly set, the
    /// fastest benchmark by median is used as the implicit baseline.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: this method mutates <paramref name="results"/> in place
    /// via index assignment (<c>results[i] = result with { ... }</c>). Safe for
    /// the current single-threaded execution model (BenchmarkSuite and
    /// BenchmarkHost both run benchmarks sequentially), but a parallel runner
    /// would need to serialize this step or collect into a new list.
    /// </remarks>
    private static void ComputeSignificance(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples)
    {
        // Identify baseline - skip errored results. Guard against all-errored results.
        var successful = results.Where(r => !r.Errored).ToList();
        if (successful.Count == 0) return;

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                    ?? successful.MinBy(r => r.Median)!;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result == baseline || result.Errored) continue;

            if (rawSamples.TryGetValue(baseline.Name, out var baselineSamples) &&
                rawSamples.TryGetValue(result.Name, out var candidateSamples))
            {
                var pValue = MannWhitneyU.Test(baselineSamples, candidateSamples);
                results[i] = result with { PValue = pValue, IsSignificant = pValue < 0.05 };
            }
        }
    }

    private IReadOnlyList<BenchmarkSuiteDefinition> FilterSuites(
        IReadOnlyList<BenchmarkSuiteDefinition> suites)
    {
        if (_filter is null) return suites;

        return suites
            .Select(s => s with
            {
                Benchmarks = s.Benchmarks
                    .Where(b => GlobMatch(_filter,
                        $"{s.Type.Name}.{b.Method.Name}"))
                    .ToList()
            })
            .Where(s => s.Benchmarks.Count > 0)
            .ToList();
    }

    private static bool GlobMatch(string pattern, string input)
    {
        // Minimal glob: * matches any sequence of characters.
        // If the pattern is exactly "*", match everything.
        if (pattern == "*") return true;

        var parts = pattern.Split('*');
        if (parts.Length == 0) return true;

        var remaining = input;

        // Anchor at start if pattern doesn't begin with *
        if (!pattern.StartsWith("*"))
        {
            var first = parts[0];
            if (!remaining.StartsWith(first, StringComparison.OrdinalIgnoreCase))
                return false;
            remaining = remaining[first.Length..];
        }

        for (var i = pattern.StartsWith("*") ? 0 : 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == parts.Length - 1 && !pattern.EndsWith("*"))
            {
                // Anchor at end
                if (!remaining.EndsWith(part, StringComparison.OrdinalIgnoreCase))
                    return false;
                break;
            }

            var idx = remaining.IndexOf(part, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            remaining = remaining[(idx + part.Length)..];
        }

        return true;
    }

    /// <summary>
    /// Fisher-Yates shuffle using the provided seed. Emits the seed to stdout
    /// so runs are reproducible.
    /// </summary>
    private static List<T> ShuffleBenchmarks<T>(List<T> items, int seed)
    {
        Console.WriteLine($"[seed: {seed}]");
        var rng = new Random(seed);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(items);
        for (var i = span.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (span[i], span[j]) = (span[j], span[i]);
        }
        return items;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: myapp.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --filter <pattern>     Run suites/methods matching glob (e.g., String*, *.Contains*)");
        Console.WriteLine("  --iterations <n>       Number of measured iterations (default: 200)");
        Console.WriteLine("  --warmup <n>           Number of warmup iterations (default: 25)");
        Console.WriteLine("  --reporter <type>      Set reporter: console, json, markdown, csv");
        Console.WriteLine("  --output <dir>         Set output directory for file-based reporters");
        Console.WriteLine("  --list                 List discovered benchmarks without running");
        Console.WriteLine("  --dry-run              Invoke each benchmark once (skip measurement)");
        Console.WriteLine("  --order <mode>         Run order: random (default) or declaration");
        Console.WriteLine("  --threshold-pct <n>    [NOT YET IMPLEMENTED] Will fail with exit code 1 if");
        Console.WriteLine("                        any benchmark regresses >N% vs baseline.");
        Console.WriteLine("  --help, -h             Show this help text");
    }

    private void ParseArgs(string[] args)
    {
        // Minimal hand-rolled argument parser (~50 lines).
        // No dependency on System.CommandLine (which is in maintenance mode).
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    _showHelp = true;
                    break;
                case "--filter" when i + 1 < args.Length:
                    _filter = args[++i];
                    break;
                case "--iterations" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var iters)
                        && iters >= MeasurementOptions.MinIterations
                        && iters <= MeasurementOptions.MaxIterations)
                        _options = _options with { Iterations = iters };
                    else
                        Console.WriteLine($"Invalid --iterations value '{args[i]}'. Must be {MeasurementOptions.MinIterations}–{MeasurementOptions.MaxIterations}.");
                    break;
                case "--warmup" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var warmup) && warmup >= 1 && warmup <= MeasurementOptions.MaxWarmupIterations)
                        _options = _options with { WarmupIterations = warmup };
                    else
                        Console.WriteLine($"Invalid --warmup value '{args[i]}'. Must be 1–{MeasurementOptions.MaxWarmupIterations}.");
                    break;
                case "--output" when i + 1 < args.Length:
                    _outputDir = ValidateOutputPath(args[++i]);
                    break;
                case "--reporter" when i + 1 < args.Length:
                    _reporters.RemoveAll(r => r is ConsoleReporter);
                    switch (args[++i]?.ToLowerInvariant())
                    {
                        case "json":     _reporters.Add(new JsonReporter()); break;
                        case "markdown": _reporters.Add(new MarkdownReporter()); break;
                        case "csv":      _reporters.Add(new CsvReporter()); break;
                        case "console":  _reporters.Add(new ConsoleReporter()); break;
                        default:
                            Console.WriteLine($"Unknown reporter: '{args[i]}'. Valid: console, json, markdown, csv");
                            break;
                    }
                    break;
                case "--order" when i + 1 < args.Length:
                    _runOrder = args[++i]?.ToLowerInvariant() == "declaration"
                        ? RunOrder.Declaration
                        : RunOrder.Random;
                    break;
                case "--threshold-pct" when i + 1 < args.Length:
                    // Not yet implemented in v1. Reject early to avoid silently-succeeding CI scripts.
                    // Set a flag here; RunAsync() checks the flag and sets Environment.ExitCode
                    // after execution completes (so a subsequent reporter error cannot clobber it).
                    Console.Error.WriteLine(
                        $"--threshold-pct is not yet implemented. Track progress at https://github.com/anomalyco/benchly");
                    _thresholdRejected = true;
                    i++; // consume the value
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var seed))
                        _seed = seed;
                    else
                        Console.WriteLine($"Invalid --seed value '{args[i]}'. Must be an integer.");
                    break;
                case "--list":
                    _listOnly = true;
                    break;
                case "--dry-run":
                    _dryRun = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown flag: '{args[i]}'. Use --help to see available options.");
                    Environment.ExitCode = 1;
                    break;
            }
        }
    }
}
```

**Key changes from v1 design:**

- **No `System.CommandLine` dependency** - hand-rolled parser (~50 lines).
- **Benchmark order randomisation** - Fisher-Yates shuffle with `--seed <n>` for reproducibility. Seed is printed on every run.
- **Significance testing** - uses `MeasurementOutcome.RawSamples` (pre-outlier-removal) for accurate Mann-Whitney U comparisons. Host significance is wired (not a no-op).
- **`[BenchmarkIterationTeardown]`** support in the action wrappers.
- **`internal` type discovery** - `BindingFlags.NonPublic` included; instantiation failures catch all exception types.
- **Improved `GlobMatch`** - handles anchor-at-start/end and `*`-only patterns. Unknown CLI flags produce warnings.
- **Per-benchmark error isolation** - a crashing benchmark produces an `Errored` result; the suite continues.
- **Inter-benchmark GC** - full Gen2 collection between benchmarks (`ForceGcBetweenBenchmarks`, default true).
- **`--dry-run`** - invokes each benchmark once without measurement for quick CI validation.
- **`--threshold-pct`** - rejected early with \"not yet implemented\" to prevent silent CI failures.
- **`WithoutConsoleOutput()`** - split into `WithoutConsoleReporter()` and `WithoutConsoleOutput()` for clarity.
- **`CancellationToken`** on `Benchmark.RunAsync` and all reporter/engine APIs.
- **Iteration setup/teardown runs outside the timed region** - passed as `iterationSetup`/`iterationTeardown` delegates to the engine.
- **Sync hot path** - `BenchmarkSuite` branches to `MeasureSync` when all benchmarks were registered with `Action`, avoiding async overhead per iteration.
- **Cached delegates** - `BenchmarkDiscoverer` builds `Func<object, object?>` delegates at discovery time, eliminating per-iteration `MethodInfo.Invoke` overhead.
- **Path validation** - all file-based reporters validate paths at construction time (not just CLI). Trailing separator prevents prefix-confusion attacks.
- **Console markup escaping** - all user-controlled strings (benchmark names, error messages) are escaped before Spectre.Console markup interpolation.
- **`MeasurementOptions`** - bounds are constants (`MinIterations`, `MaxIterations`, `MaxWarmupIterations`) referenced by both properties and CLI parser.
- **`BenchmarkAttribute`** - `Iterations` and `WarmupIterations` are `int?` (null = use host default) rather than sentinel `0`.
- **`BenchmarkHost.RunAsync`** returns `IReadOnlyList<BenchmarkResult>` for programmatic result access.
- **Tier 1 API** - `Benchmark.Run(Action)` returns `BenchmarkResult` directly; `Benchmark.Run<T>(Func<T>)` for sync with DCE protection; legacy `TimeAsync` overloads marked `[Obsolete]`.
- **`MeasureRaw`** exposed for users who want raw samples for custom analysis.
- **`Print()`** is truly synchronous (no async-over-sync) for console apps; `ToMarkdownAsync()`, `ToJsonAsync()`, and `ToCsvAsync()` extension methods are provided for the async path.
- **`OnWarmupCompleted`**, **`OnSuiteStarting`** hooks are now called by the suite engine.
- **Suite-level setup/teardown** via `.WithSuiteSetup(Action)` / `.WithSuiteTeardown(Action)`.
- **Timer resolution** (`Stopwatch.Frequency`) printed at host startup.
- **JSON/Markdown reporters** include monotonic counter and milliseconds in filenames to prevent collisions.
- **`WhenWritingNull`** removed from `JsonReporter` to preserve nullable field schema.
- **CSV null fields** emit explicit `"null"` string for strict parser compatibility.

**Key changes from v4 design (review-driven):**

- **`ConsoleReporter` Description column** - `AddRow` now populates Description cells when the column is added (was a runtime `IndexOutOfRangeException`)
- **All-errored baseline guard** - `ConsoleReporter` and both `ComputeSignificance` methods early-return when all results are errored
- **Divide-by-zero in ratio** - `ConsoleReporter` checks `baseline.Median == 0` and shows `N/A`
- **`void` sync benchmark methods** - discoverer uses `Action<object>` wrapper, avoiding `Func<object, object?>` cast failure
- **Per-iteration `Task<T>` reflection eliminated** - `ResultExtractor` cached at discovery, used in the measurement hot loop
- **Per-iteration setup/teardown reflection eliminated** - iteration lifecycle delegates cached at discovery on `BenchmarkMethodDefinition`; suite-level setup/teardown cached on `BenchmarkSuiteDefinition`
- **`OnSuiteStarting` / `OnWarmupCompleted` in `BenchmarkHost`** - both hooks now called (matches `BenchmarkSuite` behaviour)
- **Suite-level `SetupDelegate` / `TeardownDelegate` failure handling** - wrapped in try/catch with appropriate error/warning logging
- **`MarkdownReporter` invalid constructor** - duplicate `{ _outputPath = ... }` block removed
- **`MeasurementOptions.WarmupIterations` bounds** - validates `[1, MaxWarmupIterations]` like `Iterations`
- **`IqrFence` empty result fallback** - returns sorted array when IQR filter removes everything
- **Two-pass variance in `StatsSummary.Compute`** - eliminates catastrophic cancellation risk
- **`--threshold-pct` exit code race** - flag-based; `Environment.ExitCode` set after reporters finish
- **Placeholder URL replaced** - `https://github.com/anomalyco/benchly` in error message and `.csproj`
- **`PathValidation.cs` location** - `ValidateOutputPath` shown in project tree under `NBenchmark/Reporters/`
- **`samples/` directory structure fixed** - `Web/` no longer at same level as siblings

**Key changes from v5 design (review-driven):**

- **`BenchmarkResult.Errored` factory default** - `outlierMode` parameter now defaults to `OutlierMode.RemoveTop5Percent`, matching the pattern of the other optional parameters
- **`BenchmarkResult` required properties reduced** - only `Name` + the 7 timing stats + `Errored` are required. Optional fields (`RunAt`, `TotalDuration`, `MeanAllocatedBytes`, `PValue`, `IsSignificant`, `ErrorMessage`, `IsBaseline`, `OutlierMode`, `Description`, `MeasuredIterations`, `WarmupIterations`) have sensible defaults. Tests and the `Errored` factory no longer have to specify 20 fields to construct a result
- **`--threshold-pct` docs aligned with behaviour** - help text and Appendix E now correctly describe the flag as **rejected** (with exit code 1 via the `_thresholdRejected` flag), not silently accepted. The deliberate-rejection design is documented as safer for CI
- **CSV `Significant` column quoted** - name and `Significant` columns (strings) are quoted; numeric columns are unquoted. RFC 4180-conformant
- **`WarmupIterations = 0` rationale documented** - property doc comment explains the deliberate choice (prevents accidental mis-measurement for sub-µs benchmarks; the engine's loop would naturally allow 0)
- **Console/Markdown reporter header uses first non-errored result** - was `results[0]`, which would show "0 warmup / 0 measured" if the first result errored. `MarkdownReporter` also gained the all-errored guard that `ConsoleReporter` already has
- **Tier 1 `Benchmark.Run` setup/teardown limitation documented** - XML doc comment directs users to `BenchmarkSuite` or `[Benchmark]` + `[BenchmarkIterationSetup]` for iteration setup/teardown
- **Appendix B tests fixed** - two tests used `WarmupIterations = 0` which would now throw. Replaced with `1` (the math is about measured iterations, not warmup)
- **`BenchmarkSuite.ShuffleBenchmarks` prints seed** - matches `BenchmarkHost.ShuffleBenchmarks` for consistent UX
- **`ComputeSignificance` thread-safety noted** - XML doc comment flags that the in-place mutation is safe only for the current sequential execution model
- **`MeasurementEngine` static-by-design documented** - class-level XML comment explains the choice and notes the future escape hatch (interface) if extensibility is needed
- **CSV reporter markdown separator** - blank line added between the closing brace and the `---` section divider

### 7.4 Fluent Suite (Tier 2)

```csharp
// BenchmarkSuite.cs
namespace NBenchmark;

public sealed class BenchmarkSuite(string name)
{
    private readonly List<(
        string Name,
        Func<Task>? AsyncAction,
        Action? SyncAction,
        Action? Setup,
        Action? Teardown
    )> _benchmarks = [];
    private readonly List<IReporter> _reporters = [new ConsoleReporter()];
    private IBenchmarkProgress _progress = NullBenchmarkProgress.Instance;
    private MeasurementOptions _options = MeasurementOptions.Default;
    private RunOrder _runOrder = RunOrder.Random;
    private string? _baselineName;
    private Action? _suiteSetup;
    private Action? _suiteTeardown;

    // --- Add overloads ---

    // Store both sync and async actions. At execution time, sync benchmarks
    // use MeasureSync (no async overhead) while async benchmarks use MeasureAsync.

    public BenchmarkSuite Add(string name, Action action,
        Action? setup = null, Action? teardown = null)
    {
        _benchmarks.Add((name, null, action, setup, teardown));
        return this;
    }

    public BenchmarkSuite Add(string name, Func<Task> action,
        Action? setup = null, Action? teardown = null)
    {
        _benchmarks.Add((name, action, null, setup, teardown));
        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<T> action,
        Action? setup = null, Action? teardown = null)
    {
        // Wrap as an Action that consumes the return value to prevent DCE.
        // Note: the lambda captures `action` by reference; allocation per Add() is
        // one delegate, paid once at registration time, not per iteration.
        _benchmarks.Add((name, null, () => ResultSink.Consume(action()), setup, teardown));
        return this;
    }

    public BenchmarkSuite Add<T>(string name, Func<Task<T>> action,
        Action? setup = null, Action? teardown = null)
    {
        // Async path: the lambda awaits action() and consumes the result.
        // Closure captures `action` by reference; one delegate allocated per Add().
        _benchmarks.Add((name, async () => ResultSink.Consume(await action()), null, setup, teardown));
        return this;
    }

    // --- Configuration methods ---

    public BenchmarkSuite WithBaseline(string name)
    { _baselineName = name; return this; }

    public BenchmarkSuite WithIterations(int iterations)
    { _options = _options with { Iterations = iterations }; return this; }

    public BenchmarkSuite WithWarmup(int iterations)
    { _options = _options with { WarmupIterations = iterations }; return this; }

    public BenchmarkSuite WithMemory(bool enabled = true)
    { _options = _options with { MeasureAllocations = enabled }; return this; }

    public BenchmarkSuite WithOutlierMode(OutlierMode mode)
    { _options = _options with { OutlierMode = mode }; return this; }

    public BenchmarkSuite WithSignificance(bool enabled)
    { _options = _options with { EnableSignificance = enabled }; return this; }

    public BenchmarkSuite WithRunOrder(RunOrder order)
    { _runOrder = order; return this; }

    /// <summary>Run an action once before the entire suite (not timed).</summary>
    public BenchmarkSuite WithSuiteSetup(Action setup)
    { _suiteSetup = setup; return this; }

    /// <summary>Run an action once after the entire suite (not timed).</summary>
    public BenchmarkSuite WithSuiteTeardown(Action teardown)
    { _suiteTeardown = teardown; return this; }

    /// <summary>Remove the default console reporter (keeping progress output).</summary>
    public BenchmarkSuite WithoutConsoleReporter()
    { _reporters.RemoveAll(r => r is ConsoleReporter); return this; }

    public BenchmarkSuite WithReporter(IReporter reporter)
    { _reporters.Add(reporter); return this; }

    /// <summary>Suppress BOTH the default console reporter AND progress output.</summary>
    public BenchmarkSuite WithoutConsoleOutput()
    { _reporters.RemoveAll(r => r is ConsoleReporter); _progress = NullBenchmarkProgress.Instance; return this; }

    /// <summary>Provide progress hooks for live output during execution.</summary>
    public BenchmarkSuite WithProgress(IBenchmarkProgress progress)
    { _progress = progress; return this; }

    // --- Run ---

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        CancellationToken cancellationToken = default)
    {
        // If no custom progress was set, wire up default console progress.
        if (_progress is NullBenchmarkProgress)
            _progress = new ConsoleBenchmarkProgress(_options.Iterations, _options.WarmupIterations);

        // --- validate ---
        if (_baselineName is not null && !_benchmarks.Any(b => b.Name == _baselineName))
            throw new InvalidOperationException(
                $"Baseline '{_baselineName}' was not found in the suite. Registered names: " +
                string.Join(", ", _benchmarks.Select(b => b.Name)));

        // --- order ---
        var ordered = _runOrder == RunOrder.Random
            ? ShuffleBenchmarks(_benchmarks.ToList(), Random.Shared.Next())
            : _benchmarks;

        var results     = new List<BenchmarkResult>(ordered.Count);
        var rawSamples  = new Dictionary<string, double[]>();
        var total       = ordered.Count;
        var index       = 0;

        await _progress.OnSuiteStarting(
            ordered.Select(b => b.Name).ToList(), ordered.Count);

        _suiteSetup?.Invoke();

        foreach (var (benchmarkName, asyncAction, syncAction, setup, teardown) in ordered)
        {
            index++;

            await _progress.OnWarmupStarting(benchmarkName, _options.WarmupIterations);
            await _progress.OnBenchmarkStarting(benchmarkName, index, total);

            BenchmarkResult result;

            try
            {
                MeasurementOutcome outcome;
                if (syncAction is not null)
                {
                    // Sync hot path - MeasureSync avoids async state machine,
                    // Task.CompletedTask allocation, and TaskAwaiter overhead.
                    outcome = MeasurementEngine.MeasureSync(
                        name: benchmarkName,
                        action: syncAction,
                        options: _options,
                        isBaseline: _baselineName is not null && benchmarkName == _baselineName,
                        iterationSetup: setup,
                        iterationTeardown: teardown,
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    outcome = await MeasurementEngine.MeasureAsync(
                        name: benchmarkName,
                        action: asyncAction!,
                        options: _options,
                        isBaseline: _baselineName is not null && benchmarkName == _baselineName,
                        iterationSetup: setup,
                        iterationTeardown: teardown,
                        cancellationToken: cancellationToken
                    );
                }

                result = outcome.Result;
                rawSamples[benchmarkName] = outcome.RawSamples;

                await _progress.OnWarmupCompleted(benchmarkName);
            }
            catch (OperationCanceledException) { throw; }
            catch (TargetInvocationException tiex)
            {
                var inner = tiex.InnerException ?? tiex;
                var isBaseline = _baselineName is not null && benchmarkName == _baselineName;
                result = BenchmarkResult.Errored(benchmarkName, inner.ToString(),
                    isBaseline: isBaseline, outlierMode: _options.OutlierMode);
            }
            catch (Exception ex)
            {
                var isBaseline = _baselineName is not null && benchmarkName == _baselineName;
                result = BenchmarkResult.Errored(benchmarkName, ex.ToString(),
                    isBaseline: isBaseline, outlierMode: _options.OutlierMode);
            }

            results.Add(result);
            await _progress.OnBenchmarkCompleted(result);

            if (_options.ForceGcBetweenBenchmarks)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        }

        await _progress.OnSuiteCompleted(results);

        _suiteTeardown?.Invoke();

        // --- significance ---
        if (_options.EnableSignificance && results.Any(r => !r.Errored) && results.Count > 1)
            ComputeSignificance(results, rawSamples);

        // --- report ---
        foreach (var reporter in _reporters)
            await reporter.ReportAsync(results, cancellationToken);

        return results;
    }

    private static void ComputeSignificance(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples)
    {
        // Identify baseline - guard against all-errored results.
        var successful = results.Where(r => !r.Errored).ToList();
        if (successful.Count == 0) return;

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                    ?? successful.MinBy(r => r.Median)!;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result == baseline || result.Errored) continue;

            if (rawSamples.TryGetValue(baseline.Name, out var baselineSamples) &&
                rawSamples.TryGetValue(result.Name, out var candidateSamples))
            {
                var pValue = MannWhitneyU.Test(baselineSamples, candidateSamples);
                results[i] = result with { PValue = pValue, IsSignificant = pValue < 0.05 };
            }
        }
    }

    private static List<T> ShuffleBenchmarks<T>(List<T> items, int seed)
    {
        // Match BenchmarkHost: print the seed to stdout so runs are reproducible.
        // Users on BenchmarkSuite (no CLI) still get the seed in the output,
        // matching the host behaviour and making suite runs debuggable.
        Console.WriteLine($"[seed: {seed}]");
        var rng = new Random(seed);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(items);
        for (var i = span.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (span[i], span[j]) = (span[j], span[i]);
        }
        return items;
    }
}
```

### 7.5 One-Liner Entry Point (Tier 1)

```csharp
// Benchmark.cs
namespace NBenchmark;

/// <summary>
/// Static entry point for one-liner benchmarks.
/// </summary>
public static class Bench
{
    /// <summary>Measure a synchronous action. Returns BenchmarkResult directly (no async wrapper).</summary>
    /// <remarks>
    /// Tier 1 is intentionally zero-config: there are no iteration setup/teardown
    /// parameters on this method. If you need to mutate per-iteration state outside
    /// the timed region, use <see cref="BenchmarkSuite.Add(string, Action, Action?, Action?)"/>
    /// (Tier 2) or a <c>[Benchmark]</c> method with a <c>[BenchmarkIterationSetup]</c>
    /// attribute (Tier 3) instead. The measurement engine itself supports iteration
    /// setup/teardown - it's just not exposed at the Tier 1 surface.
    /// </remarks>
    public static BenchmarkResult Time(
        Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = MeasurementEngine.MeasureSync(name, action, options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    /// <summary>Measure a synchronous function, consuming its return value to prevent DCE.</summary>
    public static BenchmarkResult Time<T>(
        Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = MeasurementEngine.MeasureSync(name,
            () => ResultSink.Consume(action()),
            options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    /// <summary>Measure an async action.</summary>
    public static async Task<BenchmarkResult> TimeAsync(
        Func<Task> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = await MeasurementEngine.MeasureAsync(name, action, options, cancellationToken: cancellationToken);
        return outcome.Result;
    }

    /// <summary>Measure an async function, consuming its return value to prevent DCE.</summary>
    public static async Task<BenchmarkResult> TimeAsync<T>(
        Func<Task<T>> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        var outcome = await MeasurementEngine.MeasureAsync(
            name,
            async () => ResultSink.Consume(await action()),
            options,
            cancellationToken: cancellationToken);
        return outcome.Result;
    }

    /// <summary>Measure a synchronous action, returning raw MeasurementOutcome (includes raw samples for custom analysis).</summary>
    public static MeasurementOutcome MeasureRaw(
        Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        return MeasurementEngine.MeasureSync(name, action, options, cancellationToken: cancellationToken);
    }

    [Obsolete("Use Benchmark.Run() for sync benchmarks. TimeAsync(Action) is retained for backward compatibility.")]
    public static Task<BenchmarkResult> TimeAsync(
        Action action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Time(action, options, name, cancellationToken));
    }

    // Legacy overload retained for backward compatibility.
    [Obsolete("Use Benchmark.Run<T>() for sync benchmarks with return values.")]
    public static Task<BenchmarkResult> TimeAsync<T>(
        Func<T> action,
        MeasurementOptions? options = null,
        string name = "Benchmark",
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Time(action, options, name, cancellationToken));
    }
}
```

---

## 8. Source Generator (Optional)

A Roslyn source generator can eliminate even the reflection-based discovery, generating
strongly-typed runner code at compile time. This is a nice-to-have - implement it after the
core library is stable.

The generator would look for classes decorated with `[BenchmarkClass]` and generate a
partial class with a static `RunAsync()` method:

```csharp
// User writes:
[BenchmarkClass]
public partial class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => string.Concat("hello", " ", "world");

    [Benchmark]
    public string Interpolate() => $"hello world";
}

// Generator emits:
public partial class StringBenchmarks
{
    public static async Task RunAsync(MeasurementOptions? options = null)
    {
        var instance = new StringBenchmarks();
        await new BenchmarkSuite(nameof(StringBenchmarks))
            .Add(nameof(instance.Concat),       () => instance.Concat())
            .Add(nameof(instance.Interpolate),  () => instance.Interpolate())
            .RunAsync();
    }
}

// Caller writes:
await StringBenchmarks.RunAsync();
```

Benefits: no reflection at runtime, NativeAOT-compatible, IDE navigation works.

---

## 9. Phase 2 - Web UI

The web UI is exposed as an optional package `NBenchmark.Web`. When added, it starts a Kestrel
server alongside the benchmark run.

### Usage

```csharp
var results = await BenchmarkHost.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithWebUI(port: 5050)   // enables http://localhost:5050
    .RunAsync();
```

### Architecture

```
NBenchmark.Web/
├── BenchmarkWebHost.cs           # Registers ASP.NET Core minimal API
├── Api/
│   ├── BenchmarkEndpoints.cs     # REST endpoints
│   └── ResultsStore.cs           # In-memory results store (+ optional SQLite)
└── wwwroot/                      # Pre-built SPA, embedded as resources
    ├── index.html
    ├── app.js
    └── styles.css
```

### API Endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/suites` | List all discovered benchmark suites |
| `POST` | `/api/suites/{suite}/run` | Trigger a suite run |
| `GET` | `/api/results` | List all historical results |
| `GET` | `/api/results/{id}` | Get a specific result |
| `GET` | `/api/results/stream` | SSE stream of live run progress |
| `DELETE` | `/api/results` | Clear history |

### Live Progress via Server-Sent Events

```csharp
app.MapGet("/api/results/stream", async (
    HttpContext context,
    ResultsStore store,
    CancellationToken ct) =>
{
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");

    await foreach (var result in store.WatchAsync(ct))
    {
        var json = JsonSerializer.Serialize(result);
        await context.Response.WriteAsync($"data: {json}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});
```

### Web UI Features

- **Dashboard** - live-updating list of results as benchmarks complete
- **Bar chart comparison** - visual comparison of suite results using Chart.js
- **Historical view** - browse past runs, compare runs side by side
- **Export** - download results as JSON, CSV, or Markdown from the browser
- **Filter** - search by benchmark name, date range, or result tier (fast/medium/slow)

---

## 10. Build Order & Milestones

### Priority Tiers

**P0 - Fix before any public release:**

- Remove `System.CommandLine` dependency (hand-roll parser)
- Fix async allocation tracking (`GC.GetTotalAllocatedBytes`)
- Remove `Console.WriteLine` from file-based reporters
- Fix `ResultSink` value-type overloads (`Volatile.Write`, `Consume(bool)` fix)
- Return raw samples from engine (via `MeasurementOutcome`) for significance testing
- Per-benchmark error isolation (try/catch, `Errored` + `ErrorMessage`)
- Baseline validation (throw if `.WithBaseline("Typo")` doesn't match any `.Add()`)
- **Path traversal fix** - `ValidateOutputPath` with trailing separator + `Ordinal` (not `OrdinalIgnoreCase`)
- **Reporter path validation** - enforce in constructor (not just CLI)
- **Console markup escaping** - escape all user-controlled strings in `ConsoleReporter` + `ConsoleBenchmarkProgress`
- **Sync hot path** - `BenchmarkSuite` branches to `MeasureSync` for sync benchmarks
- **Delegates** - build cached `Func<object, object?>` at discovery time
- **Significance** - host stores `rawSamples` dict (not a no-op)
- **Return type** - `BenchmarkHost.RunAsync` returns `IReadOnlyList<BenchmarkResult>`

**P0 - v5 fixes (review-driven):**

- **ConsoleReporter Description column** - populates `Description` cells in every `AddRow` when the conditional column is added
- **All-errored baseline guard** - `ConsoleReporter` and both `ComputeSignificance` methods guard against `successful.Count == 0`
- **Divide-by-zero in ratio** - `ConsoleReporter` checks `baseline.Median == 0` and shows `N/A` instead of `Infinity`
- **void-returning sync methods** - discoverer wraps in `Action<object>` returning `null` (cannot cast void to `Func<object, object?>`)
- **Per-iteration `Task<T>` reflection** - `ResultExtractor` cached at discovery time, used in measurement loop
- **Per-iteration setup/teardown reflection** - `IterationSetupDelegate`/`IterationTeardownDelegate` cached at discovery time on `BenchmarkMethodDefinition`; suite-level `SetupDelegate`/`TeardownDelegate` cached on `BenchmarkSuiteDefinition`
- **`OnSuiteStarting` / `OnWarmupCompleted` in Host** - both hooks now called from `BenchmarkHost.RunAsync`
- **Suite-level `SetupDelegate` failure** - wrapped in try/catch; marks all benchmarks in the suite as errored
- **Suite-level `TeardownDelegate` failure** - wrapped in try/catch; logs warning but does not crash
- **`MarkdownReporter` invalid constructor** - duplicate `{ _outputPath = ... }` block removed
- **`WarmupIterations` bounds** - `MeasurementOptions.WarmupIterations` now validates `[1, MaxWarmupIterations]`
- **`IqrFence` empty result** - falls back to the sorted array when the IQR filter removes everything
- **Two-pass variance** - `StatsSummary.Compute` no longer uses the unstable `(sumSq/n) - mean²` formula
- **`--threshold-pct` exit code race** - uses a `_thresholdRejected` flag set at the end of `RunAsync` after reporters finish, not `Environment.ExitCode` at parse time
- **Placeholder URL** - `https://github.com/anomalyco/benchly` replaces `https://github.com/you/benchly` in error message and `.csproj`

**P1 - Before v1.0:**

- Add `Ratio` column and `[Baseline]` attribute
- Discover `internal` types
- Add `[BenchmarkIterationTeardown]`
- Benchmark order randomisation (Fisher-Yates + seed)
- Significance testing (Mann-Whitney U, using raw pre-outlier samples)
- Significance annotations in reporters (context-sensitive: auto-on for 2+ benchmarks)
- Inter-benchmark GC (`ForceGcBetweenBenchmarks`, default true)
- `ConsoleReporter` opt-out (`WithoutConsoleReporter` / `WithoutConsoleOutput`)
- `CancellationToken` support in `Benchmark.RunAsync`
- `--list` / `--dry-run` / `--seed` CLI flags
- `--threshold-pct` CLI flag (rejected with "not yet implemented" to prevent silent CI failures)
- `Benchmark.Run(Action)` and `Benchmark.Run<T>(Func<T>)` synchronous Tier 1
- `Benchmark.MeasureRaw` for raw sample access
- `[Obsolete]` legacy `TimeAsync(Action)` overloads
- `int?` for `BenchmarkAttribute.Iterations` / `WarmupIterations`
- `Description` column in console reporter
- Suite-level setup/teardown via `.WithSuiteSetup` / `.WithSuiteTeardown`
- `OnSuiteStarting` / `OnWarmupCompleted` hook wiring
- Double-sort elimination in stats computation
- Centralized bounds on `MeasurementOptions`
- `Stopwatch.Frequency` printed at host startup
- JSON filename collision prevention (ms + counter)
- `WhenWritingNull` removed from `JsonReporter`
- CSV null fields emit `"null"`

**P2 - Post-v1:**

- Parameterised benchmarks (`[BenchmarkArguments]`)
- Source generator
- Glob improvements

**P3 - v5 polish (review-driven, deferred):**

- Split `MeasurementEngine` warmup/measurement into separate calls so `OnWarmupCompleted` can fire between phases
- Wire specialised `ResultSink.Consume(int/long/double/bool)` overloads via type-checks at registration time (avoid per-iteration boxing for sub-µs benchmarks)
- Add `?` and `**` glob support
- Multi-runtime / multi-framework targeting

### Milestone 1 - Core (Week 1–2)

- [ ] `NBenchmark.Core` project: `MeasurementEngine`, `ResultSink`, `StatsSummary`, `Percentile`, `MannWhitneyU`
- [ ] `BenchmarkResult` and `MeasurementOptions` models (including `RunOrder`, baseline/significance fields)
- [ ] Unit tests for stats, outlier removal, and Mann-Whitney U
- [ ] `Benchmark.RunAsync()` one-liner
- [ ] `ConsoleReporter` (basic, without Spectre.Console first)
- [ ] `BenchmarkFormatter` shared formatting class
- [ ] `IBenchmarkProgress` / `NullBenchmarkProgress` / `ConsoleBenchmarkProgress`

### Milestone 2 - Suite & Polish (Week 2–3)

- [ ] `BenchmarkSuite` fluent builder (with baseline, significance, run order)
- [ ] Spectre.Console reporter with bar chart, ratio column, significance annotations
- [ ] `MarkdownReporter`, `JsonReporter`, and `CsvReporter`
- [ ] Hand-rolled CLI argument parser
- [ ] Samples project with Tier 1 and Tier 2 examples
- [ ] README with quickstart

### Milestone 3 - Host & Discovery (Week 3–4)

- [ ] `[Benchmark]`, `[BenchmarkSetup]`, `[BenchmarkTeardown]`, `[BenchmarkIterationSetup]`, `[BenchmarkIterationTeardown]` attributes
- [ ] `[Baseline = true]` support on `[Benchmark]` attribute
- [ ] `BenchmarkDiscoverer` (reflection-based, discovers `internal` types)
- [ ] `BenchmarkHost` with CLI arg support (hand-rolled parser, randomisation)
- [ ] Wire up Host significance - store `MeasurementOutcome` alongside `BenchmarkResult`, call `ComputeSignificance` with raw samples
- [ ] `--output` directory wiring for file-based reporters
- [ ] Samples project with Tier 3 examples
- [ ] NuGet packaging and GitHub Actions CI

### Milestone 4 - Source Generator (Week 5–6)

- [ ] Roslyn incremental generator project
- [ ] `[BenchmarkClass]` attribute
- [ ] Generated `RunAsync()` method
- [ ] Tests using `Microsoft.CodeAnalysis.CSharp.Testing`

### Milestone 5 - Web UI (Week 7–10)

- [ ] `NBenchmark.Web` project with minimal API
- [ ] `ResultsStore` with in-memory ring buffer
- [ ] SSE endpoint for live progress
- [ ] SPA frontend (Svelte recommended - small bundle, no build step in dev)
- [ ] Bundle and embed SPA as resource
- [ ] Optional SQLite persistence for historical results

---

## 11. Name Ideas

| Name | Feel |
|---|---|
| **Benchly** | Friendly, approachable, NuGet-clean |
| **Swiftmark** | Speed-focused, compound word |
| **Chrono** | Precise, timing-centric (check NuGet availability) |
| **PerfKit** | Toolkit feel, descriptive |
| **Marksman** | Precise, a little fun |
| **Speedo** | Fast, punchy (may conflict with brand) |
| **Tempus** | Latin for time, clean and short |
| **Blaze** | Energetic, implies speed |

Check NuGet.org, GitHub, and the C# Foundation namespace list before committing to a name.
Prefer something that reads naturally in code: `Benchmark.RunAsync(...)` (using `Benchly` as
the package but exposing a short static class) is a nice pattern.

---

## Appendix A - `BenchmarkResult` Extension Methods

Convenience extensions for printing a single result inline.

```csharp
public static class BenchmarkResultExtensions
{
    /// <summary>Print a single result to the console. Prefer PrintAsync() in async contexts.</summary>
    public static BenchmarkResult Print(this BenchmarkResult result)
    {
        // Synchronous - uses a dedicated output path without async machinery.
        // Safe in console apps (no SynchronizationContext).
        // Note: this blocks the calling thread; use PrintAsync() in library/async contexts.
        Console.WriteLine();
        var r = result;
        Console.WriteLine($"  {r.Name}: {BenchmarkFormatter.FormatNs(r.Median)} median"
            + (r.MeanAllocatedBytes.HasValue ? $" ({BenchmarkFormatter.FormatBytes(r.MeanAllocatedBytes.Value)})" : ""));
        if (r.MeanAllocatedBytes.HasValue)
            Console.WriteLine($"    Alloc: {BenchmarkFormatter.FormatBytes(r.MeanAllocatedBytes.Value)}");
        Console.WriteLine($"    Mean: {BenchmarkFormatter.FormatNs(r.Mean)}, P95: {BenchmarkFormatter.FormatNs(r.P95)}");
        Console.WriteLine($"    StdDev: {BenchmarkFormatter.FormatNs(r.StandardDeviation)}");
        Console.WriteLine();
        return result;
    }

    /// <summary>Print a single result to the console (async-safe, preferred).</summary>
    public static async Task<BenchmarkResult> PrintAsync(this BenchmarkResult result)
    {
        var reporter = new ConsoleReporter();
        await reporter.ReportAsync([result]);
        return result;
    }

    public static async Task<BenchmarkResult> ToMarkdownAsync(this BenchmarkResult result, string path = "benchmark.md")
    {
        var reporter = new MarkdownReporter(path);
        await reporter.ReportAsync([result]);
        return result;
    }

    public static async Task<BenchmarkResult> ToJsonAsync(this BenchmarkResult result, string outputDir = ".")
    {
        var reporter = new JsonReporter(outputDir);
        await reporter.ReportAsync([result]);
        return result;
    }

    public static async Task<BenchmarkResult> ToCsvAsync(this BenchmarkResult result, string path = "benchmark.csv")
    {
        var reporter = new CsvReporter(path);
        await reporter.ReportAsync([result]);
        return result;
    }
}
```

## Appendix B - Testing the Engine

```csharp
// MeasurementEngineTests.cs
public class MeasurementEngineTests
{
    [Fact]
    public async Task Measures_Action_And_Returns_Positive_Timings()
    {
        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.Delay(1),
            new MeasurementOptions { WarmupIterations = 2, Iterations = 5 }
        );
        var result = outcome.Result;

        Assert.True(result.Median > 0);
        Assert.True(result.Mean > 0);
        Assert.Equal(5, result.MeasuredIterations);
    }

    [Fact]
    public async Task Measures_Allocations_When_Enabled()
    {
        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => { _ = new byte[1024]; return Task.CompletedTask; },
            new MeasurementOptions
            {
                WarmupIterations = 2,
                Iterations = 10,
                MeasureAllocations = true,
            }
        );
        var result = outcome.Result;

        Assert.NotNull(result.MeanAllocatedBytes);
        Assert.True(result.MeanAllocatedBytes >= 1024);
    }

    [Fact]
    public async Task Outlier_Removal_Reduces_Sample_Count()
    {
        var options = new MeasurementOptions
        {
            // WarmupIterations=0 is rejected by MeasurementOptions validation
            // (min is 1 - see the property doc comment for rationale). Use 1
            // here: the math is about measured iterations, not warmup.
            WarmupIterations = 1,
            Iterations = 100,
            OutlierMode = OutlierMode.RemoveTop5Percent,
        };

        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            options
        );
        var result = outcome.Result;

        // 5% of 100 = 5 removed → 95 measured
        Assert.Equal(95, result.MeasuredIterations);
    }

    [Fact]
    public async Task Raw_Samples_Are_Preserved_Pre_Outlier_Removal()
    {
        var options = new MeasurementOptions
        {
            // WarmupIterations=0 is rejected by MeasurementOptions validation
            // (min is 1). Use 1: the math is about measured iterations, not warmup.
            WarmupIterations = 1,
            Iterations = 100,
            OutlierMode = OutlierMode.RemoveTop5Percent,
        };

        var outcome = await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            options
        );

        // Raw samples should have all 100, even though measured count is trimmed
        Assert.Equal(100, outcome.RawSamples.Length);
        Assert.Equal(95, outcome.Result.MeasuredIterations);
    }

    [Fact]
    public async Task Iteration_Setup_Runs_Each_Iteration()
    {
        var callCount = 0;
        Action setup = () => Interlocked.Increment(ref callCount);

        await MeasurementEngine.MeasureAsync(
            "test",
            () => Task.CompletedTask,
            new MeasurementOptions { WarmupIterations = 3, Iterations = 10 },
            iterationSetup: setup
        );

        // Setup runs during warmup (3) + measured iterations (10) = 13 times
        Assert.Equal(13, callCount);
    }
}
```

## Appendix C - Mann-Whitney U Tests

```csharp
// MannWhitneyUTests.cs
public class MannWhitneyUTests
{
    [Fact]
    public void Identical_Samples_Return_PValue_One()
    {
        var a = new double[] { 10, 20, 30, 40, 50 };
        var b = new double[] { 10, 20, 30, 40, 50 };

        var p = MannWhitneyU.Test(a, b);

        Assert.Equal(1.0, p, 3);
    }

    [Fact]
    public void Clearly_Different_Samples_Return_Small_PValue()
    {
        var a = new double[] { 10, 12, 11, 13, 10 };
        var b = new double[] { 100, 102, 101, 103, 100 };

        var p = MannWhitneyU.Test(a, b);

        // Two completely separated groups of 5 each → p ≈ 0.008
        Assert.True(p < 0.05);
    }

    [Fact]
    public void Slightly_Different_Samples_Return_Large_PValue()
    {
        var rng = new Random(42);
        var a = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var b = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();

        var p = MannWhitneyU.Test(a, b);

        // Overlapping distributions from same range → p likely > 0.05
        Assert.True(p > 0.05);
    }

    [Fact]
    public void Single_Element_Samples()
    {
        var a = new double[] { 10 };
        var b = new double[] { 20 };

        var p = MannWhitneyU.Test(a, b);

        Assert.True(p >= 0 && p <= 1.0);
    }

    [Fact]
    public void Empty_Sample_Returns_One()
    {
        var p = MannWhitneyU.Test(Array.Empty<double>(), new double[] { 1, 2, 3 });
        Assert.Equal(1.0, p);
    }

    [Fact]
    public void Tied_Values_Handled_Correctly()
    {
        var a = new double[] { 10, 10, 10, 20, 20 };
        var b = new double[] { 30, 30, 30, 40, 40 };

        var p = MannWhitneyU.Test(a, b);

        Assert.True(p < 0.05);
    }
}
```

## Appendix D - NuGet Package Configuration

```xml
<!-- NBenchmark/NBenchmark.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>

    <!-- Package metadata -->
    <PackageId>Benchly</PackageId>
    <Version>0.1.0</Version>
    <Authors>Your Name</Authors>
    <Description>A developer-friendly benchmarking library for modern .NET.</Description>
    <PackageTags>benchmark;performance;profiling;dotnet</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/anomalyco/benchly</RepositoryUrl>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Spectre.Console" Version="0.49.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\NBenchmark.Core\NBenchmark.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

> **Note:** The `System.CommandLine` dependency has been removed. CLI argument parsing is
> hand-rolled (~50 lines).

---

## Appendix E - Security Considerations

The primary security boundary in this library is the benchmark code itself. Since the developer
writes the benchmarks, most surface area is trusted. The following items are documented for
completeness.

### Reflection Trust Boundary

`BenchmarkHost.AddFromAssembly<T>()` discovers types via reflection and invokes arbitrary
methods (including `internal` and `private` methods decorated with `[Benchmark]`). Any
assembly passed to this method must be trusted, as its types are instantiated and methods
are invoked with full privilege. This is equivalent to executing arbitrary code.

### Path Validation

All file-based reporters (`MarkdownReporter`, `JsonReporter`, `CsvReporter`) validate that
the provided output path resolves to a location under the current working directory. Paths
outside this boundary (e.g., `../../etc/`, `/tmp/`, absolute paths outside CWD) are rejected
with an `ArgumentException`. The `--output <dir>` CLI flag applies the same validation.

```csharp
internal static string ValidateOutputPath(string path)
{
    var fullPath = Path.GetFullPath(path);
    var baseDir  = Path.GetFullPath(Directory.GetCurrentDirectory());
    // Append trailing separator to prevent prefix-confusion:
    // "/work/project-evil/x.csv" must NOT match "/work/project/".
    var withSep = baseDir.EndsWith(Path.DirectorySeparatorChar)
        ? baseDir : baseDir + Path.DirectorySeparatorChar;
    // Use Ordinal (not OrdinalIgnoreCase) - case-sensitive file systems (Linux)
    // can have distinct directories that differ only by case.
    if (fullPath != baseDir && !fullPath.StartsWith(withSep, StringComparison.Ordinal))
        throw new ArgumentException(
            $"Output path must be under the current working directory ({baseDir}). " +
            $"Received: {path}");
    return fullPath;
}
```

### CLI Input Validation

The `--iterations` and `--warmup` CLI flags use `int.TryParse` with bounds (`1`–`100,000`
for iterations, `1`–`10,000` for warmup). Invalid values produce a clear error message
rather than an unhandled `FormatException` / `OverflowException` stack trace.

### CSV Escaping

Benchmark names in CSV output are properly quoted and escaped (doubled quotes) to prevent
CSV injection if names contain commas, quotes, or newlines:

```csharp
var safeName = result.Name.Replace("\"", "\"\"");
sb.AppendLine($"\"{safeName}\",{result.Median:F1},...");
```

### Spectre.Console Markup Escaping

User-provided strings (benchmark names, error messages) are escaped before being embedded
in Spectre.Console markup strings. Spectre.Console uses `[...]` for markup; the escape
sequence is `[[` and `]]` (double brackets):

```csharp
private static string EscapeMarkup(string? text) =>
    text?.Replace("[", "[[").Replace("]", "]]") ?? "";
```

### Web UI (Phase 2)

The `NBenchmark.Web` package's embedded web server MUST bind to `localhost` only by default.
This is a hard default enforced in the Kestrel listener binding:

```csharp
// BenchmarkWebHost.cs
builder.WebHost.UseUrls("http://localhost:5050");
```

An opt-in `WithWebUI(port, bindAllInterfaces: true)` overload is available for the rare
case of remote access (e.g., CI server), but this is documented as a security-conscious
choice.

**CSRF Note:** Even on localhost, any browser tab visiting a malicious site can POST to
`http://localhost:5050/api/suites/foo/run`. While the impact is limited (CPU exhaustion),
a Phase 2 mitigation will require an `Origin` / `Sec-Fetch-Site: same-origin` check on the
run endpoint.

**SSE Auth:** `GET /api/results/stream` streams benchmark output to any local origin.
Multi-user dev boxes / shared workstations should be aware of this. Phase 2 will add an
optional session token printed to the launching terminal.

**ResultsStore Bound:** The in-memory ring buffer is bounded to the last 100 runs by default.
The optional SQLite backend has no built-in bound; this is documented for users.

### Resource Limits

The `MeasurementOptions` class exposes public constants (`MinIterations`, `MaxIterations`,
`MaxWarmupIterations`) that are the single source of truth for bounds. Both the property
setters and the CLI parser reference these constants.
`MeasurementOptions.Iterations` is capped at `MaxIterations` (100,000) and `WarmupIterations`
at `MaxWarmupIterations` (10,000). This prevents allocation of massive arrays and multi-hour
accidental benchmark runs. The bounds are enforced at the property level:

```csharp
public int Iterations
{
    get => _iterations;
    init => _iterations = value is >= 1 and <= MaxIterations
        ? value
        : throw new ArgumentOutOfRangeException(nameof(value), value,
            $"Iterations must be between {MinIterations} and {MaxIterations}");
}
```

### Glob Pattern Limitations

The CLI `--filter` flag implements a minimal glob: `*` matches any sequence of characters.
Anchoring is supported (pattern without leading `*` anchors at start; without trailing `*`
anchors at end). The following are NOT supported: `?` (single character), character classes
(`[abc]`), and `**` (directory traversal). Matching is case-insensitive using
`OrdinalIgnoreCase`.

### Dry-run Default Safe Value

`--dry-run` runs each benchmark exactly once with no warmup and no measurement, so it is
inherently safe against resource exhaustion regardless of CLI arguments.

### `--threshold-pct` Design Note

The `--threshold-pct` flag is planned for v1.0 (P1) but is currently **rejected** -
not silently accepted. When implemented, it will compare each non-baseline benchmark
against the baseline and exit with code 1 if the regression exceeds the threshold.
Until then, the parser writes a clear "not yet implemented" error to `stderr` and
sets `Environment.ExitCode = 1` (via the `_thresholdRejected` flag, applied at the
end of `RunAsync` so a subsequent reporter error cannot clobber it).

The deliberate choice to **reject** rather than silently accept is to prevent
silent CI failures: a script that includes `--threshold-pct` and currently passes
would otherwise pass for the wrong reason once the flag is implemented. Users with
the flag in their scripts will see the error and remove it; users without it are
unaffected.
