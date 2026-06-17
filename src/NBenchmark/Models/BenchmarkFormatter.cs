namespace NBenchmark;

public static class BenchmarkFormatter
{
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

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024):F1} MB",
        };
    }

    public static string FormatAlloc(long bytes) => FormatBytes(bytes);

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
