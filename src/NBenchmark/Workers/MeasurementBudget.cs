using System.Diagnostics;

namespace NBenchmark.Workers;

/// <summary>
///     How long a measuring process is allowed to take, and what runtime configuration it starts
///     under.
/// </summary>
/// <remarks>
///     Lives here rather than on the legacy child launcher because the worker path is the primary
///     consumer now, and depending on the component it replaces would keep the old one alive by
///     accident. The launcher delegates to this, so the two can never drift apart while both exist.
/// </remarks>
internal static class MeasurementBudget
{
    /// <summary>
    ///     Floor for a derived budget, so a very small tuning budget still leaves room for process
    ///     start.
    /// </summary>
    internal static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Absolute ceiling, applied to derived and explicit timeouts alike.</summary>
    internal static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    ///     Fixed allowance for process start, JIT and discovery before any measuring begins.
    /// </summary>
    internal static readonly TimeSpan StartupAllowance = TimeSpan.FromSeconds(30);

    /// <summary>Per-benchmark slack over the engine's own in-body ceiling.</summary>
    internal static readonly TimeSpan PerBenchmarkSlack = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Derives a wall-clock ceiling from the engine's own tuning budget, so the timeout scales
    ///     with what the process was actually asked to do and can never fire on a benchmark that is
    ///     merely slow.
    ///     <para>
    ///         <see cref="AutoTuneOptions.MaxTuningTime" /> times
    ///         <see cref="AutoTuneOptions.CapGraceFactor" /> is the engine's own hard ceiling on
    ///         in-body time per benchmark, so anything past that plus warmup and slack is a wedged
    ///         process rather than a busy one. <c>LaunchCount</c> is deliberately not a factor: each
    ///         replicate is its own process.
    ///     </para>
    /// </summary>
    public static TimeSpan For(MeasurementOptions options, int benchmarkCount)
    {
        ArgumentNullException.ThrowIfNull(options);

        var autoTune = options.AutoTune;

        var perBenchmark = autoTune.MaxTuningTime * autoTune.CapGraceFactor
                           + autoTune.MinWarmupTime
                           + PerBenchmarkSlack;

        var budget = StartupAllowance + perBenchmark * Math.Max(benchmarkCount, 1);

        return budget < MinTimeout ? MinTimeout
            : budget > MaxTimeout ? MaxTimeout
            : budget;
    }

    /// <summary>
    ///     Writes the runtime profile into a not-yet-started process's environment block.
    ///     <para>
    ///         This is the only moment at which JIT tiering, dynamic PGO, ReadyToRun and GC flavour
    ///         can be chosen: the runtime reads them once, at startup, and never again. Every other
    ///         part of the out-of-process design exists to make this call possible.
    ///     </para>
    /// </summary>
    public static void ApplyRuntimeProfile(ProcessStartInfo startInfo, RuntimeProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (profile is null || profile.InheritsEverything)
            return;

        foreach (var (variable, value) in profile.ToEnvironment())
        {
            startInfo.Environment[variable] = value;
        }

        // Echoed back by the measuring process so the coordinator learns what is true of it rather
        // than what it asked for. There is no managed read-back for tiering, so this is the only
        // honest way to report the configuration a result was produced under.
        startInfo.Environment[RuntimeProfile.ProfileNameEnvVar] = profile.Name;
    }
}
