namespace NBenchmark;

/// <summary>Marks a method to run after each measured sample, outside the timed region.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SampleTeardownAttribute : Attribute;
