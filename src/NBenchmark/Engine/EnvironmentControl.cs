using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
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
    ///     The env var opt-out for the always-on Debug-build / debugger-attached warning.
    ///     Set to <c>"1"</c> to suppress; the
    ///     <see cref="EnvironmentOptions.SuppressBuildConfigurationWarning" /> flag is the
    ///     programmatic equivalent. Mirrors the <c>CI=true</c> opt-out convention used by
    ///     auto-attached reporters, so CLI-only callers can silence the warning without
    ///     changing code.
    /// </summary>
    internal const string SuppressDebugWarningEnvVar = "NBENCHMARK_SUPPRESS_DEBUG_WARNING";

    /// <summary>
    ///     Once-per-process guard for <see cref="EmitBuildConfigurationGuidance" />. Both
    ///     the Single-mode facade (<c>Benchmark.Run</c>) and the Suite/Harness paths
    ///     (through <see cref="Apply" />) call it, and isolated children re-enter
    ///     <c>RunAsync</c> - without this guard the same parent process would warn twice
    ///     (once from the facade call, once from the suite/harness <c>Apply</c> call), and
    ///     a child re-running the entry assembly would re-emit the parent's warning.
    /// </summary>
    private static int _buildConfigWarningEmitted;

    /// <summary>
    ///     Cached entry-assembly configuration value. This avoids repeated reflection when
    ///     guidance checks run multiple times in a process before any warning is emitted.
    /// </summary>
    private static readonly Lazy<string?> CachedEntryAssemblyConfiguration = new(ReadEntryAssemblyConfigurationCore);

    /// <summary>
    ///     Test-only hook: resets the once-per-process guard so a test fixture can invoke
    ///     <see cref="EmitBuildConfigurationGuidance" /> repeatedly. Not intended for
    ///     production use; production callers rely on the guard to avoid double emission.
    /// </summary>
    internal static void ResetBuildConfigurationWarningGuard() => Interlocked.Exchange(ref _buildConfigWarningEmitted, 0);

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
    ///     <para>
    ///         Independent of <paramref name="options" />, this entry point also runs the
    ///         always-on Debug-build / debugger-attached guidance check (see
    ///         <see cref="EmitBuildConfigurationGuidance" />) once per process. The check
    ///         is suppressed when the current process is an isolated child (the parent
    ///         already warned), when <see cref="EnvironmentOptions.SuppressBuildConfigurationWarning" />
    ///         is set, or when the <see cref="SuppressDebugWarningEnvVar" /> env var is
    ///         <c>"1"</c>.
    ///     </para>
    /// </remarks>
    public static IDisposable Apply(EnvironmentOptions? options)
    {
        // The build-config warning is always-on and independent of the hardware/OS options
        // below; fire it before the no-op fast path so a caller with no EnvironmentOptions
        // set still gets warned once per process. Gated on not-in-child so an isolated
        // child re-entering Apply does not re-emit the parent's warning.
        EmitBuildConfigurationGuidance(options);

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

        if (!affinityApplied && assessment.CoreCount >= 4 && !assessment.IsMacOS)
        {
            warnings.Add(
                "CPU affinity was not pinned. Add --cpu-affinity 2,3 (or WithHardwareAffinity(2, 3)) "
                + "to pin the process to cores away from core 0 (often used by the OS for driver "
                + "interrupt handling) and eliminate inter-core migration noise.");
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
    ///     Emits a non-fatal warning when the entry assembly was built in <c>Debug</c>
    ///     configuration or when a debugger is attached. Both conditions defeat JIT
    ///     inlining and tier-1 optimization, so the resulting numbers are not
    ///     production-representative. The warning is always-on (the common
    ///     <c>dotnet run</c> without <c>-c Release</c> footgun is silent otherwise) and
    ///     fires at most once per process. Suppressed when the current process is an
    ///     isolated child (the parent already warned), when
    ///     <see cref="EnvironmentOptions.SuppressBuildConfigurationWarning" /> is set, or
    ///     when the <see cref="SuppressDebugWarningEnvVar" /> env var is <c>"1"</c>.
    /// </summary>
    /// <remarks>
    ///     This is the build counterpart to <see cref="EmitDedicatedHostGuidance" />: that
    ///     method warns about unsuitable *hardware*, this warns about an unsuitable *build*.
    ///     Both follow the same "warn and proceed, never refuse" philosophy - a benchmark
    ///     run should never fail because the host or build is imperfect, but the user
    ///     should know the numbers are not trustworthy.
    /// </remarks>
    internal static void EmitBuildConfigurationGuidance(EnvironmentOptions? options)
    {
        // Suppression and child-scope checks come *before* the once-per-process guard so a
        // suppressed call does not consume it - a later non-suppressed call in the same
        // process would otherwise stay silent.
        //
        // A measurement worker never reaches here: it is a fresh process whose guard starts at 0,
        // and it does not run this guidance path at all.
        if (options is { SuppressBuildConfigurationWarning: true })
            return;

        if (IsSuppressEnvVarSet())
            return;

        var warnings = new List<string>(2);
        var configuration = CachedEntryAssemblyConfiguration.Value;

        if (!string.IsNullOrEmpty(configuration)
            && configuration.Contains("Debug", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"The entry assembly was built in '{configuration}' configuration. The JIT "
                + "disables inlining and dead-code elimination under Debug, so the measured "
                + "numbers are not production-representative. Rebuild with `dotnet run -c Release` "
                + "(or set the configuration to Release in your IDE) before trusting the results. "
                + "If measuring Debug is intentional, suppress this warning with "
                + $"{SuppressDebugWarningEnvVar}=1 or EnvironmentOptions.SuppressBuildConfigurationWarning.");
        }

        if (Debugger.IsAttached)
        {
            warnings.Add(
                "A debugger is attached. The runtime suppresses inlining for methods the "
                + "debugger might step into, so timings are not production-representative even "
                + "under a Release build. Detach the debugger before measuring, or suppress this "
                + $"warning with {SuppressDebugWarningEnvVar}=1 if attaching during development "
                + "is intentional.");
        }

        if (warnings.Count == 0)
            return;

        // Once-per-process: only consume the guard when we are actually about to emit.
        // This preserves a later warning opportunity if an earlier call had no warning
        // conditions (for example, debugger attached after an initial non-debug run).
        if (Interlocked.CompareExchange(ref _buildConfigWarningEmitted, 1, 0) != 0)
            return;

        Console.Error.WriteLine("Build configuration guidance:");

        foreach (var w in warnings)
        {
            Console.Error.WriteLine($"  - {w}");
        }
    }

    /// <summary>
    ///     Reads the <c>AssemblyConfigurationAttribute</c> of the entry assembly. Returns
    ///     <c>null</c> when the attribute is absent (common for some publish layouts) so
    ///     the caller can treat absence as "no warning" rather than fail.
    /// </summary>
    private static string? ReadEntryAssemblyConfigurationCore()
    {
        try
        {
            return Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyConfigurationAttribute>()
                ?.Configuration;
        }
        catch
        {
            // Best-effort: if the attribute cannot be read (e.g. a published single-file
            // bundle with no assembly metadata), treat it as unknown rather than fail.
            return null;
        }
    }

    /// <summary>
    ///     Returns <c>true</c> when the <see cref="SuppressDebugWarningEnvVar" /> env var
    ///     is set to a truthy value (<c>"1"</c> or any case-insensitive form of
    ///     <c>"true"</c>). Any other value (or unset) returns <c>false</c>, so the caller
    ///     falls through to normal warning-condition evaluation.
    /// </summary>
    private static bool IsSuppressEnvVarSet()
    {
        var value = Environment.GetEnvironmentVariable(SuppressDebugWarningEnvVar);

        if (string.IsNullOrEmpty(value))
            return false;

        return value == "1"
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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
