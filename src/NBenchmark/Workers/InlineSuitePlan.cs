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
///         quietly-wrong one. Each benchmark body is addressed individually, exactly as Simple mode
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
                    benchmark.IterationSetup,
                    $"'{benchmark.Name}' per-iteration setup",
                    receivers,
                    out var iterationSetup,
                    out var hookRefusal))
            {
                Refused(HookStatus(hookRefusal), hookRefusal.Message);

                continue;
            }

            if (!TryAddressHook(
                    benchmark.IterationTeardown,
                    $"'{benchmark.Name}' per-iteration teardown",
                    receivers,
                    out var iterationTeardown,
                    out hookRefusal))
            {
                Refused(HookStatus(hookRefusal), hookRefusal.Message);

                continue;
            }

            bodies.Add(bodyRef with
            {
                IterationSetup = iterationSetup,
                IterationTeardown = iterationTeardown,
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
