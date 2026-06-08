# .NET Benchmarking Library - Design Document v6

> A developer-first benchmarking library for modern .NET. Simpler than BenchmarkDotNet,
> powerful enough for real work.

---

## Changes from v5

### Architecture: Two-Project → Core + Add-On

v5 used a two-project split: `NBenchmark.Core` (zero deps) + `NBenchmark` (depends on Spectre.Console).

v6 simplifies this to a **single zero-dep core** plus an **opt-in Spectre.Console add-on**:

| v5 | v6 |
|---|---|
| `NBenchmark.Core` (zero deps) + `NBenchmark` (Spectre.Console) | `NBenchmark` (zero deps) + `NBenchmark.Console` (Spectre.Console) |
| `ConsoleReporter` and `ConsoleBenchmarkProgress` live in `NBenchmark` | Moved to `NBenchmark.Console` add-on |
| Default reporter list includes `ConsoleReporter` | Default reporter list is **empty** - user opts in |
| `NBenchmark` has Spectre.Console as a hard dependency | `NBenchmark` has **zero NuGet dependencies** |

### Key Decisions

1. **Core `NBenchmark` package is zero-dep.** Only BCL APIs. `System.Text.Json` is BCL since .NET 5 and doesn't require a NuGet reference.

2. **As much functionality as possible lives in core.** The only thing that moves to `NBenchmark.Console` is code that requires `Spectre.Console`: `ConsoleReporter`, `ConsoleBenchmarkProgress`, and `PrintAsync`.

3. **File-based reporters (JSON, Markdown, CSV) + `PathValidation` + `IReporter` stay in core.** They use only BCL APIs.

4. **Tier 1 (`Bench`), Tier 2 (`BenchmarkSuite`), Tier 3 (`BenchmarkHost`), Attributes, and Discovery stay in core.**

5. **Default reporter list in core is empty.** Users who want console output add `NBenchmark.Console` and wire `new ConsoleReporter()` explicitly.

6. **`WithoutConsoleReporter()` and `WithoutConsoleOutput()` removed.** With an empty default list, there's nothing to remove.

7. **`PrintAsync` moved to `NBenchmark.Console`** (as `ConsoleBenchmarkResultExtensions.PrintAsync`). Core `Print()` method stays (uses `System.Console.WriteLine`).

---

## Project Architecture

```
NBenchmark/
├── src/
│   ├── NBenchmark/                        # Zero-dep core
│   │   ├── Benchmark.cs                       # Tier 1: one-liner entry point
│   │   ├── BenchmarkSuite.cs               # Tier 2: fluent builder
│   │   ├── BenchmarkHost.cs               # Tier 3: host + discovery + CLI
│   │   ├── Engine/
│   │   │   ├── MeasurementEngine.cs
│   │   │   ├── WarmupStrategy.cs
│   │   │   ├── GcControl.cs
│   │   │   └── ResultSink.cs
│   │   ├── Stats/
│   │   │   ├── StatsSummary.cs
│   │   │   ├── Percentile.cs
│   │   │   └── MannWhitneyU.cs
│   │   ├── Models/
│   │   │   ├── BenchmarkResult.cs
│   │   │   ├── MeasurementOutcome.cs
│   │   │   ├── MeasurementOptions.cs
│   │   │   ├── OutlierMode.cs
│   │   │   ├── RunOrder.cs
│   │   │   ├── IBenchmarkProgress.cs      # interface + NullBenchmarkProgress
│   │   │   └── BenchmarkFormatter.cs
│   │   ├── Reporters/
│   │   │   ├── IReporter.cs
│   │   │   ├── JsonReporter.cs             # BCL only (System.Text.Json)
│   │   │   ├── MarkdownReporter.cs         # BCL only
│   │   │   ├── CsvReporter.cs              # BCL only
│   │   │   └── PathValidation.cs           # BCL only
│   │   ├── Discovery/
│   │   │   ├── BenchmarkDiscoverer.cs
│   │   │   └── BenchmarkDefinition.cs
│   │   ├── Attributes/
│   │   │   ├── BenchmarkAttribute.cs
│   │   │   ├── BenchmarkSetupAttribute.cs
│   │   │   ├── BenchmarkTeardownAttribute.cs
│   │   │   ├── BenchmarkIterationSetupAttribute.cs
│   │   │   ├── BenchmarkIterationTeardownAttribute.cs
│   │   │   └── BenchmarkArgumentsAttribute.cs
│   │   └── Extensions/
│   │       └── BenchmarkResultExtensions.cs # Print(), ToMarkdownAsync(), ToJsonAsync(), ToCsvAsync()
│   │
│   └── NBenchmark.Console/                 # Spectre.Console add-on
│       ├── NBenchmark.Console.csproj       # depends on Spectre.Console + NBenchmark
│       ├── ConsoleReporter.cs              # Rich terminal table + bar chart
│       ├── ConsoleBenchmarkProgress.cs     # Live progress lines
│       └── ConsoleBenchmarkResultExtensions.cs  # PrintAsync()
│
├── samples/
│   ├── Quick/                              # Tier 1: Benchmark.Run()
│   ├── Suite/                              # Tier 2: BenchmarkSuite
│   └── Host/                               # Tier 3: BenchmarkHost
│
└── tests/
    ├── NBenchmark.Tests/                   # Core tests (43 tests)
    └── NBenchmark.Console.Tests/           # Console add-on tests (3 tests)
```

