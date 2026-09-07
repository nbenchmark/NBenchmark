namespace NBenchmark;

/// <summary>Marks a method to run once after every benchmark in the containing class has been measured.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GlobalTeardownAttribute : Attribute;
