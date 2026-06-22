---
title: "Isolated Runs (Advanced)"
description: Run benchmarks in clean child processes to avoid runtime cross-contamination.
order: 4
---

# Isolated Runs (Advanced)

Process isolation runs benchmarks in a freshly spawned child process so their measurements are not biased by runtime state - JIT warmup, heap and GC pressure, or thread-pool and process-level state - left behind by earlier work in the same process.

How you reach for isolation depends on the mode:

| Mode | Isolation | Granularity |
| --- | --- | --- |
| **Quick** (`Benchmark.Run` / `RunAsync`) | Not available - always in-process | - |
| **Suite** (`BenchmarkSuite`) | Opt in with `WithIsolation()` | The whole suite runs in one child process |
| **Host** (`BenchmarkHost`) | **On by default** | Per class, with per-benchmark and opt-out controls |

Isolation is useful when you want to reduce contamination from:

- prior JIT warmup from earlier benchmarks
- heap and GC pressure left by unrelated work
- thread-pool and process-level runtime state

## Quick mode

Quick mode is intentionally simple and always runs **in-process** - there is no isolated variant. When you need a clean process, use Suite or Host mode.

```csharp
using NBenchmark;

// Always in-process - fast and simple.
var result = Benchmark.Run(() => ColdStartSensitivePath(), name: "cold path");
```

## Suite mode

Call `WithIsolation()` on a `BenchmarkSuite` to run the **entire suite** inside a single clean child process. All benchmarks in the suite are measured there, so they compare against each other in the same fresh CLR:

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("isolated-comparison")
    .Add("baseline", () => Baseline())
    .Add("candidate", () => Candidate())
    .WithBaseline("baseline")
    .WithReporter(new ConsoleReporter())
    .WithIsolation()        // run the whole suite in one child process
    .RunAsync();
```

`WithIsolation(false)` is the default and keeps the suite in-process. Suite setup and teardown (`WithSuiteSetup` / `WithSuiteTeardown`) run inside the child, and your custom `IOutlierDetector` / `ISignificanceTest` are preserved because the child rebuilds the suite from your own `Main` rather than deserializing options.

## Host mode

Host mode is **isolated by default**: each benchmark class runs in its own clean child process. You usually don't configure anything - `BenchmarkHost.Create(args)...RunAsync()` already isolates per class.

```csharp
using NBenchmark.Attributes;

public sealed class StartupBenchmarks
{
    [Benchmark]
    public int ColdPath() => RunColdSensitiveWork();   // isolated per class by default
}
```

You can tune the granularity:

- **`[IsolatedProcess]`** on a method (finest granularity) gives that one benchmark its own dedicated child process, isolated even from siblings in the same class.
- **`[InProcess]`** on a method (or class) opts that benchmark back into the host process.
- **`--in-process`** on the command line, or **`WithIsolation(false)`** in code, disables isolation for the whole run.

```csharp
public sealed class MixedBenchmarks
{
    [Benchmark]
    public int Default() => Work();              // shares one per-class child

    [Benchmark]
    [IsolatedProcess]
    public int OwnProcess() => ColdWork();        // its own dedicated child

    [Benchmark]
    [InProcess]
    public int InHost() => HostObservableWork();  // runs in the host process
}
```

When isolation resolves to a mix, NBenchmark runs the in-process benchmarks in the host, the per-class benchmarks together in one child, and each `[IsolatedProcess]` benchmark in its own child. The host re-runs the same entry assembly for each child, executes only the requested benchmarks, and reads their results back through a temporary file (never stdout, so the child's own console output cannot corrupt the data).

See [Host mode](../usage-modes/host-mode.md#isolatedprocess) for the full attribute reference.

## Important behavior notes

- Isolation adds overhead: one process launch per child. For ordinary microbenchmarks the in-process path is faster and accurate enough.
- Isolated children always run in **declaration** order; run-order randomization applies only to in-process runs.
- `--dry-run` (equivalent to `--iterations 0 --warmup 0`) always runs in-process - no child is spawned.
- Children rebuild their measurement configuration by re-running your `Main`, so custom detectors and significance tests are preserved. Host mode additionally forwards scalar CLI overrides (iterations, warmup, confidence, and so on) to each child.

## Related

- See [Host mode](../usage-modes/host-mode.md#isolatedprocess) for `[IsolatedProcess]` and `[InProcess]` on attribute-discovered benchmarks.
- See [Suite mode](../usage-modes/suite-mode.md) for the full `BenchmarkSuite` fluent API.
- See [Samples](../samples.md) for a runnable isolated-runs sample project.
