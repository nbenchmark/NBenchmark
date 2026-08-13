---
title: Isolation Internals
description: How NBenchmark's process isolation actually works - worker discovery, addressing, the wire protocol, budget ceilings, and the captured-state transfer rules.
order: 1
---

# Isolation Internals

The [Isolated runs](../features/isolated-runs.md) page describes what isolation does for you. This page is the engineering underneath: how a worker is found and launched, how a benchmark body is addressed across a process boundary, what crosses the wire and under what rules, and how the run is kept from hanging on a wedged worker.

## The worker

The worker is a distinct executable, `nbworker`, that loads the *target* assembly by path - it is not a re-run of the entry assembly. A program with *M* isolated suites does *M* work, not *M²*, and any side effect in `Main` - a file write, an HTTP call, database seeding - happens once, in the host.

### Discovery

`Workers/WorkerLocator` finds `nbworker`, resolving **most-specific-first**:

1. An explicit path on the request (multi-runtime names another framework's worker).
2. The worker beside the *assembly under test*.
3. The worker beside the running application.

The middle step exists because the two differ under `dotnet benchmark --assembly`, where the target is a separate build with its own worker and the tool's directory has none - asking the application-wide question there costs the run its isolation silently, because the response to "no worker" is to measure in-process and carry on.

Both deployment layouts are checked (`dir/nbworker/nbworker.dll`, then `dir/nbworker.dll`); the subdirectory is the shipped one, since the worker's own `runtimeconfig.json` and `deps.json` describe a different program and must not sit beside the application's.

If the worker is missing - an incomplete restore, or `NBenchmarkDeployWorker=false` - the run fails with a message naming the directories that were searched. Set `NBENCHMARK_WORKER_PATH` to point at a specific `nbworker.dll` to override discovery, or `RequireIsolation = false` to accept a labelled host-process measurement instead.

### Shared framework configuration

`Workers/SharedFrameworkConfig` extends the worker's **shared framework set** from the target's `runtimeconfig.json` at launch. `nbworker` is a plain `Microsoft.NET.Sdk` app and declares only `Microsoft.NETCore.App`, so an assembly from a `Microsoft.NET.Sdk.Web` project - benchmarks living in the API they measure - cannot load in it: the ASP.NET assemblies are framework-provided, absent from the target's `deps.json` (so `AssemblyDependencyResolver` correctly resolves them to nothing) and expected on a TPA list the worker's process does not have.

The framework set is fixed by `hostfxr` before managed code runs, so the fix is a synthesized config passed as `dotnet exec --runtimeconfig`: the worker's own, with the target's extra frameworks appended. **Frameworks only** - `tfm`, `rollForward` and `configProperties` stay the worker's, because `RuntimeProfile` owns the worker's runtime configuration and importing the application's `System.GC.Server` would undo the thing the process boundary exists for. It is content-addressed in the temp directory and returns `null` for every target that needs nothing extra, which is every plain console target, so the ordinary launch is unchanged down to the command line. A self-contained target returns `null` too: its framework is in its own output directory, where its `deps.json` already resolves it.

The synthesized config joins the worker path and the profile in the `WorkerPrewarm` pool key, since a parked worker started without a framework can never serve a request that needs one.

The `dotnet benchmark` tool has the same problem about **itself**, one boundary earlier, and `NBenchmark.Tool/FrameworkRelaunch` applies the same resolver to it. It is the one mode that loads the assembly under test into its own process - discovery is reflection over real types, and the harness instantiates those types to build the run plan, so a `MetadataLoadContext` pass would have to be thrown away the moment the run started. `Assembly.LoadFrom` succeeds on a web target and the first `GetTypes()` throws. Since a process cannot change its own framework set, the tool re-execs itself under the merged config and forwards the child's exit code, guarded against recursion by the `NBENCHMARK_TOOL_RELAUNCHED` environment marker. A `--project` is built before the check and handed to the child as `--assembly`, so the build is not repeated; the requirements of every named target are unioned, because they all load into one process.

### Coordination

Coordination is a duplex pair of anonymous pipes carrying length-prefixed UTF-8 JSON frames (`Workers/WorkerProtocol`, `Workers/FrameChannel`). Benchmark bodies are addressed by `(assembly, MVID, metadata token)` rather than serialized (`Workers/BodyRef`, `NBenchmark.Worker/BodyResolver`).

A worker loads the assembly declaring your benchmarks into its own load context, runs the same attribute discovery the host would, measures with the same engine, and streams results back over the pipe. Three consequences are worth knowing:

- **Your `Main` does not re-run** - see above.
- **Progress is live.** Warmup and measurement phases, detector snapshots and results stream from the worker into your own `IBenchmarkProgress` and `IMeasurementObserver` as they happen. Per-*sample* observer events are the exception: they stop at the process boundary unless `--stream-samples` asks for them, because a benchmark emits them in the thousands and forwarding all of them would add measurable time to the run. The full raw samples arrive with each result either way. See [Measurement Observer](../reference/observers.md#what-an-isolated-run-delivers) for the per-callback table.
- **Results and their samples arrive together.** A worker computes every statistic over the full sample array and ships the samples on the completion frame, so `BenchmarkResult.RawSamples` is complete and significance testing reads them.

### Budget ceilings

Three ceilings guard a worker, all in `Workers/MeasurementBudget`:

- the **handshake** (30 s),
- the whole **group** (`For`, which scales with the benchmark count),
- **silence** (`IdleFrame`).

The idle one is what actually catches a wedged worker, because the group ceiling grows with the group - fifty benchmarks tolerate over half an hour of nothing. It is derived from the per-benchmark ceiling rather than fixed, because progress frames are emitted per completed sample, so a body whose single iteration runs the whole tuning budget is legitimately silent for that long.

A worker that never returns is killed, along with its whole process tree, once it exceeds the ceiling. The affected benchmarks are reported as errored, naming the timeout, rather than hanging the run.

A worker cannot outlive the run that started it, and does not keep measuring for a coordinator that is gone. It reads its inbound pipe continuously - while idle *and* while measuring - so if the coordinator exits for any reason (a clean finish, a Ctrl-C, a crash, an IDE stop button) the read ends, the worker stops at its next sample and exits on its own with a distinct exit code. Nothing supervises it, which matters because the supervisor would be the process most likely to have died.

### Raw samples on the wire

Raw samples are bounded on the wire by `Workers/SampleReservoir` (`MeasurementOptions.MaxRawSamples`, default 4096; `--emit-raw` lifts it). The worker computes every statistic over the complete array, so the cap cannot move a reported number; it bounds the sample dump, the Console sparkline and coordinator-side significance. The subset is drawn uniformly at random - not a prefix, which would be the slice nearest warmup - kept in measurement order, and seeded from the run seed so a repeat ships the same samples. `BenchmarkResult.TrimmedOrdinals` is remapped onto the reduced array; leaving it alone would mark the wrong samples and still render.

## Addressing

### By token, or by name

Two addressing modes answer two genuinely different questions. **By token** (`Body`, a `BodyRef`) is the default and the stronger guarantee: the metadata token plus the module version id names precisely the method the caller passed, in the build they passed it from. **By name** (`DeclaringTypeFullName` + `MethodName`) is for a target that is a *different build* of the same source, which is what a multi-runtime run measures - a token is only meaningful within the build that produced it, and the MVID that guards a stale token differs between two target frameworks' builds by construction, so token addressing cannot be made safe across them. Exactly one mode is set; `IsWellFormed` refuses an address carrying both, because resolving whichever branch is tested first would run one method while the request named the other.

### Generic contexts

A metadata token always names the **open** definition, so `Sort<int>` and `Sort<string>` share one token and neither can be invoked from it. `Workers/GenericArguments` names the closed arguments and `BodyRef` carries them in two lists - `TypeGenericArguments` for the declaring type, `MethodGenericArguments` for the method - because closing one does not close the other and `Box<int>.Compare<string>` needs both. The declaring type is closed first, since doing so re-resolves the method against it. Only an argument that is still a type *parameter* is refused, because an open context has no single answer to close it with.

The same treatment applies to the test-framework path (`TestMethodPayload`), which refused every generic test class and method outright - a limitation of the definition being read as one about the case being measured.

## Captured-state transfer

A benchmark body's receiver is sent to the measuring process when every field of it can be sent *faithfully*; otherwise the body is refused and the offending field is named. `Workers/StateTransfer` owns the rule, `BodyShape.TransferredReceiver` marks the shape, and `BodyRef.Captures` carries the values.

**The values are the difference.** A fabricated closure that invokes anyway, sending *no values*, is not a fallback: a body over a captured `5` runs against `0` and returns a plausible, tight-intervalled result for the wrong number. The doctrine is that anything whose behaviour is not determined by the bytes sent is refused - never guessed at.

**Faithful is stronger than "round-trips".** A `Dictionary<string,int>(StringComparer.OrdinalIgnoreCase)` round-trips into a dictionary with identical entries and different lookup cost, and no comparison of the data could ever catch it - so the rule inspects the comparer, not the entries. The accepted set is: everything `TestArgumentCodec` accepts; single-rank arrays, `List<>`, `IReadOnlyList<>`, `Memory<>`, `KeyValuePair<,>` of accepted types; `Dictionary`/`HashSet`/`SortedDictionary`/`SortedSet` **only** with a default comparer; and user types carrying `[BenchmarkState]`, which is the author asserting the same claim about their own type. Also refused: a value whose runtime type differs from its declared field type (an `IList<int>` holding a `MyPagedList` would flatten to a `List<int>`), two fields aliasing one object (rebuilding makes two objects where the benchmark sees one), and anything over `MeasurementOptions.MaxTransferredStateBytes`.

**One rule, two shapes.** A Roslyn display class and a user object a body captured `this` from are the same problem - a receiver with fields. A captured `this` is recursed rather than serialized, because a body capturing only `this` binds straight to the instance while adding one local interposes a display class holding `this` in a field; treating the second as an ordinary value would refuse what the first isolates, for a difference the user did not write. The walk is depth-bounded: display-class chains are short by construction, but a user object graph is not, and an unbounded walk overflowed the stack - a crash rather than a refusal.

### Receivers belong to the group, not to each address

`RunGroupPayload.Receivers` holds the distinct receivers a group's delegates close over, deduplicated by identity on the coordinator (`ReceiverTable`) and rehydrated exactly once in the worker (`ResolvedReceivers`); `BodyRef.ReceiverIndex` says which one a delegate binds to.

This is not an optimisation. Roslyn merges the captures of every lambda in a lexical scope into one display class, so a suite's bodies and its lifecycle hooks routinely close over one object - and a copy per address had the worker rebuild several where the coordinator has one. Measured: `.Add("bump", () => counter[0]++)` beside `.Add("observe", () => counter[0])` showed `observe` a `4` in-process and a `0` in a worker. Identical source, two different programs, decided by whether a worker was available. With one table, whatever shares an object here shares it there, and a `setup: () => Array.Clear(buffer)` hook clears the buffer its body actually reads.

The receiver's **runtime type is named on the wire** (`StateTransfer.TransferredReceiver.TypeName`). This used to be omitted, on the reasoning that every delegate sharing an entry shares its runtime type by construction, so the worker could take the type from whichever delegate reached the entry first. The premise is true and the conclusion did not follow: what the worker actually had to hand was the method's *declaring* type, and those are the same thing only when the method is declared on the object's own class. For a method group over an inherited method - `turbo.Tick` where `Tick` is declared on the base - the walk on this side reads the fields of the derived object while the worker allocated the base; when the derived class adds no fields of its own, every token still resolves, every value still lands, and the benchmark measures the base class's overrides under the derived class's name. Naming the type closes that, and it also settles the entry's type before any delegate arrives rather than leaving it to the run order.

**Factories carry captures too.** A factory is addressed by the same rule as a body, and its captured values cross the same way: `AddressedFactory.TryCreate` threads the group's receiver table through, so a prepare delegate and a body closing over the same local share one object in the worker. That is what lets `prepare: (int size) => Build(size)` cross - the `size` travels as the factory's own argument value, and `prepare: () => BuildData()` sends the local it closes over. What is still refused is anything the faithfulness rule declines - a live object (a stream, a built container, a collection with a custom comparer) has no byte-level answer that preserves its behaviour. `BodyRef.TryCreate` says this by taking the receiver table as a parameter: when no table exists there is nowhere to put captures, and a capture without one is refused.

### Per-iteration hooks over prepared state

A hook is addressed by `BodyRef.TryCreateHook` rather than `TryCreate`, and carries **no** argument values: the worker binds it to the values the *body's* slots resolved to. So `setup: d => Shuffle(d)` shuffles the array the body then sorts, where a hook carrying its own recipe would have built a second array and reset that one.

This is what makes the canonical sort benchmark writable. `prepare` runs once, so `d => Array.Sort(d)` sorts an already-sorted array from the second sample onward and reports the cost of doing nothing; the hook runs outside the timed window, on the body's own state. A hook that cannot be addressed costs the benchmark its isolation rather than being dropped - a body measured with its setup missing produces a plausible number for work that never happened.

## Recipes: factories on the wire

`Workers/AddressedFactory` is the single mechanism behind every **recipe** on the wire: the value cannot cross a process boundary, so the *instructions for building it* cross instead and the measuring process follows them. Five things use it - the argument recipes on a body (`ArgumentSource.Recipe`), the service-provider factory (`InstanceSource`), the two statistical-strategy factories (`OutlierDetectorFactory`, `SignificanceTestFactory`), and the `[BenchmarkPlan]` suite factory (`RunGroupPayload.Plan`).

A factory may carry **its own argument values**, on the `BodyRef` it is addressed by. A recipe is a body whose result is a parameter rather than a measurement, so it encodes its arguments the same way one does - which is what lets `prepare: (int size) => Build(size)` cross: the size rides as the recipe's own argument, and the local the prepare delegate closes over travels in the group's receiver table. What is still refused is a factory that is itself refused by the faithfulness rule - a live object it captures has no byte-level answer that preserves its behaviour.

### Argument sources

`Workers/ArgumentSource` says where **one** parameter of a body gets its value in the measuring process: an encoded `Value`, or a `Recipe` the worker invokes. `BodyRef.Arguments` is one list of these, aligned with the body's parameters.

The two slots replaced two separate slots on `BodyRef` - a list of encoded values and a single prepared-state factory - documented as mutually exclusive, because nothing could express the combinations in between, and four of the isolation gaps *are* those combinations: a second prepared value, a prepare delegate taking arguments of its own, a parameter sweep whose values are too complex to encode (`WithParameter("payload", ("small", () => …), ("large", () => …))`), and a sweep mixing the two. Per slot, all four are one shape and none needs a wire field of its own.

A recipe-valued sweep names its rows from the **label**, not from the value: a recipe has no value to ask until it runs, in the other process, and a benchmark's identity has to be settled before anything is measured or a stored baseline cannot match it. The recipe is not invoked in the coordinator on an isolated run - the worker builds its own - for the same reason the host-side service-provider resolvers are deferred.

### Factory resolution

The type the factory must produce is **not** carried. `NBenchmark.Worker/FactoryResolver` checks the caller's expected type against the resolved method's declared return type before invoking, and against the produced object afterwards - the resolved method's signature is a fact about the assembly on disk, where a carried type name would only be a claim about the far side. This is the same rule argument decoding follows.

`FactoryResolver` deliberately does not decide what a failure *means*. A statistical strategy that cannot be rebuilt degrades to the built-in one and the benchmark is still measurable; a service provider that cannot be rebuilt must fault the group, because constructing the benchmark type without its dependencies would measure a different object and report it under the caller's name. Those are different policies, so the resolver returns a result and each caller applies its own.

## Refusal classification

`Workers/Refusal` carries *why* a delegate could not be addressed as a value, not as prose. Three call sites used to recover it by searching the message - `refusal.Contains("captures")` - to choose between `InProcessCapturedState` and a structural status, which made every refusal string load-bearing: rewording one silently changes which remedy a user sees, and nothing in the build would say so. `Refusal.ToStatus(structural)` maps capture-shaped reasons to `InProcessCapturedState` and leaves the caller to name the rest, because the honest name differs by mode - an inline suite's structural refusal is answered by a `[BenchmarkPlan]` factory and Simple mode has no plan to point at.

### Isolation provenance

Every result carries an `IsolationStatus` saying where it was measured and, when it was not isolated, why. Six values, split by `IsRefusal()` into two groups that mean different things: `Isolated` and `InProcessRequested` are the user getting what they asked for (`--in-process`, `--dry-run`, `[InProcess]`, `Benchmark.RunInProcess`, `WithIsolation(false)`, the `--verify-isolation` comparison pass), while the four `InProcessCapturedState` / `InProcessLiveFixture` / `InProcessUnaddressablePlan` / `InProcessNoWorker` are refusals - the user asked for a worker, did not get one, and has something to act on. `IsRefusal()` and `ToRemedy()` answer the same question and are pinned against each other.

The distinction is load-bearing wherever the stamp is consumed. Keying on `!IsIsolated()` treats a deliberate in-process run as a failure, which is why the `Iso` column keys on refusal instead - "not isolated" would put a column reading `no` on every row of an `--in-process` run and, because reporters trade the two, remove the bar column. Conversely the column renders whenever *anything* was refused rather than only when statuses are mixed: a table where nothing could be isolated has one distinct status, so the mixed rule suppressed it for exactly the run a reader is most likely to misread.

**An errored row has no provenance.** A benchmark that threw was not measured in this process - it was not measured anywhere - so errored rows are excluded from `IsolationAudit.Enforce` and from both `BenchmarkTable` isolation predicates. Including them told the user their numbers carried the host's configuration when there were no numbers, and let one errored row in an otherwise isolated run flip the column flag.

Provenance reaches the reader through five channels: the stderr refusal at the moment of refusal (`SimpleModeGuidance` for Single/Suite/Plan, deduped per offender and bounded; `BenchmarkHarness.EmitIsolationRefusal` per class), the per-row stamp, the Console/Markdown `Iso` column with its remedy footer, a `Isolation` column in CSV at every detail level, and `BenchmarkResult.Print()`. JSON carries it by serializing the whole record.

**A denied explicit request says so.** `[IsolatedProcess]` being refused reads identically to a default being refused - same status, same label - so the class refusal names the benchmarks that asked, and each of their rows carries a warning. An author who decided this one benchmark mattered enough to say so is the reader most likely to act on the message.

## Required isolation

A refusal is an **error**. `MeasurementOptions.RequireIsolation` defaults to `true`, so a benchmark that asked for a worker and cannot have one fails the run rather than being measured in the host and labelled - the organising goal being that the in-process fallback is something a user asks for, never something that happens to them.

The default is only defensible because of what came before it. While a captured local, a prepared value, a scoped container and a parameter sweep over a non-scalar each cost a run its isolation, a hard error would have been a wall rather than a signal; those shapes cross now, so what remains under the gate is a small set with a one-line remedy each.

`IsolationAudit.ThrowIfRequired` keys on `IsRefusal()`, **never** on `!IsIsolated()`. That is the whole of what keeps the default acceptable: `--dry-run`, `--in-process`, `[InProcess]`, `Benchmark.RunInProcess`, `WithIsolation(false)` and `BenchmarkSuite.AddInProcess` all produce `InProcessRequested`, and every one of them stays legal. `IsolationAudit.Enforce` was retargeted the same way - keyed on `!IsIsolated()`, `--strict-isolation --dry-run` failed a build over a run that never intended to isolate anything.

The message names four things: the benchmark, the structured refusal, the remedy, and the opt-out. The last is not politeness. The gate is the default, so the first encounter with it is a run that produces numbers; a message that only says "no" turns a labelled fallback into a dead end.

**Harness mode decides for the whole run at once.** `BenchmarkHarness.ResolveIsolationPlan` answers "can a worker measure this?" for every discovered class before the first benchmark is measured, and reports every refusal in one message. The same call is made per class immediately before that class launches, which under a hard error is the difference between failing in a second and failing after classes 1..N-1 had already run. Every input to the decision except the assembly path is run-global, so one evaluation per assembly serves every class in it - in the ordinary single-assembly run, one evaluation for the set.

`--strict-isolation` maps onto `RequireIsolation` as well as auditing the results. It set a CLI field with no mapping onto the options, so the flag could only ever take the expensive path - measure everything, then report - even though the early-throw mechanism it wanted already existed and the two are the same request phrased at different times.

`BenchmarkSuite.AddInProcess` is the per-benchmark opt-out the suite surface was missing. `WithIsolation(false)` is all-or-nothing, so one body holding a live object took every other benchmark in the suite into the host process with it - the price of measuring one un-isolatable thing was every comparison it was part of. The suite splits: the addressable bodies go to a worker, the named ones are measured here and stamped `InProcessRequested`, and the merged rows are put back into declaration order because a table's order is the one thing the author fully controls.

## See also

- [Isolated runs](../features/isolated-runs.md) - the user-facing model
- [Multi-runtime](../features/multi-runtime.md) - the one mode that always isolates, and why
- [Measurement Observer](../reference/observers.md#what-an-isolated-run-delivers) - what crosses the boundary and what does not
