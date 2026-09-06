using System.Diagnostics;
using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Decides whether a suite written inline - the ordinary
///     <c>new BenchmarkSuite(...).Add(...).RunAsync()</c> shape - can be measured in a worker
///     without the author restructuring anything.
/// </summary>
/// <remarks>
///     <para>
///         This exists so isolation costs nothing in ergonomics. Requiring a
///         <c>[BenchmarkPlan]</c> factory to get accurate numbers would make the accurate path the
///         inconvenient one, and people would reasonably keep writing the inconvenient-to-avoid,
///         quietly-wrong one. Each benchmark body is addressed individually, exactly as Single mode
///         addresses a lambda, and the whole set is measured in one worker.
///     </para>
///     <para>
///         A plan factory remains the answer for suites this cannot handle - anything holding live
///         objects the worker would have to be <i>given</i> rather than able to <i>build</i>. The
///         refusal says so, so the escape hatch is discovered at the moment it is needed rather than
///         imposed on everyone up front.
///     </para>
/// </remarks>
internal static class InlineSuitePlan
{
    /// <summary>What stops an inline suite from being measured in a worker.</summary>
    internal readonly record struct Decision(
        IsolationStatus Status,
        string? Explanation,
        IReadOnlyList<BodyRef> Bodies)
    {
        public bool CanIsolate => Status.IsIsolated();

        /// <summary>The suite's own setup, addressed, when it has one.</summary>
        public BodyRef? SuiteSetup { get; init; }

        /// <inheritdoc cref="SuiteSetup" />
        public BodyRef? SuiteTeardown { get; init; }

        /// <summary>The receivers the addressed delegates share, referenced by index.</summary>
        /// <summary>
        ///     The group's receiver table, carried rather than a snapshot of its entries so the
        ///     request builder can add a strategy factory's captures to the same one the bodies used.
        /// </summary>
        public ReceiverTable? Receivers { get; init; }

        /// <summary>
        ///     R5 part 2: benchmarks demoted to run in this process instead of costing the whole suite
        ///     its isolation, each with why. Only <see cref="TryAddressWithDemotion" /> ever populates
        ///     this - demotion is not a promise <see cref="MeasurementOptions.Isolation" /> makes,
        ///     so it only happens when isolation is best-effort.
        /// </summary>
        public IReadOnlyDictionary<string, string> DemotedNames { get; init; } = new Dictionary<string, string>();

        public static Decision Refuse(IsolationStatus status, string explanation) => new(status, explanation, []);
    }

    /// <summary>
    ///     Addresses every benchmark body in the suite - and its lifecycle delegates - or explains the
    ///     first thing that made it impossible.
    /// </summary>
    public static Decision TryAddress(
        IReadOnlyList<BenchmarkEnvelope> benchmarks,
        MeasurementOptions options,
        Delegate? suiteSetup,
        Delegate? suiteTeardown)
    {
        if (!WorkerLauncher.Current.IsAvailable)
        {
            return Decision.Refuse(
                IsolationStatus.InProcessNoWorker,
                "the measurement worker (nbworker) is not deployed alongside this application. "
                + $"Looked in {WorkerLocator.DescribeSearch()}.");
        }

        if (FrameChannel.TransportRefusal is { } transportRefusal)
            return Decision.Refuse(IsolationStatus.InProcessNoWorker, transportRefusal);

        if (WorkerRunPlan.UnrebuildableStrategy(options) is { } strategyRefusal)
        {
            return Decision.Refuse(
                IsolationStatus.InProcessUnaddressablePlan,
                $"{strategyRefusal} Move the suite into a static [BenchmarkPlan] factory so the "
                + "worker constructs it the same way you did.");
        }

        // Demotion is additive and opt-in by construction: Isolation.Required promises every benchmark
        // isolates or the run is refused, and demoting only the offenders would quietly break that
        // promise instead of honouring it loudly. The strict path below is untouched by R5 part 2 -
        // same single pass, same all-or-nothing refusal, so a suite that already depends on that
        // guarantee sees no change at all.
        return options.RequiresIsolation
            ? TryAddressStrict(benchmarks, options, suiteSetup, suiteTeardown)
            : TryAddressWithDemotion(benchmarks, options, suiteSetup, suiteTeardown);
    }

