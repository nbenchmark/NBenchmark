using NBenchmark.Stats;

namespace NBenchmark.Engine;

internal static class LaunchAggregator
{
    /// <summary>One launch of one benchmark: its result and the samples that produced it.</summary>
    internal readonly record struct Launch(BenchmarkResult Result, double[] RawSamples);

    /// <summary>
    ///     Aggregates per-launch results into <see cref="LaunchStatistics" />.
    ///     Errored launches are excluded from statistics but recorded in
    ///     <see cref="LaunchDetail" />.
    /// </summary>
    public static LaunchStatistics Aggregate(IReadOnlyList<BenchmarkResult> launchResults, double confidenceLevel = 0.95)
    {
        ArgumentNullException.ThrowIfNull(launchResults);

        var successful = launchResults
            .Select((r, i) => (Result: r, Index: i))
            .Where(x => !x.Result.Errored)
            .ToList();

        var successfulMedians = successful.Select(x => x.Result.MedianNs).ToArray();
        var successfulCount = successfulMedians.Length;

        var launchMean = successfulCount > 0 ? successfulMedians.Average() : 0;
        var launchMedian = successfulCount > 0 ? MedianOf(successfulMedians) : 0;

        var launchStdDev = successfulCount > 1
            ? Math.Sqrt(successfulMedians.Sum(m => (m - launchMean) * (m - launchMean)) / (successfulCount - 1))
            : 0;

        double? ciLower = null;
        double? ciUpper = null;

        if (successfulCount > 1)
        {
            // Use the same Student-t critical value the per-benchmark stats pipeline uses. The
            // previous implementation scaled a hard-coded 95% t-table by z/z95, which is wrong:
            // t-quantiles are not linear in z. The error was largest at low df and high confidence
            // (e.g. df=2, CL=0.99 returned 5.66 vs the true 9.925 - a 43% too-narrow CI).
            var t = StudentT.CriticalValue(confidenceLevel, successfulCount - 1);
            var margin = t * launchStdDev / Math.Sqrt(successfulCount);
            ciLower = launchMean - margin;
            ciUpper = launchMean + margin;
        }

        var details = launchResults
            .Select((r, i) => new LaunchDetail
            {
                LaunchIndex = i,
                MedianNs = r.MedianNs,
                MeanNs = r.MeanNs,
                StandardDeviationNs = r.StandardDeviationNs,
                Samples = r.SampleCount,
                Duration = r.TotalDuration,
                Errored = r.Errored,
                ErrorMessage = r.ErrorMessage,
            })
            .ToList();

        double? dispersion = null;
        double? varianceRatio = null;
        double? withinStandardError = null;

        if (successfulCount > 1)
        {
            if (launchMean > 0)
                dispersion = launchStdDev / launchMean;

            // The precision a single launch *claimed* about its own median, as the mean of the
            // per-launch standard errors. Each launch computed its own s/sqrt(n) over its full sample
            // array, so this is the quantity a within-process interval is built from.
            //
            // The denominator used to be the mean per-launch standard *deviation* - the spread of
            // individual samples. That is the wrong scale by a factor of sqrt(n), and it silently
            // disabled this warning on exactly the benchmarks that need it. A within-process interval
            // is t * s / sqrt(n), not s, so asking whether between-process spread is large "compared
            // to the within-process interval" has to divide by the standard error. With n in the
            // thousands - routine for a nanosecond-scale body, where the sample target is small enough
            // to collect that many - sqrt(n) is 50-70x, so a ratio that should have read 35-50 read
            // 0.5-0.7 and never crossed the threshold. Measured on this library's own calibration
            // sample, the single-launch interval was 21x narrower than the true run-to-run spread
            // while the metric reported 0.7 and stayed silent.
            //
            // Averaging rather than taking the smallest keeps one unusually quiet launch from making
            // the whole run look irreproducible.
            var meanStandardError = successful.Average(x => x.Result.StandardErrorNs);

            if (meanStandardError > 0)
            {
                withinStandardError = meanStandardError;
                varianceRatio = launchStdDev / meanStandardError;
            }
        }

        return new LaunchStatistics
        {
            LaunchCount = successfulCount,
            LaunchMean = launchMean,
            LaunchStandardDeviation = launchStdDev,
            LaunchMedian = launchMedian,
            LaunchConfidenceIntervalLower = ciLower,
            LaunchConfidenceIntervalUpper = ciUpper,
            BetweenLaunchDispersion = dispersion,
            ProcessVarianceRatio = varianceRatio,
            WithinLaunchStandardError = withinStandardError,
            Launches = details,
        };
    }

