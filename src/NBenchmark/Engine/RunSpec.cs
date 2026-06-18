namespace NBenchmark.Engine;

/// <summary>
///     Per-benchmark configuration passed to <see cref="BenchmarkRunner" />.
///     A value-type to keep the runner's spec-list storage free of per-element
///     heap allocations when used by <c>BenchmarkSuite</c> and
///     <c>BenchmarkHost</c>.
/// </summary>
public readonly record struct RunSpec
{
    public RunSpec()
    {
    }

    public MeasurementOptions Options { get; init; } = MeasurementOptions.Default;
    public string? Description { get; init; }
    public bool IsBaseline { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public Action? IterationSetup { get; init; }
    public Action? IterationTeardown { get; init; }
    public IBenchmarkProgress Progress { get; init; } = NullBenchmarkProgress.Instance;

    /// <summary>
    ///     The class that declared the benchmark. Empty for suite-mode entries that are not
    ///     discovered from a class.
    /// </summary>
    public string ClassName { get; init; } = "";
}
