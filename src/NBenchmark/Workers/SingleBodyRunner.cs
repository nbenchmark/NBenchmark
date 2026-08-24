using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Runs one Single-mode body in a worker when it can be addressed, and in the host process when
///     it cannot.
///     <para>
///         Single mode is the entry point people reach for first - a lambda and a name - and
///         historically it was the least trustworthy mode in the library, because a lambda measured
///         in whatever process happened to be running inherits that process's JIT tiering. On bodies
///         of provably identical cost that produced a 3.27x spread and a 2.80x fabricated difference,
///         each with a tight confidence interval. The signatures are unchanged and the return is
///         still synchronous; what changed is where the measurement happens.
///     </para>
/// </summary>
internal static class SingleBodyRunner
{
    /// <summary>
    ///     Wall-clock ceiling for a single body. Derived from the tuning budget the same way a group
    ///     ceiling is, so a legitimately slow body is never killed for being slow.
    /// </summary>
    private static TimeSpan TimeoutFor(MeasurementOptions options)
        => MeasurementBudget.For(options, benchmarkCount: 1);

    /// <summary>
    ///     Measures <paramref name="body" />, isolating it when possible.
    /// </summary>
    /// <returns>
    ///     The outcome, and the status describing where it ran. Never throws for an un-isolatable
    ///     body: falling back is the designed behaviour, not an error.
    /// </returns>
    /// <param name="recipes">
    ///     Factories producing the body's arguments, aligned with its parameters and run in the worker
    ///     before warmup. <c>null</c> for the ordinary parameterless body.
    /// </param>
    public static async Task<(MeasurementOutcome Outcome, IsolationStatus Status)> RunAsync(
        string name,
        Delegate body,
        MeasurementOptions options,
        IBenchmarkProgress progress,
        Func<Task<MeasurementOutcome>> measureInProcess,
        CancellationToken cancellationToken,
        IReadOnlyList<StateRecipe?>? recipes = null,
        Delegate? iterationSetup = null,
        Delegate? iterationTeardown = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(measureInProcess);

        if (!TryPlan(
                body, name, options, recipes, iterationSetup, iterationTeardown,
                out var bodyRef, out var receivers, out var status, out var refusal))
        {
            IsolationAudit.ThrowIfRequired(options, name, status, refusal);
            SingleModeGuidance.EmitOnce(name, status, refusal);

            return (await measureInProcess().ConfigureAwait(false), status);
        }

        var request = WorkerRunPlan.WithStrategies(new RunGroupPayload
        {
            GroupId = $"single:{name}",
            Kind = WorkGroupKind.Lambdas,
            TargetAssemblyPath = bodyRef.AssemblyPath,
            Bodies = [bodyRef],
            Receivers = receivers?.Receivers ?? [],

            Options = options,
            TotalBenchmarks = 1,
        }, options, receivers);

        var group = await WorkerLauncher.Current.RunGroupAsync(
                request,
                progress,
                // Null because Single mode has no observer to forward, not because one is being
                // dropped: every Benchmark.Run/RunRaw overload takes an IBenchmarkProgress and none
                // takes an IMeasurementObserver, and MeasurementOptions carries no observer either.
                // Observers are attached per-run by BenchmarkSuite and BenchmarkHarness, which own a
                // suite lifecycle to scope the stream to. If Single mode ever grows one, it is threaded
                // through here.
                NullMeasurementObserver.Instance,
                TimeoutFor(options),
                cancellationToken)
            .ConfigureAwait(false);

        if (group.Results.Count == 1 && group.Faults.Count == 0)
        {
            var result = group.Results[0];
            var samples = group.RawSamples.GetValueOrDefault(result.Name, []);

            return (
                new MeasurementOutcome
                {
                    // The samples travelled beside the result on the wire; re-attaching them here
                    // restores the shape an in-process measurement would have produced, so callers
                    // cannot tell the difference.
                    Result = result with { RawSamples = samples },
                    RawSamples = samples,
                },
                IsolationStatus.Isolated);
        }

        // The worker could not deliver. Measuring in this process is still better than returning
        // nothing, but the result must not claim the fidelity it did not get - so it comes back
        // labelled, with the worker's own explanation attached as a warning on the row.
        var fault = group.Faults.FirstOrDefault()?.Message
                    ?? "the measurement worker returned no result.";

        // Gated like every other refusal. This one was missed because it is not an addressing decision
        // - the body crossed fine and the process on the other end died - but the outcome the caller
        // gets is identical to the ones that are gated, so leaving it open meant RequireIsolation held
        // for the failures that can be predicted and not for the ones that cannot.
        IsolationAudit.ThrowIfRequired(options, name, IsolationStatus.InProcessNoWorker, fault);

        SingleModeGuidance.EmitOnce(name, IsolationStatus.InProcessNoWorker, fault);

        var fallback = await measureInProcess().ConfigureAwait(false);

        return (
            fallback with
            {
                Result = fallback.Result with
                {
                    Warnings = [.. fallback.Result.Warnings, $"Measured in this process because {fault}"],
                },
            },
            IsolationStatus.InProcessNoWorker);
    }