    /// <summary>
    ///     The point past which a within-process interval stops describing what a re-run would produce:
    ///     between-launch spread this many times the precision a single launch claimed.
    ///     <para>
    ///         Four is a judgement rather than a derived constant. At a ratio of 1 the two agree and a
    ///         within-process interval is a fair guide; by 4 a difference the size of ordinary run-to-run
    ///         noise reads as several standard errors, which is enough for a significance test to call
    ///         it decisively real.
    ///     </para>
    ///     <para>
    ///         The threshold is unchanged from when <see cref="LaunchStatistics.ProcessVarianceRatio" />
    ///         divided by the per-sample standard deviation, but it now means something. Under the old
    ///         denominator the ratio carried a spurious <c>1/sqrt(n)</c>, so clearing 4 required
    ///         between-process spread to exceed four times the spread of individual samples - a
    ///         condition close to pathological, and one no ordinary benchmark met.
    ///     </para>
    ///     <para>
    ///         It now discriminates by body cost, which is the correct behaviour rather than a
    ///         coincidence. A cheap body collects thousands of samples, drives its standard error toward
    ///         zero, and trips the threshold; an expensive body collects a few dozen, keeps a standard
    ///         error comparable to its between-launch spread, and stays quiet. Fast microbenchmarks are
    ///         where this failure mode actually lives.
    ///     </para>
    /// </summary>
    internal const double ProcessVarianceWarningThreshold = 4.0;

    /// <summary>
    ///     A warning when run-to-run variation swamps the precision a single process claimed, or
    ///     <c>null</c> when the numbers reproduce well enough for that precision to mean what it
    ///     appears to mean.
    ///     <para>
    ///         This exists because a p-value computed from samples pooled across processes inherits
    ///         the power of the pooled count, not the reproducibility of the measurement. With enough
    ///         samples, a difference far smaller than the run-to-run noise reads as overwhelmingly
    ///         significant. Saying so is the honest alternative to reporting a verdict the data
    ///         cannot support.
    ///     </para>
    ///     <para>
    ///         The message deliberately does <em>not</em> claim the row's interval is a within-process
    ///         one. Once two or more launches are aggregated, <see cref="Average" /> replaces the
    ///         interval with the between-launch Student-t half-width, so the interval already carries
    ///         this variance. What does not is the significance test, which pools raw samples, and the
    ///         distribution columns, which are averaged per-launch estimates.
    ///     </para>
    /// </summary>
    public static string? DescribeReproducibility(LaunchStatistics? statistics)
    {
        if (statistics?.ProcessVarianceRatio is not { } ratio || ratio <= ProcessVarianceWarningThreshold)
            return null;

        var dispersion = statistics.BetweenLaunchDispersion is { } d
            ? $" Run-to-run spread is {d:P1} of the measurement."
            : "";

        return $"Run-to-run variation across {statistics.LaunchCount} launches is {ratio:F0}x the "
               + "precision any single launch reported, so this benchmark measures far more precisely "
               + "than it reproduces."
               + dispersion
               + " The Error on this row is the between-launch interval and already accounts for that, "
               + "but the significance verdict does not - it pools samples across launches, so it "
               + "inherits the power of the pooled count rather than the reproducibility of the "
               + "measurement. Treat any verdict here as provisional and compare the per-launch medians. "
               + "Raising --launch-count sharpens the reproducibility estimate; it will not narrow the "
               + "spread itself.";
    }

    /// <summary>
    ///     Attaches launch statistics to the representative result, together with the
    ///     reproducibility warning when one applies.
    ///     <para>
    ///         Every path that aggregates launches goes through here, so the warning cannot be
    ///         present on one mode's output and missing from another's. Setting
    ///         <see cref="BenchmarkResult.LaunchStatistics" /> directly is the mistake this method
    ///         exists to prevent - it is exactly how the previous composite-key defect spread across
    ///         nine call sites that quietly disagreed.
    ///     </para>
    /// </summary>
    public static BenchmarkResult Apply(BenchmarkResult representative, LaunchStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(representative);
        ArgumentNullException.ThrowIfNull(statistics);

        var result = representative with { LaunchStatistics = statistics };

        return DescribeReproducibility(statistics) is { } warning
            ? result with { Warnings = [.. result.Warnings, warning] }
            : result;
    }

