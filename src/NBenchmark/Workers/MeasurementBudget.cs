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
    ///     The OTLP endpoint the coordinator was told to export to (via <c>--otlp-endpoint</c> or the
    ///     standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>). Forwarded to every measuring process so
    ///     their telemetry reaches the same collector - the cross-process channel an in-memory
    ///     <see cref="IBenchmarkProgress" /> callback cannot provide.
    /// </summary>
    internal const string OtelEndpointEnvVar = "NBENCHMARK_OTEL_ENDPOINT";

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
    ///     The OTel-standard variables forwarded verbatim to a measuring process, so an
    ///     OpenTelemetry SDK wired up in the user's application exports from the worker to the same
    ///     collector as the coordinator.
    /// </summary>
    private static readonly string[] OtelStandardEnvVars =
    [
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_EXPORTER_OTLP_TIMEOUT",
        "OTEL_RESOURCE_ATTRIBUTES",
        "OTEL_SERVICE_NAME",
    ];

    /// <summary>
    ///     Forwards telemetry configuration into a not-yet-started measuring process.
    /// </summary>
    /// <remarks>
    ///     A process boundary severs the in-memory progress callback, so a collector endpoint is the
    ///     only way a worker's telemetry reaches the same place as the coordinator's. This was
    ///     carried by the previous child launcher and has to keep working now that measurement has
    ///     moved to workers - dropping it would silently lose every isolated benchmark's spans while
    ///     leaving in-process ones intact, which reads as an exporter fault rather than a missing
    ///     forward.
    /// </remarks>
    public static void ApplyTelemetryEnvironment(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        foreach (var name in OtelStandardEnvVars)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                startInfo.Environment[name] = value;
        }

        // The NBenchmark-specific endpoint (--otlp-endpoint) is mirrored into the standard variable
        // when the user has not set that themselves, so an SDK wired only against the standard name
        // still picks it up.
        if (Environment.GetEnvironmentVariable(OtelEndpointEnvVar) is { Length: > 0 } endpoint
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            startInfo.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint;
        }

        startInfo.Environment[OtelEndpointEnvVar] =
            Environment.GetEnvironmentVariable(OtelEndpointEnvVar) ?? "";
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
