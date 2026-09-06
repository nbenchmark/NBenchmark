---
title: "Isolated Runs"
description: Run benchmarks in clean worker processes so earlier work can't bias the measurement.
order: 1
---

# Isolated runs

NBenchmark measures your benchmarks in a **freshly spawned worker process** rather than in the process that launched them. This is **enabled by default in every mode** and requires no configuration.

Isolation is critical because the factors that most affect a measurement - such as JIT tiering, GC flavor, and thread-pool state - are fixed when a process starts. To choose these settings, the engine must start a process that hasn't begun yet. A benchmark measured in the existing process inherits whatever that process was already doing.

| Mode | Granularity |
| --- | --- |
| **Single** (`Benchmark.Run` / `RunAsync`) | One worker per call |
| **Suite** (`BenchmarkSuite`) | One worker for the whole suite |
| **Harness** (`BenchmarkHarness`) | One worker per class, with per-benchmark overrides |

## Why isolation matters

Measuring the same body both ways in the same program demonstrates the difference:

| Round | Isolated | In-process | Ratio |
| --- | --- | --- | --- |
| 0 | 329 ns | 7,009 ns | 21.3x |
| 1 | 320 ns | 6,733 ns | 21.0x |
| 2 | 322 ns | 329 ns | 1.0x |

The in-process column isn't just noisy; it's wrong by a factor of 21 until the JIT promotes the body on the third attempt. The reported confidence interval does not indicate this issue. In contrast, the isolated column provides the same number every time.

Because of this, **`--in-process` is not suitable for comparative measurements**. In-process runs of identical-cost benchmarks can vary by 3x across runs while each reports a tight confidence interval.

## Using isolation

Isolation is enabled by default. You can change the granularity or opt specific benchmarks out.

### Single mode

```csharp
// Measured in a worker. Nothing to configure.
var result = Benchmark.Run(() => Fibonacci(20), name: "fib");
```

If your benchmark requires data to be built first, name the preparation separately. The worker builds it in the measuring process:

```csharp
var result = Benchmark.Run(
    prepare: () => BuildData(),      // Runs once, before warmup, in the worker
    body:    d => Sort(d));
```

Use `prepare:` when the state is large, live (such as a `Stream`, `DbConnection`, or warmed cache), or otherwise cannot be copied. This is more faithful regardless of size because the value is built by the same code in the same process rather than being reconstructed.

`Benchmark.Warmup()` optionally starts a worker in the background so your first measured call doesn't pay the launch cost (roughly 70 ms).

### Suite mode

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort())
    .Add("array", () => ArraySort())
    .WithBaseline("bubble")
    .RunAsync();
```

The suite-mode equivalent of `prepare:` is `BenchmarkSuite.Over`, which also types each body's parameter:

```csharp
await BenchmarkSuite.Over("sorting", () => BuildData())
    .Add("array", d => Array.Sort(d))
    .Add("linq",  d => d.OrderBy(x => x).ToArray())
    .RunAsync();
```

`prepare` runs **once per benchmark**, before that benchmark's warmup, not once per suite. If two sorts shared one array, the second would measure what the first already sorted. Where the body mutates its state, reset it each sample using the `setup:` argument on `Add`, which runs outside the timed window.

### Harness mode

Each class gets its own worker by default. Two attributes change this behavior:

```csharp
public sealed class MixedBenchmarks
{
    [Benchmark]
    public int Default() => Work();               // Shares one per-class worker

    [Benchmark]
    [Isolation(Isolation.Required)]
    public int OwnProcess() => ColdWork();        // Uses its own dedicated worker

    [Benchmark]
    [Isolation(Isolation.Off)]
    public int InHost() => HostObservableWork();  // Runs in the host process
}
```

A method-level attribute overrides a class-level attribute. Applying both to the same member is an error.

## Why suites use one worker

All benchmarks in a suite share a single worker, making every ratio a **paired** comparison. The worker's CPU frequency, thermal state, and memory layout cancel out of the ratio rather than adding to it.

Measuring each benchmark in its own process might seem safer, but for four benchmarks of provably identical cost:

| Configuration | Spread across runs | Largest fabricated difference |
| --- | --- | --- |
| in-process | 3.27x | 2.80x |
| isolated per worker, host runtime configuration | 3.10x | 3.06x |
| **isolated, `steady-state` runtime configuration** | **1.03x** | **1.03x** |

Sibling contamination is rarely the dominant error; uncontrolled JIT tiering is the primary issue. Because JIT tiering is a per-process setting, it remains identical whether one worker runs one benchmark or five. Splitting them would turn every ratio into a between-process contrast without gaining accuracy.

The engine handles order effects by randomizing run order (`WithRunOrder(RunOrder.Random)`, reproducible via `WithSeed`). Additionally, `WithLaunchCount(n)` measures the suite in *n* separate workers to estimate run-to-run reproducibility.

If a benchmark genuinely pollutes its siblings - for example, by permanently filling a static cache - place it in its own suite or use `[Isolation(Isolation.Required)]`.

## Measuring in the host process

Sometimes the current process is the subject, such as for cold-start costs, first-call costs, or bodies that must observe host state (like a warm cache or open connection). Every route to this behavior is legal:

| Mode | How to request |
| --- | --- |
| Single | `Benchmark.RunInProcess(...)` and its async / prepared-state overloads |
| Suite | `AddInProcess(...)` for one benchmark, `WithIsolation(Isolation.Off)` for the whole suite |
| Harness | `[Isolation(Isolation.Off)]` on a method or class, `--in-process`, `WithIsolation(Isolation.Off)` |
| Any | `--dry-run`, which never invokes a body and so never spawns a worker |

```csharp
// One benchmark holds a live handle; the rest of the suite is still measured in a worker.
await new BenchmarkSuite("cache")
    .Add("cold", () => Parse(Payload))
    .AddInProcess("warm", () => connection.Query())
    .RunAsync();
