---
title: Isolation Internals
description: How NBenchmark's process isolation actually works - worker discovery, addressing, the wire protocol, budget ceilings, and the captured-state transfer rules.
order: 1
---

# Isolation internals

The [Isolated runs](../features/isolated-runs.md) page describes what isolation does for you. This page describes the engineering internals: how the engine finds and launches a worker, how it addresses a benchmark body across a process boundary, what crosses the wire and under what rules, and how the engine prevents the run from hanging if a worker wedges.

## The worker

The worker is a distinct executable, `nbworker`, that loads the target assembly by path; it is not a re-run of the entry assembly. A program with *M* isolated suites performs *M* units of work, not *M²*. Any side effect in `Main` - such as a file write, an HTTP call, or database seeding - happens only once, in the host.

### Discovery

`Workers/WorkerLocator` finds `nbworker` by resolving the most specific path first:

1. An explicit path provided in the request (multi-runtime runs name another framework's worker).
2. The worker located beside the assembly under test.
3. The worker located beside the running application.

The second step is necessary because these paths differ when using `dotnet benchmark --assembly`. In that mode, the target is a separate build with its own worker, and the tool's directory contains none. If the engine only checked the application-wide path, it would silently fall back to in-process measurement, losing isolation.

The engine checks both deployment layouts: `dir/nbworker/nbworker.dll` and then `dir/nbworker.dll`. The subdirectory is the shipped layout, ensuring the worker's `runtimeconfig.json` and `deps.json` do not conflict with the application's.

If the worker is missing - due to an incomplete restore or `NBenchmarkDeployWorker=false` - the run fails with a message naming the searched directories. You can override discovery by setting `NBENCHMARK_WORKER_PATH` to point to a specific `nbworker.dll`, or set `RequireIsolation = false` to accept a labeled host-process measurement.

### Shared framework configuration

`Workers/SharedFrameworkConfig` extends the worker's shared framework set using the target's `runtimeconfig.json` at launch. `nbworker` is a plain `Microsoft.NET.Sdk` app and declares only `Microsoft.NETCore.App`. Consequently, an assembly from a `Microsoft.NET.Sdk.Web` project - benchmarks living in the API they measure - cannot load in the worker by default: the ASP.NET assemblies are framework-provided and absent from the target's `deps.json`, so `AssemblyDependencyResolver` correctly resolves them to nothing, and they are expected on a TPA list the worker's process does not have.

Because `hostfxr` fixes the framework set before managed code runs, the engine creates a synthesized config passed via `dotnet exec --runtimeconfig`. This config combines the worker's config with the target's extra frameworks. The engine only imports frameworks; `tfm`, `rollForward`, and `configProperties` remain the worker's. This prevents importing the application's `System.GC.Server` setting, which would undermine the purpose of the process boundary. This synthesized config is content-addressed in the temporary directory. If a target needs no extra frameworks (like a plain console app) or is self-contained, the engine returns `null` and the ordinary launch proceeds unchanged.

The synthesized config is included in the `WorkerPrewarm` pool key. This ensures a parked worker started without a framework cannot serve a request that requires one.

The `dotnet benchmark` tool faces a similar problem and uses `NBenchmark.Tool/FrameworkRelaunch` to apply the same resolver. This is the only mode that loads the assembly under test into its own process for reflection and run-plan construction - discovery is reflection over real types, and the harness must instantiate those discovered types to build the run plan, so a `MetadataLoadContext` pass would have to be thrown away the moment the run started. `Assembly.LoadFrom` succeeds on a web target and the first `GetTypes()` throws. Since a process cannot change its own framework set, the tool re-executes itself under the merged config and forwards the child's exit code, using the `NBENCHMARK_TOOL_RELAUNCHED` environment marker to prevent recursion. When using `--project`, the engine builds the project before this check and passes the resulting assembly to the child to avoid repeated builds. The requirements of all named targets are unioned since they all load into one process.

### Coordination

Coordination uses a duplex pair of anonymous pipes carrying length-prefixed UTF-8 JSON frames (`Workers/WorkerProtocol`, `Workers/FrameChannel`). Benchmark bodies are addressed by a tuple of `(assembly, MVID, metadata token)` rather than being serialized (`Workers/BodyRef`, `NBenchmark.Worker/BodyResolver`).

A worker loads the assembly declaring your benchmarks into its own load context, performs attribute discovery, measures with the same engine as the host, and streams results back over the pipe. This has three key consequences:

- **`Main` does not re-run.**
- **Progress is live.** Warmup and measurement phases, detector snapshots, and results stream from the worker into your `IBenchmarkProgress` and `IMeasurementObserver` in real time. Per-sample observer events are the exception: they are only forwarded if `--stream-samples` is specified, as forwarding thousands of events would add measurable overhead. Full raw samples are included with each result. For more information, see the [Measurement Observer](../reference/observers.md#isolated-runs) table.
- **Results and samples arrive together.** The worker computes every statistic over the full sample array and ships the samples in the completion frame. This ensures `BenchmarkResult.RawSamples` is complete for significance testing.

### Budget ceilings

`Workers/MeasurementBudget` defines three ceilings to guard the worker:

- **Handshake:** 30 seconds.
- **Group:** Scales with the benchmark count.
- **Silence:** Based on the `IdleFrame`, derived from the per-benchmark ceiling rather than fixed at 30 seconds.

The silence ceiling is what actually catches a wedged worker, because the group ceiling grows with the benchmark count - fifty benchmarks tolerate over half an hour of nothing, and a flat 30 s would kill exactly the slow-but-honest benchmarks. It is derived rather than fixed because progress frames are emitted per completed sample, so a body whose single iteration consumes the entire tuning budget is legitimately silent for that duration.

The engine kills any worker that exceeds these ceilings, along with its entire process tree. Affected benchmarks are reported as errored with a timeout message rather than hanging the run.

A worker cannot outlive the run that started it. It reads its inbound pipe continuously; if the coordinator exits (due to a clean finish, Ctrl-C, crash, or IDE stop), the read ends, and the worker stops at the next sample and exits.

### Raw samples on the wire

Raw samples are bounded by `Workers/SampleReservoir` (default 4096 via `MeasurementOptions.MaxRawSamples`; increased via `--emit-raw`). Because the worker computes statistics over the complete array, this cap only affects the sample dump, the console sparkline, and coordinator-side significance testing. The engine draws a subset uniformly at random (not as a prefix) and seeds it from the run seed to ensure reproducibility. `BenchmarkResult.TrimmedOrdinals` is remapped onto this reduced array to ensure correct rendering.

## Addressing

### Token and name addressing

The engine uses two addressing modes:

- **By token** (`Body`, a `BodyRef`): The default and strongest guarantee. The metadata token and module version ID (MVID) uniquely identify the method in the specific build provided.
- **By name** (`DeclaringTypeFullName` + `MethodName`): Used when the target is a different build of the same source, such as in multi-runtime runs. Tokens are only meaningful within a single build, and MVIDs differ between target frameworks.

The engine uses exactly one mode. `IsWellFormed` rejects addresses that carry both, as resolving one could lead to running a different method than the one requested.

### Generic contexts

A metadata token names the open definition; for example, `Sort<int>` and `Sort<string>` share one token. `Workers/GenericArguments` handles the closed arguments. `BodyRef` carries two lists: `TypeGenericArguments` for the declaring type and `MethodGenericArguments` for the method. The engine closes the declaring type first, then re-resolves the method against it. The engine refuses arguments that are still type parameters because an open context cannot be closed.

The test-framework path (`TestMethodPayload`) applies the same treatment and refuses generic test classes and methods.

## Captured-state transfer

The engine sends a benchmark body's receiver to the measuring process only if every field can be transferred faithfully; otherwise, the body is refused, and the engine names the offending field. `Workers/StateTransfer` manages the rules, `BodyShape.TransferredReceiver` marks the shape, and `BodyRef.Captures` carries the values.

The engine prioritizes faithfulness over simple round-tripping. A closure that invokes but sends no values is not a fallback: a body over a captured `5` runs against `0` and returns a plausible, tight-intervalled result for the wrong number. The doctrine is that anything whose behavior is not determined by the bytes sent is refused - never guessed at.

"Faithful" transfer is stricter than "round-trips". A `Dictionary` with a custom comparer might round-trip its entries but change its lookup cost. Therefore, the engine inspects the comparer. The accepted set includes:
- Everything `TestArgumentCodec` accepts.
- Single-rank arrays, `List<>`, `IReadOnlyList<>`, `Memory<>`, and `KeyValuePair<,>` of accepted types.
- `Dictionary`, `HashSet`, `SortedDictionary`, and `SortedSet` using default comparers.
- User types marked with `[BenchmarkState]`.

The engine also refuses:
- Values where the runtime type differs from the declared field type.
- Multiple fields aliasing the same object.
- State exceeding `MeasurementOptions.MaxTransferredStateBytes`.

### The `[BenchmarkState]` attribute

`[BenchmarkState]` allows an author to assert that a type is faithful. This means nothing about the type's performance is carried outside its serialized data. Types holding open handles, warmed caches, or pooled buffers do not qualify, as they would measure differently upon arrival.

The attribute admits the type, but not necessarily its contents. Every member is still checked by the ordinary rules.

Members that the serializer cannot restore are refused because they would otherwise arrive at their default values:

| Member shape | Result without the check |
| --- | --- |
| Private field | Does not reach the payload |
| Public readonly field | Written to payload, discarded on return |
| Get-only property | Written to payload, discarded on return |

To fix this, use a public field or a property with a setter. Alternatively, avoid the attribute and use a preparation delegate to build the value in the measuring process.

The engine treats Roslyn display classes and user objects similarly. A captured `this` is recursed rather than serialized to ensure consistency. The walk is depth-bounded to prevent stack overflows on large user object graphs.

### Receivers and groups

`RunGroupPayload.Receivers` holds the distinct receivers a group's delegates close over. These are deduplicated by identity on the coordinator (`ReceiverTable`) and rehydrated once in the worker (`ResolvedReceivers`). `BodyRef.ReceiverIndex` links a delegate to its receiver.

This is not an optimization. Roslyn merges the captures of every lambda in a lexical scope into one display class, so a suite's bodies and its lifecycle hooks routinely close over one object, and whatever shares an object here shares it there. A `setup` hook that clears a buffer will clear the same buffer the body reads. Measured: `.Add("bump", () => counter[0]++)` beside `.Add("observe", () => counter[0])` showed `observe` a `4` in-process and a `0` in a worker. Identical source, two different programs, decided by whether a worker was available.

The engine transmits the receiver's runtime type (`StateTransfer.TransferredReceiver.TypeName`) to avoid issues with inherited methods. Without the runtime type, the worker might allocate the base class instead of the derived class, leading to incorrect measurements. For a method group over an inherited method - `turbo.Tick` where `Tick` is declared on the base - the walk on this side reads the fields of the derived object while the worker allocated the base; when the derived class adds no fields of its own, every token still resolves, every value still lands, and the benchmark measures the base class's overrides under the derived class's name. Naming the runtime type closes that, and it also settles the entry's type before any delegate arrives rather than leaving it to the run order.

Factories also carry captures. `AddressedFactory.TryCreate` uses the group's receiver table so that a prepare delegate and a body closing over the same local share one object in the worker.

### Per-iteration hooks

Hooks are addressed via `BodyRef.TryCreateHook` and carry no argument values. Instead, the worker binds the hook to the values the body's slots resolved to. For example, `setup: d => Shuffle(d)` shuffles the same array the body then sorts.

This allows for the canonical sort benchmark: `prepare` runs once, and the hook runs outside the timed window to reset the state for each sample. If a hook cannot be addressed, the benchmark loses isolation rather than dropping the hook, as missing setup would produce incorrect results.

### The `[BenchmarkPlan]` factory contract

For suites that cannot be described via serialization - such as those using live fixtures or custom containers - you can move the suite into a static factory and use `RunPlanAsync`. The worker invokes the factory in its own process, so the suite is constructed there.

```csharp
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

The factory must follow these constraints:

- **Must be `static` and capture nothing.** The worker locates it by metadata token and has no receiver to bind to.
- **Invoked multiple times.** It runs once in the coordinator (to read the baseline, reporters, and profile) and once per replicate in each measuring worker.
- **Must only wire delegates.** Do not perform the actual work in the factory, as it would run on top of the measurement. Use `WithSuiteSetup` or a `prepare` delegate for state creation.
- **Wrong shapes throw.** A method marked `[BenchmarkPlan]` that doesn't meet these requirements throws an exception to alert the author.

`RunPlansAsync(typeof(Plans))` runs every `[BenchmarkPlan]` on a type, each in its own worker.

## Recipes: factories on the wire

`Workers/AddressedFactory` is the mechanism for every recipe on the wire. Since values cannot cross process boundaries, the engine sends the instructions for building them instead. Recipes are used for:
- Body argument recipes (`ArgumentSource.Recipe`).
- Service-provider factories (`InstanceSource`).
- Statistical-strategy factories (`OutlierDetectorFactory`, `SignificanceTestFactory`).
- Suite factories (`RunGroupPayload.Plan`).

A factory can carry its own argument values via the `BodyRef` used to address it. This allows `prepare: (int size) => Build(size)` to work: the size is a recipe argument, and any captured locals travel in the receiver table.

### Argument sources

`Workers/ArgumentSource` determines how a body parameter gets its value: either as an encoded `Value` or a `Recipe` the worker invokes. `BodyRef.Arguments` lists these sources, aligned with the body's parameters.

This unified approach supports combinations that were previously impossible. The two slots replaced two separate slots on `BodyRef` - a list of encoded values and a single prepared-state factory - documented as mutually exclusive, because nothing could express the combinations in between, and four of the isolation gaps are those combinations: a second prepared value, a prepare delegate taking arguments of its own, a parameter sweep whose values are too complex to encode (`WithParameter("payload", ("small", () => …), ("large", () => …))`), and a sweep mixing the two. Per slot, all four are one shape and none needs a wire field of its own.

In a recipe-valued sweep, the engine names rows based on the label, not the value, because the value isn't known until the recipe runs in the worker. The recipe is not invoked in the coordinator for isolated runs to avoid unnecessary work.

### Factory resolution

The type the factory must produce is **not** carried. `NBenchmark.Worker/FactoryResolver` checks the caller's expected type against the resolved method's declared return type before invoking, and against the produced object afterwards - the resolved method's signature is a fact about the assembly on disk, where a carried type name would only be a claim about the far side. This is the same rule argument decoding follows.

`FactoryResolver` does not determine the policy for failures. For example, a failed statistical strategy might degrade to a built-in one, while a failed service provider must fault the entire group.

## Refusal classification

`Workers/Refusal` describes why a delegate could not be addressed as a value, not as prose. Three call sites used to recover it by searching the message - `refusal.Contains("captures")` - to choose between `InProcessCapturedState` and a structural status, which made every refusal string load-bearing: rewording one silently changes which remedy a user sees, and nothing in the build would say so. `Refusal.ToStatus(structural)` maps capture-related reasons to `InProcessCapturedState`, leaving the caller to name other refusals based on the current mode, because the honest name differs by mode - an inline suite's structural refusal is answered by a `[BenchmarkPlan]` factory and Single mode has no plan to point at.

### Isolation provenance

Every result includes an `IsolationStatus` indicating where it was measured and, if not isolated, why.

- **Requested In-Process:** `Isolated` and `InProcessRequested` occur when the user explicitly asks for the current state (e.g., via `--in-process` or `[InProcess]`).
- **Refusals:** `InProcessCapturedState`, `InProcessLiveFixture`, `InProcessUnaddressablePlan`, and `InProcessNoWorker` occur when the engine cannot isolate the run.

The `Iso` column in reports keys on refusals rather than just `!IsIsolated()`. Keying on `!IsIsolated()` treats a deliberate in-process run as a failure, which would put a column reading `no` on every row of an `--in-process` run and, because reporters trade the two, remove the bar column. This ensures that deliberate in-process runs don't look like failures. Conversely, the column renders whenever *anything* was refused rather than only when statuses are mixed: a table where nothing could be isolated has one distinct status, so the mixed rule suppressed it for exactly the run a reader is most likely to misread.

Errored rows have no provenance because they were not measured. This prevents errored rows from incorrectly flagging a run as non-isolated.

Provenance is communicated through five channels: the stderr refusal at the moment of refusal (`SingleModeGuidance` for Single/Suite/Plan, deduped per offender and bounded; `BenchmarkHarness.EmitIsolationRefusal` per class), the per-row stamp, the Console/Markdown `Iso` column with its remedy footer, a `Isolation` column in CSV at every detail level, and `BenchmarkResult.Print()`. JSON serializes the full record.

If a user explicitly requests isolation via `[IsolatedProcess]` and it is refused, the engine names the benchmarks that asked and adds a warning to their rows.

## Required isolation

A refusal is treated as an error. `MeasurementOptions.RequireIsolation` defaults to `true`, meaning a benchmark that cannot be isolated fails the run instead of falling back to the host process.

This default is acceptable because most isolation gaps (captured locals, prepared values, scoped containers) now support isolation.

`IsolationAudit.ThrowIfRequired` keys on `IsRefusal()`. This allows requests like `--dry-run` or `[InProcess]` to remain legal.

When a run fails due to isolation, the engine reports the benchmark, the structured refusal, the remedy, and the opt-out flag.

In harness mode, `BenchmarkHarness.ResolveIsolationPlan` answers "can a worker measure this?" for every discovered class before the first benchmark is measured, and reports every refusal in one message. The same call is made per class immediately before that class launches, which under a hard error is the difference between failing in a second and failing after classes 1..N-1 had already run. Every input to the decision except the assembly path is run-global, so one evaluation per assembly serves every class in it - in the ordinary single-assembly run, one evaluation for the set. This allows the engine to fail early.

`--strict-isolation` maps to `RequireIsolation` and audits the results. It previously set a CLI field with no mapping onto the options, so the flag could only ever take the expensive path - measure everything, then report - even though the early-throw mechanism it wanted already existed and the two are the same request phrased at different times.

`BenchmarkSuite.AddInProcess` provides a per-benchmark opt-out for suites. `WithIsolation(false)` is all-or-nothing, so one body holding a live object took every other benchmark in the suite into the host process with it - the price of measuring one un-isolatable thing was every comparison it was part of. The suite splits: addressable bodies go to a worker, while named bodies are measured in-process and stamped `InProcessRequested`, and the merged rows are put back into declaration order because a table's order is the one thing the author fully controls.

## See also

For more information, see:

- [Isolated runs](../features/isolated-runs.md) - The user-facing model
- [Multi-runtime](../features/multi-runtime.md) - Why this mode always isolates
- [Measurement Observer](../reference/observers.md#isolated-runs) - Data crossing the boundary and what does not
