using System.Runtime.CompilerServices;

namespace NBenchmark.Integration.MSTest.Tests;

/// <summary>
///     Silences the once-per-process guidance NBenchmark writes to stderr when a benchmark is
///     measured in the host process. These integration tests deliberately exercise the in-process
///     path (no measurement worker is deployed alongside the test assembly), so the guidance is
///     noise here rather than actionable. The env-var opt-outs are the public API the warnings
///     themselves recommend.
/// </summary>
internal static class TestProjectInitialization
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_ISOLATION_WARNING", "1");
        Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_RUNTIME_PROFILE_WARNING", "1");
    }
}