    /// <summary>
    ///     Today's all-or-nothing addressing, unchanged: every benchmark has to cross or the whole
    ///     suite is refused. What <see cref="TryAddress" /> uses under
    ///     <see cref="MeasurementOptions.Isolation" />.
    /// </summary>
    private static Decision TryAddressStrict(
        IReadOnlyList<BenchmarkEnvelope> benchmarks,
        MeasurementOptions options,
        Delegate? suiteSetup,
        Delegate? suiteTeardown)
    {
        // The suite's lifecycle is addressed rather than refused for existing. A setup that captures
        // still cannot cross - it would have to run here and prepare state the benchmarks never see -
        // but one that captures nothing is exactly as reproducible in the worker as the bodies are.
        // One table for the whole group, so a body and a hook that closed over the same local land on
        // one entry and the worker rehydrates it once. Anything sharing an object here shares it there.
        var receivers = new ReceiverTable(options.MaxTransferredStateBytes);

        if (!TryAddressHook(suiteSetup, "WithSuiteSetup", receivers, out var setupRef, out var lifecycleRefusal))
            return Decision.Refuse(HookStatus(lifecycleRefusal), lifecycleRefusal.Message);

        if (!TryAddressHook(suiteTeardown, "WithSuiteTeardown", receivers, out var teardownRef, out lifecycleRefusal))
            return Decision.Refuse(HookStatus(lifecycleRefusal), lifecycleRefusal.Message);

        var bodies = new List<BodyRef>(benchmarks.Count);

        // Every offender, not the first. A suite is addressed as a set - one worker measures all of
        // it - so a single un-addressable body costs the whole suite its isolation, and returning on
        // the first one made that a sequence of re-runs: fix body 2, discover body 5, fix body 5,
        // discover body 9. The set is what the user has to act on, so the set is what gets reported.
        var offenders = new List<string>();
        var firstStatus = IsolationStatus.Isolated;

        void Refused(IsolationStatus status, string message)
        {
            if (offenders.Count == 0)
                firstStatus = status;

            offenders.Add(message);
        }

        foreach (var benchmark in benchmarks)
        {
            if (benchmark.Body is null)
            {
                Refused(
                    IsolationStatus.InProcessUnaddressablePlan,
                    $"'{benchmark.Name}' was not added as a plain delegate, so there is no compiled "
                    + "method for a worker to address.");

                continue;
            }

            if (!BodyRef.TryCreate(
                    benchmark.Body,
                    benchmark.Name,
                    out var bodyRef,
                    out var refusal,
                    benchmark.Arguments,
                    benchmark.StateRecipes,
                    receivers))
            {
                // Naming the benchmark matters, because a suite has several and only some of them are
                // the problem.
                Refused(
                    refusal.ToStatus(IsolationStatus.InProcessUnaddressablePlan),
                    $"'{benchmark.Name}' {refusal.Message}");

                continue;
            }

            if (!TryAddressHook(
                    benchmark.SampleSetup,
                    $"'{benchmark.Name}' per-iteration setup",
                    receivers,
                    out var sampleSetup,
                    out var hookRefusal))
            {
                Refused(HookStatus(hookRefusal), hookRefusal.Message);

                continue;
            }

            if (!TryAddressHook(
                    benchmark.SampleTeardown,
                    $"'{benchmark.Name}' per-iteration teardown",
                    receivers,
                    out var sampleTeardown,
                    out hookRefusal))
            {
                Refused(HookStatus(hookRefusal), hookRefusal.Message);

                continue;
            }

            bodies.Add(bodyRef with
            {
                SampleSetup = sampleSetup,
                SampleTeardown = sampleTeardown,
            });
        }

        if (offenders.Count > 0)
            return Decision.Refuse(firstStatus, DescribeOffenders(offenders));

        return bodies.Count == 0
            ? Decision.Refuse(IsolationStatus.InProcessRequested, "the suite has no benchmarks.")
            : new Decision(IsolationStatus.Isolated, null, bodies)
            {
                SuiteSetup = setupRef,
                SuiteTeardown = teardownRef,
                Receivers = receivers,
            };
    }

