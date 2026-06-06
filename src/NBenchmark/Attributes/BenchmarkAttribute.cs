namespace NBenchmark.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkAttribute : Attribute
{
    public string? Description { get; set; }
    public bool Baseline { get; set; }
    public int? Iterations { get; set; }
    public int? WarmupIterations { get; set; }
}
