using System.Diagnostics;
using System.Globalization;

namespace NBenchmark;

/// <summary>
///     Opt-in hardware/OS controls that reduce measurement noise at its source, rather
///     than only reacting to it statistically. When set, <see cref="NBenchmark.Engine.EnvironmentControl" />
///     applies them for the duration of a run and restores the prior values on exit.
///     <para>
///         All fields are nullable and default to <c>null</c> (do nothing). Leave them
///         unset for the zero-ceremony "just run my benchmark" path; set them when you
///         are on a dedicated host and want to reduce preemption, frequency-scaling, and
///         co-tenant noise before a serious measurement.
///     </para>
/// </summary>
public record EnvironmentOptions
{
    public static readonly EnvironmentOptions Default = new();

    /// <summary>
    ///     The set of CPU cores the benchmark process is pinned to (processor affinity).
    ///     Core indices are zero-based and logical (as reported by the OS); out-of-range
    ///     indices are rejected by <see cref="NBenchmark.Engine.EnvironmentControl" />. Duplicate indices
    ///     are silently deduplicated (OR-ing the same bit is idempotent). When
    ///     <c>null</c>, affinity is left untouched.
    /// </summary>
    /// <remarks>
    ///     Pinning to a single core (e.g. <c>[0]</c>) removes inter-core migration noise
    ///     but caps throughput to one thread. Pinning to a small group (e.g. <c>[2, 3]</c>)
    ///     is the typical sweet spot for single-threaded benchmarks on a busy machine: it
    ///     avoids core 0 (often used by the OS) and leaves the scheduler room to honour
    ///     affinity without starving the benchmark.
    /// </remarks>
    public IReadOnlyList<int>? CpuAffinity { get; init; }

    /// <summary>
    ///     The process priority to request for the benchmark process. When <c>null</c>,
    ///     priority is left untouched. <see cref="ProcessPriorityClass.High" /> is the
    ///     recommended value for dedicated benchmark hosts; it preempts normal work but
    ///     does not require admin privileges. <see cref="ProcessPriorityClass.RealTime" />
    ///     can starve the OS and is discouraged.
    /// </summary>
    public ProcessPriorityClass? ProcessPriority { get; init; }

    /// <summary>
    ///     When <c>true</c>, <see cref="NBenchmark.Engine.EnvironmentControl" /> probes the host before each
    ///     run and emits a console warning when it detects conditions that typically
    ///     inflate noise: a low CPU core count (shared-tenant CI runners), an unraisable
    ///     process priority, or (on macOS) unobservable frequency scaling and thermal
    ///     throttling. The run still proceeds - this is guidance, not a gate. Defaults to
    ///     <c>false</c>.
    /// </summary>
    public bool HostQualityWarnings { get; init; }

    /// <summary>
    ///     Whether the OS controls that belong to the measuring <i>thread</i> are applied:
    ///     thread affinity matching <see cref="CpuAffinity" />, a thread priority matching
    ///     <see cref="ProcessPriority" />, and - on macOS - the
    ///     <c>QOS_CLASS_USER_INTERACTIVE</c> elevation that keeps the thread on an Apple Silicon
    ///     performance core. <b>On by default</b>, unlike the other members of this record: it
    ///     needs no configuration to be useful and the default-off alternative is a Mac measured
    ///     on whichever core the scheduler picked.
    ///     <para>
    ///         Set to <c>false</c> (<c>--no-thread-control</c>) to measure under the host's
    ///         default thread scheduling - which is what you want if the *scheduling* is the
    ///         subject, and not otherwise. See <see cref="NBenchmark.Engine.ThreadEnvironmentControl" />.
    ///     </para>
    /// </summary>
    public bool ThreadControl { get; init; } = true;

    /// <summary>
    ///     Parses a comma-separated list of non-negative integers (e.g. <c>"0"</c> or
    ///     <c>"2,3"</c>) into an affinity list. Whitespace is tolerated. Returns
    ///     <c>null</c> for a null or blank input. Throws <see cref="FormatException" />
    ///     when any token is missing, non-numeric, negative, or greater than or equal to
    ///     the host's logical core count (<see cref="System.Environment.ProcessorCount" />)
    ///     - callers should surface the message as a CLI error.
    /// </summary>
    public static IReadOnlyList<int>? ParseCpuAffinity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return null;

        var coreCount = Environment.ProcessorCount;
        var result = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var idx) || idx < 0)
                throw new FormatException($"'{parts[i]}' is not a valid non-negative CPU index.");

            if (idx >= coreCount)
            {
                throw new FormatException(
                    $"CPU index {idx} is out of range for this host (0..{coreCount - 1}).");
            }

            result[i] = idx;
        }

        return result;
    }
}
