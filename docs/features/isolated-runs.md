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
| **Single** (`Benchmark.Run` / `RunAsync`) | **On by default** | One worker per call |
| **Suite** (`BenchmarkSuite`) | **On by default** | The whole suite runs in one worker |
| **Harness** (`BenchmarkHarness`) | **On by default** | Per class, with per-benchmark and opt-out controls |

Isolation is useful when you want to reduce contamination from:

- prior JIT warmup from earlier benchmarks
- heap and GC pressure left by unrelated work
- thread-pool and process-level runtime state

## Single mode

Single mode isolates by default. The call signature is unchanged - including the synchronous return - and the body is measured in a worker started under the configured runtime profile:

```csharp
using NBenchmark;

// Measured in a worker, under the steady-state runtime profile.
var result = Benchmark.Run(() => Fibonacci(20), name: "fib");
```

This matters more here than anywhere else, because a lambda measured in whatever process happens to be running inherits that process's JIT tiering. The same body, measured both ways in the same program:

| round | isolated | in-process | ratio |
| --- | --- | --- | --- |
| 0 | 329 ns | 7,009 ns | 21.3x |
| 1 | 320 ns | 6,733 ns | 21.0x |
| 2 | 322 ns | 329 ns | 1.0x |

The in-process column is not noisy - it is *wrong*, by a factor of 21, until the JIT happens to promote the body to tier 1 on the third attempt. Nothing in the reported confidence interval hints at that. The isolated column is the same number every time.

### When the body cannot be isolated

A body that **captures state from its enclosing scope** cannot be measured in a worker, because the captured values live only in this process:

```csharp
var iterations = 1000;

// Captures `iterations`, so this is measured in-process and labelled.
var result = Benchmark.Run(() => Work(iterations));
```

NBenchmark measures it here, prints the reason once, and stamps `IsolationStatus.InProcessCapturedState` on the result. It never reconstructs the captured state: doing so was tried, and it did not fail loudly - it returned a plausible number for the wrong value. To isolate a body like this, remove the capture (use a constant, or move the state into a benchmark class field).

### Measuring this process on purpose

`RunInProcess` is not a fallback - it is the correct choice when the current process *is* the subject: cold-start and first-call cost, or a body that must observe host state such as a warm cache or an open connection. It measures here silently, with no warning, and stamps `IsolationStatus.InProcessRequested`:

```csharp
// Deliberate: measuring first-call cost, where disabling tiering would measure the wrong thing.
var cold = Benchmark.RunInProcess(() => ColdStartSensitivePath(), name: "cold path");
```

`Benchmark.Warmup()` optionally starts a worker in the background so the first measured call does not pay the roughly 70 ms launch.

## Suite mode

An ordinary suite isolates with no ceremony. Nothing about how you write it changes:

```csharp
await new BenchmarkSuite("sorting")
    .Add("bubble", () => BubbleSort())
    .Add("array", () => ArraySort())
    .WithBaseline("bubble")
    .WithReporter(new ConsoleReporter())
    .RunAsync();
```

Each body is a non-capturing lambda, so NBenchmark addresses the compiled method behind each one and measures the whole suite in a worker. `WithIsolation(false)` opts back into the host process, deliberately and without a warning.

### Why one worker for the whole suite

All of a suite's benchmarks share a single worker, so every ratio between them is a **paired, within-process** comparison - the worker's CPU frequency, thermal state and address-space layout cancel out of the ratio rather than adding to it.

Measuring each benchmark in its own process sounds safer, but the measurements say otherwise. On four benchmarks of provably identical cost:

| Configuration | Spread across runs | Largest fabricated difference |
| --- | --- | --- |
| in-process | 3.27x | 2.80x |
| **isolated per child, host runtime configuration** | **3.10x** | **3.06x** |
| isolated, `steady-state` runtime configuration | 1.03x | 1.03x |

Per-benchmark isolation buys the middle row. Sibling contamination was never the dominant error - uncontrolled JIT tiering was, and that is a *per-process* setting which is identical whether one worker runs one benchmark or five. Splitting them would convert every ratio into a between-process contrast, inflating its variance for no accuracy gain.

Residual order effects are handled instead by randomizing run order per replicate (`WithRunOrder(RunOrder.Random)`, reproducible via `WithSeed`), and `WithLaunchCount(n)` measures the suite in *n* separate workers to estimate run-to-run reproducibility.

