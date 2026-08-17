using System.Runtime.InteropServices;

namespace NBenchmark.Interop;

/// <summary>
///     Reads the calling <em>thread's</em> own CPU consumption since it started, so a measurement
///     can tell whether a sample actually held the CPU for the whole timed window - a fact the OS
///     knows and reports, rather than a guess inferred from the timing value alone.
///     <para>
///         Uses <c>[LibraryImport]</c> (source-generated, AOT- and trim-safe) rather than
///         <c>[DllImport]</c>, matching <see cref="NativeThreadControl" />. <see cref="TryRead" />
///         never throws: it returns <c>false</c> on any failure or on a platform with no thread-CPU
///         clock, so a caller degrades to "occupancy unknown" rather than failing a run over a
///         diagnostic reading.
///     </para>
///     <para>
///         The unit differs per platform and is never compared in absolute terms:
///     </para>
///     <list type="table">
///         <listheader><term>Platform</term><description>API / unit</description></listheader>
///         <item>
///             <term>Linux</term>
///             <description>
///                 <c>clock_gettime(CLOCK_THREAD_CPUTIME_ID, timespec*)</c>, nanoseconds. A real
///                 syscall rather than a vDSO-backed one, so it costs roughly 150-500 ns - cheap
///                 next to a typical sample, but not free.
///             </description>
///         </item>
///         <item>
///             <term>macOS</term>
///             <description>
///                 <c>clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID)</c>, nanoseconds. A Mach trap
///                 whose cost this repository does not advertise a number for - it is measured, not
///                 assumed, by <c>InterferenceCostProbe</c>, which is the guard that makes the
///                 feature safe to default on despite that.
///             </description>
///         </item>
///         <item>
///             <term>Windows</term>
///             <description>
///                 <c>QueryThreadCycleTime(GetCurrentThread(), out ulong)</c>, CPU cycles - a cheap
///                 call, but a different unit from the other two platforms.
///             </description>
///         </item>
///     </list>
///     <para>
///         Because Windows reports cycles while Linux and macOS report nanoseconds, no cross-platform
///         calibration is possible or needed. The engine only ever computes the per-sample occupancy
///         ratio <c>cpuDelta / wallDelta</c> and compares it against that <em>same benchmark's own</em>
///         median ratio - both sides of the comparison share whatever unit this platform happens to
///         read in, so the ratio is self-consistent within one run even though the raw numbers mean
///         different things on different hosts.
///     </para>
/// </summary>
internal static partial class ThreadCpuClock
{
    /// <summary><c>CLOCK_THREAD_CPUTIME_ID</c> on Linux, from <c>time.h</c>.</summary>
    private const int ClockThreadCpuTimeIdLinux = 3;

    /// <summary>
    ///     <c>CLOCK_THREAD_CPUTIME_ID</c> on Darwin, from <c>_time.h</c>. Verified against this
    ///     repository's development machine's SDK: Darwin numbers this constant differently from
    ///     Linux (16 vs. 3), so the two platforms cannot share one literal.
    /// </summary>
    private const int ClockThreadCpuTimeIdMac = 16;

    /// <summary>
    ///     Whether a thread-CPU-time read succeeds on this host, measured once per process so an
    ///     unsupported platform (or a locked-down sandbox that denies the syscall) pays exactly one
    ///     failed call rather than one per sample. Mirrors the once-per-process
    ///     <see cref="Lazy{T}" /> pattern in <c>ClockResolutionProbe</c>.
    /// </summary>
    private static readonly Lazy<bool> CachedAvailability =
        new(() => TryRead(out _), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     <c>true</c> when <see cref="TryRead" /> can produce a reading on this host. Callers should
    ///     check this before relying on the feature rather than reading <see cref="TryRead" />'s
    ///     return value sample-by-sample, since a platform without the API would otherwise report
    ///     "unavailable" freshly on every single sample.
    /// </summary>
    internal static bool IsAvailable => CachedAvailability.Value;

    /// <summary>
    ///     Reads the calling thread's cumulative CPU time (Linux/macOS, nanoseconds) or CPU cycle
    ///     count (Windows) since the thread started. Returns <c>false</c>, leaving
    ///     <paramref name="value" /> at <c>0</c>, when the platform has no thread-CPU-time API or the
    ///     call failed - this method never throws.
    /// </summary>
    internal static bool TryRead(out long value)
    {
        value = 0;

        if (OperatingSystem.IsLinux())
        {
            var ts = default(Timespec);

            if (Linux.clock_gettime(ClockThreadCpuTimeIdLinux, ref ts) != 0)
                return false;

            value = ts.Seconds * 1_000_000_000L + ts.Nanoseconds;
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            // Apple's documented failure mode for an invalid clock_id is a return of 0, which a
            // valid reading can also legitimately produce right at process start - a false negative
            // there is harmless (it just looks like "not available yet"), while treating an error as
            // success would poison every ratio computed from it.
            var ns = Mac.clock_gettime_nsec_np(ClockThreadCpuTimeIdMac);

            if (ns == 0)
                return false;

            value = unchecked((long)ns);
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            if (!Windows.QueryThreadCycleTime(Windows.GetCurrentThread(), out var cycles))
                return false;

            value = unchecked((long)cycles);
            return true;
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private static partial class Linux
    {
        [LibraryImport("libc", SetLastError = true)]
        internal static partial int clock_gettime(int clockId, ref Timespec ts);
    }

    private static partial class Mac
    {
        [LibraryImport("libSystem.dylib")]
        internal static partial ulong clock_gettime_nsec_np(int clockId);
    }

    private static partial class Windows
    {
        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr GetCurrentThread();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryThreadCycleTime(IntPtr threadHandle, out ulong cycleTime);
    }
}