    /// <summary>The suite's own lifecycle, as an <c>ownersOf</c> key that no benchmark name can collide with.</summary>
    private const string SuiteLifecycleOwner = "\0suite-lifecycle";

    /// <summary>
    ///     R5 part 2: addresses what can be addressed, and demotes only the benchmarks that cannot -
    ///     rather than costing the whole suite its isolation - by repeating the addressing pass with
    ///     the offenders excluded until a pass finds none. What <see cref="TryAddress" /> uses when
    ///     <see cref="MeasurementOptions.Isolation" /> is off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why repeating the pass, rather than fixing up one pass's table.</b> Addressing a body
    ///         that turns out to be excluded leaves its receiver's entries in the group's identity set
    ///         - the budget it spent, the objects it claimed - so re-attempting the *remaining* bodies
    ///         against that same table would answer against a table that never should have held them.
    ///         A fresh <see cref="ReceiverTable" /> per pass is the cheap alternative: rebuilding it a
    ///         few times costs nothing next to a worker launch, and it is the only way to guarantee the
    ///         table a pass succeeds with never held anything excluded.
    ///     </para>
    ///     <para>
    ///         <b>Why a collision cascades to everyone already sharing the index, not just the two
    ///         sides of it.</b> The guard the entry was blocked on: an offender sharing a receiver with
    ///         a kept body must not be split, because splitting them sends one shared object to the
    ///         worker as a rebuilt clone while the demoted side keeps mutating the live original - the
    ///         two stop seeing each other's writes with nothing in the result saying so. Cross-receiver
    ///         sharing is refused outright by design (see <see cref="StateTransfer.TryCaptureField" />,
    ///         the "as well as something else in this group" refusal), so every collision this ever
    ///         sees is exactly that shape - never two kept bodies wrongly split from each other, because
    ///         two kept bodies by construction do not collide.
    ///     </para>
    ///     <para>
    ///         <b>What this does not close.</b> A receiver's fields are walked in declaration order and
    ///         the walk returns at the first one that fails, so a body refused for an earlier, unrelated
    ///         field - a live <c>Stream</c>, say - never reaches a later field that would have collided
    ///         with a kept receiver. That collision goes undetected, and the two are split anyway. Left
    ///         open rather than closed by making the walk collect every field's outcome instead of the
    ///         first, which would change what every other refusal in this file reports its reason as.
    ///     </para>
    /// </remarks>
    private static Decision TryAddressWithDemotion(
        IReadOnlyList<BenchmarkEnvelope> benchmarks,
        MeasurementOptions options,
        Delegate? suiteSetup,
        Delegate? suiteTeardown)
    {
        var excluded = new HashSet<string>();
        var reasons = new Dictionary<string, string>();
        var firstStatus = IsolationStatus.Isolated;

        // Bounded by the number of names that could ever be excluded, plus the pass that finds none -
        // a hard stop rather than a loop trusted to converge on its own.
        for (var attempt = 0; attempt <= benchmarks.Count; attempt++)
        {
            var pass = TryAddressPass(benchmarks, options, suiteSetup, suiteTeardown, excluded);

            if (pass.LifecycleFailure is { } lifecycleFailure)
                return lifecycleFailure;

            if (pass.OffenderNames.Count == 0)
            {
                if (pass.Bodies.Count == 0)
                {
                    // Nothing addressed at all - demoting the lot is the same outcome a whole-suite
                    // refusal would have been, so report it the same way rather than inventing a
                    // second phrasing for "none of this isolates".
                    return excluded.Count == 0
                        ? Decision.Refuse(IsolationStatus.InProcessRequested, "the suite has no benchmarks.")
                        : Decision.Refuse(firstStatus, DescribeOffenders([.. reasons.Values]));
                }

                var decision = new Decision(IsolationStatus.Isolated, null, pass.Bodies)
                {
                    SuiteSetup = pass.SuiteSetup,
                    SuiteTeardown = pass.SuiteTeardown,
                    Receivers = pass.Receivers,
                };

                return excluded.Count == 0
                    ? decision
                    : decision with { DemotedNames = reasons };
            }

            // The suite's own setup or teardown is not a benchmark this can exclude - demoting a
            // per-benchmark body that shares a receiver with it would still send that receiver to the
            // worker, addressed, which is exactly the split the guard above exists to refuse. Falling
            // back to the all-or-nothing pass is the same safe default TryAddressStrict already is.
            if (pass.OffenderNames.Contains(SuiteLifecycleOwner))
                return TryAddressStrict(benchmarks, options, suiteSetup, suiteTeardown);

            if (excluded.Count == 0)
                firstStatus = pass.FirstOffenderStatus;

            foreach (var name in pass.OffenderNames)
            {
                excluded.Add(name);
                reasons[name] = pass.OffenderMessages[name];
            }
        }

        // Unreachable: OffenderNames only ever names something not yet in `excluded`, so `excluded`
        // grows by at least one every iteration that does not return, and there are only
        // benchmarks.Count names to exhaust.
        throw new UnreachableException("R5 part 2's demotion loop did not converge.");
    }

