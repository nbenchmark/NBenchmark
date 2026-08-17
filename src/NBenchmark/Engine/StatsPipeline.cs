using System.Diagnostics;
using NBenchmark.Engine.Detectors;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

/// <summary>
///     The full reject -> trim -> summary -> warnings pipeline that turns raw per-op timings and
///     allocation deltas into a <see cref="ProcessedMeasurements" />. Exposed so an
///     external consumer that has captured raw samples (for example NBenchmark.Studio,
///     ingesting OTLP) can run exactly the same statistical processing the engine uses,
///     without reimplementing interference rejection, outlier trimming, and warning generation.
/// </summary>
public static class StatsPipeline
{
    public static ProcessedMeasurements Run(
        double[] rawTimings,
        long[]? rawAllocations,
        MeasurementOptions options,
        int[]? perSampleGcCounts = null,
        double[]? perSampleOccupancy = null)
    {
        // Evidence-based interference rejection runs first, deliberately as a separate stage from
        // OutlierTrim: "was this sample valid?" (evidence: CPU occupancy) and "is this value an
        // outlier?" (evidence: the timing distribution) are different questions from different
        // evidence. IOutlierDetector.Classify never sees a rejected sample, so every built-in and
        // every user-supplied detector composes with this filter for free.
        var rejection = InterferenceRejector.Reject(rawTimings, perSampleOccupancy, options.Interference);

        // OutlierTrim.TrimDetailed clones internally and does not mutate the input, so the surviving
        // set stays in arrival order and TrimmedOrdinals - relative to that surviving set - map back
        // correctly once remapped onto the original rawTimings ordinals below.
        var trimResult = OutlierTrim.TrimDetailed(rejection.SurvivingTimings, options.ResolveOutlierDetector());

        Debug.Assert(IsSorted(trimResult.Kept),
            "OutlierTrim must produce sorted output; Percentile.Compute requires sorted input.");

        // Tail metrics (percentiles/min/max/histogram) read from the full pre-trim, post-rejection set
        // by default so the fence does not trim out the very tail those metrics exist to describe.
        // Rejected samples are excluded even here: they measure the OS, not the code, so they are
        // contamination rather than tail and must not appear in a distribution describing the
        // benchmark. Passing null keeps them on the trimmed (Kept) set - the Trimmed escape hatch.
        var tailSource = options.TailMetricsBasis == TailMetricsBasis.Raw
            ? trimResult.SortedAll
            : null;

        // The trim context is what lets the reported interval be the Winsorized (Yuen) one: a
        // t-interval on the kept set alone describes a run that produced only the inliers, which is
        // not the run that happened. It is passed separately from tailSource even though both can
        // be the same array - the tail basis is a user choice about which distribution the
        // percentiles describe, while the interval correction is not optional.
        var stats = StatsSummary.Compute(
            trimResult.Kept,
            options.ConfidenceLevel,
            options.ReportedPercentiles,
            options.EnableHistogram,
            options.HistogramBucketCount,
            tailSource,
            TrimContext.From(trimResult));

        // OutlierTrim's ordinals are positions in rejection.SurvivingTimings, not in rawTimings -
        // remap them back onto the original sample stream so every ordinal-based consumer
        // (RecoverDiscardedOrdinals, SampleReservoir, the GC-correlation annotation below) keeps
        // pointing at the right raw sample.
        var trimmedOrdinals = RemapOrdinals(trimResult.TrimmedOrdinals, rejection.SurvivingOriginalIndices);

        long? meanAllocs = rawAllocations is not null ? ComputeMean(rawAllocations) : null;
        var interferenceRejectedCount = rejection.RejectedOriginalIndices.Length;
        var warnings = BuildWarnings(
            trimResult.Kept, trimResult.Discarded, rawTimings, trimmedOrdinals, perSampleGcCounts,
            interferenceRejectedCount, options.Interference);

        // The total discarded before stats were computed - confirmed OS preemption plus statistical
        // outliers - which is what OutliersRemoved has always meant: rawTimings.Length minus however
        // many samples fed the summary.
        var outliersRemoved = rawTimings.Length - trimResult.Kept.Length;

        return new ProcessedMeasurements(
                stats,
                trimResult.Kept.Length,
                meanAllocs,
                trimResult.Q1,
                trimResult.Q3,
                trimResult.InterquartileRange,
                trimResult.LowerFence,
                trimResult.UpperFence,
                outliersRemoved,
                rawAllocations,
                trimmedOrdinals)
            {
                Warnings = warnings,
                InterferenceRejectedCount = interferenceRejectedCount,
                MedianOccupancyRatio = rejection.MedianOccupancyRatio,
                InterferenceDisabledReason = rejection.DisabledReason,
            };
    }