    /// <summary>
    ///     Collapses several launches of one benchmark into the single row the user sees, by
    ///     <b>averaging</b> the per-launch estimates.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This replaced picking the lowest-median launch, which was a one-sided bias with the
    ///         worst possible shape. Each launch is now a fresh worker, so the differences between
    ///         them are a real systematic component - the very thing <c>LaunchCount</c> exists to
    ///         measure - and taking the minimum over it selects for the luckiest process draw: the
    ///         quietest core, the kindest page and address-space layout. Raising the replicate count to
    ///         improve the estimate made the headline <i>more</i> optimistic, which is precisely
    ///         backwards for a number a regression gate reads.
    ///     </para>
    ///     <para>
    ///         It also closes a disagreement between neighbouring columns. Significance testing has
    ///         always run on samples pooled across every launch, while the displayed median came from
    ///         one of them - so the number and the p-value beside it described different data.
    ///     </para>
    ///     <para>
    ///         <b>The reported interval is derived from the launches, not from one of them.</b>
    ///         <see cref="BenchmarkResult.StandardErrorNs" /> becomes the standard error of the mean of
    ///         launch medians and <see cref="BenchmarkResult.MarginOfErrorNs" /> its Student-t half-width,
    ///         so the interval describes how well the number <i>reproduces</i> rather than how
    ///         precisely one process measured it. On this library's own sample those differ by a factor
    ///         of twenty.
    ///     </para>
    ///     <para>
    ///         <b>The Winsorized (Yuen) correction is deliberately not applied here.</b> A trimming
    ///         correction answers "how much variance did the fence remove from this process's
    ///         samples?", and above one launch the reported interval is not describing one process's
    ///         samples at all - it is the spread of <c>k</c> launch medians, which contains the
    ///         between-process component the trimming correction has no view of and is the wider
    ///         question by a large factor. Layering a within-launch correction onto a between-launch
    ///         interval would mix two scales. The correction still reaches this path twice, on its
    ///         own terms: the single-launch case passes the per-launch result through untouched, so
    ///         it carries the Yuen interval; and <see cref="LaunchStatistics.WithinLaunchStandardError" />
    ///         - the denominator of <c>ProcessVarianceRatio</c> - is the mean of the per-launch
    ///         standard errors, which are now the Winsorized ones. The ratio therefore falls slightly,
    ///         which is correct: part of what it was attributing to between-process variance was the
    ///         within-launch interval being too narrow.
    ///     </para>
    ///     <para>
    ///         <b>Why the raw samples are not pooled and the statistics recomputed.</b> That is the
    ///         obvious implementation and it is wrong here: an isolated worker computes its statistics
    ///         over its full sample array and then ships a bounded <c>SampleReservoir</c> subset.
    ///         Recomputing from what crossed the wire would quietly move every reported statistic onto
    ///         a 4096-sample subset. Averaging per-launch estimates keeps each launch's statistics
    ///         computed over all of its own samples.
    ///     </para>
    /// </remarks>
    /// <param name="launches">
    ///     One entry per launch, in launch order, each pairing a result with its own raw samples.
    ///     Errored launches are excluded from the statistics but still appear in
    ///     <see cref="LaunchStatistics.Launches" />.
    ///     <para>
    ///         Result and samples travel together rather than in parallel lists addressed by index.
    ///         The lists were not always the same length - a launch that produced no result for a name
    ///         was filtered out of one and not the other - so an index into one silently addressed a
    ///         different launch in the other. Pairing them makes that unrepresentable, which is the same
    ///         reason raw samples ride inside their own completion frame on the wire.
    ///     </para>
    /// </param>
    public static BenchmarkResult Combine(IReadOnlyList<Launch> launches)
    {
        ArgumentNullException.ThrowIfNull(launches);

        if (launches.Count == 0)
            throw new ArgumentException("At least one launch is required.", nameof(launches));

        var launchResults = launches.Select(l => l.Result).ToList();
        var successful = launches.Where(l => !l.Result.Errored).ToList();

        // Read off the results rather than passed in. Every launch already carries the level the
        // engine measured it at, so there is one source of truth and no call site can quietly
        // aggregate at 95% a run the user asked to see at 99%. Threading it as a parameter is how the
        // previous code came to use the default everywhere while the results said otherwise.
        var confidenceLevel = successful.Count > 0 ? successful[0].Result.ConfidenceLevel : 0.95;
        var statistics = Aggregate(launchResults, confidenceLevel);

        // Every launch failed. There is nothing to average, and the first failure's message is the
        // most useful thing to show.
        if (successful.Count == 0)
            return Apply(launchResults[0], statistics);

        var combined = successful.Count == 1
            ? successful[0].Result
            : Average(successful.Select(l => l.Result).ToList(), statistics, confidenceLevel);

        // The launch whose median sits closest to the combined one. Samples, their trim marks and the
        // histogram have to come from a single launch - the ordinals index into the samples, and marks
        // against a different array would point at the wrong ones - so the honest choice is the most
        // representative launch rather than the fastest. Picked against the *combined* median so the
        // distribution shown is the one nearest the number printed above it.
        var representative = Nearest(successful, combined.MedianNs);

        return Apply(
            combined with
            {
                Histogram = representative.Result.Histogram,
                TrimmedOrdinals = representative.Result.TrimmedOrdinals,
                RawSamples = representative.RawSamples,
            },
            statistics);
    }

