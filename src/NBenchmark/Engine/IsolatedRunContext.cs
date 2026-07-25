namespace NBenchmark.Engine;

/// <summary>
///     Distinguishes the two kinds of isolated run that share the unified child launcher.
/// </summary>
internal enum IsolatedRunKind
{
    /// <summary>A suite launched via <c>BenchmarkSuite.WithIsolation()</c>.</summary>
    Suite,

    /// <summary>A discovered Host-mode class running under isolated-by-default execution.</summary>
    Host,
}

/// <summary>
///     The resolved per-benchmark isolation decision in Harness mode, after layering the
///     global <c>--in-process</c> flag on top of the discovered <c>IsolationMode</c>.
/// </summary>
internal enum IsolationDecision
{
    /// <summary>Run in the host process.</summary>
    InProcess,

    /// <summary>Run with the rest of its declaring class in one shared child process.</summary>
    PerClass,

    /// <summary>Run alone in its own dedicated child process.</summary>
    PerBenchmark,
}

/// <summary>
///     The scalar measurement overrides forwarded to a Host-mode child. The child rebuilds
///     its base <see cref="MeasurementOptions" /> by re-running the same entry point (so
///     custom detectors and significance tests survive), then applies these CLI-derived
///     scalars on top - mirroring what the parent applies in-process.
/// </summary>
internal sealed record MeasurementOverrides
{
    public int? Iterations { get; init; }
    public int? WarmupIterations { get; init; }
    public int? OpsPerSample { get; init; }
    public double? ConfidenceLevel { get; init; }
    public double? SignificanceLevel { get; init; }
    public OutlierMode? OutlierMode { get; init; }
    public TailMetricsBasis? TailMetricsBasis { get; init; }
    public MeasurementProfile? Profile { get; init; }
    public bool? ForceGc { get; init; }
    public bool? NoAllocations { get; init; }
    public bool? NoGcBetweenBenchmarks { get; init; }
    public double? MinPracticalEffect { get; init; }

    // Auto-tune scalar overrides. AutoTuneOptions is rebuilt by the child re-running its entry
    // point, so only these CLI-derived deltas travel; the object itself is never serialized.
    public AutoTunePreset? Preset { get; init; }
    public double? CiTarget { get; init; }
    public int? MinSamples { get; init; }
    public int? MaxSamples { get; init; }
    public int? MinWarmup { get; init; }
    public int? MaxWarmup { get; init; }
    public TimeSpan? MaxTuningTime { get; init; }

    public AutoTuneCapBehavior? CapBehavior { get; init; }

    public double? WarmupBudgetFraction { get; init; }

    public double? CapGraceFactor { get; init; }

    public TimeSpan? MinWarmupTime { get; init; }

    public bool? NoJitQuiescence { get; init; }

    public TimeSpan? JitQuietPeriod { get; init; }

    public TimeSpan? MinMeasurementTime { get; init; }

    public double? DriftTolerance { get; init; }

    public int? MaxDriftRestarts { get; init; }

    public int? LaunchCount { get; init; }

    public IReadOnlyList<double>? ReportedPercentiles { get; init; }

    public bool? NoHistogram { get; init; }

    public DiagnosticsMode? Diagnostics { get; init; }

    /// <summary>
    ///     Environment controls forwarded to a Host-mode child so it can pin itself to
    ///     the same cores and priority as the parent. The child re-runs the entry point
    ///     to rebuild its base options, then applies these deltas on top - mirroring what
    ///     the parent applies in-process. <c>null</c> means the parent set nothing.
    /// </summary>
    public EnvironmentOptions? Environment { get; init; }

