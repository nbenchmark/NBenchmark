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
        if (!TryAddressHook(suiteSetup, "WithSuiteSetup", out var setupRef, out var lifecycleRefusal))
            return Decision.Refuse(HookStatus(lifecycleRefusal), lifecycleRefusal!);

        if (!TryAddressHook(suiteTeardown, "WithSuiteTeardown", out var teardownRef, out lifecycleRefusal))
            return Decision.Refuse(HookStatus(lifecycleRefusal), lifecycleRefusal!);

        var bodies = new List<BodyRef>(benchmarks.Count);

        foreach (var benchmark in benchmarks)
        {
            if (benchmark.Body is null)
            {
                return Decision.Refuse(
                    IsolationStatus.InProcessUnaddressablePlan,
                    $"'{benchmark.Name}' was not added as a plain delegate, so there is no compiled "
                    + "method for a worker to address. Move the suite into a static [BenchmarkPlan] "
                    + "factory.");
            }

            if (!BodyRef.TryCreate(
                    benchmark.Body,
                    benchmark.Name,
                    out var bodyRef,
                    out var refusal,
                    benchmark.Arguments,
                    benchmark.StateFactory))
            {
                // Overwhelmingly the common case: the lambda captures a local. Naming the benchmark
                // matters, because a suite has several and only one of them is the problem.
                var status = refusal is not null && refusal.Contains("captures", StringComparison.Ordinal)
                    ? IsolationStatus.InProcessCapturedState
                    : IsolationStatus.InProcessUnaddressablePlan;

                return Decision.Refuse(
                    status,
                    $"'{benchmark.Name}' {refusal} Either remove the capture, or move the suite into "
                    + "a static [BenchmarkPlan] factory so the worker builds that state itself.");
            }

            if (!TryAddressHook(
                    benchmark.IterationSetup,
                    $"'{benchmark.Name}' per-iteration setup",
                    out var iterationSetup,
                    out var hookRefusal))
            {
                return Decision.Refuse(HookStatus(hookRefusal), hookRefusal!);
            }

            if (!TryAddressHook(
                    benchmark.IterationTeardown,
                    $"'{benchmark.Name}' per-iteration teardown",
                    out var iterationTeardown,
                    out hookRefusal))
            {
                return Decision.Refuse(HookStatus(hookRefusal), hookRefusal!);
            }

            bodies.Add(bodyRef with
            {
                IterationSetup = iterationSetup,
                IterationTeardown = iterationTeardown,
            });
        }

        return bodies.Count == 0
            ? Decision.Refuse(IsolationStatus.InProcessRequested, "the suite has no benchmarks.")
            : new Decision(IsolationStatus.Isolated, null, bodies)
            {
                SuiteSetup = setupRef,
                SuiteTeardown = teardownRef,
            };
    }

    /// <summary>
    ///     Addresses one lifecycle delegate, or explains why it cannot cross. A <c>null</c> hook is a
    ///     success with nothing to carry.
    /// </summary>
    private static bool TryAddressHook(
        Delegate? hook,
        string description,
        out BodyRef? addressed,
        out string? refusal)
    {
        addressed = null;
        refusal = null;

        if (hook is null)
            return true;

        if (BodyRef.TryCreate(hook, description, out var hookRef, out var hookRefusal))
        {
            addressed = hookRef;
            return true;
        }

        refusal = $"{description} {hookRefusal} A lifecycle delegate runs in the process that measures, "
                  + "so one holding state from here would prepare something the benchmarks never see. "
                  + "Move the suite into a static [BenchmarkPlan] factory so the worker builds that "
                  + "state itself.";

        return false;
    }

    /// <summary>
    ///     Classifies a hook refusal the same way a body refusal is classified, so a captured local in a
    ///     setup delegate reports the remedy for a capture rather than the generic one.
    /// </summary>
    private static IsolationStatus HookStatus(string? refusal)
        => refusal is not null && refusal.Contains("captures", StringComparison.Ordinal)
            ? IsolationStatus.InProcessCapturedState
            : IsolationStatus.InProcessUnaddressablePlan;

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
        BodyRef? suiteTeardown = null)
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

            Options = options,
            Order = order,
            Seed = seed,
            TotalBenchmarks = bodies.Count,
        }, options);
}
