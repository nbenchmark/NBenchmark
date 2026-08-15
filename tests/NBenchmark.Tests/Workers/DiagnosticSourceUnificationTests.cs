using System.Reflection;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Guards the worker's deferral of <c>System.Diagnostics.DiagnosticSource</c> to the default
///     load context.
/// </summary>
/// <remarks>
///     <para>
///         This protects against a failure with no symptom. <c>ActivitySource</c> and <c>Meter</c>
///         publish to static state inside <c>DiagnosticSource</c>. If the worker's load context
///         resolves a second copy of that assembly from the target's output, an OpenTelemetry SDK
///         built inside the worker subscribes to one registry while the engine publishes to the
///         other: no exception, no warning, and no telemetry from any isolated run.
///     </para>
///     <para>
///         It is also framework-dependent, which is what makes it easy to ship broken. Under
///         <c>net10.0</c> the shared framework supplies the version <c>OpenTelemetry.Api</c> asks
///         for and nothing is copied next to the target, so the whole question disappears and a
///         <c>net10.0</c>-only test matrix passes either way. Under <c>net8.0</c> and <c>net9.0</c>
///         NuGet copies its own, and the deferral is the only thing preventing the split.
///     </para>
///     <para>
///         Asserted through reflection on the private list because the alternative - spawning a
///         worker against a multi-targeted fixture and inspecting the trace it exports - is an
///         end-to-end test with a collector in it. <c>samples/Telemetry</c> multi-targets
///         <c>net8.0</c> for exactly that check; this is the unit-level tripwire that fails first
///         and says why.
///     </para>
/// </remarks>
public class DiagnosticSourceUnificationTests
{
    [Fact]
    public void The_Worker_Unifies_DiagnosticSource_With_The_Default_Context()
    {
        Assert.Contains("System.Diagnostics.DiagnosticSource", EngineAssemblies());
    }

    [Fact]
    public void The_Worker_Unifies_Every_Package_Whose_Types_Cross_The_Boundary()
    {
        // NBenchmark itself is the one that must never be reloaded: the target's output contains a
        // copy, and loading it would produce a second BenchmarkAttribute type, so discovery would
        // find no benchmarks at all and report no error.
        Assert.Contains("NBenchmark", EngineAssemblies());
    }

    private static IReadOnlyCollection<string> EngineAssemblies()
    {
        // Loaded from the path baked in at build time rather than by simple name: nbworker is an
        // executable the tests launch, not a reference they compile against, so it is not on the
        // probing path of this assembly.
        var workerAssembly = Assembly.LoadFrom(WorkerLocatorForTests.WorkerAssemblyPath());

        var loadContext = workerAssembly.GetType("NBenchmark.Worker.BenchmarkLoadContext", throwOnError: true)!;

        var field = loadContext.GetField("EngineAssemblies", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        return (HashSet<string>)field.GetValue(null)!;
    }
}
