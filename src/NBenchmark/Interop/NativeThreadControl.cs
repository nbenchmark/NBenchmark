using System.Runtime.InteropServices;

namespace NBenchmark.Interop;

/// <summary>
///     The platform calls that control the *calling thread* - affinity on Linux and Windows,
///     quality-of-service class on macOS. Every entry point returns a <c>bool</c> and never
///     throws: an unavailable call is answered with <c>false</c> so the caller degrades to the
///     current behaviour rather than failing a run over a scheduler hint.
///     <para>
///         This is the first native interop in the engine. It uses <c>[LibraryImport]</c> rather
///         than <c>[DllImport]</c> so the marshalling stubs are source-generated, which keeps the
///         assembly trim- and AOT-safe; a <c>DllImport</c> would be resolved reflectively and
///         warn under both.
///     </para>
///     <para>
///         Thread scope, not process scope, is the point. <see cref="EnvironmentControl" /> pins
///         the whole process, which does not stop the runtime's own threads - finalizer,
///         background GC, tiering JIT - from sharing the pinned core, and on macOS the process
///         call does not exist at all. The macOS quality-of-service call is moreover *self-only*:
///         it is settable on the calling thread and no other, so it can only live here.
///     </para>
/// </summary>
internal static partial class NativeThreadControl
{
    /// <summary>
    ///     <c>QOS_CLASS_USER_INTERACTIVE</c>, from <c>sys/qos.h</c>. On Apple Silicon this is what
    ///     keeps a thread on the performance cores: a default-QoS thread is eligible for an
    ///     efficiency core, where the same body runs several times slower with nothing in the
    ///     timings to say so - it presents as a bimodal distribution.
    /// </summary>
    private const uint QosClassUserInteractive = 0x21;

    /// <summary>
    ///     The size handed to <c>sched_setaffinity</c> as <c>cpusetsize</c>. The kernel's real
    ///     <c>cpu_set_t</c> is larger, but it accepts any multiple of <c>sizeof(unsigned long)</c>
    ///     and treats the remaining CPUs as unset - which is what a mask covering CPUs 0-63 means.
    ///     Hosts with more than 64 logical CPUs cannot be addressed through this path, and
    ///     <see cref="EnvironmentControl.BuildAffinityMask" /> already produces a 64-bit mask, so
    ///     nothing is lost here that was available before.
    /// </summary>
    private const int CpuSetSize = sizeof(ulong);

