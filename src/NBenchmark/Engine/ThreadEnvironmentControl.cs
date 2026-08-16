using System.Diagnostics;
using System.Runtime.Versioning;
using NBenchmark.Interop;

namespace NBenchmark.Engine;

/// <summary>
///     Applies the OS controls that belong to the *thread* running the measurement loop, and
///     restores them on dispose. The sibling of <see cref="EnvironmentControl" />, which applies
///     the process-scoped ones.
///     <para>
///         Two things the process scope cannot do. Pinning a process does not stop the runtime's
///         own threads - finalizer, background GC, tiering JIT - from sharing the pinned core, so
///         the measured thread still contends with the runtime that hosts it. And on macOS the
///         process-level call does not exist at all: what decides whether a thread runs on an
///         Apple Silicon performance core is its quality-of-service class, which is settable only
///         on the calling thread.
///     </para>
///     <para>
///         The macOS half of that is bounded by something the API does not advertise and this
///         repository measured: Darwin refuses the class change on any thread carrying an explicit
///         scheduling priority, which is every thread the .NET runtime creates. It therefore
///         applies on the process main thread and is refused on a thread-pool thread. The refusal
///         is reported rather than swallowed - see
///         <see cref="NativeThreadControl.TrySetUserInteractiveQos" />.
///     </para>
///     <para>
///         Same contract as <see cref="EnvironmentControl.Apply" />: never throws, warns and
///         proceeds on a refusal, and restores on dispose. A benchmark run must not fail because
///         a host declined a scheduler hint.
///     </para>
/// </summary>
public static class ThreadEnvironmentControl
{
    /// <summary>
    ///     Once-per-process guard for the "quality of service could not be raised" warning. The
    ///     control is on by default and the scope opens once per worker group, so a host where
    ///     the call is unavailable would otherwise print the same line on every group.
    /// </summary>
    private static int _qosWarningEmitted;

    /// <summary>
    ///     Test-only hook: resets the once-per-process warning guard so a fixture can drive the
    ///     warning path more than once.
    /// </summary>
    internal static void ResetQosWarningGuard() => Interlocked.Exchange(ref _qosWarningEmitted, 0);

    /// <summary>
    ///     Applies <paramref name="options" /> to the <b>calling</b> thread and returns a scope
    ///     that restores the prior state on dispose. <c>null</c> options is <i>not</i> a no-op:
    ///     the macOS quality-of-service elevation is on by default and needs no configuration, so
    ///     a caller that set nothing still gets it. Opt out with
    ///     <see cref="EnvironmentOptions.ThreadControl" /> (<c>--no-thread-control</c>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What is applied, per platform:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <b>Linux</b> - <c>sched_setaffinity</c> on the calling task, when
    ///                 <see cref="EnvironmentOptions.CpuAffinity" /> is set. Thread priority is
    ///                 deliberately not touched: under the default <c>SCHED_OTHER</c> policy a
    ///                 thread priority is a no-op, and applying one would advertise a control that
    ///                 does nothing.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>Windows</b> - <c>SetThreadAffinityMask</c> when
    ///                 <see cref="EnvironmentOptions.CpuAffinity" /> is set, and a matching
    ///                 <see cref="Thread.Priority" /> when
    ///                 <see cref="EnvironmentOptions.ProcessPriority" /> is set.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>macOS</b> - <c>QOS_CLASS_USER_INTERACTIVE</c> on the calling thread,
    ///                 <i>where Darwin permits it</i>. Thread affinity is not available at all.
    ///                 See <see cref="NativeThreadControl.TrySetUserInteractiveQos" />: the
    ///                 runtime's own threads carry an explicit scheduling priority and their class
    ///                 is immutable, so the elevation lands for a measurement running on the
    ///                 process main thread and is refused - and reported - for one running on the
    ///                 thread pool.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         The scope is bound to the thread that opened it. An <c>await</c> can resume a
    ///         measurement on a different thread, and restoring a mask onto a thread that never
    ///         carried it would pin an unrelated pool thread - so a dispose that arrives on
    ///         another thread restores nothing. The applied thread's own settings die with it,
    ///         which for a single-use worker process is the whole of its lifetime.
    ///     </para>
    /// </remarks>
    public static IDisposable Apply(EnvironmentOptions? options)
    {
        if (options is { ThreadControl: false })
            return NoOpScope.Instance;

        var priorAffinity = 0UL;
        var affinityApplied = false;

        if (options?.CpuAffinity is { } affinity && ThreadAffinitySupported())
        {
            try
            {
                var mask = (ulong)(long)EnvironmentControl.BuildAffinityMask(affinity);

                // Held for the lifetime of the scope, not just the call. The CLR is free to move a
                // managed thread between OS threads in a hosted scenario, which would strand the
                // mask on a thread nothing is measuring on.
                Thread.BeginThreadAffinity();

                if (NativeThreadControl.TrySetThreadAffinity(mask, out priorAffinity))
                {
                    affinityApplied = true;
                }
                else
                {
                    Thread.EndThreadAffinity();

                    Console.Error.WriteLine(
                        $"Warning: could not pin the measurement thread to [{string.Join(", ", affinity)}]. "
                        + "The process affinity, if applied, still holds.");
                }
            }
            catch (Exception ex) when (IsApplyException(ex))
            {
                Console.Error.WriteLine(
                    $"Warning: could not pin the measurement thread to [{string.Join(", ", affinity)}]: {ex.Message}");
            }
        }

        var priorPriority = Thread.CurrentThread.Priority;
        var priorityApplied = false;

        // Windows only, on purpose. See the remarks: a SCHED_OTHER thread priority is a no-op, so
        // setting one on Linux would be a control that reports success and changes nothing.
        if (options?.ProcessPriority is { } priority && OperatingSystem.IsWindows())
        {
            try
            {
                Thread.CurrentThread.Priority = ToThreadPriority(priority);
                priorityApplied = true;
            }
            catch (Exception ex) when (IsApplyException(ex))
            {
                Console.Error.WriteLine(
                    $"Warning: could not raise the measurement thread's priority: {ex.Message}");
            }
        }

        var priorQos = 0U;
        var qosApplied = false;

        if (OperatingSystem.IsMacOS())
        {
            if (NativeThreadControl.TrySetUserInteractiveQos(out priorQos, out var qosError))
            {
                qosApplied = true;
            }
            else if (Interlocked.CompareExchange(ref _qosWarningEmitted, 1, 0) == 0)
            {
                Console.Error.WriteLine(
                    qosError == NativeThreadControl.Eperm
                        ? "Note: this measurement runs on a thread the runtime created, whose "
                          + "quality-of-service class macOS will not let us change - the runtime gives "
                          + "such threads an explicit scheduling priority, and Darwin treats that as "
                          + "opting out of quality of service. The class is left unspecified, which is "
                          + "eligible for a performance core but not pinned to one. Only the process "
                          + "main thread, used by Single mode and by an in-process suite, can be raised."
                        : "Warning: could not raise the measurement thread to user-interactive quality "
                          + "of service. On Apple Silicon the thread may be scheduled onto an efficiency "
                          + "core, where the same code runs several times slower - which presents as a "
                          + "bimodal distribution rather than as an error.");
            }
        }

        if (!affinityApplied && !priorityApplied && !qosApplied)
            return NoOpScope.Instance;

        return new RestoreScope(
            Environment.CurrentManagedThreadId,
            priorAffinity,
            affinityApplied,
            priorPriority,
            priorityApplied,
            priorQos,
            qosApplied);
    }