    /// <summary>
    ///     Decides whether this body can be measured in a worker.
    /// </summary>
    private static bool TryPlan(
        Delegate body,
        string name,
        MeasurementOptions options,
        IReadOnlyList<StateRecipe?>? recipes,
        Delegate? iterationSetup,
        Delegate? iterationTeardown,
        out BodyRef bodyRef,
        out ReceiverTable? receiverTable,
        out IsolationStatus status,
        out string? refusal)
    {
        bodyRef = null!;
        receiverTable = null;

        if (!WorkerLauncher.Current.IsAvailable)
        {
            status = IsolationStatus.InProcessNoWorker;

            refusal = "the measurement worker (nbworker) is not deployed alongside this application. "
                      + $"Looked in {WorkerLocator.DescribeSearch()}.";

            return false;
        }

        if (FrameChannel.TransportRefusal is { } transportRefusal)
        {
            status = IsolationStatus.InProcessNoWorker;
            refusal = transportRefusal;

            return false;
        }

        // A pinned detector or significance test that a worker cannot rebuild would otherwise be
        // silently replaced by the built-in one, scoring the result under a method the caller did not
        // choose. Measuring here keeps the strategy they were explicit about.
        if (WorkerRunPlan.UnrebuildableStrategy(options) is { } strategyRefusal)
        {
            status = IsolationStatus.InProcessLiveFixture;
            refusal = strategyRefusal;

            return false;
        }

        // One body, so the table can only ever hold one entry - but Single mode still goes through it,
        // because a body and its receiver are addressed the same way in every mode and a second path
        // is a second thing to keep in step.
        var receivers = new ReceiverTable(options.MaxTransferredStateBytes);

        if (!BodyRef.TryCreate(
                body,
                name,
                out bodyRef,
                out var bodyRefusal,
                arguments: null,
                recipes,
                receivers))
        {
            // The reason is carried, not recovered from the message. This used to search the text for
            // the word "captures" to decide which remedy the user was shown, which made every refusal
            // string load-bearing prose.
            status = bodyRefusal.ToStatus(IsolationStatus.InProcessLiveFixture);
            refusal = bodyRefusal.Message;

            return false;
        }

        // Addressed after the body and against the same receiver table, so a hook and the body it
        // belongs to bind to one object rather than to a copy each. A hook that cannot cross costs the
        // benchmark its isolation rather than being dropped: a body measured without its setup reports
        // a plausible number for work that never happened.
        if (!TryAddressHook(iterationSetup, name, "setup", receivers, out var setupRef, out status, out refusal)
            || !TryAddressHook(
                iterationTeardown, name, "teardown", receivers, out var teardownRef, out status, out refusal))
        {
            return false;
        }

        bodyRef = bodyRef with { IterationSetup = setupRef, IterationTeardown = teardownRef };

        receiverTable = receivers;
        status = IsolationStatus.Isolated;
        refusal = null;

        return true;
    }

    private static bool TryAddressHook(
        Delegate? hook,
        string name,
        string role,
        ReceiverTable receivers,
        out BodyRef? addressed,
        out IsolationStatus status,
        out string? refusal)
    {
        addressed = null;
        status = IsolationStatus.Isolated;
        refusal = null;

        if (hook is null)
            return true;

        if (BodyRef.TryCreateHook(hook, $"{name} ({role})", out var hookRef, out var hookRefusal, receivers))
        {
            addressed = hookRef;

            return true;
        }

        status = hookRefusal.ToStatus(IsolationStatus.InProcessLiveFixture);
        refusal = $"its per-iteration {role} {hookRefusal.Message}";

        return false;
    }
}