    /// <summary>
    ///     Sets the calling thread's CPU affinity to <paramref name="mask" /> and reports the
    ///     previous mask through <paramref name="previous" />, so a scope can restore exactly what
    ///     it displaced rather than guessing at a default. Returns <c>false</c> on any platform
    ///     that does not support thread affinity (macOS) or when the call failed.
    /// </summary>
    internal static bool TrySetThreadAffinity(ulong mask, out ulong previous)
    {
        previous = 0;

        if (mask == 0)
            return false;

        if (OperatingSystem.IsLinux())
        {
            // Read before write: unlike Windows, sched_setaffinity does not report what it
            // replaced, and a scope that cannot restore is a scope that leaves the host pinned.
            ulong current = 0;

            if (Linux.sched_getaffinity(0, CpuSetSize, ref current) != 0)
                return false;

            // pid 0 is "the calling task", which for a threaded process is this thread and not the
            // process - the whole reason this is a thread-level control on Linux.
            if (Linux.sched_setaffinity(0, CpuSetSize, ref mask) != 0)
                return false;

            previous = current;
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            var prior = Windows.SetThreadAffinityMask(Windows.GetCurrentThread(), (nuint)mask);

            if (prior == 0)
                return false;

            previous = prior;
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Restores a mask captured by <see cref="TrySetThreadAffinity" />. Separate from the
    ///     setter because the restore path has nothing to capture and must not care whether the
    ///     call reports a prior value.
    /// </summary>
    internal static bool TryRestoreThreadAffinity(ulong mask)
    {
        if (mask == 0)
            return false;

        if (OperatingSystem.IsLinux())
            return Linux.sched_setaffinity(0, CpuSetSize, ref mask) == 0;

        if (OperatingSystem.IsWindows())
            return Windows.SetThreadAffinityMask(Windows.GetCurrentThread(), (nuint)mask) != 0;

        return false;
    }

    /// <summary>
    ///     <c>EPERM</c>. Darwin's pthread functions return an error number rather than setting
    ///     <c>errno</c>, so this is compared against the return value directly.
    /// </summary>
    internal const int Eperm = 1;

    /// <summary>
    ///     Raises the calling thread to <c>QOS_CLASS_USER_INTERACTIVE</c> on macOS and reports the
    ///     class it displaced through <paramref name="previousQosClass" />. Returns <c>false</c>
    ///     off macOS, or when either call failed; <paramref name="error" /> carries the Darwin
    ///     error number, which the caller uses to tell an unavailable API from a refusal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Measured on this platform, because the answer is not what the API suggests.</b>
    ///         Darwin refuses this call with <see cref="Eperm" /> on any thread that carries an
    ///         explicit scheduling priority, and the .NET runtime gives every thread it creates
    ///         one - a thread-pool thread and a <see cref="Thread" /> alike come back
    ///         <c>QOS_CLASS_UNSPECIFIED</c> and immutable. The <i>process main thread</i> is the
    ///         exception: it is created by the kernel, inherits the process's user-interactive
    ///         class, and accepts the call.
    ///     </para>
    ///     <para>
    ///         So this succeeds for a measurement that runs on the main thread and is refused for
    ///         one that has been handed to the thread pool - which is the honest description of
    ///         the control, and why the caller reports the refusal rather than treating a
    ///         <c>false</c> as nothing having happened.
    ///     </para>
    /// </remarks>
    internal static bool TrySetUserInteractiveQos(out uint previousQosClass, out int error)
    {
        previousQosClass = 0;
        error = 0;

        if (!OperatingSystem.IsMacOS())
            return false;

        error = Mac.pthread_get_qos_class_np(Mac.pthread_self(), out var current, out _);

        if (error != 0)
            return false;

        error = Mac.pthread_set_qos_class_self_np(QosClassUserInteractive, 0);

        if (error != 0)
            return false;

        previousQosClass = current;
        return true;
    }

    /// <summary>
    ///     Reads the calling thread's current quality-of-service class. Exists so a test can
    ///     assert that the elevation took effect rather than only that the call returned success -
    ///     the repository has no mocking library, so the seam is a method.
    /// </summary>
    internal static bool TryReadQos(out uint qosClass)
    {
        qosClass = 0;

        if (!OperatingSystem.IsMacOS())
            return false;

        return Mac.pthread_get_qos_class_np(Mac.pthread_self(), out qosClass, out _) == 0;
    }

    /// <summary>
    ///     <c>QOS_CLASS_USER_INTERACTIVE</c>, exposed for the assertion above.
    /// </summary>
    internal static uint UserInteractiveQosClass => QosClassUserInteractive;

    /// <summary>
    ///     Restores a quality-of-service class captured by
    ///     <see cref="TrySetUserInteractiveQos" />.
    /// </summary>
    internal static bool TryRestoreQos(uint qosClass)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        return Mac.pthread_set_qos_class_self_np(qosClass, 0) == 0;
    }

    private static partial class Linux
    {
        [LibraryImport("libc", SetLastError = true)]
        internal static partial int sched_getaffinity(int pid, nuint cpusetsize, ref ulong mask);

        [LibraryImport("libc", SetLastError = true)]
        internal static partial int sched_setaffinity(int pid, nuint cpusetsize, ref ulong mask);
    }

    private static partial class Windows
    {
        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr GetCurrentThread();

        /// <summary>
        ///     Returns the thread's previous affinity mask, or zero on failure - which is why the
        ///     Windows path needs no companion read call.
        /// </summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial nuint SetThreadAffinityMask(IntPtr thread, nuint affinityMask);
    }

    private static partial class Mac
    {
        [LibraryImport("libSystem.dylib")]
        internal static partial IntPtr pthread_self();

        [LibraryImport("libSystem.dylib")]
        internal static partial int pthread_get_qos_class_np(
            IntPtr thread,
            out uint qosClass,
            out int relativePriority);

        /// <summary>
        ///     Self-only by design: the Darwin API exposes no way to set another thread's class.
        /// </summary>
        [LibraryImport("libSystem.dylib")]
        internal static partial int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
    }
}