### Package Dependencies

| Package | Used In | Purpose |
|---|---|---|
| (none) | `NBenchmark` | **Zero NuGet dependencies** - BCL only |
| `Spectre.Console` | `NBenchmark.Console` | Rich terminal output |

`NBenchmark` is embeddable anywhere, including NativeAOT projects. The console add-on is opt-in.

### Namespaces

| Namespace | Project |
|---|---|
| `NBenchmark` | Core (Bench, BenchmarkResult, MeasurementOptions, etc.) |
| `NBenchmark.Engine` | Core (MeasurementEngine, ResultSink) |
| `NBenchmark.Stats` | Core (StatsSummary, Percentile, MannWhitneyU) |
| `NBenchmark.Reporters` | Core (IReporter, JsonReporter, MarkdownReporter, CsvReporter) |
| `NBenchmark.Discovery` | Core (BenchmarkDiscoverer) |
| `NBenchmark.Attributes` | Core (BenchmarkAttribute, etc.) |
| `NBenchmark.Console` | Add-on (ConsoleReporter, ConsoleBenchmarkProgress) |

---

## API Design

### Tier 1 - One-Liner (core)

```csharp
using NBenchmark;
using NBenchmark.Console;  // add-on for PrintAsync

// Sync
var result = Benchmark.Run(() => MyMethod());
result.Print();                    // core - plain Console.WriteLine

// Async
var result = await Benchmark.RunAsync(async () => await FetchAsync());
await result.PrintAsync();         // add-on - Spectre.Console table
```

### Tier 2 - Fluent Suite (core)

```csharp
using NBenchmark;
using NBenchmark.Console;  // add-on for ConsoleReporter/Progress
using NBenchmark.Stats;

var results = await new BenchmarkSuite("String Formatting")
    .Add("Concat",        () => string.Concat("hello", " ", "world"))
    .Add("Interpolate",   () => $"hello world")
    .WithBaseline("Concat")
    .WithWarmup(50)
    .WithIterations(500)
    .WithOutlierMode(OutlierMode.RemoveTop5Percent)
    .WithReporter(new ConsoleReporter())          // add-on
    .WithProgress(new ConsoleBenchmarkProgress(500, 50))  // add-on
    .RunAsync();
```

### Tier 3 - Benchmark Host (core)

```csharp
using NBenchmark;
using NBenchmark.Console;  // add-on

return await BenchmarkHost.Create(args)
    .AddFromAssembly<MyBenchmarks>()
    .WithReporter(new ConsoleReporter())          // add-on
    .WithProgress(new ConsoleBenchmarkProgress(200, 25))  // add-on
    .RunAsync();
```

### Default Behavior (No Add-On)

Without `NBenchmark.Console`, the core runs silently by default:

- `BenchmarkSuite.RunAsync()` - no console output, returns results
- `BenchmarkHost.RunAsync()` - prints timer resolution to plain `Console.WriteLine`, discovers and runs benchmarks, results available programmatically
- `result.Print()` - plain `Console.WriteLine` summary
- `--reporter console` - prints a hint about adding the `NBenchmark.Console` package

Users who want rich console output add the `NBenchmark.Console` package and wire the reporter/progress explicitly.

---

## CLI Arguments

```
myapp.exe                          # run all discovered benchmarks
myapp.exe --filter String*         # run suites matching glob
myapp.exe --iterations 1000        # override iterations
myapp.exe --warmup 100             # override warmup
myapp.exe --reporter json          # file reporters available in core
myapp.exe --reporter console       # hint: requires NBenchmark.Console package
myapp.exe --output ./results       # set output directory
myapp.exe --list                   # list discovered benchmarks
myapp.exe --dry-run                # invoke each once, skip measurement
myapp.exe --order declaration      # preserve declaration order
myapp.exe --threshold-pct 5        # [NOT YET IMPLEMENTED]
myapp.exe --help | -h              # show help text
```

---

## Design Principles (unchanged from v5)

- **One line to start.** The simplest useful case must be a single expression.
- **No out-of-process execution by default.** Run in the same process.
- **Async-native.** `async`/`await` is a first-class citizen.
- **Modern .NET only.** Target net10.0+. No .NET Framework baggage.
- **Sensible defaults.** Warmup, GC, outlier trimming, allocation tracking, significance testing.
- **Layered complexity.** Simple things are simple; complicated things are possible.
- **Zero-dep core.** The `NBenchmark` package has no NuGet dependencies.
- **Comparison-first design.** When you benchmark more than one thing, output tells you which is faster and whether the difference is real.