    /// <summary>
    ///     Maps a process priority class onto the nearest managed thread priority, so
    ///     <c>--priority high</c> means the same thing at both scopes rather than raising the
    ///     process and leaving the measuring thread at its default within it.
    /// </summary>
    internal static ThreadPriority ToThreadPriority(ProcessPriorityClass priority)
        => priority switch
        {
            ProcessPriorityClass.Idle => ThreadPriority.Lowest,
            ProcessPriorityClass.BelowNormal => ThreadPriority.BelowNormal,
            ProcessPriorityClass.AboveNormal => ThreadPriority.AboveNormal,
            ProcessPriorityClass.High => ThreadPriority.Highest,
            ProcessPriorityClass.RealTime => ThreadPriority.Highest,
            _ => ThreadPriority.Normal,
        };

    /// <summary>
    ///     Thread affinity exists on Linux and Windows. macOS exposes no thread-affinity API at
    ///     all - the quality-of-service class is the placement control there, and it is what this
    ///     scope applies instead.
    /// </summary>
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("windows")]
    internal static bool ThreadAffinitySupported()
        => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    /// <summary>
    ///     The exception filter for the apply path: swallow the argument- and OS-level failures a
    ///     locked-down or unusual host produces. Anything else propagates.
    /// </summary>
    private static bool IsApplyException(Exception ex)
        => ex is ArgumentException
            or InvalidOperationException
            or PlatformNotSupportedException
            or UnauthorizedAccessException;

    /// <summary>The scope returned when nothing was applied.</summary>
    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     Restores the thread state this scope displaced. Only the settings actually applied are
    ///     restored, and only when dispose arrives on the thread that applied them.
    /// </summary>
    private sealed class RestoreScope : IDisposable
    {
        private readonly bool _affinityApplied;
        private readonly ulong _priorAffinity;
        private readonly ThreadPriority _priorPriority;
        private readonly uint _priorQos;
        private readonly bool _priorityApplied;
        private readonly bool _qosApplied;
        private readonly int _threadId;
        private bool _disposed;

        public RestoreScope(
            int threadId,
            ulong priorAffinity,
            bool affinityApplied,
            ThreadPriority priorPriority,
            bool priorityApplied,
            uint priorQos,
            bool qosApplied)
        {
            _threadId = threadId;
            _priorAffinity = priorAffinity;
            _affinityApplied = affinityApplied;
            _priorPriority = priorPriority;
            _priorityApplied = priorityApplied;
            _priorQos = priorQos;
            _qosApplied = qosApplied;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            // A continuation resumed elsewhere: restoring here would write this scope's captured
            // state onto a thread that never carried it. Leaving the original thread as it is
            // costs nothing - a worker process is single-use, and an in-process run's pool thread
            // is returned to a pool the run no longer draws from.
            if (Environment.CurrentManagedThreadId != _threadId)
                return;

            if (_affinityApplied)
            {
                NativeThreadControl.TryRestoreThreadAffinity(_priorAffinity);
                Thread.EndThreadAffinity();
            }

            if (_priorityApplied)
            {
                try
                {
                    Thread.CurrentThread.Priority = _priorPriority;
                }
                catch (Exception ex) when (IsApplyException(ex))
                {
                    Console.Error.WriteLine(
                        $"Warning: could not restore the measurement thread's priority: {ex.Message}");
                }
            }

            if (_qosApplied)
                NativeThreadControl.TryRestoreQos(_priorQos);
        }
    }
}
