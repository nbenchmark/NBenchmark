namespace NBenchmark.Engine;

/// <summary>
///     Applies include/exclude category rules to a list of benchmarks. Include rules are
///     OR semantics within a source: a benchmark runs if it matches at least one included
///     category from that source. When multiple include sources are supplied (for example
///     programmatic and CLI), every non-empty source must match. Exclude rules are also OR:
///     a benchmark is removed if it matches any excluded category. When no include filter is
///     present, every benchmark is eligible unless excluded. Untagged benchmarks are removed
///     when any include filter is present.
/// </summary>
internal static class CategoryFilter
{
    public static bool Matches(
        IReadOnlyList<string> categories,
        IReadOnlyList<string> include,
        IReadOnlyList<string> exclude,
        bool hasIncludeFilter)
    {
        return Matches(categories, include, [], exclude, hasIncludeFilter);
    }

    public static bool Matches(
        IReadOnlyList<string> categories,
        IReadOnlyList<string> primaryInclude,
        IReadOnlyList<string> secondaryInclude,
        IReadOnlyList<string> exclude,
        bool hasIncludeFilter)
    {
        if (exclude.Count > 0 && HasAny(categories, exclude))
            return false;

        if (!hasIncludeFilter)
            return true;

        if (primaryInclude.Count > 0 && !HasAny(categories, primaryInclude))
            return false;

        if (secondaryInclude.Count > 0 && !HasAny(categories, secondaryInclude))
            return false;

        return true;
    }

    private static bool HasAny(IReadOnlyList<string> categories, IReadOnlyList<string> candidates)
    {
        foreach (var category in categories)
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(category, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
