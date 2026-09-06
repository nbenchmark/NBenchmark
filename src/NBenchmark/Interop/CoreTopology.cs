using System.Runtime.InteropServices;

namespace NBenchmark.Interop;

/// <summary>
///     The host's split into performance and efficiency cores, where the platform will tell us.
///     <para>
///         <see cref="Environment.ProcessorCount" /> counts logical CPUs and says nothing about
///         whether they are the same speed. On Apple Silicon they are not: an M1 Max reports 10,
///         of which 8 are performance cores and 2 are efficiency cores several times slower. A
///         benchmark scheduled onto the latter reports a number that is not wrong about the
///         machine and is wrong about the code, with nothing in the timings to distinguish the
///         two - which is why the count is worth reading rather than assuming.
///     </para>
/// </summary>
internal static partial class CoreTopology
{
    /// <summary>
    ///     Read once per process. The values cannot change while the process runs, and
    ///     <c>sysctlbyname</c> is a syscall - <see cref="NBenchmark.Engine.EnvironmentControl.AssessHost" /> is
    ///     called from guidance, from the test-integration gates, and once per worker group.
    /// </summary>
    private static readonly Lazy<(int Performance, int Efficiency)> Cached = new(Read);

    /// <summary>
    ///     The number of performance ("P") cores, or <c>0</c> when the host does not report a
    ///     core split. Zero means <i>unknown</i>, never <i>none</i> - every host has at least one
    ///     core to run on, so a caller must treat zero as "no information" rather than as a count.
    /// </summary>
    internal static int PerformanceCoreCount => Cached.Value.Performance;

    /// <summary>
    ///     The number of efficiency ("E") cores, or <c>0</c> when the host does not report a core
    ///     split. A homogeneous host legitimately has zero of these, so this value is only
    ///     meaningful alongside a non-zero <see cref="PerformanceCoreCount" />.
    /// </summary>
    internal static int EfficiencyCoreCount => Cached.Value.Efficiency;

    /// <summary>
    ///     Reads the split. Kept <c>internal</c> and separate from the cache so a test can drive
    ///     it directly, following the <see cref="Engine.Detectors.ClockResolutionProbe" />
    ///     precedent - the repository has no mocking library, so a seam is a method rather than an
    ///     interface.
    /// </summary>
    /// <remarks>
    ///     Only macOS is implemented. Linux exposes the same information through
    ///     <c>/sys/devices/system/cpu/cpu*/cpu_capacity</c> on heterogeneous ARM hosts and Windows
    ///     through <c>GetSystemCpuSetInformation</c>; neither is read here, because neither is a
    ///     host the engine currently gives different advice about. Both return "unknown", which is
    ///     the honest answer rather than a fabricated one.
    /// </remarks>
    internal static (int Performance, int Efficiency) Read()
    {
        if (!OperatingSystem.IsMacOS())
            return (0, 0);

        // hw.nperflevels is the count of performance *levels*, not of cores. One level is a
        // homogeneous machine (every Intel Mac, and the base M-series in some configurations),
        // where there is no split to report and the P/E distinction does not apply.
        if (!TryReadSysctl("hw.nperflevels", out var levels) || levels < 2)
            return (0, 0);

        // Level 0 is always the fastest; levels are ordered by descending performance. Anything
        // past level 1 is folded into the efficiency count rather than dropped, so a future
        // three-level part is described conservatively instead of partially.
        if (!TryReadSysctl("hw.perflevel0.logicalcpu", out var performance) || performance <= 0)
            return (0, 0);

        var efficiency = 0;

        for (var level = 1; level < levels; level++)
        {
            if (TryReadSysctl($"hw.perflevel{level}.logicalcpu", out var count) && count > 0)
                efficiency += (int)count;
        }

        return ((int)performance, efficiency);
    }

    /// <summary>
    ///     Reads one integer sysctl by name. Returns <c>false</c> for an absent key, a key of a
    ///     different width, or any failure - the caller treats every one of those as "unknown".
    /// </summary>
    private static bool TryReadSysctl(string name, out long value)
    {
        value = 0;

        try
        {
            // sysctl reports these as 32- or 64-bit depending on the key, and writes only as many
            // bytes as the key holds. Starting from a zeroed 64-bit slot means a 32-bit answer
            // lands in the low half correctly on a little-endian host, which every Darwin host is.
            long buffer = 0;
            var length = (nuint)sizeof(long);

            if (Native.sysctlbyname(name, ref buffer, ref length, IntPtr.Zero, 0) != 0)
                return false;

            if (length is not (4 or 8))
                return false;

            value = buffer;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static partial class Native
    {
        [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int sysctlbyname(
            string name,
            ref long oldp,
            ref nuint oldlenp,
            IntPtr newp,
            nuint newlen);
    }
}