    private readonly record struct PassResult(
        IReadOnlyList<BodyRef> Bodies,
        BodyRef? SuiteSetup,
        BodyRef? SuiteTeardown,
        ReceiverTable Receivers,
        IReadOnlySet<string> OffenderNames,
        IReadOnlyDictionary<string, string> OffenderMessages,
        IsolationStatus FirstOffenderStatus,
        Decision? LifecycleFailure);

    /// <summary>
    ///     One addressing attempt against a fresh table, skipping every name in <paramref name="excluded" />
    ///     entirely - not addressing them at all, rather than addressing and then discarding, so their
    ///     budget and identity-set entries never exist for this pass's survivors to collide with.
    /// </summary>
    private static PassResult TryAddressPass(
        IReadOnlyList<BenchmarkEnvelope> benchmarks,
        MeasurementOptions options,
        Delegate? suiteSetup,
        Delegate? suiteTeardown,
        IReadOnlySet<string> excluded)
    {
        var receivers = new ReceiverTable(options.MaxTransferredStateBytes);

        if (!TryAddressHook(suiteSetup, "WithSuiteSetup", receivers, out var setupRef, out var lifecycleRefusal))
        {
            return new PassResult(
                [], null, null, receivers, new HashSet<string>(), new Dictionary<string, string>(),
                IsolationStatus.Isolated, Decision.Refuse(HookStatus(lifecycleRefusal), lifecycleRefusal.Message));
        }

        if (!TryAddressHook(suiteTeardown, "WithSuiteTeardown", receivers, out var teardownRef, out lifecycleRefusal))
        {
            return new PassResult(
                [], null, null, receivers, new HashSet<string>(), new Dictionary<string, string>(),
                IsolationStatus.Isolated, Decision.Refuse(HookStatus(lifecycleRefusal), lifecycleRefusal.Message));
        }

        // Which name(s) already own each receiver index committed so far this pass - the suite's own
        // lifecycle included, under a key no benchmark name can spell, so a collision with it is
        // findable the same way a collision with an ordinary benchmark is.
        var ownersOf = new Dictionary<int, List<string>>();

        AttributeOwner(ownersOf, setupRef, SuiteLifecycleOwner);
        AttributeOwner(ownersOf, teardownRef, SuiteLifecycleOwner);

        var bodies = new List<BodyRef>(benchmarks.Count);
        var offenderNames = new HashSet<string>();
        var offenderMessages = new Dictionary<string, string>();
        var firstStatus = IsolationStatus.Isolated;

        void Offend(string name, IsolationStatus status, string message)
        {
            if (!offenderNames.Add(name))
                return;

            if (offenderNames.Count == 1)
                firstStatus = status;

            offenderMessages[name] = message;
        }

        // Every name already sharing the colliding index is pulled in too, not just the one attempt
        // that happened to collide - see TryAddressWithDemotion's remarks on why splitting them is
        // never safe to do silently.
        void Cascade(int owner, string collidingName)
        {
            if (!ownersOf.TryGetValue(owner, out var owners))
                return;

            foreach (var ownerName in owners)
            {
                Offend(
                    ownerName,
                    IsolationStatus.InProcessCapturedState,
                    $"'{ownerName}' shares captured state with '{collidingName}', which cannot be "
                    + "addressed on its own - splitting them apart would send one shared object across "
                    + "as two, so neither isolates.");
            }
        }

        foreach (var benchmark in benchmarks)
        {
            if (excluded.Contains(benchmark.Name))
                continue;

            if (benchmark.Body is null)
            {
                Offend(
                    benchmark.Name,
                    IsolationStatus.InProcessUnaddressablePlan,
                    $"'{benchmark.Name}' was not added as a plain delegate, so there is no compiled "
                    + "method for a worker to address.");

                continue;
            }

            var checkpoint = receivers.Save();

            if (!BodyRef.TryCreate(
                    benchmark.Body,
                    benchmark.Name,
                    out var bodyRef,
                    out var refusal,
                    benchmark.Arguments,
                    benchmark.StateRecipes,
                    receivers))
            {
                receivers.Restore(checkpoint);
                Offend(benchmark.Name, refusal.ToStatus(IsolationStatus.InProcessUnaddressablePlan),
                    $"'{benchmark.Name}' {refusal.Message}");

                if (refusal.EntangledReceiverIndex is { } owner)
                    Cascade(owner, benchmark.Name);

                continue;
            }

            if (!TryAddressHook(
                    benchmark.SampleSetup,
                    $"'{benchmark.Name}' per-iteration setup",
                    receivers,
                    out var sampleSetup,
                    out var hookRefusal))
            {
                receivers.Restore(checkpoint);
                Offend(benchmark.Name, HookStatus(hookRefusal), hookRefusal.Message);

                if (hookRefusal.EntangledReceiverIndex is { } owner)
                    Cascade(owner, benchmark.Name);

                continue;
            }

            if (!TryAddressHook(
                    benchmark.SampleTeardown,
                    $"'{benchmark.Name}' per-iteration teardown",
                    receivers,
                    out var sampleTeardown,
                    out hookRefusal))
            {
                receivers.Restore(checkpoint);
                Offend(benchmark.Name, HookStatus(hookRefusal), hookRefusal.Message);

                if (hookRefusal.EntangledReceiverIndex is { } owner)
                    Cascade(owner, benchmark.Name);

                continue;
            }

            bodies.Add(bodyRef with
            {
                SampleSetup = sampleSetup,
                SampleTeardown = sampleTeardown,
            });

            AttributeOwner(ownersOf, bodyRef, benchmark.Name);
            AttributeOwner(ownersOf, sampleSetup, benchmark.Name);
            AttributeOwner(ownersOf, sampleTeardown, benchmark.Name);
        }

        // A benchmark cascaded into offending after this pass already recorded it as addressed - its
        // body is still sitting in `bodies`. That is fine: a non-empty OffenderNames means the caller
        // discards this pass's Bodies wholesale and reruns with the wider exclusion, so a stale entry
        // here is never read as a real answer.
        return new PassResult(
            bodies, setupRef, teardownRef, receivers, offenderNames, offenderMessages, firstStatus, null);
    }

