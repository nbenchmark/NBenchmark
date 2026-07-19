using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace NBenchmark.Engine;

/// <summary>
///     Applies and restores the opt-in hardware/OS controls carried by
///     <see cref="EnvironmentOptions" />. A single apply call returns a disposable
///     scope that restores the prior process affinity and priority on dispose, so a
///     run never leaves the host in an elevated or pinned state.
///     <para>
///         This is the proactive counterpart to the statistical noise handling in
///         <c>OutlierDetectors</c> and <c>BimodalDetector</c>: those modules react to noise
///         after the fact; this module reduces noise at the source (CPU migration, priority
///         preemption, shared-host jitter) before the timer starts.
///     </para>
/// </summary>
public static class EnvironmentControl
{
    /// <summary>
    ///     Applies <paramref name="options" /> to the current process and returns a scope
    ///     that restores the prior state on dispose. <c>null</c> options (the default) is
    ///     a no-op and returns a no-op scope, so callers do not need to branch.
    /// </summary>
    /// <remarks>
    ///     Errors while applying affinity or priority are swallowed and surfaced as
    ///     console warnings instead of throwing - a benchmark run should never fail
    ///     because the host refused a priority bump (common on locked-down CI runners).
    ///     The dedicated-host guidance probe is always run when requested, independent of
    ///     whether the other settings were successfully applied.
    /// </remarks>
    public static IDisposable Apply(EnvironmentOptions? options)
    {
        if (options is null || (options.CpuAffinity is null
                                && options.ProcessPriority is null
                                && !options.DedicatedHostGuidance))
            return NoOpScope.Instance;

        var process = Process.GetCurrentProcess();

        // ProcessorAffinity is only supported on Linux and Windows. Capture the prior
        // mask only on those platforms so RestoreScope has something valid to write back;
        // on macOS affinity is never applied, so no prior value is needed.
        var affinitySupported = AffinitySupported();
        var priorAffinity = affinitySupported ? process.ProcessorAffinity : IntPtr.Zero;
        var priorPriority = process.PriorityClass;
        var affinityApplied = false;
        var priorityApplied = false;

        if (options.CpuAffinity is { } affinity)
        {
            // ProcessorAffinity is only supported on Linux and Windows; on macOS the
            // setaffinity syscall is not exposed by the BCL. We still accept the option
            // (a user's config should not crash on a dev laptop) but skip the apply and
            // let the guidance probe explain why.
            if (affinitySupported)
            {
                try
                {
                    process.ProcessorAffinity = BuildAffinityMask(affinity);
                    affinityApplied = true;
                }
                catch (Exception ex) when (IsApplyOrRestoreException(ex))
                {
                    Console.Error.WriteLine(
                        $"Warning: could not set CPU affinity to [{string.Join(", ", affinity)}]: {ex.Message}");
                }
            }
            else
            {
                Console.Error.WriteLine(
                    "Warning: --cpu-affinity is not supported on macOS and was ignored. "
                    + "Pin to a Linux or Windows host for CPU affinity control.");
            }
        }

        if (options.ProcessPriority is { } priority)
        {
            try
            {
                process.PriorityClass = priority;
                priorityApplied = true;
            }
            catch (Exception ex) when (IsApplyOrRestoreException(ex))
            {
                Console.Error.WriteLine(
                    $"Warning: could not raise process priority to {priority}: {ex.Message}");
            }
        }

        if (options.DedicatedHostGuidance)
            EmitDedicatedHostGuidance(affinityApplied, priorityApplied);

        return new RestoreScope(process, priorAffinity, priorPriority, affinityApplied, priorityApplied);
    }

    /// <summary>
    ///     Assesses the current host for benchmark suitability. Returns a
    ///     <see cref="HostAssessment" /> with the core count, macOS flag, and a
    ///     <see cref="HostAssessment.IsSharedRunner" /> flag that is <c>true</c> when
    ///     the host looks like a shared or unsuitable benchmark environment (fewer than
    ///     4 logical CPUs, or macOS with its unobservable frequency scaling).
    /// </summary>
    public static HostAssessment AssessHost()
    {
        var coreCount = Environment.ProcessorCount;
        var isMac = OperatingSystem.IsMacOS();
        var isShared = coreCount < 4 || isMac;

        return new HostAssessment(coreCount, isMac, isShared);
    }

