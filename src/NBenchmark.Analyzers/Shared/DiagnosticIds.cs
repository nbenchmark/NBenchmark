namespace NBenchmark.Analyzers.Shared;

public static class DiagnosticIds
{
    public const string MissingParameterlessConstructor = "NB0001";
    public const string StaticBenchmarkMethod = "NB0002";
    public const string BenchmarkCaseArity = "NB0003";
    public const string NoObservableSideEffect = "NB0004";
    public const string NoWorkBody = "NB0005";
    public const string MultipleBaselines = "NB0006";
    public const string DuplicateLifecycleMethod = "NB0007";
    public const string BenchmarkAttributeRange = "NB0008";
    public const string MeasurementOptionsRange = "NB0009";
    public const string ThrowawayBody = "NB0010";
    public const string PerClassWithScopedService = "NB0011";
    public const string BenchmarkCaseConflict = "NB0012";
}
