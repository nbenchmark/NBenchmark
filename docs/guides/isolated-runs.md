---
title: "Isolated Runs (Advanced)"
description: Run Quick, Suite, and Host benchmarks in clean child processes to avoid runtime cross-contamination.
order: 6
---

# Isolated Runs (Advanced)

NBenchmark normally runs benchmarks in-process for speed and DX. For advanced scenarios where runtime state can bias measurements, use isolated runs.

Isolation is available in all three usage modes:

- Quick mode via `Benchmark.RunIsolated*`
- Suite mode via `BenchmarkSuite.RunIsolated*`
- Host mode via `[IsolatedProcess]`

Isolation is useful when you want to reduce contamination from:

- prior JIT warmup from earlier benchmarks
- heap and GC pressure left by unrelated work
- thread-pool and process-level runtime state

## Quick mode isolation

Use `RunIsolated` and `RunIsolatedAsync` when you want a single benchmark in a fresh child process:

```csharp
using NBenchmark;

var result = Benchmark.RunIsolated(
    () => ColdStartSensitivePath(),
    name: "cold path");

var asyncResult = await Benchmark.RunIsolatedAsync(
    async () => await LoadDataAsync(),
    name: "cold async path");
```

## Suite mode isolation

Use `RunIsolatedAsync` (or `RunIsolated`) on `BenchmarkSuite` to run each benchmark in its own child process:

```csharp
using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("isolated-comparison")
    .Add("baseline", () => Baseline())
    .Add("candidate", () => Candidate())
    .WithBaseline("baseline")
    .WithReporter(new ConsoleReporter())
    .RunIsolatedAsync();
```

## Host mode isolation

In Host mode, apply `[IsolatedProcess]` to a benchmark method (or to the benchmark class) to run discovered benchmarks in child processes:

```csharp
using NBenchmark.Attributes;

public sealed class StartupBenchmarks
{
    [Benchmark]
    [IsolatedProcess]
    public int ColdPath() => RunColdSensitiveWork();
}
```

This works with normal host execution (`BenchmarkHost.Create(args)...RunAsync()`) and all regular CLI options.

## Important behavior notes

- Isolation adds overhead: one process launch per benchmark.
- In isolated suite runs, `WithSuiteSetup` and `WithSuiteTeardown` execute inside each benchmark child process.
- Quick and Suite isolated APIs replay the benchmark callsite in the child process and return a serialized result payload to the parent.
- Quick and Suite replay matching uses caller metadata plus isolated-call invocation order; reordering isolated calls in startup paths can change replay targeting.
- If multiple `RunIsolated*` callsites run during the same child startup path, only the requested invocation is the isolated target; non-target callsites run in-process in that child CLR.
- Host mode isolation (`[IsolatedProcess]`) re-runs the host entry assembly in a child and executes only the targeted discovered benchmark.
- For ordinary microbenchmarks, in-process mode is usually faster and sufficient.

## Related

- See [Host mode](./host-mode.md) for `[IsolatedProcess]` on attribute-discovered benchmarks.
- See [Samples](../samples.md) for a runnable isolated-runs sample project.
