---
title: "Isolated Runs"
description: Run benchmarks in clean workers to avoid runtime cross-contamination.
order: 4
---

# Isolated Runs

Process isolation runs benchmarks in a freshly spawned worker so their measurements are not biased by runtime state - JIT warmup, heap and GC pressure, or thread-pool and process-level state - left behind by earlier work in the same process.

Isolation is on by default in every mode. What differs is the granularity, and what happens when a benchmark cannot be isolated:

| Mode | Isolation | Granularity |
| --- | --- | --- |
| **Single** (`Benchmark.Run` / `RunAsync`) | **On by default** | One worker per call |
| **Suite** (`BenchmarkSuite`) | **On by default** | The whole suite runs in one worker |
| **Harness** (`BenchmarkHarness`) | **On by default** | Per class, with per-benchmark and opt-out controls |

You do not need to turn it on. It is worth understanding because it is what removes contamination from:

- prior JIT warmup from earlier benchmarks
- heap and GC pressure left by unrelated work
- thread-pool and process-level runtime state

and because a benchmark that *cannot* be isolated is measured in the host process and labelled, rather than being quietly measured under whatever configuration the host happened to start with. The `Iso` column in your output is where that shows up.

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

NBenchmark measures it here, prints the reason once, and stamps `IsolationStatus.InProcessCapturedState` on the result. It never reconstructs the captured state - a fabricated closure does not throw, it returns plausible, silently wrong numbers. To isolate a body like this, remove the capture (use a constant, or move the state into a benchmark class field).

