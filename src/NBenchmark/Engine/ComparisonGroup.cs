namespace NBenchmark.Engine;

/// <summary>
///     Defines which results may legitimately be compared against each other. Significance
///     testing, effect sizes and baseline ratios partition results by this key first, so an
///     invalid comparison is never formed rather than being formed and then filtered. The
///     threshold gate (<see cref="ThresholdCheck.HasRegressionAcrossGroups" />) partitions by
///     this key <i>and</i> by benchmark class, so a regression is only ever flagged against a
///     baseline measured under the same configuration in the same class.
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
    /// <summary>
    ///     The partition key for a result. Results with equal keys are comparable.
    ///     <para>
    ///         The third element is whether the measurement ran in a process NBenchmark launched
    ///         and configured. The runtime profile name alone is a proxy for that, and the proxy
    ///         fails for <see cref="RuntimeProfile.Host" />: <c>ApplyRuntimeProfile</c> writes no
    ///         marker for a profile that inherits everything, so an isolated worker launched under
    ///         <c>--runtime-profile host</c> reports <c>"host"</c> exactly like the coordinator. Without
    ///         the isolation fact, a clean-room row and a dirty-host row would share a key and so share
    ///         a significance group and a ratio column.
    ///     </para>
    ///     <para>
    ///         Keying on <see cref="IsolationStatusExtensions.IsIsolated" /> rather than on the
    ///         specific refusal status keeps two host rows refused for different reasons in the same
    ///         group: both ran in this process under this configuration, so the ratio between them is
    ///         sound and is not withheld.
    ///     </para>
    /// </summary>
    public static (string RuntimeMoniker, string RuntimeProfileName, bool Isolated) KeyFor(BenchmarkResult result)
        => (result.TargetFramework, result.RuntimeProfileName, result.IsolationStatus.IsIsolated());

    /// <summary>Whether two results may be compared against each other.</summary>
    public static bool SameGroup(BenchmarkResult left, BenchmarkResult right)
        => KeyFor(left) == KeyFor(right);

    /// <summary>
    ///     The single median a result is ranked by wherever a baseline is picked: the median of
    ///     per-launch medians when the run had more than one launch, otherwise the result's own
    ///     median. Used by both the table baseline (<see cref="PickBaseline" />) and the
    ///     significance baseline so the two name the same row.
    /// </summary>
    /// <remarks>
    ///     After <see cref="LaunchAggregator.Combine" />, <see cref="BenchmarkResult.MedianNs" /> is
    ///     the <i>mean</i> of per-launch medians and <see cref="LaunchStatistics.LaunchMedian" /> is
    ///     the <i>median</i> of them. With a skewed launch those disagree, so ranking the table by
    ///     <c>MedianNs</c> and significance by <c>LaunchMedian ?? MedianNs</c> picks two different
    ///     baselines for the same numbers.
    /// </remarks>
    internal static double ComparisonMedian(BenchmarkResult result)
        => result.LaunchStatistics?.LaunchMedian ?? result.MedianNs;

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
            .ThenBy(g => g.Min(ComparisonMedian))
            .First()
            .MinBy(ComparisonMedian);
    }
}