/// <summary>
///     The once-per-process note explaining why a Single-mode benchmark was not isolated.
/// </summary>
/// <remarks>
///     <para>
///         Once per <i>offender</i> and per distinct reason, rather than once per call. Single mode is
///         used in loops and scripts; a message on every <c>Benchmark.Run</c> would be noise, and
///         noise is how a warning stops being read.
///     </para>
///     <para>
///         Keyed on the name as well as the status, because keying on the status alone made a script
///         with twenty <c>Benchmark.Run</c> calls - fifteen of them refused - print one line naming
///         the first offender and leave the other fourteen invisible outside
///         <see cref="BenchmarkResult.IsolationStatus" />. A reader would fix the one benchmark they
///         were told about and have no reason to think the rest were affected. Bounded by
///         <see cref="MaxReported" /> so a genuinely large loop still cannot flood stderr.
///     </para>
/// </remarks>
internal static class SingleModeGuidance
{
    internal const string SuppressEnvVar = "NBENCHMARK_SUPPRESS_ISOLATION_WARNING";

    /// <summary>
    ///     How many distinct offenders are named before the note falls back to counting them. High
    ///     enough to cover a hand-written suite, low enough that a generated loop cannot fill a
    ///     terminal.
    /// </summary>
    private const int MaxReported = 10;

    private static readonly HashSet<(string Name, IsolationStatus Status)> Reported = [];

    private static int _suppressedCount;

    public static void EmitOnce(string name, IsolationStatus status, string? explanation)
    {
        if (status.IsIsolated() || IsSuppressed())
            return;

        lock (Reported)
        {
            if (!Reported.Add((name, status)))
                return;

            if (Reported.Count > MaxReported)
            {
                // Said once, at the boundary, so the reader knows the list they are looking at is
                // not the whole list. Silence here would be indistinguishable from there being
                // nothing more to report.
                if (++_suppressedCount == 1)
                {
                    Console.Error.WriteLine(
                        $"Isolation: more than {MaxReported} benchmarks were not isolated; further "
                        + $"notes are suppressed. Every result carries its own status - set "
                        + $"{SuppressEnvVar}=1 to silence these entirely.");
                }

                return;
            }
        }

        Console.Error.WriteLine(
            $"Isolation: '{name}' was measured in this process because "
            + (explanation ?? "it could not be addressed across a process boundary."));

        if (status.ToRemedy() is { } remedy)
            Console.Error.WriteLine($"  To isolate it: {remedy}.");

        Console.Error.WriteLine(
            "  In-process results inherit this process's JIT tiering and GC configuration, are "
            + $"stamped 'host', and are never compared against isolated ones. Set {SuppressEnvVar}=1 "
            + "to silence this, or RuntimeProfile.Host to accept it deliberately.");
    }

    /// <summary>
    ///     Reports that a <c>[BenchmarkPlan]</c> factory could not be addressed, without claiming
    ///     anything about where the suite ends up being measured.
    /// </summary>
    /// <remarks>
    ///     The plan path falls back to measuring the suite as an inline one, where the bodies are
    ///     addressed individually - and that routinely succeeds where the factory did not, because a
    ///     factory capturing a local is refused while the non-capturing bodies it wires up are not.
    ///     <see cref="EmitOnce" />'s wording ("was measured in this process") would be false in
    ///     exactly that case, which is the case the plan API exists to serve.
    /// </remarks>
    public static void EmitPlanRefusal(string name, IsolationStatus status, string? explanation)
    {
        if (status.IsIsolated() || IsSuppressed())
            return;

        lock (Reported)
        {
            if (!Reported.Add((name, status)))
                return;
        }

        Console.Error.WriteLine(
            $"Isolation: the benchmark plan for '{name}' could not be addressed because "
            + (explanation ?? "it could not be addressed across a process boundary.")
            + " Measuring the suite as an inline suite instead; each result says where it ran.");

        if (status.ToRemedy() is { } remedy)
            Console.Error.WriteLine($"  To isolate the plan itself: {remedy}.");
    }

    internal static void ResetForTesting()
    {
        lock (Reported)
        {
            Reported.Clear();
            _suppressedCount = 0;
        }
    }

    private static bool IsSuppressed()
    {
        var value = Environment.GetEnvironmentVariable(SuppressEnvVar);

        return !string.IsNullOrEmpty(value)
               && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }
}