If a specific benchmark genuinely pollutes its siblings - one that permanently fills a static cache, say - put it in its own suite. Harness mode's `[IsolatedProcess]` gives per-benchmark isolation when you need it named at the benchmark level.

### When a suite cannot be isolated on its own

Some suites hold things a worker cannot be handed and must instead **build for itself**. NBenchmark says which benchmark and why, then measures in the host process and labels the results:

- a body that **captures a local** - the captured value exists only in your process
- **suite setup/teardown**, or per-iteration setup/teardown - live delegates that would otherwise run on the wrong side of the boundary, preparing state the benchmarks never see
- **parameterized** benchmarks, which close over their parameter values
- a custom `IOutlierDetector` / `ISignificanceTest` that cannot be rebuilt from a type name

For those, move the suite into a static factory and hand the method group to `RunPlanAsync`. The worker invokes *your factory* in its own process, so all of that is constructed there rather than described to it - nothing has to be serializable:

```csharp
using NBenchmark.Attributes;

await BenchmarkSuite.RunPlanAsync(BuildSuite);

[BenchmarkPlan]
static BenchmarkSuite BuildSuite()
{
    var payload = new byte[4096];

    return new BenchmarkSuite("hashing")
        .Add("hash", () => Hash(payload))
        .WithSuiteSetup(() => Random.Shared.NextBytes(payload));
}
```

The factory must be `static` and capture nothing itself, so a worker can locate it by metadata token. `RunPlansAsync(typeof(Plans))` runs every `[BenchmarkPlan]` on a type, each in its own worker. A method marked `[BenchmarkPlan]` but shaped wrongly throws rather than being skipped - a silently skipped suite gives its author nothing to go on.

### `WithIsolation()`

`WithIsolation()` predates all of this. It still works, but isolates by **re-executing your whole program** to rebuild the suite, so side effects in `Main` repeat once per child and *M* isolated suites do *M²* work. It is no longer needed: a plain `RunAsync()` already isolates.

## Harness mode

Harness mode is **isolated by default**: each benchmark class runs in its own clean child process. You usually don't configure anything - `BenchmarkHarness.Create(args)...RunAsync()` already isolates per class.

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

When isolation resolves to a mix, NBenchmark runs the in-process benchmarks in the host, the per-class benchmarks together in one child, and each `[IsolatedProcess]` benchmark in its own child.

See [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) for the full attribute reference.

### How the child works

Harness mode measures in a dedicated worker process, `nbworker`, which ships inside the NBenchmark package and is copied next to your application at build time. The coordinator - the process you started - plans the work, aggregates statistics and renders reports, but never measures.

A worker loads the assembly declaring your benchmarks into its own load context, runs the same attribute discovery the host would, measures with the same engine, and streams results back over a private pipe. Three consequences are worth knowing:

- **Your `Main` does not re-run.** Earlier versions re-executed the entry assembly for every child, so a program with *M* isolated suites did *M²* work and any side effect in `Main` - a file write, an HTTP call, database seeding - happened once per child. A worker is given an assembly and a class name instead.
- **Progress is live.** Warmup and measurement phases stream from the worker into your own `IBenchmarkProgress` and `IMeasurementObserver` as they happen. Per-*sample* observer events stop at the process boundary, because forwarding thousands of them would add measurable time to the run; the full raw samples still arrive with each result.
- **Results and their samples arrive together.** There is no side table to look them up in, which is what previously allowed every isolated result to lose its samples and silently disable significance testing.

If the worker is missing - an incomplete restore, or `NBenchmarkDeployWorker=false` - benchmarks are measured in the host process, the reason is printed, and the results are stamped `host`. Set `NBENCHMARK_WORKER_PATH` to point at a specific `nbworker.dll` if you need to override discovery.

### What cannot be isolated

A worker does not re-run your entry point, so anything NBenchmark holds as *live code in the coordinator* has no counterpart there. These fall back to in-process measurement, with the reason printed and the results stamped `host`:

- **Instance factories and service providers** (`WithInstanceFactory`, `WithServiceProvider`). A worker can construct a type, but it cannot reproduce a factory that exists only in your process. Building the type directly instead would measure a differently-configured object while reporting it as though nothing had changed.
- **Benchmarks declared in an assembly with no file on disk** - a single-file or in-memory build.
- **Custom `IOutlierDetector` / `ISignificanceTest` instances that cannot be rebuilt from a type name** - one constructed with arguments, for example. Strategies with a parameterless constructor travel fine.

