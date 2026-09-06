namespace NBenchmark;

/// <summary>
///     Tags a benchmark (or an entire benchmark class) with a category. Categories are
///     used for discovery-time filtering and for grouping in reports.
///     <para>
///         Apply <c>[BenchmarkCategory("name")]</c> to a method to tag that benchmark,
///         or to a class to tag every benchmark declared in that class. A method
///         inherits all categories from its declaring class. Multiple categories are
///         declared by applying the attribute multiple times.
///     </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class BenchmarkCategoryAttribute(string name) : Attribute
{
    /// <summary>The category name.</summary>
    public string Name { get; } = Normalize(name);

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
