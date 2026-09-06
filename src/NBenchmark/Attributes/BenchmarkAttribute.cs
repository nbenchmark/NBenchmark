namespace NBenchmark;

[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkAttribute : Attribute
{
    private const int Unset = -1;

    public string? Description { get; set; }
    public bool Baseline { get; set; }
    public int Samples { get; set; } = Unset;
    public int WarmupSamples { get; set; } = Unset;
    public int LaunchCount { get; set; } = Unset;

    // Internal: the sentinel these read is a workaround for `int?` not being a legal attribute
    // argument type, so the question they answer - "did the author set this one?" - belongs to the
    // engine that has to layer the value onto the run's options, not to the author who just wrote a
    // number. Three public getters over one private sentinel only invite a consumer to depend on it.
    internal bool HasSamplesOverride => Samples >= 0;
    internal bool HasWarmupSamplesOverride => WarmupSamples >= 0;
    internal bool HasLaunchCountOverride => LaunchCount >= 0;
}
