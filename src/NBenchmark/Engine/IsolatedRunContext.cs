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
///     The resolved per-benchmark isolation decision in Host mode, after layering the
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
    public MeasurementProfile? Profile { get; init; }
    public bool? ForceGc { get; init; }
    public bool? NoAllocations { get; init; }

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

    public int? LaunchCount { get; init; }

    public IReadOnlyList<double>? ReportedPercentiles { get; init; }

    public bool? NoHistogram { get; init; }

    public static MeasurementOverrides FromCliArgs(CliArgs cliArgs) => new()
    {
        Iterations = cliArgs.Iterations,
        WarmupIterations = cliArgs.WarmupIterations,
        OpsPerSample = cliArgs.OpsPerSample,
        ConfidenceLevel = cliArgs.ConfidenceLevel,
        SignificanceLevel = cliArgs.Alpha,
        OutlierMode = cliArgs.OutlierMode,
        Profile = cliArgs.Profile,
        ForceGc = cliArgs.ForceGc,
        NoAllocations = cliArgs.NoAllocations,
        Preset = cliArgs.AutoTunePreset,
        CiTarget = cliArgs.CiTarget,
        MinSamples = cliArgs.MinSamples,
        MaxSamples = cliArgs.MaxSamples,
        MinWarmup = cliArgs.MinWarmup,
        MaxWarmup = cliArgs.MaxWarmup,
        MaxTuningTime = cliArgs.MaxTuningTime,
        CapBehavior = cliArgs.AutoTuneCapBehavior,
        LaunchCount = cliArgs.LaunchCount,
        ReportedPercentiles = cliArgs.ReportedPercentiles,
        NoHistogram = cliArgs.NoHistogram,
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

        if (Profile.HasValue)
            result = result with { Profile = Profile.Value };

        if (ForceGc.HasValue)
            result = result with { ForceGcBeforeEachIterationOverride = ForceGc.Value };

        if (NoAllocations.HasValue)
            result = result with { MeasureAllocationsOverride = !NoAllocations.Value };

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

        return result;
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
                Result = r,
                RawSamples = rawSamples.TryGetValue(r.Name, out var samples) ? samples : [],
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
