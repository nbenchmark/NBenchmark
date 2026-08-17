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

    /// <summary>Run with the rest of its declaring class in one shared worker process.</summary>
    PerClass,

    /// <summary>Run alone in its own dedicated worker process.</summary>
    PerBenchmark,
}

/// <summary>
///     The CLI-derived deltas layered on top of a programmatically-built
///     <see cref="MeasurementOptions" />, so a command-line flag overrides a <c>WithOptions</c> call
///     on that field alone and leaves every other field as the caller configured it.
/// </summary>
/// <remarks>
///     This record is purely a merge step inside one process; nothing here is serialized. Under the
///     previous file-based isolation design it was also the wire format for a child process, which
///     re-ran the user's entry point to rebuild its base options and then applied these scalars on
///     top. A worker now receives the resolved <see cref="MeasurementOptions" /> whole, so the only
///     surviving job is <c>CliArgs</c> -&gt; <c>MeasurementOptions</c>.
/// </remarks>
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
    ///     The runtime-startup configuration requested on the command line. It reaches a worker as
    ///     environment variables written before the process starts - the only point at which the
    ///     runtime reads them - so what lands on <see cref="MeasurementOptions" /> here is the record
    ///     of what was asked for, which is what gets stamped on every result.
    /// </summary>
    public RuntimeProfile? RuntimeProfile { get; init; }
    public bool? ForceGc { get; init; }
    public bool? NoAllocations { get; init; }

    /// <summary>
    ///     Set by <c>--strict-isolation</c>, which asks for the same thing
    ///     <see cref="MeasurementOptions.RequireIsolation" /> does and had no way to say so.
    /// </summary>
    /// <remarks>
    ///     One-way, like <c>--emit-raw</c>: the flag turns the requirement on and its absence leaves
    ///     whatever was configured alone. Without this mapping the flag could only ever take the
    ///     expensive path - measure everything, audit the results, set an exit code - even though the
    ///     early-throw mechanism it wanted already existed and the two are the same request phrased at
    ///     different times. Both still run: the throw catches a refusal before any work, and
    ///     <see cref="Workers.IsolationAudit.Enforce" /> remains the backstop for anything that reaches
    ///     the results without having passed a gate.
    /// </remarks>
    public bool? RequireIsolation { get; init; }
    public bool? NoGcBetweenBenchmarks { get; init; }
    public double? MinPracticalEffect { get; init; }
    public double? MinRelativeShift { get; init; }

    // Auto-tune scalar overrides, layered onto whichever AutoTuneOptions the caller configured
    // (or onto Preset when one was named) rather than replacing it wholesale.
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

    public IReadOnlyList<double>? ReportedPercentiles { get; init; }

    public bool? NoHistogram { get; init; }

    /// <summary>
    ///     Switches off the host drift canary (<see cref="DriftCanaryOptions.Enabled" />), set by
    ///     <c>--no-drift-canary</c>.
    /// </summary>
    public bool? NoDriftCanary { get; init; }

    /// <summary>
    ///     Switches off the thread-level OS controls
    ///     (<see cref="EnvironmentOptions.ThreadControl" />), set by <c>--no-thread-control</c>.
    /// </summary>
    public bool? NoThreadControl { get; init; }

    /// <summary>
    ///     Switches off evidence-based interference rejection
    ///     (<see cref="InterferenceOptions.Enabled" />), set by <c>--no-interference-filter</c>.
    /// </summary>
    public bool? NoInterferenceFilter { get; init; }

    /// <summary>
    ///     Lifts the cap on how many raw samples an isolated worker returns
    ///     (<see cref="MeasurementOptions.MaxRawSamples" />), set by <c>--emit-raw</c>.
    /// </summary>
    public bool? EmitRaw { get; init; }

    /// <summary>
    ///     Turns on live forwarding of the per-sample observer stream out of an isolated worker
    ///     (<see cref="MeasurementOptions.StreamSamples" />), set by <c>--stream-samples</c>.
    /// </summary>
    public bool? StreamSamples { get; init; }

    public DiagnosticsMode? Diagnostics { get; init; }

    /// <summary>
    ///     CPU affinity, process priority and dedicated-host guidance as requested on the command
    ///     line. Unlike the runtime profile these are settable at any time, so a worker applies them
    ///     to itself from the options it was sent. <c>null</c> means no environment flag was passed.
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
        RequireIsolation = cliArgs.StrictIsolation ? true : null,
        NoGcBetweenBenchmarks = cliArgs.NoGcBetweenBenchmarks ? true : null,
        MinPracticalEffect = cliArgs.MinPracticalEffect,
        MinRelativeShift = cliArgs.MinRelativeShift,
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
        ReportedPercentiles = cliArgs.ReportedPercentiles,
        NoHistogram = cliArgs.NoHistogram,
        NoDriftCanary = cliArgs.NoDriftCanary ? true : null,
        NoThreadControl = cliArgs.NoThreadControl ? true : null,
        NoInterferenceFilter = cliArgs.NoInterferenceFilter ? true : null,
        EmitRaw = cliArgs.EmitRaw,
        StreamSamples = cliArgs.StreamSamples ? true : null,
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

        if (RequireIsolation is true)
            result = result with { RequireIsolation = true };

        if (MinPracticalEffect.HasValue)
            result = result with { MinimumPracticalEffect = MinPracticalEffect.Value };

        if (MinRelativeShift.HasValue)
            result = result with { MinimumRelativeShift = MinRelativeShift.Value };

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

        if (ReportedPercentiles is not null)
            result = result with { ReportedPercentiles = ReportedPercentiles };

        if (NoHistogram.HasValue && NoHistogram.Value)
            result = result with { EnableHistogram = false };

        // One-way, like --emit-raw below: the flag asks for the canary to be off, and its absence
        // means "leave whatever was configured alone" rather than "impose the default", so a
        // programmatic WithDriftCanary(false) survives a parsed command line.
        if (NoDriftCanary is true)
            result = result with { DriftCanary = result.DriftCanary with { Enabled = false } };

        // One-way: the flag asks for everything, and its absence means "leave whatever was
        // configured alone" rather than "impose the default". A programmatic MaxRawSamples would
        // otherwise be silently reset by any run that parsed a command line.
        if (EmitRaw.HasValue && EmitRaw.Value)
            result = result with { MaxRawSamples = MeasurementOptions.UnboundedRawSamples };

        // One-way for the same reason: --stream-samples asks for the stream, and its absence must
        // not switch off a programmatic WithOptions that asked for it.
        if (StreamSamples.HasValue && StreamSamples.Value)
            result = result with { StreamSamples = true };

        if (Diagnostics.HasValue)
            result = result with { Diagnostics = DiagnosticsOptions.FromMode(Diagnostics.Value) };

        if (Environment is not null)
            result = result with { Environment = MergeEnvironment(result.Environment, Environment) };

        // After the environment merge, not inside it: the flag has to be able to create an
        // EnvironmentOptions where none existed, since ThreadControl is the one member of that
        // record that does something when every other member is unset. One-way for the same reason
        // as --no-drift-canary above - the absence of the flag means "leave what was configured
        // alone", so a programmatic WithThreadControl(false) survives a parsed command line.
        if (NoThreadControl is true)
        {
            result = result with
            {
                Environment = (result.Environment ?? new EnvironmentOptions()) with { ThreadControl = false },
            };
        }

        // One-way, like --no-drift-canary and --no-thread-control above: the flag asks for the
        // filter to be off, and its absence means "leave whatever was configured alone" rather than
        // "impose the default", so a programmatic WithInterferenceFilter(false) survives a parsed
        // command line.
        if (NoInterferenceFilter is true)
            result = result with { Interference = result.Interference with { Enabled = false } };

        return result;
    }

    /// <summary>
    ///     Builds the <see cref="EnvironmentOptions" /> carried by overrides from the CLI
    ///     flags. Returns <c>null</c> when no environment flag was set, so an absent flag leaves any
    ///     programmatic configuration alone instead of overwriting it with defaults.
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
    ///     Layers CLI environment settings on top of any programmatic ones. CLI
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

/// <summary>A single benchmark result paired with the raw samples a worker sent alongside it.</summary>
internal sealed record IsolatedResultItem
{
    public required BenchmarkResult Result { get; init; }
    public required double[] RawSamples { get; init; }
}