    public static MeasurementOverrides FromCliArgs(CliArgs cliArgs) => new()
    {
        Iterations = cliArgs.Iterations,
        WarmupIterations = cliArgs.WarmupIterations,
        OpsPerSample = cliArgs.OpsPerSample,
        ConfidenceLevel = cliArgs.ConfidenceLevel,
        SignificanceLevel = cliArgs.Alpha,
        OutlierMode = cliArgs.OutlierMode,
        TailMetricsBasis = cliArgs.TailMetricsBasis,
        Profile = cliArgs.Profile,
        ForceGc = cliArgs.ForceGc,
        NoAllocations = cliArgs.NoAllocations,
        NoGcBetweenBenchmarks = cliArgs.NoGcBetweenBenchmarks ? true : null,
        MinPracticalEffect = cliArgs.MinPracticalEffect,
        Preset = cliArgs.AutoTunePreset,
        CiTarget = cliArgs.CiTarget,
        MinSamples = cliArgs.MinSamples,
        MaxSamples = cliArgs.MaxSamples,
        MinWarmup = cliArgs.MinWarmup,
        MaxWarmup = cliArgs.MaxWarmup,
        MaxTuningTime = cliArgs.MaxTuningTime,
        CapBehavior = cliArgs.AutoTuneCapBehavior,
        WarmupBudgetFraction = cliArgs.WarmupBudgetFraction,
        CapGraceFactor = cliArgs.CapGraceFactor,
        MinWarmupTime = cliArgs.MinWarmupTime,
        NoJitQuiescence = cliArgs.NoJitQuiescence,
        JitQuietPeriod = cliArgs.JitQuietPeriod,
        MinMeasurementTime = cliArgs.MinMeasurementTime,
        DriftTolerance = cliArgs.DriftTolerance,
        MaxDriftRestarts = cliArgs.MaxDriftRestarts,
        LaunchCount = cliArgs.LaunchCount,
        ReportedPercentiles = cliArgs.ReportedPercentiles,
        NoHistogram = cliArgs.NoHistogram,
        Diagnostics = cliArgs.Diagnostics,
        Environment = BuildEnvironmentFromCli(cliArgs),
    };