    /// <summary>
    ///     Emits a non-fatal warning when the host looks like a shared or unsuitable
    ///     benchmark environment. Called after the apply attempt so the message can note
    ///     whether affinity/priority were actually applied.
    /// </summary>
    private static void EmitDedicatedHostGuidance(bool affinityApplied, bool priorityApplied)
    {
        var assessment = AssessHost();
        var warnings = new List<string>();

        if (assessment.IsSharedRunner && assessment.CoreCount < 4)
        {
            warnings.Add(
                $"Only {assessment.CoreCount} logical CPU(s) detected. Microbenchmarking on a "
                + "shared-tenant host (common in CI) inflates noise; pin to a dedicated host when "
                + "comparing baselines.");
        }

        if (assessment.IsMacOS)
        {
            warnings.Add(
                "On macOS, frequency scaling and thermal throttling are not directly observable. "
                + "Run on wall power with minimal background load, and prefer a dedicated Linux/Windows "
                + "host for CI regression gates.");
        }

        if (!priorityApplied && assessment.CoreCount >= 4)
        {
            warnings.Add(
                "Process priority was not raised. Add --priority high (or WithProcessPriority) "
                + "to reduce preemption by unrelated OS work and tighten the measurement tail.");
        }

        if (warnings.Count == 0)
            return;

        Console.Error.WriteLine("Dedicated-host guidance:");

        foreach (var w in warnings)
        {
            Console.Error.WriteLine($"  - {w}");
        }
    }

    /// <summary>
    ///     Builds the <see cref="IntPtr" /> affinity mask from a list of logical CPU
    ///     indices. Validates that every index is within the current machine's logical
    ///     core count and throws <see cref="ArgumentException" /> with a useful message
    ///     otherwise (the caller catches this and surfaces it as a warning).
    /// </summary>
    internal static IntPtr BuildAffinityMask(IReadOnlyList<int> affinity)
    {
        var coreCount = Environment.ProcessorCount;
        long mask = 0;

        foreach (var idx in affinity)
        {
            if (idx < 0 || idx >= coreCount)
            {
                throw new ArgumentException(
                    $"CPU index {idx} is out of range for this host (0..{coreCount - 1}).",
                    nameof(affinity));
            }

            mask |= 1L << idx;
        }

        if (IntPtr.Size < 8 && mask >> (IntPtr.Size * 8) != 0)
        {
            throw new ArgumentException(
                "CPU affinity mask exceeds the addressable bit width on this 32-bit host.",
                nameof(affinity));
        }

        if (mask == 0)
            throw new ArgumentException("CPU affinity list is empty.", nameof(affinity));

        return (IntPtr)mask;
    }

    /// <summary>
    ///     The exception filter for apply/restore: swallow the OS- and argument-level
    ///     failures that are expected on locked-down or unusual hosts. Anything else
    ///     (e.g. <see cref="OperationCanceledException" />) propagates.
    /// </summary>
    private static bool IsApplyOrRestoreException(Exception ex)
        => ex is ArgumentException
            or Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException
            or UnauthorizedAccessException;

    /// <summary>
    ///     <see cref="Process.ProcessorAffinity" /> is only supported on Linux and
    ///     Windows. The <c>[SupportedOSPlatformGuard]</c> attribute teaches the CA1416
    ///     analyzer that a <c>true</c> return from this method means the surrounding
    ///     platform-specific call is safe, silencing the warning at guarded sites while
    ///     keeping the method itself callable on every TFM.
    /// </summary>
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("windows")]
    private static bool AffinitySupported() => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    /// <summary>An no-op scope returned when environment control is a no-op.</summary>
    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     Restores the prior process affinity and priority on dispose. Only restores
    ///     the fields that were actually changed.
    /// </summary>
    private sealed class RestoreScope : IDisposable
    {
        private readonly bool _affinityApplied;
        private readonly IntPtr _priorAffinity;
        private readonly bool _priorityApplied;
        private readonly ProcessPriorityClass _priorPriority;
        private readonly Process _process;
        private bool _disposed;

        public RestoreScope(
            Process process,
            IntPtr priorAffinity,
            ProcessPriorityClass priorPriority,
            bool affinityApplied,
            bool priorityApplied)
        {
            _process = process;
            _priorAffinity = priorAffinity;
            _priorPriority = priorPriority;
            _affinityApplied = affinityApplied;
            _priorityApplied = priorityApplied;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_affinityApplied && AffinitySupported())
            {
                try
                {
                    _process.ProcessorAffinity = _priorAffinity;
                }
                catch (Exception ex) when (IsApplyOrRestoreException(ex))
                {
                    Console.Error.WriteLine($"Warning: could not restore CPU affinity: {ex.Message}");
                }
            }

            if (_priorityApplied)
            {
                try
                {
                    _process.PriorityClass = _priorPriority;
                }
                catch (Exception ex) when (IsApplyOrRestoreException(ex))
                {
                    Console.Error.WriteLine($"Warning: could not restore process priority: {ex.Message}");
                }
            }

            _process.Dispose();
        }
    }
}
