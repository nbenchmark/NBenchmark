namespace NBenchmark;

/// <summary>Marks a method to run before each measured sample, outside the timed region.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SampleSetupAttribute : Attribute;
