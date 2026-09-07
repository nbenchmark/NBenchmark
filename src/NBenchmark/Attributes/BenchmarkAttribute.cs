namespace NBenchmark;

/// <summary>Marks a method as a benchmark to be discovered and measured by Suite/Harness mode.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkAttribute : Attribute
{
    private const int Unset = -1;

    /// <summary>An optional display label for this benchmark, shown in place of its method name.</summary>
    public string? Description { get; set; }

    /// <summary>Marks this benchmark as the baseline other benchmarks in its group are compared against.</summary>
    public bool Baseline { get; set; }

    /// <summary>Overrides <see cref="MeasurementOptions.Samples" /> for this benchmark. Unset by default (auto).</summary>
    public int Samples { get; set; } = Unset;

    /// <summary>Overrides <see cref="MeasurementOptions.WarmupSamples" /> for this benchmark. Unset by default (auto).</summary>
    public int WarmupSamples { get; set; } = Unset;

    /// <summary>Overrides the launch count for this benchmark. Unset by default (auto).</summary>
    public int LaunchCount { get; set; } = Unset;

    // Internal: the sentinel these read is a workaround for `int?` not being a legal attribute
    // argument type, so the question they answer - "did the author set this one?" - belongs to the
    // engine that has to layer the value onto the run's options, not to the author who just wrote a
    // number. Three public getters over one private sentinel only invite a consumer to depend on it.
    internal bool HasSamplesOverride => Samples >= 0;
    internal bool HasWarmupSamplesOverride => WarmupSamples >= 0;
    internal bool HasLaunchCountOverride => LaunchCount >= 0;
}
