using System.Collections.Concurrent;
using System.Diagnostics;

namespace NBenchmark.Engine;

/// <summary>
///     Tracks every live measurement worker so none can outlive the coordinator that started it. A
///     worker is pure overhead once the coordinator is gone - it holds a CPU busy producing a number
///     nobody will read - so the coordinator takes its workers with it.
///     <para>
///         This is the <i>backstop</i>, not the primary mechanism. A worker blocks reading its
///         inbound pipe, so a coordinator that dies closes the write end, the read returns
///         end-of-stream and the worker exits on its own within a few milliseconds. What this covers
///         is the case that structure cannot: a worker wedged inside a benchmark body that never
///         returns, which is not reading the pipe to notice.
///     </para>
///     <para>
///         It also covers workers that were pre-spawned and never handed out - they are registered
///         at start, so <see cref="Workers.WorkerPrewarm" /> needs no teardown path of its own.
///     </para>
///     <para>
///         Hooks are installed lazily on the first <see cref="Track" /> call, so a process
///         that never spawns a worker registers no handlers and pays nothing.
///     </para>
/// </summary>
internal static class ChildProcessReaper
{
    /// <summary>How long a killed worker is given to actually exit before we stop waiting.</summary>
    internal const int KillGraceMilliseconds = 2_000;

    private static readonly ConcurrentDictionary<int, Process> Live = new();

    private static int _hooksInstalled;

    /// <summary>The number of workers currently being tracked. Test-visible.</summary>
    internal static int TrackedCount => Live.Count;

    /// <summary>
    ///     Registers a started worker. Call immediately after <see cref="Process.Start()" />,
    ///     passing the id captured at that point - reading <see cref="Process.Id" /> later can
    ///     throw once the object is disposed.
    /// </summary>
    public static void Track(int processId, Process process)
    {
        EnsureHooksInstalled();
        Live[processId] = process;
    }

    /// <summary>Removes a worker that exited (or was killed) under the coordinator's control.</summary>
    public static void Untrack(int processId) => Live.TryRemove(processId, out _);

    /// <summary>
    ///     Kills a worker and its whole process tree, then waits a bounded grace period for it to
    ///     exit. The tree matters because a benchmark body is arbitrary user code and may itself have
    ///     spawned helpers. Every failure mode is swallowed: the process may have exited between the
    ///     check and the kill, or the OS may refuse, and neither is actionable while tearing a run
    ///     down.
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (IsExpectedKillFailure(ex))
        {
            return;
        }

        try
        {
            process.WaitForExit(KillGraceMilliseconds);
        }
        catch (Exception ex) when (IsExpectedKillFailure(ex))
        {
            // The child is unreachable; nothing further to do.
        }
    }

    /// <summary>
    ///     Kills every tracked worker. Runs on process exit and on Ctrl-C, and is safe to call
    ///     more than once.
    /// </summary>
    internal static void KillAllTracked()
    {
        foreach (var (processId, process) in Live.ToArray())
        {
            Live.TryRemove(processId, out _);
            KillTree(process);
        }
    }

    /// <summary>
    ///     A kill or wait failed for a reason that is expected while tearing down: the worker
    ///     already exited, the handle is gone, or the OS refused. Anything else propagates.
    /// </summary>
    private static bool IsExpectedKillFailure(Exception ex)
        => ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or ObjectDisposedException;

    private static void EnsureHooksInstalled()
    {
        if (Interlocked.CompareExchange(ref _hooksInstalled, 1, 0) != 0)
            return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAllTracked();

        // Additive and deliberately non-cancelling: we do not set e.Cancel, so the host's own
        // Ctrl-C behaviour is unchanged and any handler the user registered still runs. This
        // exists because a Ctrl-C during a run is the most likely way to orphan a worker.
        try
        {
            Console.CancelKeyPress += (_, _) => KillAllTracked();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
        {
            // No console (a service host, or stdin redirected). ProcessExit still covers it.
        }
    }
}