The rule throughout is to refuse rather than guess. Reconstructing captured state was tried and did not fail loudly: it returned plausible, *wrong* numbers - a body over a captured `5` measured as though it were `1`, with no error and a tight confidence interval.

## Why isolation actually matters

The intuitive case for isolation is that a benchmark should not inherit JIT, GC or thread-pool state left behind by its siblings. That is true, but it is not the main reason, and measuring it shows why.

Four benchmarks with provably identical cost, measured repeatedly:

| Configuration | Spread across runs | Largest fabricated difference |
| --- | --- | --- |
| in-process | 3.27x | 2.80x |
| isolated, host runtime configuration | 3.10x | 3.06x |
| **isolated, `steady-state` runtime configuration** | **1.03x** | **1.03x** |

Isolation on its own barely helped. What fixed it was disabling tiered compilation - and **that can only be done to a process that has not started yet**, because the runtime reads the setting once at startup and never again.

So the process boundary is not the remedy. It is the *delivery mechanism* for the [runtime profile](../reference/cli.md#--runtime-profile-profile), which is the remedy. Isolation without it produces numbers that are reproducible and wrong, reported with a tight confidence interval - which is worse than being obviously noisy, because it invites trust.

This is also why an in-process benchmark can never be as trustworthy as an isolated one, no matter how many samples it collects: it is stuck with whatever configuration its host process was started with. NBenchmark reports that rather than hiding it - in-process results are stamped `host`, and results measured under different runtime configurations are never compared against each other.

## Important behavior notes

- Isolation adds overhead: one process launch per child. A worker costs roughly 70 ms to start and complete its handshake; a legacy `WithIsolation()` child re-runs your entry point and costs about 200 ms. Against the per-benchmark wall-clock floor of about 600 ms (`MinWarmupTime` plus `MinMeasurementTime`), either is a small tax for a comparison group of any size.
- **Do not rely on `--in-process` for anything comparative.** On four benchmarks with provably identical cost, repeated in-process runs spanned 3.27x and fabricated a 2.80x difference between two of them, while reporting a tight confidence interval on each. The same benchmarks measured in workers under the default runtime profile spanned 1.03x. See `plans/out-of-process-pivot.md` for the measurements and the reason.
- **`LaunchCount` is a replicate count, and each replicate is a fresh process.** In Harness mode, `--launch-count 3` measures the group in three separate workers, each with its own shuffle order derived from the session seed. That is what gives a run-to-run reproducibility estimate rather than three repetitions inside one process - and reproducibility, not within-process precision, is what a regression gate should read.
- Run-order randomization is honoured in Harness workers and in `RunPlanAsync` suites. Legacy `WithIsolation()` children still run in **declaration** order.
- `--dry-run` (equivalent to `--iterations 0 --warmup 0`) always runs in-process - no child is spawned.
- `RunPlanAsync` suites need no configuration transfer at all: the worker runs your factory, so custom detectors, significance tests and lifecycle delegates are constructed there rather than described to it. Harness workers receive the resolved configuration directly and rebuild custom strategies from their type names. Legacy `WithIsolation()` children rebuild everything by re-running your `Main`.
- A child that never returns is killed, along with its whole process tree, once it exceeds a wall-clock ceiling derived from the tuning budget (`MaxTuningTime` and `CapGraceFactor`, plus warmup and process-start allowances). The affected benchmarks are reported as errored, naming the timeout, rather than hanging the run. Raise `--max-tuning-time` if the work is genuinely that slow.
- A worker cannot outlive the run that started it. It blocks reading its inbound pipe, so if the coordinator exits for any reason - a clean finish, a Ctrl-C, a crash, an IDE stop button - the read ends and the worker exits on its own, measured at 7 ms. Nothing supervises it, which matters because the supervisor would be the process most likely to have died.

## Related

- See [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) for `[IsolatedProcess]` and `[InProcess]` on attribute-discovered benchmarks.
- See [Suite mode](../usage-modes/suite-mode.md) for the full `BenchmarkSuite` fluent API.
- See [Samples](../samples.md) for a runnable isolated-runs sample project.
