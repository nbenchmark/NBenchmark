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
}
