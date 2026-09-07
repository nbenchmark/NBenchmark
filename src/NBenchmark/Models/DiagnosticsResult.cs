namespace NBenchmark;

/// <summary>
///     Runtime diagnostic counters collected alongside a benchmark's timing samples, gated by
///     <see cref="MeasurementOptions.Diagnostics" />. A counter not requested (see
///     <see cref="Collected" />) is <c>null</c> rather than zero.
/// </summary>
public sealed record DiagnosticsResult
{
    /// <summary>Gen0 garbage collections observed across the measured samples.</summary>
    public long? Gen0Collections { get; init; }

    /// <summary>Gen1 garbage collections observed across the measured samples.</summary>
    public long? Gen1Collections { get; init; }

    /// <summary>Gen2 garbage collections observed across the measured samples.</summary>
    public long? Gen2Collections { get; init; }

    /// <summary>Managed heap bytes committed at the end of measurement.</summary>
    public long? HeapCommittedBytes { get; init; }

    /// <summary>Managed heap bytes fragmented (unusable) at the end of measurement.</summary>
    public long? HeapFragmentedBytes { get; init; }

    /// <summary>Exceptions thrown per operation, averaged across the measured samples.</summary>
    public double? ExceptionCountPerOp { get; init; }

    /// <summary>CPU time consumed per operation, in nanoseconds.</summary>
    public double? CpuTimeNsPerOp { get; init; }

    /// <summary>The ratio of CPU time to wall-clock time over the measured duration.</summary>
    public double? CpuWallRatio { get; init; }

    /// <summary>Which counters this result carries - the resolved <see cref="MeasurementOptions.Diagnostics" />.</summary>
    public DiagnosticsOptions Collected { get; init; } = DiagnosticsOptions.None;
}
