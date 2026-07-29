using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Decides whether a group of benchmarks can be measured in a worker, and says why not when it
///     cannot.
///     <para>
///         The rule throughout is <b>refuse rather than guess</b>. A worker does not re-run the
///         user's entry point, so anything the coordinator holds as live code - an instance factory,
///         a service provider, a captured local - has no counterpart in the worker. Substituting
///         something plausible was probed and produced measurements that were silently <i>wrong</i>
///         rather than absent: a body over a captured <c>5</c> returned a result for <c>1</c>, with no
///         error and a tight confidence interval. Declining, and labelling the result, is the only
///         honest option.
///     </para>
/// </summary>
internal static class WorkerRunPlan
{
    /// <summary>Why a group is being measured in the host process instead of a worker.</summary>
    internal enum Refusal
    {
        /// <summary>No refusal - the group can be isolated.</summary>
        None = 0,

        /// <summary>The worker is not deployed next to the application.</summary>
        WorkerNotDeployed,

        /// <summary>The benchmarks were requested to run in-process, so nothing was refused.</summary>
        RequestedInProcess,

        /// <summary>
        ///     Instances come from a user-supplied factory or service provider. A worker can
        ///     construct a type, but it cannot reproduce a factory that lives in the coordinator's
        ///     object graph.
        /// </summary>
        LiveInstanceFactory,

        /// <summary>The declaring assembly has no file on disk to load (single-file or in-memory).</summary>
        NoAssemblyOnDisk,

        /// <summary>
        ///     A custom outlier detector or significance test cannot be rebuilt in a worker, because
        ///     only its type name crosses the boundary and it needs constructor arguments.
        /// </summary>
        UnrebuildableStrategy,
    }

    internal readonly record struct Decision(Refusal Refusal, string? Explanation)
    {
        public bool CanIsolate => Refusal == Refusal.None;

        public static Decision Allow() => new(Refusal.None, null);

        /// <summary>
        ///     The status to stamp on results this decision sends to the host process, so the reason
        ///     travels with the numbers rather than living only in a console message that scrolls by.
        /// </summary>
        public IsolationStatus Status => Refusal switch
        {
            Refusal.None => IsolationStatus.Isolated,
            Refusal.WorkerNotDeployed => IsolationStatus.InProcessNoWorker,
            Refusal.LiveInstanceFactory => IsolationStatus.InProcessLiveFixture,
            Refusal.NoAssemblyOnDisk => IsolationStatus.InProcessUnaddressablePlan,
            Refusal.UnrebuildableStrategy => IsolationStatus.InProcessLiveFixture,
            _ => IsolationStatus.InProcessRequested,
        };
    }

    /// <summary>
    ///     Whether a discovered class can be measured in a worker.
    /// </summary>
    /// <param name="declaringAssemblyLocation">
    ///     File path of the assembly declaring the benchmarks, or empty when it has none.
    /// </param>
    /// <param name="usesInstanceFactory">
    ///     Whether the harness resolves instances through <c>WithInstanceFactory</c> or
    ///     <c>WithServiceProvider</c>.
    /// </param>
    /// <param name="options">
    ///     The measurement configuration, so a strategy object that cannot be rebuilt in a worker is
    ///     caught here rather than silently downgraded. <c>null</c> skips the check.
    /// </param>
    public static Decision ForDiscoveredClass(
        string? declaringAssemblyLocation,
        bool usesInstanceFactory,
        MeasurementOptions? options = null)
    {
        // Asked about the assembly under test, not about this application. Those differ under
        // `dotnet benchmark --assembly`, where the target build has its own worker beside it and the
        // tool's directory has none - the application-wide question answers "no worker" there and
        // silently costs the run its isolation.
        if (!WorkerLauncher.Current.IsAvailableFor(declaringAssemblyLocation))
        {
            return new Decision(
                Refusal.WorkerNotDeployed,
                "the measurement worker (nbworker) is not deployed alongside this application or the "
                + "assembly under test, so no child process is available to control JIT tiering or GC "
                + "flavour. It normally arrives with the NBenchmark package; looked in "
                + $"{WorkerLocator.DescribeSearch(declaringAssemblyLocation)}.");
        }

        if (string.IsNullOrEmpty(declaringAssemblyLocation))
        {
            return new Decision(
                Refusal.NoAssemblyOnDisk,
                "the assembly declaring these benchmarks has no file on disk (a single-file or "
                + "in-memory build), so a worker has nothing to load.");
        }

        if (usesInstanceFactory)
        {
            return new Decision(
                Refusal.LiveInstanceFactory,
                "benchmark instances come from an instance factory or service provider, which is live "
                + "code in this process and cannot be reproduced in a worker. Constructing the type "
                + "directly instead would measure a differently-configured object and report it as "
                + "though nothing had changed.");
        }

        if (options is not null && UnrebuildableStrategy(options) is { } strategyRefusal)
            return new Decision(Refusal.UnrebuildableStrategy, strategyRefusal);

        return Decision.Allow();
    }

