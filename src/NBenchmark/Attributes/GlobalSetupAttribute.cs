namespace NBenchmark;

/// <summary>Marks a method to run once before any benchmark in the containing class is measured.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GlobalSetupAttribute : Attribute;
