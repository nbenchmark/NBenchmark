namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkAttribute : Attribute
{
    private const int Unset = -1;

    public string? Description { get; set; }
    public bool Baseline { get; set; }
    public int Iterations { get; set; } = Unset;
    public int WarmupIterations { get; set; } = Unset;
    public int LaunchCount { get; set; } = Unset;

    public bool HasIterationsOverride => Iterations >= 0;
    public bool HasWarmupIterationsOverride => WarmupIterations >= 0;
    public bool HasLaunchCountOverride => LaunchCount >= 0;
}
