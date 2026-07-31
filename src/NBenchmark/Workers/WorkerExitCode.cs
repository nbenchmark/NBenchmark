namespace NBenchmark.Workers;

/// <summary>
///     Exit codes the coordinator can distinguish when a worker dies early.
/// </summary>
/// <remarks>
///     Declared on the coordinator's side of the boundary rather than inside <c>nbworker</c>, because
///     both processes have an interest in them: the worker returns them and
///     <see cref="WorkerHost.ExitDescription" /> reports them, so a diagnostic that says "exit code
///     71" can be traced to a meaning without opening the worker's source.
/// </remarks>
internal static class WorkerExitCode
{
    public const int Success = 0;
    public const int BadArguments = 64;
    public const int NoHandshake = 65;
    public const int ProtocolError = 66;
    public const int Crashed = 70;

    /// <summary>
    ///     The coordinator's end of the pipe closed while a group was being measured, so the worker
    ///     stopped measuring and exited on its own.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="Success" /> on purpose. Both are self-chosen exits, but this one
    ///     says the worker <i>noticed it had been orphaned</i> - which is the difference between the
    ///     orphan-avoidance mechanism working and a worker that happened to finish anyway.
    /// </remarks>
    public const int CoordinatorLost = 71;
}