    /// <summary>Records that <paramref name="owner" /> is (also) using <paramref name="addressed" />'s receiver.</summary>
    private static void AttributeOwner(Dictionary<int, List<string>> ownersOf, BodyRef? addressed, string owner)
    {
        if (addressed?.ReceiverIndex is not { } index)
            return;

        if (!ownersOf.TryGetValue(index, out var owners))
            ownersOf[index] = owners = [];

        owners.Add(owner);
    }

    /// <summary>
    ///     One message naming every body that cannot cross, and the remedy they share.
    /// </summary>
    /// <remarks>
    ///     A single offender reads as a sentence, which is what nearly every suite has. Several are
    ///     listed, because the reader has to change all of them before the suite isolates and finding
    ///     that out one re-run at a time is the friction this exists to remove.
    /// </remarks>
    private static string DescribeOffenders(IReadOnlyList<string> offenders)
    {
        const string Remedy = " Either build that state with a prepare delegate, or move the suite "
                              + "into a static [BenchmarkPlan] factory so the worker builds it itself.";

        if (offenders.Count == 1)
            return offenders[0] + Remedy;

        return $"{offenders.Count} of its benchmarks cannot be addressed: "
               + string.Join(" ", offenders)
               + " The whole suite is measured in one worker, so all of them have to cross for any of"
               + " them to."
               + Remedy;
    }

