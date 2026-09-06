namespace NBenchmark;

/// <summary>
///     Provides benchmark argument cases from a parameterless source method
///     that returns an enumerable of value tuples.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ArgumentsSourceAttribute(string sourceName) : Attribute
{
    /// <summary>
    ///     The name of the parameterless source method that yields the argument tuples.
    /// </summary>
    public string SourceName { get; } = sourceName;
}
