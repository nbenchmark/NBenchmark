namespace NBenchmark.Stats;

/// <summary>
///     Estimates the ratio between two benchmarks from their per-replicate measurements, on the log
///     scale, as a <b>paired</b> comparison.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why paired.</b> A comparison group is measured co-resident in one worker per replicate,
///         so replicate <i>i</i> of the candidate and replicate <i>i</i> of the baseline ran in the
///         same process, on the same core draw, under the same thermal state and the same
///         address-space layout. Dividing them cancels all of that out of the ratio. Dividing the two
///         <i>aggregated</i> medians instead throws the pairing away and leaves every worker-to-worker
///         difference in the numerator and denominator independently - which is the entire statistical
///         reason the group is co-resident, discarded at the last step.
///     </para>
///     <para>
///         <b>Why the log scale.</b> A ratio is multiplicative, and its sampling distribution is
///         right-skewed: 2x slower and 2x faster are equally large effects but sit at +1.0 and -0.5 on
///         a linear scale. Taking logs makes them symmetric (+0.69 and -0.69), which is what a
///         Student-t interval assumes. Exponentiating back gives an interval that is multiplicatively
///         symmetric about the estimate and cannot straddle zero - a linear interval on a ratio near
///         1.0 with real spread routinely produces a negative lower bound, which is not a ratio.
///     </para>
///     <para>
///         The point estimate is therefore the <b>geometric</b> mean of the per-replicate ratios,
///         which is not the ratio of the arithmetic means. That difference is the correction: the
///         geometric mean is the unbiased estimator of a multiplicative effect, and it is unmoved by
///         one replicate in which <i>both</i> benchmarks happened to run slowly.
///     </para>
/// </remarks>
internal static class LogRatio
{
    /// <summary>
    ///     The paired ratio of <paramref name="candidate" /> to <paramref name="baseline" />, or
    ///     <c>null</c> when the two cannot be paired.
    /// </summary>
    /// <param name="candidate">Per-replicate medians of the benchmark being measured.</param>
    /// <param name="baseline">Per-replicate medians of the reference, in the same replicate order.</param>
    /// <param name="confidenceLevel">
    ///     The level for the returned interval, matching the one the measurements were taken at.
    /// </param>
    /// <returns>
    ///     <c>null</c> when fewer than two replicates can be paired. One pair is a ratio, not an
    ///     estimate of one - it carries no information about how much the ratio would move on a
    ///     re-run, and returning it with no interval would be indistinguishable from the unpaired
    ///     ratio the caller already has.
    /// </returns>
    public static RatioEstimate? Estimate(
        IReadOnlyList<double> candidate,
        IReadOnlyList<double> baseline,
        double confidenceLevel = 0.95)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);

        var pairs = Math.Min(candidate.Count, baseline.Count);
        var logRatios = new List<double>(pairs);

        for (var i = 0; i < pairs; i++)
        {
            // A non-positive median is a replicate that errored or never measured. Dropping the pair
            // rather than the single value is what keeps the comparison paired: an unmatched
            // replicate contributes a difference between two different processes.
            if (candidate[i] > 0 && baseline[i] > 0)
                logRatios.Add(Math.Log(candidate[i]) - Math.Log(baseline[i]));
        }

        if (logRatios.Count < 2)
            return null;

        var count = logRatios.Count;
        var mean = logRatios.Average();

        var variance = logRatios.Sum(d => (d - mean) * (d - mean)) / (count - 1);
        var standardError = Math.Sqrt(variance / count);
        var margin = StudentT.CriticalValue(confidenceLevel, count - 1) * standardError;

        return new RatioEstimate
        {
            Value = Math.Exp(mean),
            Lower = Math.Exp(mean - margin),
            Upper = Math.Exp(mean + margin),
            Replicates = count,
            ConfidenceLevel = confidenceLevel,
        };
    }

    /// <summary>
    ///     The paired ratio between two results, taken from the per-launch detail they carry, or
    ///     <c>null</c> when either was measured in a single launch.
    /// </summary>
    /// <remarks>
    ///     Replicates are matched by <see cref="LaunchDetail.LaunchIndex" /> rather than by position in
    ///     the list, because an errored launch is recorded but contributes no median - so two results
    ///     can hold lists of different lengths whose <i>n</i>th entries are different replicates.
    ///     Matching by position there would pair a candidate's second worker against the baseline's
    ///     third and report the difference between two processes as a property of the code.
    /// </remarks>
    public static RatioEstimate? Estimate(BenchmarkResult candidate, BenchmarkResult baseline)
        => Estimate(candidate, baseline, candidate.ConfidenceLevel);

    /// <summary>
    ///     The paired ratio between two results, taken from the per-launch detail they carry, at a
    ///     caller-chosen confidence level - the run's significance level (<c>1 - alpha</c>) for the
    ///     launch-blocked verdict, rather than the measurement confidence level the display ratio
    ///     uses. See <see cref="Estimate(NBenchmark.BenchmarkResult,NBenchmark.BenchmarkResult)" />
    ///     for the matching semantics; this overload only replaces the level.
    /// </summary>
    public static RatioEstimate? Estimate(BenchmarkResult candidate, BenchmarkResult baseline, double confidenceLevel)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);

        if (candidate.LaunchStatistics is not { } candidateLaunches
            || baseline.LaunchStatistics is not { } baselineLaunches)
            return null;

        var baselineByIndex = baselineLaunches.Launches
            .Where(l => !l.Errored)
            .ToDictionary(l => l.LaunchIndex, l => l.MedianNs);

        var paired = candidateLaunches.Launches
            .Where(l => !l.Errored && baselineByIndex.ContainsKey(l.LaunchIndex))
            .OrderBy(l => l.LaunchIndex)
            .ToList();

        return Estimate(
            paired.Select(l => l.MedianNs).ToList(),
            paired.Select(l => baselineByIndex[l.LaunchIndex]).ToList(),
            confidenceLevel);
    }

    /// <summary>
    ///     The paired ratio of a result to a divisor that is not itself a benchmark - the calibration
    ///     standard, whose per-launch medians the caller holds as a plain list.
    /// </summary>
    /// <param name="baselineLaunchMedians">
    ///     The divisor's median for each launch, <b>indexed by launch index</b>: entry <i>i</i> is the
    ///     value measured in the same worker as launch <i>i</i> of <paramref name="candidate" />. A
    ///     non-positive entry means that launch produced no divisor, and drops the pair.
    ///     <para>
    ///         Indexed rather than "in order" because that is the whole load-bearing assumption. A list
    ///         that had silently dropped its failed launches would line entry 1 up against launch 2 and
    ///         report the difference between two processes as a property of the code.
    ///     </para>
    /// </param>
    public static RatioEstimate? Estimate(
        BenchmarkResult candidate,
        IReadOnlyList<double> baselineLaunchMedians)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baselineLaunchMedians);

        if (candidate.LaunchStatistics is not { } launches)
            return null;

        var candidateMedians = new double[baselineLaunchMedians.Count];

        foreach (var launch in launches.Launches)
        {
            if (launch.Errored || launch.LaunchIndex < 0 || launch.LaunchIndex >= candidateMedians.Length)
                continue;

            candidateMedians[launch.LaunchIndex] = launch.MedianNs;
        }

        return Estimate(candidateMedians, baselineLaunchMedians, candidate.ConfidenceLevel);
    }
}