    /// <summary>
    ///     Addresses one lifecycle delegate, or explains why it cannot cross. A <c>null</c> hook is a
    ///     success with nothing to carry.
    /// </summary>
    private static bool TryAddressHook(
        Delegate? hook,
        string description,
        ReceiverTable receivers,
        out BodyRef? addressed,
        out Refusal refusal)
    {
        addressed = null;
        refusal = Refusal.None;

        if (hook is null)
            return true;

        // The same table the bodies use. A hook exists to act on the body's state, so it has to end up
        // bound to the body's receiver rather than a copy of it - which is exactly what a shared table
        // gives: they closed over one display class here, so they get one entry and one rehydration
        // there. Addressed against a private copy, `setup: () => Array.Clear(buffer)` would have
        // cleared a buffer the body never reads.
        if (BodyRef.TryCreateHook(hook, description, out var hookRef, out var hookRefusal, receivers))
        {
            addressed = hookRef;

            return true;
        }

        refusal = hookRefusal with
        {
            Message = $"{description} {hookRefusal.Message} A lifecycle delegate runs in the process "
                      + "that measures, so one holding state from here would prepare something the "
                      + "benchmarks never see. Move the suite into a static [BenchmarkPlan] factory so "
                      + "the worker builds that state itself.",
        };

        return false;
    }

    /// <summary>
    ///     Classifies a hook refusal the same way a body refusal is classified, so a captured local in a
    ///     setup delegate reports the remedy for a capture rather than the generic one.
    /// </summary>
    private static IsolationStatus HookStatus(Refusal refusal)
        => refusal.ToStatus(IsolationStatus.InProcessUnaddressablePlan);

    /// <summary>
    ///     Builds the request that measures all of an inline suite's bodies together in one worker.
    /// </summary>
    /// <param name="order">
    ///     The suite's configured run order, which the worker applies to the bodies it was sent.
    ///     Threaded through rather than baked in: the previous isolated path hardcoded declaration
    ///     order, so <see cref="RunOrder.Random" /> - the default - was silently discarded the moment
    ///     isolation was on, which is now always.
    /// </param>
    public static RunGroupPayload Request(
        string suiteName,
        IReadOnlyList<BodyRef> bodies,
        MeasurementOptions options,
        RunOrder order,
        int? seed,
        int replicate,
        BodyRef? suiteSetup = null,
        BodyRef? suiteTeardown = null,
        ReceiverTable? receivers = null)
        => WorkerRunPlan.WithStrategies(new RunGroupPayload
        {
            GroupId = $"suite:{suiteName}#{replicate}",
            Kind = WorkGroupKind.Lambdas,
            SuiteSetup = suiteSetup,
            SuiteTeardown = suiteTeardown,

            // Every body in a suite must come from one assembly for a single worker to load them.
            // In practice they are all written together in the same file; a suite spanning assemblies
            // still works, because the worker's resolver follows the target's dependency graph.
            TargetAssemblyPath = bodies[0].AssemblyPath,
            Bodies = bodies,
            Receivers = receivers?.Receivers ?? [],

            Options = options,
            Order = order,
            Seed = seed,
            TotalBenchmarks = bodies.Count,
        }, options, receivers);
}