    /// <summary>
    ///     Translates ordinals computed against a filtered array back onto the original array's
    ///     index space, via the parallel index map a filtering stage produces.
    /// </summary>
    private static int[] RemapOrdinals(int[] filteredOrdinals, int[] filteredToOriginal)
    {
        if (filteredOrdinals.Length == 0)
            return [];

        var remapped = new int[filteredOrdinals.Length];

        for (var i = 0; i < filteredOrdinals.Length; i++)
        {
            remapped[i] = filteredToOriginal[filteredOrdinals[i]];
        }

        return remapped;
    }

    private static IReadOnlyList<string> BuildWarnings(
        double[] trimmed,
        double[] discarded,
        double[] rawTimings,
        int[] trimmedOrdinals,
        int[]? perSampleGcCounts,
        int interferenceRejectedCount,
        InterferenceOptions interferenceOptions)
    {
        var warnings = new List<string>();

        var cluster = BimodalDetector.DetectSlowCluster(trimmed, discarded, rawTimings.Length);
        var gcCorrelatedOutliers = CountGcCorrelatedOutliers(trimmedOrdinals, perSampleGcCounts);
        var statisticalOutliers = trimmedOrdinals.Length;

        // Interference rejection found confirmed evidence, so it takes the headline: fold the
        // statistical-outlier and (when present) bimodal/GC-correlation detail into one message
        // rather than reporting the same discarded samples from two angles. This mirrors the
        // pre-existing fold-in of GC correlation into the bimodal warning below.
        if (interferenceRejectedCount > 0)
        {
            var totalDiscarded = interferenceRejectedCount + statisticalOutliers;

            var message =
                $"{totalDiscarded} sample(s) discarded - {interferenceRejectedCount} confirmed preempted "
                + $"by the OS (CPU occupancy well below this benchmark's own median), {statisticalOutliers} "
                + "statistical outlier(s).";

            if (cluster is { } clusterValue)
            {
                var (count, center) = clusterValue;

                message +=
                    $" {count} of the statistical outliers form a distinct cluster near "
                    + $"{BenchmarkFormatter.FormatNs(center)} rather than scattered noise - possible "
                    + "bimodal distribution; investigate further (e.g. lock contention or cache misses).";
            }

            if (gcCorrelatedOutliers > 0)
                message += $" ({gcCorrelatedOutliers} of the statistical outliers coincided with a garbage collection.)";

            warnings.Add(message);
        }
        else if (cluster is { } clusterValue)
        {
            var (count, center) = clusterValue;

            var message =
                $"{count} discarded outlier(s) form a distinct cluster near {BenchmarkFormatter.FormatNs(center)} "
                + "rather than scattered noise - possible bimodal distribution; investigate this tail latency "
                + "(e.g. GC pauses, lock contention, or cache misses).";

            // If the discarded cluster is GC-correlated, say so - it answers the first question a
            // bimodal warning raises ("was that a GC?") instead of leaving the user to re-run.
            if (gcCorrelatedOutliers > 0)
                message += $" ({gcCorrelatedOutliers} of the discarded outliers coincided with a garbage collection.)";

            warnings.Add(message);
        }
        else if (gcCorrelatedOutliers > 0)
        {
            var removed = trimmedOrdinals.Length;
            warnings.Add(
                $"{gcCorrelatedOutliers} of {removed} removed outlier(s) coincided with a garbage collection.");
        }

        // The "this host is too noisy to trust" signal: a separate warning from the fold-in above,
        // since it is about the host rather than about explaining any one discarded sample.
        if (interferenceRejectedCount > 0 && rawTimings.Length > 0
            && (double)interferenceRejectedCount / rawTimings.Length >= interferenceOptions.HighRejectionWarningFraction)
        {
            warnings.Add(
                $"{interferenceRejectedCount} of {rawTimings.Length} samples "
                + $"({(double)interferenceRejectedCount / rawTimings.Length:P0}) were rejected as confirmed OS "
                + "preemption - this host is too noisy to trust for a precise measurement right now. "
                + "Consider a quieter host, --cpu-affinity, or re-running once background load has cleared.");
        }

        // i.i.d. sanity checks on the arrival-order stream (drift, autocorrelation).
        warnings.AddRange(SampleQuality.BuildWarnings(rawTimings));

        return warnings.Count == 0 ? [] : warnings;
    }

