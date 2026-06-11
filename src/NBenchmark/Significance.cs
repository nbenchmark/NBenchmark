using NBenchmark.Stats;

namespace NBenchmark;

internal static class Significance
{
    public static void ApplyIfEnabled(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples,
        MeasurementOptions options)
    {
        if (!options.EnableSignificance)
            return;

        if (results.Count(r => !r.Errored) < 2)
            return;

        ComputeSignificance(results, rawSamples, options.SignificanceLevel);
    }

    /// <summary>
    ///     Computes the Mann-Whitney U significance verdict for each non-baseline result
    ///     against the baseline and updates <paramref name="results" /> <b>in place</b>:
    ///     each affected element is replaced with an updated <see cref="BenchmarkResult" />.
    /// </summary>
    public static void ComputeSignificance(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples,
        double significanceLevel = 0.05)
    {
        var successful = results.Where(r => !r.Errored).ToList();

        if (successful.Count == 0)
            return;

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                       ?? successful.MinBy(r => r.Median)!;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];

            if (result == baseline || result.Errored)
                continue;

            if (rawSamples.TryGetValue(baseline.Name, out var baselineSamples) &&
                rawSamples.TryGetValue(result.Name, out var candidateSamples))
            {
                var pValue = MannWhitneyU.Test(baselineSamples, candidateSamples);

                results[i] = double.IsNaN(pValue)
                    ? result with { PValue = null, SignificanceVerdict = SignificanceVerdict.NotTested }
                    : result with
                    {
                        PValue = pValue, SignificanceVerdict = pValue < significanceLevel ? SignificanceVerdict.Significant : SignificanceVerdict.NotSignificant,
                    };
            }
        }
    }
}
