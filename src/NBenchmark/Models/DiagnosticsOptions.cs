namespace NBenchmark;

/// <summary>
///     Controls which runtime diagnostic counters are collected during measurement, and
///     shapes the resulting <see cref="DiagnosticsResult" />. See
///     <see cref="MeasurementOptions.Diagnostics" />.
/// </summary>
public sealed record DiagnosticsOptions
{
    /// <summary>The default: GC collection counts only (cheap, always available).</summary>
    public static readonly DiagnosticsOptions Default = new() { GcCollectionCounts = true };

    /// <summary>Every counter enabled.</summary>
    public static readonly DiagnosticsOptions All = new()
    {
        GcCollectionCounts = true,
        GcHeapInfo = true,
        Exceptions = true,
        CpuTime = true,
    };

    /// <summary>No counters collected.</summary>
    public static readonly DiagnosticsOptions None = new();

    /// <summary>Whether Gen0/Gen1/Gen2 collection counts are collected.</summary>
    public bool GcCollectionCounts { get; init; }

    /// <summary>Whether managed heap committed/fragmented bytes are collected.</summary>
    public bool GcHeapInfo { get; init; }

    /// <summary>Whether the per-operation exception count is collected.</summary>
    public bool Exceptions { get; init; }

    /// <summary>Whether CPU time and the CPU/wall ratio are collected.</summary>
    public bool CpuTime { get; init; }

    /// <summary>Whether any counter is enabled.</summary>
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
