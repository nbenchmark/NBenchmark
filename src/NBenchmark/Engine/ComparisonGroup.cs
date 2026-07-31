namespace NBenchmark.Engine;

/// <summary>
///     Defines which results may legitimately be compared against each other. Significance
///     testing, effect sizes, baseline ratios and the threshold gate all partition results by this
///     key first, so an invalid comparison is never formed rather than being formed and then
///     filtered.
///     <para>
///         Two dimensions matter, and both are properties of the environment rather than of the
///         code under test:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>Runtime moniker</b> - net8 versus net10 is not a meaningful comparison.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Runtime profile</b> - a benchmark measured with tiered compilation disabled
///                 cannot be compared with one measured under the host's inherited configuration.
///                 On four benchmarks of provably identical cost, that difference alone moved the
///                 measured value by roughly 3.3x, so comparing across it produces a large and
///                 entirely fabricated effect. This is also what keeps an in-process result (which
///                 always reports <c>"host"</c>) out of the same table as an isolated one.
///             </description>
///         </item>
///     </list>
/// </summary>
internal static class ComparisonGroup
{
    /// <summary>The partition key for a result. Results with equal keys are comparable.</summary>
    public static (string RuntimeMoniker, string RuntimeProfileName) KeyFor(BenchmarkResult result)
        => (result.RuntimeMoniker, result.RuntimeProfileName);

    /// <summary>Whether two results may be compared against each other.</summary>
    public static bool SameGroup(BenchmarkResult left, BenchmarkResult right)
        => KeyFor(left) == KeyFor(right);

    /// <summary>
    ///     Picks the baseline for a set of results: an explicitly declared one, else the fastest
    ///     result in the <i>largest</i> comparison group.
    /// </summary>
    /// <remarks>
    ///     Not simply the fastest result overall. An in-process row is typically the fastest in a
    ///     mixed table - it inherits the host's tiered-compilation state rather than the profile the
    ///     workers were launched with - so ranking by median alone hands the baseline to the one row
    ///     nothing else can be compared against, and every remaining ratio is then withheld. Picking
    ///     within the largest group keeps the comparison the user actually came for.
    /// </remarks>
    public static BenchmarkResult? PickBaseline(IReadOnlyList<BenchmarkResult> successful)
    {
        if (successful.Count == 0)
            return null;

        if (successful.FirstOrDefault(r => r.IsBaseline) is { } declared)
            return declared;

        return successful
            .GroupBy(KeyFor)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(r => r.Median))
            .First()
            .MinBy(r => r.Median);
    }
}
