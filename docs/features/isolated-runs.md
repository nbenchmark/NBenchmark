---
title: "Isolated Runs"
description: Run benchmarks in clean worker processes so earlier work can't bias the measurement.
order: 1
---

# Isolated Runs

NBenchmark measures your benchmarks in a **freshly spawned worker process** rather than in the
process that launched them. This is **on by default in every mode** and needs no configuration.

It matters because the things that most affect a measurement - JIT tiering, GC flavor, thread-pool
state - are fixed when a process starts. The only way to choose them is to start a process that
hasn't begun yet. A benchmark measured in whatever process happens to be running inherits whatever
that process was already doing.

| Mode | Granularity |
| --- | --- |
| **Single** (`Benchmark.Run` / `RunAsync`) | One worker per call |
| **Suite** (`BenchmarkSuite`) | One worker for the whole suite |
| **Harness** (`BenchmarkHarness`) | One worker per class, with per-benchmark overrides |

## What it buys you

The same body, measured both ways in the same program:

| Round | Isolated | In-process | Ratio |
| --- | --- | --- | --- |
| 0 | 329 ns | 7,009 ns | 21.3x |
| 1 | 320 ns | 6,733 ns | 21.0x |
| 2 | 322 ns | 329 ns | 1.0x |

The in-process column isn't noisy - it's *wrong*, by a factor of 21, until the JIT happens to
promote the body on the third attempt. Nothing in the reported confidence interval hints at that.
The isolated column is the same number every time.

This is why **`--in-process` is not suitable for anything comparative**. In-process runs of
identical-cost benchmarks can spread 3x across runs while each reports a tight confidence interval.

## Using it

You don't turn it on - it's already on. What you may want to change is the granularity, or opt a
specific benchmark out.

### Single mode

```csharp
// Measured in a worker. Nothing to configure.
var result = Benchmark.Run(() => Fibonacci(20), name: "fib");
```

If your benchmark needs data built first, name the preparation separately and the worker builds it
in the process that measures:

```csharp
var result = Benchmark.Run(
    prepare: () => BuildData(),      // runs once, before warmup, in the worker
    body:    d => Sort(d));
```

Use `prepare:` when the state is large, live (a `Stream`, a `DbConnection`, a warmed cache), or
otherwise can't be copied. It's also more faithful whatever the size, because the value is built by
the same code in the same process rather than reconstructed there.

`Benchmark.Warmup()` optionally starts a worker in the background so your first measured call
doesn't pay the roughly 70 ms launch.

### Suite mode

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort())
    .Add("array", () => ArraySort())
    .WithBaseline("bubble")
    .RunAsync();
```

The suite-mode equivalent of `prepare:` is `BenchmarkSuite.Over`, which also types each body's
parameter:

```csharp
await BenchmarkSuite.Over("sorting", () => BuildData())
    .Add("array", d => Array.Sort(d))
    .Add("linq",  d => d.OrderBy(x => x).ToArray())
    .RunAsync();
```

`prepare` runs **once per benchmark**, before that benchmark's warmup - not once per suite. Two
sorts sharing one array would have the second measure what the first already sorted. Where the body
mutates its state, reset it each iteration with the `setup:` argument on `Add`, which runs outside
the timed window.

### Harness mode

Each class gets its own worker by default. Two attributes change that:

```csharp
public sealed class MixedBenchmarks
{
    [Benchmark]
    public int Default() => Work();               // shares one per-class worker

    [Benchmark]
    [IsolatedProcess]
    public int OwnProcess() => ColdWork();        // its own dedicated worker

    [Benchmark]
    [InProcess]
    public int InHost() => HostObservableWork();  // runs in the host process
}
```

A method-level attribute beats a class-level one. Both on the same member is an error - they ask for
opposite things.

## Why one worker for a whole suite

All of a suite's benchmarks share a single worker, so every ratio between them is a **paired**
comparison - the worker's CPU frequency, thermal state, and memory layout cancel out of the ratio
instead of adding to it.

Measuring each benchmark in its own process sounds safer, but on four benchmarks of provably
identical cost:

| Configuration | Spread across runs | Largest fabricated difference |
| --- | --- | --- |
| in-process | 3.27x | 2.80x |
| isolated per worker, host runtime configuration | 3.10x | 3.06x |
| **isolated, `steady-state` runtime configuration** | **1.03x** | **1.03x** |

Sibling contamination was never the dominant error - uncontrolled JIT tiering was, and that's a
*per-process* setting, identical whether one worker runs one benchmark or five. Splitting them would
turn every ratio into a between-process contrast for no accuracy gain.

Order effects are handled instead by randomizing run order (`WithRunOrder(RunOrder.Random)`,
reproducible via `WithSeed`), and `WithLaunchCount(n)` measures the suite in *n* separate workers to
estimate run-to-run reproducibility.

If one benchmark genuinely pollutes its siblings - one that permanently fills a static cache, say -
put it in its own suite, or give it `[IsolatedProcess]`.

## Measuring in the host process on purpose

Sometimes the current process *is* the subject: cold-start cost, first-call cost, or a body that
must observe host state like a warm cache or an open connection. That's a legitimate request, not a
fallback, and every route to it is legal:

| Mode | How to ask |
| --- | --- |
| Single | `Benchmark.RunInProcess(...)` and its async / prepared-state overloads |
| Suite | `AddInProcess(...)` for one benchmark, `WithIsolation(false)` for the whole suite |
| Harness | `[InProcess]` on a method or class, `--in-process`, `WithIsolation(false)` |
| Any | `--dry-run`, which never invokes a body and so never spawns a worker |

```csharp
// One benchmark holds a live handle; the rest of the suite is still measured in a worker.
await new BenchmarkSuite("cache")
    .Add("cold", () => Parse(Payload))
    .AddInProcess("warm", () => connection.Query())
    .RunAsync();
