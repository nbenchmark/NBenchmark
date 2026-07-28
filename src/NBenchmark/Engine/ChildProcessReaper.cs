using System.Collections.Concurrent;
using System.Diagnostics;

namespace NBenchmark.Engine;

/// <summary>
///     Tracks every live isolated child process so none can outlive its parent. A benchmark
///     child is pure overhead once the parent is gone - it holds a CPU busy running a
///     measurement nobody will read - so the parent must take its children with it.
///     <para>
///         Previously the launcher never called <see cref="Process.Kill(bool)" /> at all: on
///         cancellation the <see cref="OperationCanceledException" /> escaped, the
///         <c>finally</c> deleted the request and output temp files, and the child kept
///         running against files that no longer existed. This closes that hole.
///     </para>
///     <para>
///         Hooks are installed lazily on the first <see cref="Track" /> call, so a process
///         that never spawns a child registers no handlers and pays nothing.
///     </para>
/// </summary>
internal static class ChildProcessReaper
{
    /// <summary>How long a killed child is given to actually exit before we stop waiting.</summary>
    internal const int KillGraceMilliseconds = 2_000;

    private static readonly ConcurrentDictionary<int, Process> Live = new();

    private static int _hooksInstalled;

    /// <summary>The number of children currently being tracked. Test-visible.</summary>
    internal static int TrackedCount => Live.Count;

    /// <summary>
    ///     Registers a started child. Call immediately after <see cref="Process.Start()" />,
    ///     passing the id captured at that point - reading <see cref="Process.Id" /> later can
    ///     throw once the object is disposed.
    /// </summary>
    public static void Track(int processId, Process process)
    {
        EnsureHooksInstalled();
        Live[processId] = process;
    }

    /// <summary>Removes a child that exited (or was killed) under the launcher's control.</summary>
    public static void Untrack(int processId) => Live.TryRemove(processId, out _);

    /// <summary>
    ///     Kills a child and its whole process tree, then waits a bounded grace period for it
    ///     to exit. The tree matters because the child re-runs the user's entry point, which
    ///     may itself have spawned helpers. Every failure mode is swallowed: the process may
    ///     have exited between the check and the kill, or the OS may refuse, and neither is
    ///     actionable while tearing a run down.
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
    ///     Kills every tracked child. Runs on process exit and on Ctrl-C, and is safe to call
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
    ///     A kill or wait failed for a reason that is expected while tearing down: the child
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
        // exists because a Ctrl-C during a run is the most likely way to orphan a child.
        try
        {
            Console.CancelKeyPress += (_, _) => KillAllTracked();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
        {
            // No console (a service host, or stdin redirected). ProcessExit still covers us.
        }
    }
}
