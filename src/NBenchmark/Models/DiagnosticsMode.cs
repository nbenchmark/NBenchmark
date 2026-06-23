namespace NBenchmark;

/// <summary>
///     Diagnostics mode bit flags. The named presets (<see cref="Gc" />, <see cref="GcAndCpu" />,
///     <see cref="All" />) are aliases over these flags and may be combined.
/// </summary>
[Flags]
public enum DiagnosticsMode
{
    None = 0,
    GcCollectionCounts = 1 << 0,
    GcHeapInfo = 1 << 1,
    Exceptions = 1 << 2,
    CpuTime = 1 << 3,

    Gc = GcCollectionCounts,
    GcAndCpu = GcCollectionCounts | CpuTime,
    All = GcCollectionCounts | GcHeapInfo | Exceptions | CpuTime,
}