    public MeasurementOptions Apply(MeasurementOptions options)
    {
        var result = options;

        if (Iterations.HasValue)
            result = result with { Iterations = Iterations.Value };

        if (WarmupIterations.HasValue)
            result = result with { WarmupIterations = WarmupIterations.Value };

        if (ConfidenceLevel.HasValue)
            result = result with { ConfidenceLevel = ConfidenceLevel.Value };

        if (SignificanceLevel.HasValue)
            result = result with { SignificanceLevel = SignificanceLevel.Value };

        if (OutlierMode.HasValue)
            result = result with { OutlierMode = OutlierMode.Value, OutlierDetector = null };

        if (TailMetricsBasis.HasValue)
            result = result with { TailMetricsBasis = TailMetricsBasis.Value };

        if (Profile.HasValue)
            result = result with { Profile = Profile.Value };

        if (ForceGc.HasValue)
            result = result with { ForceGcBeforeEachIterationOverride = ForceGc.Value };

        if (NoAllocations.HasValue)
            result = result with { MeasureAllocationsOverride = !NoAllocations.Value };

        if (NoGcBetweenBenchmarks is true)
            result = result with { ForceGcBetweenBenchmarksOverride = false };

        if (MinPracticalEffect.HasValue)
            result = result with { MinimumPracticalEffect = MinPracticalEffect.Value };

        // Layer auto-tune scalars: start from the preset when given, else the current knobs.
        var autoTune = Preset.HasValue ? AutoTuneOptions.FromPreset(Preset.Value) : result.AutoTune;
        var autoTuneChanged = Preset.HasValue;

        if (CiTarget.HasValue)
        {
            autoTune = autoTune with { CiTarget = CiTarget.Value };
            autoTuneChanged = true;
        }

        if (MinSamples.HasValue)
        {
            autoTune = autoTune with { MinSamples = MinSamples.Value };
            autoTuneChanged = true;
        }

        if (MaxSamples.HasValue)
        {
            autoTune = autoTune with { MaxSamples = MaxSamples.Value };
            autoTuneChanged = true;
        }

        if (MinWarmup.HasValue)
        {
            autoTune = autoTune with { MinWarmup = MinWarmup.Value };
            autoTuneChanged = true;
        }

        if (MaxWarmup.HasValue)
        {
            autoTune = autoTune with { MaxWarmup = MaxWarmup.Value };
            autoTuneChanged = true;
        }

        if (MaxTuningTime.HasValue)
        {
            autoTune = autoTune with { MaxTuningTime = MaxTuningTime.Value };
            autoTuneChanged = true;
        }

        if (CapBehavior.HasValue)
        {
            autoTune = autoTune with { CapBehavior = CapBehavior.Value };
            autoTuneChanged = true;
        }

        if (WarmupBudgetFraction.HasValue)
        {
            autoTune = autoTune with { WarmupBudgetFraction = WarmupBudgetFraction.Value };
            autoTuneChanged = true;
        }

        if (CapGraceFactor.HasValue)
        {
            autoTune = autoTune with { CapGraceFactor = CapGraceFactor.Value };
            autoTuneChanged = true;
        }

        if (MinWarmupTime.HasValue)
        {
            autoTune = autoTune with { MinWarmupTime = MinWarmupTime.Value };
            autoTuneChanged = true;
        }

        if (NoJitQuiescence is true)
        {
            autoTune = autoTune with { RequireJitQuiescence = false };
            autoTuneChanged = true;
        }

        if (JitQuietPeriod.HasValue)
        {
            autoTune = autoTune with { JitQuietPeriod = JitQuietPeriod.Value };
            autoTuneChanged = true;
        }

        if (MinMeasurementTime.HasValue)
        {
            autoTune = autoTune with { MinMeasurementTime = MinMeasurementTime.Value };
            autoTuneChanged = true;
        }

        if (DriftTolerance.HasValue)
        {
            autoTune = autoTune with { MeasurementDriftTolerance = DriftTolerance.Value };
            autoTuneChanged = true;
        }

        if (MaxDriftRestarts.HasValue)
        {
            autoTune = autoTune with { MeasurementRestartLimit = MaxDriftRestarts.Value };
            autoTuneChanged = true;
        }

        if (autoTuneChanged)
            result = result with { AutoTune = autoTune };

        if (OpsPerSample.HasValue)
            result = result with { OpsPerSample = OpsPerSample.Value };

        if (LaunchCount.HasValue)
            result = result with { LaunchCount = LaunchCount.Value };

        if (ReportedPercentiles is not null)
            result = result with { ReportedPercentiles = ReportedPercentiles };

        if (NoHistogram.HasValue && NoHistogram.Value)
            result = result with { EnableHistogram = false };

        if (Diagnostics.HasValue)
            result = result with { Diagnostics = DiagnosticsOptions.FromMode(Diagnostics.Value) };

        if (Environment is not null)
            result = result with { Environment = MergeEnvironment(result.Environment, Environment) };

        return result;
    }

    /// <summary>
    ///     Builds the <see cref="EnvironmentOptions" /> carried by overrides from the CLI
    ///     flags. Returns <c>null</c> when no environment flag was set, so the child does
    ///     nothing when the parent set nothing.
    /// </summary>
    private static EnvironmentOptions? BuildEnvironmentFromCli(CliArgs cliArgs)
    {
        if (cliArgs.CpuAffinity is null
            && cliArgs.ProcessPriority is null
            && !cliArgs.DedicatedHostGuidance)
            return null;

        return new EnvironmentOptions
        {
            CpuAffinity = cliArgs.CpuAffinity,
            ProcessPriority = cliArgs.ProcessPriority,
            DedicatedHostGuidance = cliArgs.DedicatedHostGuidance,
        };
    }

