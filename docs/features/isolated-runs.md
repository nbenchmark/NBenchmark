---
title: "Isolated Runs"
description: Run benchmarks in clean workers to avoid runtime cross-contamination.
order: 1
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

### What a captured value costs

A body that **captures a local** is still measured in a worker. The captured values are sent with the address, so the body a worker binds is the body you wrote, holding what you gave it:

```csharp
var iterations = 1000;

// Captures `iterations`. Isolated: the 1000 travels with the address.
var result = Benchmark.Run(() => Work(iterations));
```

For state the worker should *build* rather than be sent - and for anything the capture rule below declines - name the preparation separately, and the worker builds it in the process that measures:

```csharp
var result = Benchmark.Run(
    prepare: () => BuildData(),           // runs once, before warmup, in the worker
    body:    d => Sort(d));
```

Reach for `prepare:` when the state is:

- **big** - past `MaxTransferredStateBytes` (8 MiB) a capture is refused, and below it a large value still costs the wire what a worker could rebuild in less time
- **live** - a `Stream`, a `DbConnection`, an open handle, a warmed cache. These cannot cross at all, and a recipe builds them in the process that measures
- **unfaithful** - anything the rule below declines, most often a collection with a custom comparer

It is also strictly more faithful whatever the size, because the value is then built by the same code in the same process rather than reconstructed there. A prepare delegate that closes over a local of its own is fine - `prepare: () => BuildData(size)` sends the `size` and builds the data in the worker, which is the point. In Suite mode the same idea is `BenchmarkSuite.Over` (see below).

When you do not use `prepare:`, what crosses is a closed set: primitives, strings, `DateOnly`/`TimeOnly`/`Uri`/`Version`/`BigInteger` and the other codec-backed scalars, rectangular and jagged arrays, the ordered collections (`List`, `Queue`, `Stack`, `LinkedList`, `ImmutableArray`, `ReadOnlyCollection`, `ArraySegment`...), the keyed ones when their comparer is reproducible, and types you have marked `[BenchmarkState]`. The rule is stronger than "it round-trips": **nothing about how the value performs is carried outside its serialized contents** - a dictionary with a custom comparer is refused even though its entries round-trip, because the comparer's lookup cost is part of what you would be measuring. What is checked is **what the value is**, not what its field says - a field declared as an interface crosses when the object in it is one the rule admits:

```csharp
IReadOnlyList<int> values = new List<int> { 4, 5, 6 };

Benchmark.Run(() => Sum(values));   // isolated: rebuilt as the List<int> it actually is
```

