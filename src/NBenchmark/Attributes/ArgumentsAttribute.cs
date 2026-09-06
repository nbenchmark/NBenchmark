namespace NBenchmark;

/// <summary>
///     Supplies one set of arguments for a parametrized benchmark method.
///     Apply multiple times to run the same method with different inputs.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ArgumentsAttribute(params object[] arguments) : Attribute
{
    /// <summary>
    ///     The argument values to pass to the benchmark method for this case.
    /// </summary>
    public object[] Arguments { get; } = arguments;
}
