namespace NBenchmark;

/// <summary>
///     Measures every <c>[Benchmark]</c> in this class on each of the named target frameworks,
///     e.g. <c>[Runtimes("net8.0", "net10.0")]</c>. The shorthand <c>"net10"</c> is also accepted.
/// </summary>
/// <remarks>
///     Target frameworks are strings rather than an enum because an attribute argument must be a
///     compile-time constant, and a closed set would put a new .NET release behind an NBenchmark
///     release. An unrecognized moniker throws when the class is discovered, not when it is run.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RuntimesAttribute : Attribute
{
    private readonly RuntimeMoniker[] _runtimes;

    /// <summary>Creates the attribute from target-framework monikers.</summary>
    /// <exception cref="FormatException">One of the values is not a target framework.</exception>
    public RuntimesAttribute(params string[] targetFrameworks)
    {
        _runtimes = targetFrameworks is null
            ? []
            : Array.ConvertAll(targetFrameworks, RuntimeMoniker.Parse);
    }

    /// <summary>The runtimes this class is measured on.</summary>
    public IReadOnlyList<RuntimeMoniker> Runtimes => _runtimes;
}
