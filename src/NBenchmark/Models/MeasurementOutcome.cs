namespace NBenchmark;

public sealed record MeasurementOutcome
{
    public required BenchmarkResult Result { get; init; }

    /// <summary>
    ///     The raw per-op nanoseconds of every measured sample, in sample order, before outlier
    ///     trimming. Shares storage with <see cref="BenchmarkResult.RawSamples" />; mutating
    ///     this array will also mutate the result's view of the samples. Treat as read-only.
    /// </summary>
    public required double[] RawSamples { get; init; }
}
