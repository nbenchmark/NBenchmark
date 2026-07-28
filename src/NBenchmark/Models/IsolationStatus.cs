namespace NBenchmark;

/// <summary>
///     Where a measurement actually ran, and - when it did not run in a worker - why.
///     <para>
///         <see cref="BenchmarkResult.RuntimeProfileName" /> already says <i>what</i> configuration a
///         result was measured under. This says <i>why</i> it was that one. The distinction matters
///         because every reason below has a different remedy, and a user looking at a result stamped
///         <c>host</c> otherwise has no way to tell whether they asked for that, whether a package is
///         missing, or whether their benchmark is shaped in a way that cannot cross a process
///         boundary.
///     </para>
/// </summary>
public enum IsolationStatus
{
    /// <summary>
    ///     Measured in a dedicated worker process launched with the requested runtime profile. This
    ///     is the only status under which the reported runtime configuration was chosen rather than
    ///     inherited.
    /// </summary>
    Isolated = 0,

    /// <summary>
    ///     Measured in the host process because that is what was asked for - <c>--in-process</c>,
    ///     <c>WithIsolation(false)</c>, <c>[InProcess]</c>, <c>--dry-run</c>, or
    ///     <c>Benchmark.RunInProcess</c>. Nothing was refused; the numbers still inherit the host's
    ///     runtime configuration and are labelled accordingly.
    /// </summary>
    InProcessRequested = 1,

    /// <summary>
    ///     Measured in the host process because the benchmark body captures state from its enclosing
    ///     scope. Captured values live only in this process, and reconstructing them in a worker was
    ///     found to return plausible but silently wrong numbers rather than failing, so isolation is
    ///     refused instead. Rewrite the body so it captures nothing - move the state into a field on
    ///     a benchmark class, or into a <c>[BenchmarkPlan]</c> factory the worker can invoke.
    /// </summary>
    InProcessCapturedState = 2,

    /// <summary>
    ///     Measured in the host process because its instances come from live code in this process -
    ///     an instance factory, a service provider, or a test fixture. A worker can construct a type
    ///     but cannot reproduce a factory it has never seen.
    /// </summary>
    InProcessLiveFixture = 3,

    /// <summary>
    ///     Measured in the host process because the suite is built inline and has no addressable
    ///     entry point for a worker to call. Add a <c>[BenchmarkPlan]</c> factory to make it
    ///     isolatable.
    /// </summary>
    InProcessUnaddressablePlan = 4,

    /// <summary>
    ///     Measured in the host process because no measurement worker was available - usually an
    ///     incomplete package restore, or <c>NBenchmarkDeployWorker=false</c>. This is the one status
    ///     that indicates a deployment problem rather than a property of the benchmark.
    /// </summary>
    InProcessNoWorker = 5,
}

/// <summary>Presentation helpers for <see cref="IsolationStatus" />.</summary>
public static class IsolationStatusExtensions
{
    /// <summary>Whether the measurement ran in a process NBenchmark launched and configured.</summary>
    public static bool IsIsolated(this IsolationStatus status) => status == IsolationStatus.Isolated;

    /// <summary>
    ///     A short column label. Kept to a few characters because it appears in a table alongside
    ///     numbers, where a sentence would push the measurements off the screen.
    /// </summary>
    public static string ToLabel(this IsolationStatus status) => status switch
    {
        IsolationStatus.Isolated => "isolated",
        IsolationStatus.InProcessRequested => "in-process",
        IsolationStatus.InProcessCapturedState => "in-process (captures)",
        IsolationStatus.InProcessLiveFixture => "in-process (fixture)",
        IsolationStatus.InProcessUnaddressablePlan => "in-process (inline)",
        IsolationStatus.InProcessNoWorker => "in-process (no worker)",
        _ => "in-process",
    };

    /// <summary>
    ///     A one-line explanation of what to do about it, for a table footer. <c>null</c> when there
    ///     is nothing to act on.
    /// </summary>
    public static string? ToRemedy(this IsolationStatus status) => status switch
    {
        IsolationStatus.InProcessCapturedState =>
            "rewrite the body so it captures nothing from its enclosing scope, or move it into a "
            + "benchmark class",
        IsolationStatus.InProcessLiveFixture =>
            "instances come from a factory or fixture this process owns; a worker cannot reproduce it",
        IsolationStatus.InProcessUnaddressablePlan =>
            "add a [BenchmarkPlan] factory so a worker can build the suite itself",
        IsolationStatus.InProcessNoWorker =>
            "the nbworker measurement host is not deployed; try a clean restore",
        _ => null,
    };
}
