namespace NBenchmark;

/// <summary>The garbage collector flavour a <see cref="RuntimeProfile" /> asks for.</summary>
public enum GcMode
{
    /// <summary>Workstation GC - the default for a console application.</summary>
    Workstation,

    /// <summary>Server GC - one heap per core, higher throughput, higher memory use.</summary>
    Server,
}

/// <summary>
///     The runtime-startup configuration a benchmark is measured under: JIT tiering, dynamic
///     PGO, ReadyToRun, and GC flavour.
///     <para>
///         These knobs exist as a first-class concept because <b>none of them can be changed in
///         an already-running process</b> - the runtime reads them once at startup. That, and not
///         cross-benchmark state contamination, is the real reason a measurement needs its own
///         process. The process boundary is the delivery mechanism; this type is the payload.
///     </para>
///     <para>
///         Why it matters, measured on four benchmarks with provably identical cost: with tiering
///         left at its default, repeated in-process runs spanned 3.27x and fabricated a 2.80x
///         difference between two of them. Isolating them into child processes made the numbers
///         reproducible (1.08x spread) but left them roughly 3.3x too high - precisely wrong.
///         Only isolation <i>plus</i> <see cref="SteadyState" /> was both precise and accurate
///         (1.04x spread). See <c>plans/out-of-process-pivot.md</c>.
///     </para>
/// </summary>
public sealed record RuntimeProfile
{
    /// <summary>
    ///     Marker variable set on every child the launcher spawns, so the child can report the
    ///     profile it was launched under by name. The runtime exposes no managed read-back for
    ///     tiering - <c>AppContext.GetData("System.Runtime.TieredCompilation")</c> returns null
    ///     even when the setting is demonstrably in effect - so a process cannot introspect its
    ///     own JIT configuration and must echo what it was given.
    /// </summary>
    internal const string ProfileNameEnvVar = "NBENCHMARK_RUNTIME_PROFILE";

    /// <summary>
    ///     Inherit whatever the host process was started with and set nothing. This is the only
    ///     honest profile for an in-process measurement, because the knobs cannot be applied
    ///     after startup. Also the profile to use when reproducing an older result.
    /// </summary>
    public static readonly RuntimeProfile Host = new() { Name = "host" };

    /// <summary>
    ///     Fully-optimized steady-state throughput, and the default. Disables tiered compilation
    ///     and ReadyToRun so every method is jitted at full optimization on first call, which
    ///     removes the dominant source of measurement error for short bodies.
    ///     <para>
    ///         Honest limitations: it forbids on-stack replacement, changes startup behaviour, and
    ///         is <b>the wrong choice for measuring cold-start or first-call cost</b>. It also
    ///         costs wall clock - everything is compiled eagerly at full optimization.
    ///     </para>
    /// </summary>
    public static readonly RuntimeProfile SteadyState = new()
    {
        Name = "steady-state",
        TieredCompilation = false,
        TieredPgo = false,
        ReadyToRun = false,
    };

    /// <summary>
    ///     The configuration real applications ship with: tiering, dynamic PGO and ReadyToRun all
    ///     on. Set explicitly rather than inherited, so the run is reproducible regardless of the
    ///     host's environment.
    ///     <para>
    ///         Use it to answer "what will my users actually see?". Be aware that it is
    ///         <i>imprecise</i> - this is the configuration measured at a 3.27x spread - so raise
    ///         <see cref="MeasurementOptions.LaunchCount" /> and read the cross-launch interval
    ///         rather than the within-launch one.
    ///     </para>
    /// </summary>
    public static readonly RuntimeProfile Production = new()
    {
        Name = "production",
        TieredCompilation = true,
        TieredPgo = true,
        ReadyToRun = true,
    };

    /// <summary>
    ///     <see cref="SteadyState" /> plus non-concurrent server GC, for measuring code that will
    ///     run under a server-GC host such as ASP.NET Core. Allocation-heavy benchmarks can behave
    ///     very differently here.
    /// </summary>
    public static readonly RuntimeProfile ServerGc = new()
    {
        Name = "server-gc",
        TieredCompilation = false,
        TieredPgo = false,
        ReadyToRun = false,
        Gc = GcMode.Server,
        ConcurrentGc = false,
    };

    private static readonly RuntimeProfile[] Known = [Host, SteadyState, Production, ServerGc];

    /// <summary>
    ///     The profile's name, as printed in every report header and stamped on every
    ///     <see cref="BenchmarkResult.RuntimeProfileName" />. Custom profiles should use a name
    ///     that is not one of the built-in ones.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Whether tiered compilation is enabled. <c>null</c> leaves the runtime default in place.
    ///     Setting it to <c>false</c> is the single highest-impact accuracy knob for short bodies.
    /// </summary>
    public bool? TieredCompilation { get; init; }

