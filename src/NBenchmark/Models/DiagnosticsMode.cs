namespace NBenchmark;

/// <summary>
///     The <c>--diagnostics</c> flag's parse target, and the compact form the CSV report stamps.
/// </summary>
/// <remarks>
///     Internal on purpose, for the reason given on <see cref="AutoTunePreset" />:
///     <see cref="DiagnosticsOptions" /> is the model, and a parallel flags enum meant two
///     <c>WithDiagnostics</c> overloads on every builder over one concept. <see cref="Gc" /> and
///     <see cref="GcAndCpu" /> survive only because <c>--diagnostics gc</c> and
///     <c>--diagnostics gcandcpu</c> are the words users type.
/// </remarks>
[Flags]
internal enum DiagnosticsMode
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
