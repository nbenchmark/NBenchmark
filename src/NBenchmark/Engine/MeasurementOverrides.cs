using NBenchmark.Observers;

namespace NBenchmark.Engine;

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

    /// <summary>
    ///     The runtime-startup configuration requested on the command line. Unlike the other
    ///     overrides this is not applied by the child to itself - the parent already applied it to
    ///     the child's environment block before startup, because that is the only point at which
    ///     the runtime reads it. It travels so the child's effective options agree with reality.
    /// </summary>
    public RuntimeProfile? RuntimeProfile { get; init; }
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

    /// <summary>
    ///     Lifts the cap on how many raw samples an isolated worker returns
    ///     (<see cref="MeasurementOptions.MaxRawSamples" />), set by <c>--emit-raw</c>.
    /// </summary>
    public bool? EmitRaw { get; init; }

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
        RuntimeProfile = cliArgs.RuntimeProfile,
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
        EmitRaw = cliArgs.EmitRaw,
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

        if (RuntimeProfile is not null)
            result = result with { RuntimeProfile = RuntimeProfile };

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

        // One-way: the flag asks for everything, and its absence means "leave whatever was
        // configured alone" rather than "impose the default". A programmatic MaxRawSamples would
        // otherwise be silently reset by any run that parsed a command line.
        if (EmitRaw.HasValue && EmitRaw.Value)
            result = result with { MaxRawSamples = MeasurementOptions.UnboundedRawSamples };

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

/// <summary>A single benchmark result plus its raw per-iteration samples, as shipped from a child.</summary>
internal sealed record IsolatedResultItem
{
    public required BenchmarkResult Result { get; init; }
    public required double[] RawSamples { get; init; }
}
