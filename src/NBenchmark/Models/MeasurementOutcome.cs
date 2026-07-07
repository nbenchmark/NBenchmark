namespace NBenchmark;

public sealed record MeasurementOutcome
{
    public required BenchmarkResult Result { get; init; }
    public required double[] RawSamples { get; init; }
}
