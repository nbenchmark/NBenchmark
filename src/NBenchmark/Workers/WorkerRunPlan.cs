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
    /// <summary>
    ///     The role names carried on every <see cref="AddressedFactory" /> this class builds.
    /// </summary>
    /// <remarks>
    ///     Constants rather than literals repeated at the addressing site and the refusal site,
    ///     because the worker phrases its diagnostics from the role and a coordinator that named the
    ///     same factory two ways would produce two vocabularies for one failure. The instance-source
    ///     roles live on <see cref="InstanceSource" />, beside the kinds they name.
    /// </remarks>
    public const string OutlierDetectorRole = "the outlier detector factory";

    public const string SignificanceTestRole = "the significance test factory";

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
    /// <param name="instanceSource">
    ///     How the harness resolves benchmark instances, or <c>null</c> when it constructs them
    ///     directly. A source carrying an addressable recipe is no obstacle to isolation - the worker
    ///     runs the recipe and builds an equivalent container or factory in the process that measures.
    /// </param>
    /// <param name="options">
    ///     The measurement configuration, so a strategy object that cannot be rebuilt in a worker is
    ///     caught here rather than silently downgraded. <c>null</c> skips the check.
    /// </param>
    public static Decision ForDiscoveredClass(
        string? declaringAssemblyLocation,
        InstanceSource? instanceSource = null,
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

        // A worker exists but this process cannot talk to it. Grouped with "not deployed" because the
        // consequence is identical - no child process is available - and answered before launching
        // rather than after, since the alternative is one dead worker per group with a fault the
        // coordinator cannot connect to its cause.
        if (FrameChannel.TransportRefusal is { } transportRefusal)
            return new Decision(Refusal.WorkerNotDeployed, transportRefusal);

        if (string.IsNullOrEmpty(declaringAssemblyLocation))
        {
            return new Decision(
                Refusal.NoAssemblyOnDisk,
                "the assembly declaring these benchmarks has no file on disk (a single-file or "
                + "in-memory build), so a worker has nothing to load.");
        }

        // The source answers for itself: it knows whether it holds a live object or an addressable
        // recipe, which the two unrelated fields this replaced could not express between them.
        if (instanceSource?.Refusal() is { } sourceRefusal)
            return new Decision(Refusal.LiveInstanceFactory, sourceRefusal);

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

        // A factory answers the question outright: the worker runs it and gets the caller's own object,
        // arguments and all, so there is nothing about the strategy left to refuse.
        var candidates = new (object? Strategy, Delegate? Factory, string Role)[]
        {
            (options.OutlierDetector, options.OutlierDetectorFactory, OutlierDetectorRole),
            (options.SignificanceTest, options.SignificanceTestFactory, SignificanceTestRole),
        };

        foreach (var (strategy, factory, role) in candidates)
        {
            if (factory is not null)
            {
                // The factory itself still has to be addressable. One that captures is refused for the
                // same reason a capturing body is - and saying so names the actionable fix, which is to
                // make the factory static.
                if (!AddressedFactory.TryCreate(factory, role, out _, out var factoryRefusal))
                {
                    return $"the factory supplied for '{strategy?.GetType().Name ?? "a custom strategy"}' "
                           + $"{factoryRefusal}";
                }

                continue;
            }

            _ = StrategyTypeName(strategy, out var refusal);

            if (refusal is not null)
                return refusal;
        }

        return null;
    }

    /// <summary>
    ///     Fills in every way a custom statistical strategy can reach a worker - type name or factory
    ///     address - on a request being built.
    /// </summary>
    /// <remarks>
    ///     One helper rather than four assignments repeated at five request-building sites. The repo has
    ///     already paid for that shape once: two call sites disagreeing about how raw samples were keyed
    ///     silently emptied every isolated result's sample array. Adding a fifth field here cannot be
    ///     forgotten at one site, because there is only one site.
    /// </remarks>
    public static RunGroupPayload WithStrategies(RunGroupPayload payload, MeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);

        return payload with
        {
            OutlierDetectorTypeName = StrategyTypeName(options.OutlierDetector, out _),
            SignificanceTestTypeName = StrategyTypeName(options.SignificanceTest, out _),
            OutlierDetectorFactory = AddressedFactory.OrNull(options.OutlierDetectorFactory, OutlierDetectorRole),
            SignificanceTestFactory = AddressedFactory.OrNull(options.SignificanceTestFactory, SignificanceTestRole),
        };
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
        InstanceSourcePayload? instanceSource = null,
        InstanceLifetime? instanceLifetimeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(declaringType);

        return WithStrategies(
            new RunGroupPayload
            {
                GroupId = $"{declaringType.FullName}#{replicate}",
                Kind = WorkGroupKind.DiscoveredClass,
                TargetAssemblyPath = declaringType.Assembly.Location,
                DeclaringTypeFullName = declaringType.FullName,
                BenchmarkNames = benchmarkNames,

                Options = options,
                Order = order,
                Seed = DeriveSeed(sessionSeed, replicate),
                DisplayPrefix = declaringType.Name,
                DefaultInstanceLifetime = defaultInstanceLifetime,
                InstanceLifetimeOverride = instanceLifetimeOverride,
                StartIndex = startIndex,
                TotalBenchmarks = totalBenchmarks,
                InstanceSource = instanceSource,
            },
            options);
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
