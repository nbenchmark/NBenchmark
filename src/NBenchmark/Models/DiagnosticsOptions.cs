namespace NBenchmark;

public sealed record DiagnosticsOptions
{
    public static readonly DiagnosticsOptions Default = new() { GcCollectionCounts = true };

    public static readonly DiagnosticsOptions All = new()
    {
        GcCollectionCounts = true,
        GcHeapInfo = true,
        Exceptions = true,
        CpuTime = true,
    };

    public static readonly DiagnosticsOptions None = new();

    public bool GcCollectionCounts { get; init; }

    public bool GcHeapInfo { get; init; }

    public bool Exceptions { get; init; }

    public bool CpuTime { get; init; }

    public bool Any => GcCollectionCounts || GcHeapInfo || Exceptions || CpuTime;

    internal DiagnosticsMode ToMode()
    {
        var mode = DiagnosticsMode.None;

        if (GcCollectionCounts)
            mode |= DiagnosticsMode.GcCollectionCounts;

        if (GcHeapInfo)
            mode |= DiagnosticsMode.GcHeapInfo;

        if (Exceptions)
            mode |= DiagnosticsMode.Exceptions;

        if (CpuTime)
            mode |= DiagnosticsMode.CpuTime;

        return mode;
    }

    internal static DiagnosticsOptions FromMode(DiagnosticsMode mode)
    {
        var unknownFlags = mode & ~DiagnosticsMode.All;

        if (unknownFlags != DiagnosticsMode.None)
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown DiagnosticsMode flag value.");

        if (mode == DiagnosticsMode.None)
            return None;

        return new DiagnosticsOptions
        {
            GcCollectionCounts = (mode & DiagnosticsMode.GcCollectionCounts) != 0,
            GcHeapInfo = (mode & DiagnosticsMode.GcHeapInfo) != 0,
            Exceptions = (mode & DiagnosticsMode.Exceptions) != 0,
            CpuTime = (mode & DiagnosticsMode.CpuTime) != 0,
        };
    }
}