    /// <summary>
    ///     Counts trimmed samples whose per-sample GC delta was positive - i.e. a collection
    ///     happened during that sample. Returns 0 when GC counts were not collected.
    /// </summary>
    private static int CountGcCorrelatedOutliers(int[] trimmedOrdinals, int[]? perSampleGcCounts)
    {
        if (perSampleGcCounts is null || trimmedOrdinals.Length == 0)
            return 0;

        var count = 0;

        foreach (var ordinal in trimmedOrdinals)
        {
            if (ordinal >= 0 && ordinal < perSampleGcCounts.Length && perSampleGcCounts[ordinal] > 0)
                count++;
        }

        return count;
    }

    private static long ComputeMean(long[] values)
    {
        double sum = 0;

        for (var i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return (long)(sum / values.Length);
    }

    private static bool IsSorted(double[] values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
                return false;
        }

        return true;
    }
}

/// <summary>
///     The processed output of <see cref="StatsPipeline.Run" />: the summary statistics, the
///     measured (post-trim) iteration count, mean allocations, the quartile statistics and
///     fence boundaries, the outlier count, the raw allocation samples, the original
///     ordinals of every trimmed sample, and any warnings the pipeline produced.
/// </summary>
public sealed record ProcessedMeasurements(
    StatsSummary Stats,
    int MeasuredIterations,
    long? MeanAllocatedBytes,
    double Q1,
    double Q3,
    double InterquartileRange,
    double? LowerFence,
    double? UpperFence,
    int OutliersRemoved,
    long[]? RawAllocations,
    int[] TrimmedOrdinals)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DiagnosticsResult? DiagnosticsResult { get; init; }

    /// <summary>
    ///     How many of <see cref="OutliersRemoved" /> were confirmed OS preemption (evidence-based
    ///     interference rejection) rather than statistical outliers. <c>0</c> when the filter is
    ///     disabled, could not run (see <see cref="InterferenceDisabledReason" />), or found nothing.
    /// </summary>
    public int InterferenceRejectedCount { get; init; }

    /// <summary>
    ///     This benchmark's own median CPU-occupancy ratio, the value the interference filter
    ///     rejected against. <c>null</c> when the filter did not run.
    /// </summary>
    public double? MedianOccupancyRatio { get; init; }

    /// <summary>
    ///     Why the interference filter did not reject anything on its own initiative, or
    ///     <c>null</c> when it ran normally. See <see cref="AutoTuneDiagnostic.InterferenceDisabledReason" />.
    /// </summary>
    public string? InterferenceDisabledReason { get; init; }
}