See [Isolation internals: captured-state transfer](../deep-dives/isolation-internals.md#captured-state-transfer) for the full fidelity model, including the per-position checks.

#### Extending the set with `[BenchmarkState]`

A type of your own joins the set when you mark it, which is the remedy the refusal message names:

```csharp
[BenchmarkState]
public sealed record Query(string Text, int Limit, string[] Fields);

var query = new Query("select", 10, ["id", "name"]);

Benchmark.Run(() => Search(query));   // isolated: the query is sent by value
```

It is an assertion you are making, and the claim is not that the type round-trips - most types do. It is that **nothing about how it performs is carried outside its serialized data**. A type holding an open handle, a warmed cache or a pooled buffer does not qualify: it would arrive intact and measure differently, which is the one failure a benchmark must not have.

The attribute admits the type; it does not admit what the type holds. Every member is still checked by the ordinary rule, so a dictionary with an irreproducible comparer is refused inside an attributed type exactly as it is outside one. Members the serializer cannot restore are refused too, with the member named - a private field never reaches the payload at all, and a public *readonly* field or a get-only property is written to it and silently discarded on the way back. Each would arrive at its default.

When in doubt, do not use it. Naming the preparation costs one delegate and is strictly more faithful, because the value is then built in the process that measures it rather than reconstructed there.

A value NBenchmark cannot vouch for is **declined, never guessed at**: a fabricated closure does not throw, it returns plausible, silently wrong numbers. See [When isolation is refused](#when-isolation-is-refused) for what happens then.

If a specific benchmark genuinely pollutes its siblings - one that permanently fills a static cache, say - put it in its own suite. Harness mode's `[IsolatedProcess]` gives per-benchmark isolation when you need it named at the benchmark level.

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

### Prepared state: `BenchmarkSuite.Over`

In Suite mode the `prepare:` idea is `BenchmarkSuite.Over`, and the payoff is larger: one worker measures the whole suite, so a single body that cannot be addressed takes every sibling in-process with it. The state is declared at construction because that is what types each body's parameter - there is no configuration to carry across and no ordering to get wrong.

```csharp
await BenchmarkSuite.Over("sorting", () => BuildData())
    .Add("array", d => Array.Sort(d))
    .Add("linq",  d => d.OrderBy(x => x).ToArray())
    .WithBaseline("array")
    .RunAsync();
```

`prepare` runs **once per benchmark**, before that benchmark's warmup - not once per suite. Two sorts sharing one array would have the second measure what the first already sorted, and under the default random run order which one that is would change between runs. Where the body mutates its state and you need it reset every iteration, use the per-iteration `setup:` argument on `Add`, which runs outside the timed region.

### What still cannot be isolated on its own

A few things a worker must be *given* rather than able to *build*:

- a **live object** - a `Stream`, a `DbConnection`, an `HttpClient`, a mock, an `IClassFixture`, a built `IServiceProvider`. There is no address for one, and no bytes that reproduce how it performs
- a capture past **`MaxTransferredStateBytes`** (8 MiB, configurable). The remedy is a prepare delegate, not a larger ceiling
- **two receivers sharing one object** - a body and a hook in different scopes both pointing at one array. Rebuilding them makes two arrays where your program has one, so the sharing has to be reproduced by a recipe instead
- a **parameter value** outside the marshallable set (primitives, strings, enums, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, `DateOnly`, `TimeOnly`, `Uri`, `Version`, `BigInteger`)
- an assembly with **no file on disk** - single-file, in-memory or dynamically emitted

Several things are *not* on this list: a lambda capturing `this`, a capturing lifecycle delegate, a capturing `prepare` delegate, a custom `IOutlierDetector` or `ISignificanceTest` built with constructor arguments, a DI container built by a factory, and a capturing `[BenchmarkPlan]` factory all isolate - each is a recipe or a value, and both cross.

```csharp
.WithOutlierDetector(() => new KeepFastestDetector(fraction))
.WithSignificanceTest(() => new MedianRatioSignificanceTest(minimum))
```

```csharp
.UseDependencyInjection<MyBenchmarks>(() => BuildServices(connectionString))
```

What still fails there is passing a *built* container or a *constructed* strategy instance rather than a factory: the object itself cannot cross, and only a factory describes how to make another one.

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

The factory must be `static` and capture nothing itself, so a worker can locate it by metadata token. It is invoked once in the coordinator - to read the baseline, reporters and runtime profile the worker launches under - and once per replicate in each worker that measures it, so it must only wire delegates together and never do the work itself. Build the suite's real state in `WithSuiteSetup` or a `prepare` delegate; a factory that builds real state runs that work once per launch on top of the measurement. `RunPlansAsync(typeof(Plans))` runs every `[BenchmarkPlan]` on a type, each in its own worker. A method marked `[BenchmarkPlan]` but shaped wrongly throws rather than being skipped - a silently skipped suite gives its author nothing to go on.

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

A method-level attribute beats a class-level one, which is how a mostly-in-process class forces one benchmark into a worker. Both on the *same* member is an error (analyzer NB0015, and discovery refuses it too): the two ask for opposite things, so the combination is refused rather than silently resolved.

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

### What cannot be isolated

A worker does not re-run your entry point, so anything NBenchmark holds as *live code in the coordinator* has no counterpart there. These are **refused**, and a refusal fails the run - see [When isolation is refused](#when-isolation-is-refused):

- **A built `IServiceProvider` or a live instance factory** passed as an object rather than as a factory. A worker can construct a type, but it cannot reproduce a container that exists only in your process, and building the type directly instead would measure a differently-configured object while reporting it as though nothing had changed. Pass a factory - `WithServiceProvider(BuildServices)` - and the worker builds an equivalent container in the process that measures. The factory may close over a connection string or a flag; what it may not do is hand over the container itself.
- **Benchmarks declared in an assembly with no file on disk** - a single-file or in-memory build.
- **A custom `IOutlierDetector` / `ISignificanceTest` passed as a constructed instance.** Only a type name would cross, and only a parameterless constructor could be reached at the other end, so `new KeepFastestDetector(0.9)` cannot be rebuilt from one. Pass a factory instead and the argument travels with it - see [Custom statistics](../guides/custom-statistics.md).

The rule throughout is to refuse rather than guess. Reconstructing captured state does not fail loudly: it returns plausible, *wrong* numbers - a body over a captured `5` measures as though it were `1`, with no error and a tight confidence interval.

## When isolation is refused

**A refusal is an error.** `MeasurementOptions.RequireIsolation` defaults to `true`, so a benchmark that asked for a worker and cannot have one fails the run rather than being measured in the host process and labelled. In Harness mode the check runs at *discovery* time, before the first benchmark is measured, and reports every un-isolatable class in one message.

What remains under the gate is the small set in *What cannot be isolated* above, and each member of it has a one-line remedy.

A suite is addressed as a set - one worker measures all of it - so one body that cannot cross costs every sibling its isolation. The message names **every** offender rather than the first, because you would otherwise fix one, re-run, and discover the next.

**In-process measurement is something you ask for.** Every deliberate route to the host process is legal and is *not* a refusal - the gate keys on the four refusal statuses, never on "was not isolated":

| Mode | How to ask |
|---|---|
| Harness | `[InProcess]` on a method or class, `--in-process`, `WithIsolation(false)` |
| Simple | `Benchmark.RunInProcess(...)` and its `RunInProcessAsync` / prepared-state overloads |
| Suite | `BenchmarkSuite.AddInProcess(...)` for one benchmark, `WithIsolation(false)` for the whole suite |
| Any | `--dry-run`, which never invokes a body and so never spawns a worker |

All of these stamp `IsolationStatus.InProcessRequested`, are excluded from `--strict-isolation`, and are never given a ratio against an isolated row - the configuration difference between the two processes does not go away because it was asked for.

```csharp
// One benchmark holds a live handle; the rest of the suite is still measured in a worker.
await new BenchmarkSuite("cache")
    .Add("cold", () => Parse(Payload))
    .AddInProcess("warm", () => connection.Query())   // stamped in-process, by request
    .RunAsync();
```

`AddInProcess` exists because `WithIsolation(false)` is all-or-nothing: with it, a single un-addressable body takes every other benchmark in the suite into the host process with it.

To accept labelled fallbacks everywhere instead - the right setting for scratchpad use, where a number measured here and clearly stamped beats no number at all - turn the requirement off:

```csharp
Benchmark.Run(body, new MeasurementOptions { RequireIsolation = false });
new BenchmarkSuite("s").WithRequireIsolation(false);
BenchmarkHarness.Create(args).WithRequireIsolation(false);
```

## Checking isolation rather than trusting it

Two flags make the claim verifiable on your own code:

- **`--strict-isolation`** turns `RequireIsolation` on for the run and audits the results as well, naming every refused benchmark and its remedy. It is a backstop rather than the primary gate - the requirement is on by default - and it keys on *refusal*: a deliberate `--in-process` or `--dry-run` run passes it, because there is nothing to act on. Use it wherever a pipeline gates on benchmark numbers: a benchmark that quietly fell back - a build agent without the worker deployed, or a body that captures state - cannot be compared against a baseline measured under a different runtime configuration.
- **`--verify-isolation`** measures everything a second time in the host process and prints the per-benchmark difference, so you can see what your own numbers would have been. It reports a ratio per benchmark rather than an aggregate, because the finding is that host measurement is *unpredictable* rather than uniformly wrong. The comparison pass publishes nothing - no reporters, no output files, no exit code - so a diagnostic command cannot change the build's outcome.

  It is skipped, with a reason, on a run that used `--runtimes`. This process is one runtime, so there is no in-process counterpart for the other builds; comparing every runtime against the same host row would print a table that looks like a finding and is not one.

See [CLI reference](../reference/cli.md#--strict-isolation) for both.

## Important behavior notes

- Isolation adds overhead: one worker launch per group. A worker costs roughly 70 ms to start and complete its handshake. Against the per-benchmark wall-clock floor of about 600 ms (`MinWarmupTime` plus `MinMeasurementTime`), either is a small tax for a comparison group of any size.
- **Do not rely on `--in-process` for anything comparative.** In-process runs of identical-cost benchmarks can spread 3x across runs while each reports a tight confidence interval. See the table under [Why one worker for the whole suite](#why-one-worker-for-the-whole-suite) for the measurements and the reason.
- **`LaunchCount` is a replicate count, and each replicate is a fresh process.** In Harness mode, `--launch-count 3` measures the group in three separate workers, each with its own shuffle order derived from the session seed. That is what gives a run-to-run reproducibility estimate rather than three repetitions inside one process - and reproducibility, not within-process precision, is what a regression gate should read.
- Run-order randomization is honoured everywhere, and each replicate derives a distinct order from the session seed, so run order is a randomized nuisance factor rather than a fixed confound.
- `--dry-run` (equivalent to `--iterations 0 --warmup 0`) always runs in-process - no worker is spawned.
- `RunPlanAsync` suites need no configuration transfer at all: the worker runs your factory, so custom detectors, significance tests and lifecycle delegates are constructed there rather than described to it. Harness workers receive the resolved configuration directly and rebuild custom strategies from their type names.
- A worker that never returns is killed, along with its whole process tree, once it exceeds a wall-clock ceiling derived from the tuning budget (`MaxTuningTime` and `CapGraceFactor`, plus warmup and process-start allowances). The affected benchmarks are reported as errored, naming the timeout, rather than hanging the run. Raise `--max-tuning-time` if the work is genuinely that slow.
- A worker cannot outlive the run that started it, and does not keep measuring for a coordinator that is gone. It reads its inbound pipe continuously - while idle *and* while measuring - so if the coordinator exits for any reason (a clean finish, a Ctrl-C, a crash, an IDE stop button) the read ends, the worker stops at its next sample and exits on its own with a distinct exit code. Nothing supervises it, which matters because the supervisor would be the process most likely to have died.

## See also

- [Isolation internals](../deep-dives/isolation-internals.md) - the engineering underneath: how a worker is found and launched, what crosses the wire and how, and why a refusal is classified the way it is.
- [Harness mode](../usage-modes/harness-mode.md#isolatedprocess) - `[IsolatedProcess]` and `[InProcess]` on attribute-discovered benchmarks.
- [Suite mode](../usage-modes/suite-mode.md) - the full `BenchmarkSuite` fluent API.
- [Samples](../samples.md) - a runnable isolated-runs sample project.
