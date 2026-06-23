namespace NBenchmark;

public sealed record DiagnosticsResult
{
    public long? Gen0Collections { get; init; }
    public long? Gen1Collections { get; init; }
    public long? Gen2Collections { get; init; }
    public long? HeapCommittedBytes { get; init; }
    public long? HeapFragmentedBytes { get; init; }
    public double? ExceptionCountPerOp { get; init; }
    public double? CpuTimeNsPerOp { get; init; }
    public double? CpuWallRatio { get; init; }
    public DiagnosticsMode Mode { get; init; }
}
