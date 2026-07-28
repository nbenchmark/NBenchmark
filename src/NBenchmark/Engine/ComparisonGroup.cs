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
}