```

`AddInProcess` exists because `WithIsolation(Isolation.Off)` is all-or-nothing. Without it, a single un-isolatable body would force every other benchmark in the suite into the host process.

These rows are labeled in the `Iso` column and are never given a ratio against an isolated row, as the configuration difference between two processes does not vanish just because it was requested.

## When isolation is refused

**A refusal is an error.** `Isolation` defaults to `Isolation.Required`, so a benchmark that asks for a worker but cannot have one **fails the run** rather than being quietly measured in the host process.

Reconstructing state doesn't always fail loudly; it can return plausible but wrong numbers. For example, a body over a captured `5` might measure as if it were `1` with no error and a tight confidence interval. NBenchmark declines to measure rather than guessing.

Most data crosses to the worker automatically: ordinary data (such as `int`, `string`, `int[]`, `List<T>`, or a record of those) is sent by value. Lambdas capturing `this`, capturing lifecycle delegates, and containers built by a factory also isolate correctly. The following items cannot cross:

| Item | Remedy |
| --- | --- |
| **Live objects** (e.g., `Stream`, `DbConnection`, `HttpClient`, mock, or built `IServiceProvider`) | Build it in a `prepare:` delegate, or pass a factory instead of the object |
| **Values with behavior not carried by contents** (e.g., a dictionary with a custom comparer) | Use a `prepare:` delegate, or mark your own type with `[BenchmarkState]` |
| **Captures larger than 8 MiB** (`MaxTransferredStateBytes`) | Use a `prepare:` delegate |
| **Shared objects** (two benchmarks sharing one object through different closures) | Build the shared state in one shared `prepare:` delegate |
| **Suites requiring user-code construction** | Use a static `[BenchmarkPlan]` factory with `RunPlanAsync` |
| **Assemblies with no file on disk** (single-file, in-memory, or dynamically emitted) | Build to disk |

The engine names every offending benchmark in a suite at once so you can fix all of them before re-running.

The refusal is a `BenchmarkIsolationException`, which carries the `IsolationStatus` and the remedy as properties as well as in the message - so a test adapter or a CI reporter can act on it without parsing text. See [Exceptions](../reference/exceptions.md).

To accept labeled host-process measurements - which is reasonable for scratchpad use - turn the requirement off:

```csharp
Benchmark.Run(body, new MeasurementOptions { Isolation = Isolation.Preferred });
new BenchmarkSuite("s").WithIsolation(Isolation.Preferred);
BenchmarkHarness.Create(args).WithIsolation(Isolation.Preferred);
```

For more information, see [Isolation internals: captured-state transfer](../deep-dives/isolation-internals.md#captured-state-transfer), which covers the complete set of types that cross, the `[BenchmarkState]` contract, and the `[BenchmarkPlan]` factory rules.

## Verifying isolation

Two flags make the isolation claim verifiable:

- **`--strict-isolation`** audits the results and names every refused benchmark and its remedy. Use this in pipelines that gate on benchmark numbers. It keys on *refusal*, so a deliberate `--in-process` or `--dry-run` run passes.
- **`--verify-isolation`** measures everything a second time in the host process and prints the per-benchmark difference. This allows you to see what the numbers would have been without isolation. This pass does not change the build's outcome.

For more information, see the [CLI reference](../reference/cli.md#isolation).

## Additional details

- **Isolation adds about 70 ms per worker launch.** Compared to the per-benchmark floor of roughly 600 ms, this is a small tax.
- **`LaunchCount` is a replicate count, and each replicate uses a fresh process.** This provides a run-to-run reproducibility estimate rather than repeating the measurement inside one process.
- **Wedged workers cannot hang the run.** The engine kills a worker once it exceeds a wall-clock ceiling and reports its benchmarks as errored. Increase `--max-tuning-time` if the work is genuinely slow.
- **Workers cannot outlive the run.** If the coordinating process exits (clean finish, Ctrl-C, crash, or IDE stop), the worker stops at its next sample and exits.

## See also

For more information, see the following pages:

- [Isolation internals](../deep-dives/isolation-internals.md) - How the engine finds and launches workers, what crosses the wire, and how refusals are classified.
- [Harness mode](../usage-modes/harness-mode.md#isolationisolationrequired) - The `[Isolation(Isolation.Required)]` and `[Isolation(Isolation.Off)]` attributes.
- [Exceptions](../reference/exceptions.md) - `BenchmarkIsolationException` and the rest of the hierarchy.
- [Suite mode](../usage-modes/suite-mode.md) - The full `BenchmarkSuite` API.
- [Samples](../samples.md) - A runnable isolated-runs sample project.