    /// <summary>
    ///     Averages the distribution estimates across launches, sums the counts, and replaces the
    ///     interval with the between-launch one.
    /// </summary>
    private static BenchmarkResult Average(
        IReadOnlyList<BenchmarkResult> successful,
        LaunchStatistics statistics,
        double confidenceLevel)
    {
        var count = successful.Count;
        var mean = successful.Average(r => r.MeanNs);
        var median = successful.Average(r => r.MedianNs);

        // The standard error of the *mean of launch medians*, which is what the combined median is.
        // LaunchStandardDeviation is the spread of those medians, so this is the textbook s/sqrt(k) -
        // and the reason the reported interval finally describes reproducibility.
        var standardError = statistics.LaunchStandardDeviation / Math.Sqrt(count);

        var marginOfError = count > 1
            ? StudentT.CriticalValue(confidenceLevel, count - 1) * standardError
            : successful[0].MarginOfErrorNs;

        return successful[0] with
        {
            MeanNs = mean,
            MedianNs = median,

            // Extremes over everything observed, so the minimum of minima and the maximum of maxima.
            // Averaging an extreme would report a value no launch actually saw at either end.
            MinNs = successful.Min(r => r.MinNs),
            MaxNs = successful.Max(r => r.MaxNs),

            Percentiles = AveragePercentiles(successful),
            StandardDeviationNs = successful.Average(r => r.StandardDeviationNs),
            StandardErrorNs = standardError,
            MarginOfErrorNs = marginOfError,
            ConfidenceLevel = confidenceLevel,
            CoefficientOfVariation = successful.Average(r => r.CoefficientOfVariation),
            Q1Ns = successful.Average(r => r.Q1Ns),
            Q3Ns = successful.Average(r => r.Q3Ns),
            InterquartileRangeNs = successful.Average(r => r.InterquartileRangeNs),
            MedianAbsoluteDeviationNs = successful.Average(r => r.MedianAbsoluteDeviationNs),
            Skewness = successful.Average(r => r.Skewness),
            Kurtosis = successful.Average(r => r.Kurtosis),

            // A fence is a threshold derived from Q1Ns/Q3Ns, so it follows them. Present only when every
            // launch reported one, since averaging over a subset would describe a different rule.
            LowerFenceNs = AverageOrNull(successful, r => r.LowerFenceNs),
            UpperFenceNs = AverageOrNull(successful, r => r.UpperFenceNs),

            // The median confidence interval describes reproducibility *between* launches, not
            // precision within one: the Student-t interval over the k launch medians, centred on
            // the combined median and using the same between-launch margin as MarginOfErrorNs above.
            // Averaging each launch's own within-launch (distribution-free) interval instead
            // printed a narrow band around the mean that described spread inside a single process
            // while saying nothing about run-to-run variation - a second interval about the same
            // number with no label to distinguish it from the margin line. The within-launch
            // distribution-free interval is kept only on the single-launch path (see Combine),
            // where there is no between-launch spread to describe.
            MedianConfidenceIntervalLowerNs = median - marginOfError,
            MedianConfidenceIntervalUpperNs = median + marginOfError,

            // Counts and durations are totals: the run really did take this long and really did
            // measure this many samples.
            SampleCount = successful.Sum(r => r.SampleCount),
            OutliersRemoved = successful.Sum(r => r.OutliersRemoved),
            TotalOperations = successful.Sum(r => r.TotalOperations),
            TotalDuration = Total(successful, r => r.TotalDuration),
            MeasuredDuration = Total(successful, r => r.MeasuredDuration),

            // Derived from the averaged times rather than averaged themselves: throughput is 1/time,
            // and the mean of reciprocals is not the reciprocal of the mean. Averaging both
            // independently would print a rate that contradicts the duration beside it.
            OperationsPerSecond = Throughput(mean),
            MedianOperationsPerSecond = Throughput(median),

            // Allocation samples do not cross the process boundary, so these are combined from the
            // per-launch summaries. A median of medians rather than a mean, because allocation per op
            // is usually identical across launches and a single anomalous launch should not move it.
            AllocatedBytesMedian = MedianOrNull(successful, r => r.AllocatedBytesMedian),
            AllocatedBytesP95 = MedianOrNull(successful, r => r.AllocatedBytesP95),
            AllocatedBytesMax = successful.Select(r => r.AllocatedBytesMax).Where(v => v.HasValue).Max(),
            AllocatedBytesMean = MeanOrNull(successful, r => r.AllocatedBytesMean),

            // Averaged rather than taken from one launch. Each launch is its own worker with its
            // own canary origin, so the absolute readings are not comparable between them - but
            // RelativeToRunStart is dimensionless and normalised within each launch, so its mean
            // is the honest answer to "across the launches, how far into a drifting host was this
            // row measured?". The completed-benchmark count averages for the same reason: under a random run order each
            // launch places the benchmark differently, and no single launch's index describes the
            // aggregate.
            HostTimeline = AverageTimeline(successful),

            // A warning any launch raised is true of the run, so they union rather than being taken
            // from one launch. Deduplicated because the same condition usually fires in all of them.
            Warnings = successful
                .SelectMany(r => r.Warnings)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
        };
    }

