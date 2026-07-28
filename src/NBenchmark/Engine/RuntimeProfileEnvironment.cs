namespace NBenchmark.Engine;

/// <summary>
///     Reports the runtime configuration the <b>current process</b> is actually running under, by
///     reading its own environment.
///     <para>
///         This is deliberately a read of reality rather than a read of intent. The runtime exposes
///         no managed read-back for tiering, so a result cannot be stamped from
///         <see cref="MeasurementOptions.RuntimeProfile" />: that is what the caller
///         <i>asked for</i>, and an in-process measurement cannot honour it because the knobs are
///         fixed at startup. Stamping intent would make every in-process result claim a fidelity
///         it does not have.
///     </para>
///     <para>
///         A child spawned by <see cref="ChildProcessLauncher" /> sees both the
///         <c>DOTNET_*</c> knobs and the <see cref="RuntimeProfile.ProfileNameEnvVar" /> marker in
///         its own environment, so it reports the profile by name with no plumbing across the
///         process boundary. A host process sees no marker and reports
///         <see cref="RuntimeProfile.Host" /> - together with any <c>DOTNET_*</c> knobs the user
///         set themselves, which are real and worth surfacing.
///     </para>
/// </summary>
internal static class RuntimeProfileEnvironment
{
    /// <summary>
    ///     Every variable that can change measured timings and is fixed at process start. Read
    ///     from the environment rather than derived from a profile, so a knob the user set by hand
    ///     is reported just as faithfully as one NBenchmark applied.
    /// </summary>
    private static readonly string[] ObservedVariables =
    [
        "DOTNET_TieredCompilation",
        "DOTNET_TieredPGO",
        "DOTNET_ReadyToRun",
        "DOTNET_gcServer",
        "DOTNET_gcConcurrent",
    ];

    private static readonly Lazy<CapturedRuntimeProfile> Cached = new(CaptureCore);

    /// <summary>
    ///     What this process is running under. Read once and cached: these variables are only
    ///     consulted by the runtime at startup, so a later change to the environment would be a
    ///     lie rather than an update.
    /// </summary>
    public static CapturedRuntimeProfile Current => Cached.Value;

    /// <summary>
    ///     Env-var opt-out for the not-applied guidance, mirroring
    ///     <c>NBENCHMARK_SUPPRESS_DEBUG_WARNING</c> so a CLI-only caller can silence it without
    ///     changing code.
    /// </summary>
    internal const string SuppressWarningEnvVar = "NBENCHMARK_SUPPRESS_RUNTIME_PROFILE_WARNING";

    private static int _guidanceEmitted;

    /// <summary>
    ///     Emits a once-per-process note when a runtime profile was requested but the measuring
    ///     process was not launched with one - which is the case for every in-process run, since
    ///     the knobs are read at startup.
    ///     <para>
    ///         This is guidance rather than a per-result warning on purpose. Simple mode is always
    ///         in-process, so a per-result warning would attach to every single
    ///         <c>Benchmark.Run</c> and train people to ignore warnings. It follows the same
    ///         once-per-process, warn-and-proceed pattern as the Debug-build guidance in
    ///         <see cref="EnvironmentControl.EmitBuildConfigurationGuidance" />, which exists for
    ///         exactly this class of "your environment limits fidelity" message. The
    ///         <see cref="BenchmarkResult.RuntimeProfileName" /> stamp is always present regardless.
    ///     </para>
    /// </summary>
    public static void EmitNotAppliedGuidanceOnce(MeasurementOptions? options)
    {
        var requested = options?.RuntimeProfile;

        if (requested is null || requested.InheritsEverything)
            return;

        if (Current.WasApplied)
            return;

        if (options is { SuppressRuntimeProfileWarning: true } || IsSuppressEnvVarSet())
            return;

        if (Interlocked.CompareExchange(ref _guidanceEmitted, 1, 0) != 0)
            return;

        // Scoped to "benchmarks measured in this process", not to the whole run. This method is
        // only reached from an in-process measurement, so in an otherwise-isolated Harness run it
        // fires for the [InProcess] benchmarks alone - claiming the run was unprofiled would be
        // wrong. Kept short deliberately: a wall of text on every run trains people to skip it.
        Console.Error.WriteLine(
            $"Runtime profile: benchmarks measured in this process could not use the "
            + $"'{requested.Name}' profile ({requested.Describe()}).");

        Console.Error.WriteLine(
            "  JIT tiering, PGO, ReadyToRun and GC flavour are fixed when a process starts, so "
            + "they can only be applied to a benchmark that runs in its own child process. "
            + "Affected results are stamped 'host' and are not compared against isolated ones.");

        Console.Error.WriteLine(
            "  In-process numbers are materially less trustworthy: on benchmarks of provably "
            + "identical cost they spanned 3.27x and fabricated a 2.80x difference, each reported "
            + "with a tight confidence interval. Harness mode isolates by default; Suite mode needs "
            + $"WithIsolation(). To accept the host's configuration, set RuntimeProfile.Host - or "
            + $"{SuppressWarningEnvVar}=1 to silence this without changing the profile.");
    }

    /// <summary>Test-only hook: resets the once-per-process guidance guard.</summary>
    internal static void ResetGuidanceGuardForTesting() => Interlocked.Exchange(ref _guidanceEmitted, 0);

    private static bool IsSuppressEnvVarSet()
    {
        var value = Environment.GetEnvironmentVariable(SuppressWarningEnvVar);

        return !string.IsNullOrEmpty(value)
               && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static CapturedRuntimeProfile CaptureCore()
    {
        var knobs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var variable in ObservedVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);

            if (!string.IsNullOrWhiteSpace(value))
                knobs[variable] = value.Trim();
        }

        var marker = Environment.GetEnvironmentVariable(RuntimeProfile.ProfileNameEnvVar);

        // No marker means nobody launched us with a profile, so whatever we see was inherited.
        var name = string.IsNullOrWhiteSpace(marker) ? RuntimeProfile.Host.Name : marker.Trim();

        return new CapturedRuntimeProfile(
            Name: name,
            Knobs: RuntimeProfile.Describe(knobs),
            WasApplied: !string.IsNullOrWhiteSpace(marker));
    }
}

/// <summary>
///     The runtime configuration a process is actually running under.
/// </summary>
/// <param name="Name">
///     The profile name, or <c>"host"</c> when the process was not launched with one.
/// </param>
/// <param name="Knobs">
///     A compact description of the startup knobs in effect, or empty when none are set.
/// </param>
/// <param name="WasApplied">
///     <c>true</c> when a profile was deliberately applied to this process at launch. <c>false</c>
///     means the measurement is running under whatever the host happened to be started with,
///     which is the case for every in-process run.
/// </param>
internal readonly record struct CapturedRuntimeProfile(string Name, string Knobs, bool WasApplied);