    /// <summary>
    ///     Layers override environment settings on top of any programmatic ones. CLI
    ///     flags win on a per-field basis for nullable fields (the same pattern as the
    ///     other overrides); unset CLI fields preserve the programmatic value. The
    ///     <see cref="EnvironmentOptions.DedicatedHostGuidance" /> flag is a bool and
    ///     uses OR semantics - enabling it on either side enables it.
    /// </summary>
    private static EnvironmentOptions? MergeEnvironment(EnvironmentOptions? programmatic, EnvironmentOptions cli)
    {
        if (programmatic is null)
            return cli;

        return programmatic with
        {
            CpuAffinity = cli.CpuAffinity ?? programmatic.CpuAffinity,
            ProcessPriority = cli.ProcessPriority ?? programmatic.ProcessPriority,
            DedicatedHostGuidance = cli.DedicatedHostGuidance || programmatic.DedicatedHostGuidance,
        };
    }
}

/// <summary>
///     The serialized request a parent writes for its child. A Suite request carries the
///     callsite identity used to replay exactly the right <c>RunAsync</c> call; a Host
///     request carries the declaring type plus the benchmark names to run. Both carry the
///     benchmark display names (for naming child results and any errored fallbacks).
/// </summary>
internal sealed record IsolatedRunRequest
{
    public required IsolatedRunKind Kind { get; init; }

    // Suite callsite-replay identity.
    public int InvocationOrdinal { get; init; }
    public string? CallerFilePath { get; init; }
    public int CallerLineNumber { get; init; }
    public string? CallerMemberName { get; init; }
    public string? SuiteName { get; init; }

    // Host discovery identity.
    public string? DeclaringTypeFullName { get; init; }

    // Shared: which benchmarks to expect results for and how to name them. For Host the
    // names also select which discovered benchmarks the child runs.
    public string DisplayPrefix { get; init; } = "";
    public IReadOnlyList<string> BenchmarkDisplayNames { get; init; } = [];

    // Host-only scalar measurement overrides (Suite children rebuild their own options).
    public MeasurementOverrides Overrides { get; init; } = new();

    /// <summary>
    ///     Observer names the parent resolved from <c>--observer</c> flags and programmatic
    ///     <c>WithObserver</c> calls. The child resolves each through
    ///     <c>NBenchmark.Observers.ObserverRegistry</c> so the same observers (e.g. the
    ///     <c>live</c> dashboard observer, an OTLP-exporting observer) fire in the child as in
    ///     the parent. Empty when the parent attached no observer, in which case the child runs
    ///     with <c>NullMeasurementObserver.Instance</c>. The child re-runs the entry assembly, so
    ///     <c>[ModuleInitializer]</c> self-registration populates the registry identically - the
    ///     names resolve to the same factories.
    /// </summary>
    public IReadOnlyList<string> ObserverNames { get; init; } = [];

    /// <summary>
    ///     The runtime the parent built this child for. When set, the child stamps
    ///     <see cref="RuntimeMonikerExtensions.ToTargetFramework" /> onto every
    ///     <see cref="BenchmarkResult.RuntimeMoniker" /> it produces.
    /// </summary>
    public RuntimeMoniker? RuntimeMoniker { get; init; }

    /// <summary>
    ///     Explicit path to the entry assembly DLL. When set, the launcher uses
    ///     <c>dotnet exec</c> with this path instead of re-running the current process.
    /// </summary>
    public string? EntryAssemblyPath { get; init; }
}

/// <summary>A single benchmark result plus its raw per-iteration samples, as shipped from a child.</summary>
internal sealed record IsolatedResultItem
{
    public required BenchmarkResult Result { get; init; }
    public required double[] RawSamples { get; init; }
}

/// <summary>The full set of results a child writes back to its parent.</summary>
internal sealed record IsolatedPayload
{
    public required IReadOnlyList<IsolatedResultItem> Items { get; init; }
}