    /// <summary>
    ///     The reason a pinned outlier detector or significance test cannot be rebuilt in a worker,
    ///     or <c>null</c> when both can be.
    /// </summary>
    /// <remarks>
    ///     Only a type name crosses the boundary, so a strategy built with constructor arguments
    ///     cannot be reconstructed there - and the worker falls back to the built-in one. That is the
    ///     quietest failure available: the body is measured correctly and then scored under a
    ///     different statistical method than the caller asked for, with nothing in the output saying
    ///     so. Declining to isolate keeps the caller's own strategy, which is the thing they were
    ///     explicit about.
    /// </remarks>
    public static string? UnrebuildableStrategy(MeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var strategy in new object?[] { options.OutlierDetector, options.SignificanceTest })
        {
            _ = StrategyTypeName(strategy, out var refusal);

            if (refusal is not null)
                return refusal;
        }

        return null;
    }

    /// <summary>
    ///     Builds a request for one replicate of a discovered-class group.
    /// </summary>
    /// <param name="replicate">
    ///     The 0-based replicate index. Each replicate is a fresh worker with a distinct shuffle
    ///     seed, which is what turns run order into a randomized nuisance factor rather than a fixed
    ///     confound - the previous isolated path hardcoded declaration order and silently discarded
    ///     <see cref="RunOrder.Random" /> whenever isolation was on, which in Harness mode is always.
    /// </param>
    public static RunGroupPayload DiscoveredClassRequest(
        Type declaringType,
        IReadOnlyList<string> benchmarkNames,
        MeasurementOptions options,
        InstanceLifetime defaultInstanceLifetime,
        RunOrder order,
        int? sessionSeed,
        int replicate,
        int startIndex,
        int totalBenchmarks,
        string? outlierDetectorTypeName,
        string? significanceTestTypeName)
    {
        ArgumentNullException.ThrowIfNull(declaringType);

        return new RunGroupPayload
        {
            GroupId = $"{declaringType.FullName}#{replicate}",
            Kind = WorkGroupKind.DiscoveredClass,
            TargetAssemblyPath = declaringType.Assembly.Location,
            DeclaringTypeFullName = declaringType.FullName,
            BenchmarkNames = benchmarkNames,

            // LaunchCount is the replicate count and is spent by the coordinator spawning workers,
            // so each worker measures exactly once. Leaving it above 1 here would multiply the two.
            Options = options with { LaunchCount = 1 },
            OutlierDetectorTypeName = outlierDetectorTypeName,
            SignificanceTestTypeName = significanceTestTypeName,
            Order = order,
            Seed = DeriveSeed(sessionSeed, replicate),
            DisplayPrefix = declaringType.Name,
            DefaultInstanceLifetime = defaultInstanceLifetime,
            StartIndex = startIndex,
            TotalBenchmarks = totalBenchmarks,
        };
    }

    /// <summary>
    ///     Derives a per-replicate seed from the session seed, so each replicate shuffles
    ///     differently while the whole run stays reproducible from one number. Returns <c>null</c>
    ///     when no session seed was pinned, letting each replicate pick its own.
    /// </summary>
    internal static int? DeriveSeed(int? sessionSeed, int replicate)
    {
        if (sessionSeed is not { } seed)
            return null;

        // A cheap avalanche rather than seed+replicate, so consecutive replicates do not produce
        // correlated shuffles from adjacent seeds.
        unchecked
        {
            var mixed = (uint)seed * 2654435761u ^ (uint)replicate * 2246822519u;
            mixed ^= mixed >> 15;
            mixed *= 2654435761u;
            mixed ^= mixed >> 13;

            return (int)(mixed & 0x7FFFFFFF);
        }
    }

    /// <summary>
    ///     The assembly-qualified type name of a strategy object, or <c>null</c> when there is none
    ///     to send. A strategy that cannot be reconstructed from a type name alone - one built with
    ///     constructor arguments or holding captured state - is reported so the caller can decline to
    ///     isolate rather than quietly measuring under a different statistical method.
    /// </summary>
    public static string? StrategyTypeName(object? strategy, out string? refusal)
    {
        refusal = null;

        if (strategy is null)
            return null;

        var type = strategy.GetType();

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            refusal = $"'{type.Name}' has no parameterless constructor, so it cannot be rebuilt in a "
                      + "worker; only its type name can cross the process boundary.";

            return null;
        }

        return type.AssemblyQualifiedName;
    }
}
