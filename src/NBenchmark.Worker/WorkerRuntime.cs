using System.Reflection;
using System.Runtime.Versioning;

namespace NBenchmark.Worker;

/// <summary>
///     Facts about the worker process that the coordinator needs in order to decide whether it can
///     trust the worker at all.
/// </summary>
internal static class WorkerRuntime
{
    /// <summary>
    ///     The worker's own target framework, e.g. <c>net10.0</c>, read from the assembly's
    ///     <see cref="TargetFrameworkAttribute" /> rather than from
    ///     <c>Environment.Version</c>. The distinction matters: a net8.0 worker rolls forward onto a
    ///     newer runtime, so the running runtime version would misreport which build is measuring.
    /// </summary>
    public static readonly string TargetFramework = ResolveTargetFramework();

    /// <summary>
    ///     The version of the engine assembly this worker will measure with. The coordinator
    ///     compares it against its own, because the worker unifies <c>NBenchmark</c> from its
    ///     default load context rather than loading the target's copy - so a skew would measure
    ///     against different engine code than the user compiled against, with no other symptom.
    /// </summary>
    public static readonly string EngineVersion =
        typeof(MeasurementOptions).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string ResolveTargetFramework()
    {
        var moniker = typeof(WorkerRuntime).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()
            ?.FrameworkName;

        if (string.IsNullOrWhiteSpace(moniker))
            return $"net{Environment.Version.Major}.{Environment.Version.Minor}";

        // ".NETCoreApp,Version=v10.0" -> "net10.0"
        var marker = moniker.IndexOf("Version=v", StringComparison.Ordinal);

        return marker < 0
            ? moniker
            : $"net{moniker[(marker + "Version=v".Length)..]}";
    }
}