    /// <summary>
    ///     Whether dynamic PGO is enabled. <c>null</c> leaves the runtime default in place.
    ///     Only meaningful when <see cref="TieredCompilation" /> is on, since dynamic PGO collects
    ///     its profile in tier-0 code.
    /// </summary>
    public bool? TieredPgo { get; init; }

    /// <summary>
    ///     Whether precompiled ReadyToRun code is used. <c>null</c> leaves the runtime default in
    ///     place. Disabling it forces the JIT to generate code for framework methods too, which is
    ///     what makes <see cref="SteadyState" /> uniform rather than a mix of R2R and jitted code.
    /// </summary>
    public bool? ReadyToRun { get; init; }

    /// <summary>The GC flavour. <c>null</c> leaves the runtime default (workstation) in place.</summary>
    public GcMode? Gc { get; init; }

    /// <summary>
    ///     Whether the background (concurrent) GC is enabled. <c>null</c> leaves the runtime
    ///     default in place. Turning it off removes a background thread whose pauses land in the
    ///     measurement at unpredictable points.
    /// </summary>
    public bool? ConcurrentGc { get; init; }

    /// <summary>
    ///     Extra environment variables applied to the child verbatim, for knobs this type does not
    ///     model. Applied after the modelled knobs, so an entry here wins.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    ///     <c>true</c> when the profile sets nothing at all, so launching a child with it is
    ///     indistinguishable from inheriting the parent's environment.
    /// </summary>
    public bool InheritsEverything
        => TieredCompilation is null
           && TieredPgo is null
           && ReadyToRun is null
           && Gc is null
           && ConcurrentGc is null
           && ExtraEnvironment.Count == 0;

    /// <summary>
    ///     Resolves a profile name as accepted by <c>--runtime-profile</c>. Matching is
    ///     case-insensitive and tolerates both <c>steady-state</c> and <c>steadystate</c>.
    /// </summary>
    public static bool TryParse(string? name, out RuntimeProfile profile)
    {
        profile = Host;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalized = name.Replace("-", "", StringComparison.Ordinal).Trim();

        foreach (var candidate in Known)
        {
            if (string.Equals(
                    candidate.Name.Replace("-", "", StringComparison.Ordinal),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The names accepted by <c>--runtime-profile</c>, for help text and error messages.</summary>
    public static IReadOnlyList<string> KnownNames => Known.Select(p => p.Name).ToList();

    /// <summary>
    ///     The environment variables that apply this profile to a freshly started process. Only
    ///     the knobs this profile actually sets appear, so a profile that inherits everything
    ///     yields an empty map and the child's environment is left alone.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        if (TieredCompilation is { } tiered)
            env["DOTNET_TieredCompilation"] = tiered ? "1" : "0";

        if (TieredPgo is { } pgo)
            env["DOTNET_TieredPGO"] = pgo ? "1" : "0";

        if (ReadyToRun is { } r2r)
            env["DOTNET_ReadyToRun"] = r2r ? "1" : "0";

        if (Gc is { } gc)
            env["DOTNET_gcServer"] = gc == GcMode.Server ? "1" : "0";

        if (ConcurrentGc is { } concurrent)
            env["DOTNET_gcConcurrent"] = concurrent ? "1" : "0";

        foreach (var (key, value) in ExtraEnvironment)
        {
            env[key] = value;
        }

        return env;
    }

    /// <summary>
    ///     A compact, stable description of the knobs this profile sets, for report headers -
    ///     e.g. <c>"tiered=off pgo=off r2r=off"</c>. Empty when nothing is set.
    /// </summary>
    public string Describe() => Describe(ToEnvironment());

    /// <summary>
    ///     Formats an environment map the same way <see cref="Describe()" /> does, so knobs read
    ///     back from a live process render identically to knobs derived from a profile.
    /// </summary>
    internal static string Describe(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.Count == 0)
            return "";

        var parts = new List<string>(environment.Count);

        foreach (var (variable, label) in KnobLabels)
        {
            if (environment.TryGetValue(variable, out var value))
                parts.Add($"{label}={(value == "1" ? OnLabel(variable) : OffLabel(variable))}");
        }

        // Anything not modelled above is reported verbatim so ExtraEnvironment is never hidden.
        foreach (var (key, value) in environment)
        {
            if (!KnobLabels.ContainsKey(key))
                parts.Add($"{key}={value}");
        }

        return string.Join(" ", parts);
    }

    private static readonly Dictionary<string, string> KnobLabels = new(StringComparer.Ordinal)
    {
        ["DOTNET_TieredCompilation"] = "tiered",
        ["DOTNET_TieredPGO"] = "pgo",
        ["DOTNET_ReadyToRun"] = "r2r",
        ["DOTNET_gcServer"] = "gc",
        ["DOTNET_gcConcurrent"] = "concurrentGc",
    };

    private static string OnLabel(string variable)
        => variable == "DOTNET_gcServer" ? "server" : "on";

    private static string OffLabel(string variable)
        => variable == "DOTNET_gcServer" ? "workstation" : "off";
}
