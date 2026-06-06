namespace NBenchmark;

public sealed class MeasurementOutcome
{
    public required BenchmarkResult Result { get; init; }
    public required double[] RawSamples { get; init; }
}