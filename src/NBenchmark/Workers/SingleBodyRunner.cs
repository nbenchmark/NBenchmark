using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Runs one Simple-mode body in a worker when it can be addressed, and in the host process when
///     it cannot.
///     <para>
///         Simple mode is the entry point people reach for first - a lambda and a name - and
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
    public static async Task<(MeasurementOutcome Outcome, IsolationStatus Status)> RunAsync(
        string name,
        Delegate body,
        MeasurementOptions options,
        IBenchmarkProgress progress,
        Func<Task<MeasurementOutcome>> measureInProcess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(measureInProcess);

        if (!TryPlan(body, name, options, out var bodyRef, out var status, out var refusal))
        {
            SimpleModeGuidance.EmitOnce(name, status, refusal);

            return (await measureInProcess().ConfigureAwait(false), status);
        }

        var request = new RunGroupPayload
        {
            GroupId = $"single:{name}",
            Kind = WorkGroupKind.Lambdas,
            TargetAssemblyPath = bodyRef.AssemblyPath,
            Bodies = [bodyRef],

            // A worker measures once; Simple mode has no replicate concept of its own, so a
            // LaunchCount above 1 would silently multiply the work inside a single process rather
            // than giving the between-process estimate it implies.
            Options = options with { LaunchCount = 1 },
            OutlierDetectorTypeName = WorkerRunPlan.StrategyTypeName(options.OutlierDetector, out _),
            SignificanceTestTypeName = WorkerRunPlan.StrategyTypeName(options.SignificanceTest, out _),
            TotalBenchmarks = 1,
        };

        var group = await WorkerLauncher.Current.RunGroupAsync(
                request,
                progress,
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

        SimpleModeGuidance.EmitOnce(name, IsolationStatus.InProcessNoWorker, fault);

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
        out BodyRef bodyRef,
        out IsolationStatus status,
        out string? refusal)
    {
        bodyRef = null!;

        if (!WorkerLauncher.Current.IsAvailable)
        {
            status = IsolationStatus.InProcessNoWorker;

            refusal = "the measurement worker (nbworker) is not deployed alongside this application. "
                      + $"Looked in {WorkerLocator.DescribeSearch()}.";

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

        if (!BodyRef.TryCreate(body, name, out bodyRef, out refusal))
        {
            // A capturing body is by far the most common refusal here, and the one with a remedy the
            // user can act on, so it is distinguished from the rest.
            status = refusal is not null && refusal.Contains("captures", StringComparison.Ordinal)
                ? IsolationStatus.InProcessCapturedState
                : IsolationStatus.InProcessLiveFixture;

            return false;
        }

        status = IsolationStatus.Isolated;
        refusal = null;

        return true;
    }
}

/// <summary>
///     The once-per-process note explaining why a Simple-mode benchmark was not isolated.
/// </summary>
/// <remarks>
///     Once per process, and per distinct reason, rather than once per call. Simple mode is used in
///     loops and scripts; a message on every <c>Benchmark.Run</c> would be noise, and noise is how a
///     warning stops being read. The per-result <see cref="BenchmarkResult.IsolationStatus" /> stamp
///     carries the same information without competing for attention.
/// </remarks>
internal static class SimpleModeGuidance
{
    internal const string SuppressEnvVar = "NBENCHMARK_SUPPRESS_ISOLATION_WARNING";

    private static readonly HashSet<IsolationStatus> Reported = [];

    public static void EmitOnce(string name, IsolationStatus status, string? explanation)
    {
        if (status.IsIsolated() || IsSuppressed())
            return;

        lock (Reported)
        {
            if (!Reported.Add(status))
                return;
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

    internal static void ResetForTesting()
    {
        lock (Reported)
        {
            Reported.Clear();
        }
    }

    private static bool IsSuppressed()
    {
        var value = Environment.GetEnvironmentVariable(SuppressEnvVar);

        return !string.IsNullOrEmpty(value)
               && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }
}
