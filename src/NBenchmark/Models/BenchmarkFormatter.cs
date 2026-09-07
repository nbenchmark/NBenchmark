namespace NBenchmark;

/// <summary>
///     Human-readable formatting for the numeric values a <see cref="BenchmarkResult" /> reports -
///     durations, byte counts, and throughput - each scaled to a sensible unit.
/// </summary>
public static class BenchmarkFormatter
{
    /// <summary>
    ///     Formats a nanosecond value scaled to ns, µs, ms or s, e.g. <c>1.23 ms</c>.
    /// </summary>
    public static string FormatNs(double ns)
    {
        return ns switch
        {
            < 1_000 => $"{ns:F1} ns",
            < 1_000_000 => $"{ns / 1_000:F2} µs",
            < 1_000_000_000 => $"{ns / 1_000_000:F2} ms",
            _ => $"{ns / 1_000_000_000:F2} s",
        };
    }

    /// <summary>
    ///     Formats a byte count scaled to B, KiB, MiB or GiB, e.g. <c>4.0 KiB</c>.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KiB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MiB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GiB",
        };
    }

    /// <summary>Formats an allocation byte count. Alias for <see cref="FormatBytes" />.</summary>
    public static string FormatAlloc(long bytes) => FormatBytes(bytes);

    /// <summary>
    ///     Formats an operations-per-second rate scaled to ops/s, Kops/s, Mops/s, Gops/s or
    ///     Tops/s, e.g. <c>1.23 Mops/s</c>. Returns <c>"-"</c> for a negative or <c>NaN</c> value.
    /// </summary>
    public static string FormatOpsPerSecond(double opsPerSecond)
    {
        if (double.IsNaN(opsPerSecond) || opsPerSecond < 0)
            return "-";

        return opsPerSecond switch
        {
            < 1_000 => $"{opsPerSecond:F1} ops/s",
            < 1_000_000 => $"{opsPerSecond / 1_000:F2} Kops/s",
            < 1_000_000_000 => $"{opsPerSecond / 1_000_000:F2} Mops/s",
            < 1_000_000_000_000 => $"{opsPerSecond / 1_000_000_000:F2} Gops/s",
            _ => $"{opsPerSecond / 1_000_000_000_000:F2} Tops/s",
        };
    }

    /// <summary>Formats a per-operation nanosecond value. Alias for <see cref="FormatNs" />.</summary>
    public static string FormatNsPerOp(double ns) => FormatNs(ns);

    /// <summary>
    ///     Formats a <see cref="TimeSpan" /> scaled to ns, µs, ms, s, min or h, e.g. <c>2.50 min</c>.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        // Implemented with netstandard2.0-compatible TimeSpan members only (Ticks, TotalMilliseconds,
        // TotalSeconds, TotalMinutes) so the formatter can be used safely across all target frameworks.
        const double ticksPerNanosecond = 100.0;
        const double millisecondsPerSecond = 1_000.0;
        const double millisecondsPerMinute = 60.0 * millisecondsPerSecond;
        const double millisecondsPerHour = 60.0 * millisecondsPerMinute;

        var totalMs = duration.TotalMilliseconds;

        return totalMs switch
        {
            < 0.001 => $"{duration.Ticks * ticksPerNanosecond:F0} ns",
            < 1.0 => $"{duration.Ticks * ticksPerNanosecond / 1_000.0:F2} µs",
            < millisecondsPerSecond => $"{totalMs:F2} ms",
            < millisecondsPerMinute => $"{totalMs / millisecondsPerSecond:F2} s",
            < millisecondsPerHour => $"{totalMs / millisecondsPerMinute:F2} min",
            _ => $"{totalMs / millisecondsPerHour:F2} h",
        };
    }
}