The analyzer package reports this at compile time as [NB0014](../reference/analyzers.md#nb0014---capturing-body-cannot-be-isolated), naming the symbols captured - which is more precise than the runtime can be, since by then they are fields on a compiler-generated class. It is informational rather than a warning, because capturing is the idiomatic way to benchmark over prepared data. See the analyzer page for the full lowering table - which shapes capture, which do not, and why a `static` lambda does not change the answer.

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
| **isolated per worker, host runtime configuration** | **3.10x** | **3.06x** |
| isolated, `steady-state` runtime configuration | 1.03x | 1.03x |

Per-benchmark isolation buys the middle row. Sibling contamination was never the dominant error - uncontrolled JIT tiering was, and that is a *per-process* setting which is identical whether one worker runs one benchmark or five. Splitting them would convert every ratio into a between-process contrast, inflating its variance for no accuracy gain.

Residual order effects are handled instead by randomizing run order per replicate (`WithRunOrder(RunOrder.Random)`, reproducible via `WithSeed`), and `WithLaunchCount(n)` measures the suite in *n* separate workers to estimate run-to-run reproducibility.

If a specific benchmark genuinely pollutes its siblings - one that permanently fills a static cache, say - put it in its own suite. Harness mode's `[IsolatedProcess]` gives per-benchmark isolation when you need it named at the benchmark level.

### Prepared state: hand over a recipe, not a value

The obvious way to benchmark over input is to build it and close over it, and that is the one shape a worker cannot measure:

```csharp
var data = BuildData();
Benchmark.Run(() => Sort(data));          // captures 'data' -> measured in this process
```

The captured value exists in your process and nowhere else. NBenchmark will not fabricate a replacement - probing showed a fabricated closure does not throw, it returns plausible, silently wrong numbers - so the body is measured here and labelled `host`.

Split the preparation into its own delegate and both halves capture nothing, so both can be addressed. The worker follows the recipe in the process that measures:

```csharp
Benchmark.Run(
    prepare: () => BuildData(),           // runs once, before warmup, in the worker
    body:    d => Sort(d));
```

In Suite mode the same idea is `WithState`, and the payoff is larger: one worker measures the whole suite, so a single capturing body takes every sibling in-process with it.

```csharp
await new BenchmarkSuite("sorting")
    .WithState(() => BuildData())
    .Add("array", d => Array.Sort(d))
    .Add("linq",  d => d.OrderBy(x => x).ToArray())
    .WithBaseline("array")
    .RunAsync();
```

`prepare` runs **once per benchmark**, before that benchmark's warmup - not once per suite. Two sorts sharing one array would have the second measure what the first already sorted, and under the default random run order which one that is would change between runs. Where the body mutates its state and you need it reset every iteration, use the per-iteration `setup:` argument on `Add`, which runs outside the timed region.

### What still cannot be isolated on its own

A few things a worker must be *given* rather than able to *build*. NBenchmark says which benchmark and why, then measures in the host process and labels the results:

- a body or lifecycle delegate that **captures a local**, and the same for a `prepare` delegate - the remedy is the split above
- a lambda capturing **`this`**, an `IClassFixture`, a mock or a `[MemberData]` graph: there is no address for a live object. In a test, use `[PerformanceFact]` / `[Performance]`, which address the test method itself
- a **parameter value** outside the marshallable set (primitives, strings, enums, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`)
- a suite carrying **both** `WithState` and `WithParameter`
- an assembly with **no file on disk** - single-file, in-memory or dynamically emitted

A custom `IOutlierDetector` or `ISignificanceTest` needing constructor arguments, and a DI container, are no longer on this list: pass a static factory instead of an instance and the worker builds its own.

```csharp
.WithOutlierDetector(static () => new KeepFastestDetector(0.90))
.WithSignificanceTest(static () => new MedianRatioSignificanceTest(25))
```

```csharp
.UseDependencyInjection<MyBenchmarks>(BuildServices)   // static IServiceProvider BuildServices()
```

For anything genuinely left, move the suite into a static factory and hand the method group to `RunPlanAsync`. The worker invokes *your factory* in its own process, so all of that is constructed there rather than described to it - nothing has to be serializable:

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

## Harness mode

Harness mode is **isolated by default**: each benchmark class runs in its own clean worker. You usually don't configure anything - `BenchmarkHarness.Create(args)...RunAsync()` already isolates per class.

```csharp
using NBenchmark.Attributes;

public sealed class StartupBenchmarks
{
    [Benchmark]
    public int ColdPath() => RunColdSensitiveWork();   // isolated per class by default
}
```

You can tune the granularity:

- **`[IsolatedProcess]`** on a method (finest granularity) gives that one benchmark its own dedicated worker, isolated even from siblings in the same class.
- **`[InProcess]`** on a method (or class) opts that benchmark back into the host process.
- **`--in-process`** on the command line, or **`WithIsolation(false)`** in code, disables isolation for the whole run.

```csharp
public sealed class MixedBenchmarks
{
    [Benchmark]
    public int Default() => Work();              // shares one per-class worker

    [Benchmark]
    [IsolatedProcess]
    public int OwnProcess() => ColdWork();        // its own dedicated worker

    [Benchmark]
    [InProcess]
    public int InHost() => HostObservableWork();  // runs in the host process
}
```

When isolation resolves to a mix, NBenchmark runs the in-process benchmarks in the host, the per-class benchmarks together in one worker, and each `[IsolatedProcess]` benchmark in its own worker.

See [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) for the full attribute reference.

### How the worker works

Harness mode measures in a dedicated worker process, `nbworker`, which ships inside the NBenchmark package and is copied next to your application at build time. The coordinator - the process you started - plans the work, aggregates statistics and renders reports, but never measures.

A worker loads the assembly declaring your benchmarks into its own load context, runs the same attribute discovery the host would, measures with the same engine, and streams results back over a private pipe. Three consequences are worth knowing:

- **Your `Main` does not re-run.** A worker is given an assembly and a class name rather than re-executing the entry assembly, so a program with *M* isolated suites does *M* work, not *M²*, and any side effect in `Main` - a file write, an HTTP call, database seeding - happens once, in the host.
- **Progress is live.** Warmup and measurement phases, detector snapshots and results stream from the worker into your own `IBenchmarkProgress` and `IMeasurementObserver` as they happen. Per-*sample* observer events are the exception: they stop at the process boundary unless `--stream-samples` asks for them, because a benchmark emits them in the thousands and forwarding all of them would add measurable time to the run. The full raw samples arrive with each result either way. See [Measurement Observer](../reference/observers.md#what-an-isolated-run-delivers) for the per-callback table.
- **Results and their samples arrive together.** A worker computes every statistic over the full sample array and ships the samples on the completion frame, so `BenchmarkResult.RawSamples` is complete and significance testing reads them.

If the worker is missing - an incomplete restore, or `NBenchmarkDeployWorker=false` - benchmarks are measured in the host process, the reason is printed, and the results are stamped `host`. Set `NBENCHMARK_WORKER_PATH` to point at a specific `nbworker.dll` if you need to override discovery.

### What cannot be isolated

A worker does not re-run your entry point, so anything NBenchmark holds as *live code in the coordinator* has no counterpart there. These fall back to in-process measurement, with the reason printed and the results stamped `host`:

- **Instance factories and service providers** (`WithInstanceFactory`, `WithServiceProvider`). A worker can construct a type, but it cannot reproduce a factory that exists only in your process. Building the type directly instead would measure a differently-configured object while reporting it as though nothing had changed.
- **Benchmarks declared in an assembly with no file on disk** - a single-file or in-memory build.
- **Custom `IOutlierDetector` / `ISignificanceTest` instances that cannot be rebuilt from a type name** - one constructed with arguments, for example. Strategies with a parameterless constructor travel fine.

The rule throughout is to refuse rather than guess. Reconstructing captured state was tried and did not fail loudly: it returned plausible, *wrong* numbers - a body over a captured `5` measured as though it were `1`, with no error and a tight confidence interval.

## Checking isolation rather than trusting it

Two flags make the claim verifiable on your own code:

- **`--strict-isolation`** fails the run if any benchmark was measured in the host process, naming each one and its remedy. Use it wherever a pipeline gates on benchmark numbers: a benchmark that quietly fell back - a build agent without the worker deployed, or a body that captures state - cannot be compared against a baseline measured under a different runtime configuration.
- **`--verify-isolation`** measures everything a second time in the host process and prints the per-benchmark difference, so you can see what your own numbers would have been. It reports a ratio per benchmark rather than an aggregate, because the finding is that host measurement is *unpredictable* rather than uniformly wrong. The comparison pass publishes nothing - no reporters, no output files, no exit code - so a diagnostic command cannot change the build's outcome.

  It is skipped, with a reason, on a run that used `--runtimes`. This process is one runtime, so there is no in-process counterpart for the other builds; comparing every runtime against the same host row would print a table that looks like a finding and is not one.

See [CLI reference](../reference/cli.md#--strict-isolation) for both.

## Important behavior notes

- Isolation adds overhead: one worker launch per group. A worker costs roughly 70 ms to start and complete its handshake. Against the per-benchmark wall-clock floor of about 600 ms (`MinWarmupTime` plus `MinMeasurementTime`), either is a small tax for a comparison group of any size.
- **Do not rely on `--in-process` for anything comparative.** In-process runs of identical-cost benchmarks can spread 3x across runs while each reports a tight confidence interval. See the table under [Why one worker for the whole suite](#why-one-worker-for-the-whole-suite) and `plans/out-of-process-pivot.md` for the measurements and the reason.
- **`LaunchCount` is a replicate count, and each replicate is a fresh process.** In Harness mode, `--launch-count 3` measures the group in three separate workers, each with its own shuffle order derived from the session seed. That is what gives a run-to-run reproducibility estimate rather than three repetitions inside one process - and reproducibility, not within-process precision, is what a regression gate should read.
- Run-order randomization is honoured everywhere, and each replicate derives a distinct order from the session seed, so run order is a randomized nuisance factor rather than a fixed confound.
- `--dry-run` (equivalent to `--iterations 0 --warmup 0`) always runs in-process - no worker is spawned.
- `RunPlanAsync` suites need no configuration transfer at all: the worker runs your factory, so custom detectors, significance tests and lifecycle delegates are constructed there rather than described to it. Harness workers receive the resolved configuration directly and rebuild custom strategies from their type names.
- A worker that never returns is killed, along with its whole process tree, once it exceeds a wall-clock ceiling derived from the tuning budget (`MaxTuningTime` and `CapGraceFactor`, plus warmup and process-start allowances). The affected benchmarks are reported as errored, naming the timeout, rather than hanging the run. Raise `--max-tuning-time` if the work is genuinely that slow.
- A worker cannot outlive the run that started it, and does not keep measuring for a coordinator that is gone. It reads its inbound pipe continuously - while idle *and* while measuring - so if the coordinator exits for any reason (a clean finish, a Ctrl-C, a crash, an IDE stop button) the read ends, the worker stops at its next sample and exits on its own with a distinct exit code. Nothing supervises it, which matters because the supervisor would be the process most likely to have died.

## Related

- See [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) for `[IsolatedProcess]` and `[InProcess]` on attribute-discovered benchmarks.
- See [Suite mode](../usage-modes/suite-mode.md) for the full `BenchmarkSuite` fluent API.
- See [Samples](../samples.md) for a runnable isolated-runs sample project.