/// <summary>
///     The child-side half of isolation: tracks whether the current process is running as
///     an isolated child, what it was asked to run, and where to write its results. The
///     parent-side process launch lives in <see cref="ChildProcessLauncher" />.
/// </summary>
internal static class IsolatedRunContext
{
    private static int SuiteInvocationSequence;
    private static readonly AsyncLocal<IsolatedRunScope?> Scope = new();

    /// <summary>True when the current process is executing as an isolated child.</summary>
    public static bool IsActive => Scope.Value is not null;

    public static bool TryGetActiveRequest(out IsolatedRunRequest request)
    {
        var scope = Scope.Value;

        if (scope is null)
        {
            request = null!;
            return false;
        }

        request = scope.Request;
        return true;
    }

    /// <summary>
    ///     Returns the next ordinal for a suite <c>RunAsync</c> call. Parent and child
    ///     re-run the same entry point, so incrementing on every call keeps their ordinals
    ///     in lock-step and lets the child identify the requested callsite unambiguously.
    /// </summary>
    public static int NextSuiteInvocationOrdinal() => Interlocked.Increment(ref SuiteInvocationSequence);

    internal static void ResetInvocationOrdinalsForTesting() => Interlocked.Exchange(ref SuiteInvocationSequence, 0);

    public static bool IsSuiteRequestMatch(
        int invocationOrdinal,
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        string suiteName)
    {
        if (!TryGetActiveRequest(out var request))
            return false;

        return request.Kind == IsolatedRunKind.Suite
               && request.InvocationOrdinal == invocationOrdinal
               && PathEquals(request.CallerFilePath, callerFilePath)
               && request.CallerLineNumber == callerLineNumber
               && string.Equals(request.CallerMemberName, callerMemberName, StringComparison.Ordinal)
               && string.Equals(request.SuiteName, suiteName, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Writes the child's results to the output file the parent supplied. A no-op when
    ///     the current process is not an isolated child (or has no output path), so the
    ///     same in-process run helper can be reused by the parent.
    /// </summary>
    public static async Task WriteChildPayloadIfRequestedAsync(
        IReadOnlyList<BenchmarkResult> results,
        IReadOnlyDictionary<string, double[]> rawSamples,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(rawSamples);

        var outputPath = Scope.Value?.OutputPath;

        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        var items = results
            .Select(r => new IsolatedResultItem
            {
                Result = r with { RawSamples = [] },
                RawSamples = rawSamples.TryGetValue($"{r.Name}\0{r.RuntimeMoniker}", out var samples) ? samples : [],
            })
            .ToList();

        await ChildProcessLauncher.WritePayloadAsync(outputPath, items, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Child entry wrapper. If the request/output environment variables are set this
    ///     process is an isolated child: read the request, establish the scope, and run the
    ///     action. Otherwise run the action unchanged (the common, parent path).
    /// </summary>
    public static async Task<T> WithCurrentRequestAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var requestPath = Environment.GetEnvironmentVariable(ChildProcessLauncher.RequestPathEnvVar);

        if (string.IsNullOrWhiteSpace(requestPath))
            return await action().ConfigureAwait(false);

        var request = await ChildProcessLauncher.ReadRequestAsync(requestPath).ConfigureAwait(false);
        var outputPath = Environment.GetEnvironmentVariable(ChildProcessLauncher.OutputPathEnvVar);

        var prior = Scope.Value;
        Scope.Value = new IsolatedRunScope(request, outputPath);

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Scope.Value = prior;
        }
    }

    internal static async Task<T> WithActiveRequestForTestingAsync<T>(
        IsolatedRunRequest request,
        string? outputPath,
        Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);

        var prior = Scope.Value;
        Scope.Value = new IsolatedRunScope(request, outputPath);

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Scope.Value = prior;
        }
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (OperatingSystem.IsWindows())
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private sealed record IsolatedRunScope(IsolatedRunRequest Request, string? OutputPath);
}