```

`AddInProcess` exists because `WithIsolation(false)` is all-or-nothing: without it, a single
un-isolatable body takes every other benchmark in the suite into the host process with it.

These rows are labeled in the `Iso` column and are never given a ratio against an isolated row - the
configuration difference between two processes doesn't go away because it was asked for.

## When isolation is refused

**A refusal is an error.** `RequireIsolation` defaults to `true`, so a benchmark that asked for a
worker and can't have one **fails the run** rather than being quietly measured in the host process.

The reason is that reconstructing state doesn't fail loudly - it returns plausible, *wrong* numbers.
A body over a captured `5` would measure as though it were `1`, with no error and a tight confidence
interval. NBenchmark declines rather than guesses.

Most things cross to the worker without you doing anything: ordinary data (an `int`, a string, an
`int[]`, a `List<T>`, a record of those) is sent by value, and a lambda capturing `this`, a
capturing lifecycle delegate, or a container built by a factory all isolate fine. What remains is a
short list, each with a one-line remedy:

| What can't cross | Remedy |
| --- | --- |
| A **live object** - a `Stream`, `DbConnection`, `HttpClient`, mock, or built `IServiceProvider` | Build it in a `prepare:` delegate, or pass a *factory* instead of the object |
| A value whose behavior isn't carried by its contents - e.g. a dictionary with a custom comparer | A `prepare:` delegate, or mark your own type `[BenchmarkState]` |
| A capture larger than **8 MiB** (`MaxTransferredStateBytes`) | A `prepare:` delegate |
| **Two benchmarks sharing one object** through different closures | Build the shared state in one `prepare:` delegate they share |
| A suite that must be built by user code | A static `[BenchmarkPlan]` factory with `RunPlanAsync` |
| An assembly with **no file on disk** - single-file, in-memory, or dynamically emitted | Build to disk |

A suite is measured by one worker, so the error names **every** offending benchmark at once rather
than the first - you'd otherwise fix one, re-run, and discover the next.

To accept labeled host-process measurements instead - reasonable for scratchpad use, where a clearly
stamped number beats no number - turn the requirement off:

```csharp
Benchmark.Run(body, new MeasurementOptions { RequireIsolation = false });
new BenchmarkSuite("s").WithRequireIsolation(false);
BenchmarkHarness.Create(args).WithRequireIsolation(false);
```

**Go deeper:** [Isolation internals: captured-state transfer](../deep-dives/isolation-internals.md#captured-state-transfer)
has the complete set of types that cross, the `[BenchmarkState]` contract, and the `[BenchmarkPlan]`
factory rules.

## Checking isolation rather than trusting it

Two flags make the claim verifiable on your own code:

- **`--strict-isolation`** audits the results and names every refused benchmark and its remedy. Use
  it wherever a pipeline gates on benchmark numbers. It keys on *refusal*, so a deliberate
  `--in-process` or `--dry-run` run passes.
- **`--verify-isolation`** measures everything a second time in the host process and prints the
  per-benchmark difference, so you can see what your own numbers would have been. The comparison
  pass publishes nothing and can't change the build's outcome.

See [CLI reference](../reference/cli.md#--strict-isolation).

## Things worth knowing

- **Isolation costs about 70 ms per worker launch.** Against the per-benchmark floor of roughly
  600 ms, that's a small tax for a comparison group of any size.
- **`LaunchCount` is a replicate count, and each replicate is a fresh process.** That's what makes it
  a run-to-run reproducibility estimate rather than three repetitions inside one process - and
  reproducibility is what a regression gate should read.
- **A wedged worker can't hang the run.** It's killed once it exceeds a wall-clock ceiling and its
  benchmarks are reported as errored. Raise `--max-tuning-time` if the work is genuinely that slow.
- **A worker can't outlive the run that started it.** If the coordinating process exits for any
  reason - a clean finish, Ctrl-C, a crash, an IDE stop button - the worker stops at its next sample
  and exits on its own.

## See also

- [Isolation internals](../deep-dives/isolation-internals.md) - how a worker is found and launched, what crosses the wire, and why a refusal is classified the way it is
- [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) - the `[IsolatedProcess]` and `[InProcess]` attributes
- [Suite mode](../usage-modes/suite-mode.md) - the full `BenchmarkSuite` API
- [Samples](../samples.md) - a runnable isolated-runs sample project