    /// <summary>
    ///     Averages each reported percentile across launches, keyed by the percentile itself.
    /// </summary>
    /// <remarks>
    ///     Keyed rather than positional: a launch that errored or ran under a different
    ///     <c>ReportedPercentiles</c> would otherwise have its p50 averaged into another launch's p95.
    ///     Only percentiles every launch reported survive, so a partially-present one is dropped rather
    ///     than averaged over a subset and presented as though it were not.
    /// </remarks>
    private static IReadOnlyList<PercentileEntry> AveragePercentiles(IReadOnlyList<BenchmarkResult> successful)
        => successful
            .SelectMany(r => r.Percentiles)
            .GroupBy(p => p.Percentile)
            .Where(g => g.Count() == successful.Count)
            .OrderBy(g => g.Key)
            .Select(g => new PercentileEntry(g.Key, g.Average(p => p.Value)))
            .ToList();

    /// <summary>
    ///     The mean drift-canary stamp over the launches that produced one, or <c>null</c> when
    ///     none did. Averaged over the launches that have a stamp rather than requiring all of
    ///     them, because one launch whose canary reading came back unusable should not remove the
    ///     timeline from the row.
    /// </summary>
    private static HostTimeline? AverageTimeline(IReadOnlyList<BenchmarkResult> results)
    {
        var stamps = results.Select(r => r.HostTimeline).OfType<HostTimeline>().ToList();

        if (stamps.Count == 0)
            return null;

        return new HostTimeline
        {
            BeforeNs = stamps.Average(s => s.BeforeNs),
            AfterNs = stamps.Average(s => s.AfterNs),
            RelativeToRunStart = stamps.Average(s => s.RelativeToRunStart),
            CompletedBenchmarks = stamps.Average(s => s.CompletedBenchmarks),
        };
    }

    /// <summary>The launch whose median is nearest <paramref name="target" />.</summary>
    private static Launch Nearest(IReadOnlyList<Launch> successful, double target)
        => successful.MinBy(l => Math.Abs(l.Result.MedianNs - target));

    private static double Throughput(double nanoseconds)
        => nanoseconds > 0 ? 1_000_000_000.0 / nanoseconds : double.NaN;

    private static TimeSpan Total(IReadOnlyList<BenchmarkResult> results, Func<BenchmarkResult, TimeSpan> select)
        => results.Aggregate(TimeSpan.Zero, (sum, r) => sum + select(r));

    /// <summary>An average over the launches that reported a value, or <c>null</c> when none did.</summary>
    private static double? AverageOrNull(
        IReadOnlyList<BenchmarkResult> results,
        Func<BenchmarkResult, double?> select)
    {
        var values = results.Select(select).OfType<double>().ToList();

        return values.Count == 0 ? null : values.Average();
    }

    private static long? MeanOrNull(IReadOnlyList<BenchmarkResult> results, Func<BenchmarkResult, long?> select)
    {
        var values = results.Select(select).OfType<long>().ToList();

        return values.Count == 0 ? null : (long)values.Average();
    }

    private static long? MedianOrNull(IReadOnlyList<BenchmarkResult> results, Func<BenchmarkResult, long?> select)
    {
        var values = results.Select(select).OfType<long>().OrderBy(v => v).ToList();

        if (values.Count == 0)
            return null;

        var mid = values.Count / 2;

        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private static double MedianOf(double[] values)
    {
        var sorted = values.Length switch
        {
            0 => Array.Empty<double>(),
            1 => values,
            _ => values.OrderBy(v => v).ToArray(),
        };

        if (sorted.Length == 0)
            return 0;

        var mid = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
