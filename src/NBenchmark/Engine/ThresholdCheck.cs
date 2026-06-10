namespace NBenchmark.Engine;

internal static class ThresholdCheck
{
    public static (bool HasRegression, IReadOnlyList<string> RegressedNames) HasRegression(
        IReadOnlyList<BenchmarkResult> results, int thresholdPct)
    {
        if (thresholdPct <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdPct), "Must be a positive integer (1 or greater).");

        var successful = results.Where(r => !r.Errored).ToList();

        if (successful.Count <= 1)
            return (false, Array.Empty<string>());

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                       ?? successful.MinBy(r => r.Median)!;

        var regressed = new List<string>();

        if (baseline.Median <= 0)
        {
            // Ratio comparison is undefined at zero; treat any positive candidate median as slower.
            for (var i = 0; i < successful.Count; i++)
            {
                var result = successful[i];

                if (ReferenceEquals(result, baseline))
                    continue;

                if (result.Median > 0)
                    regressed.Add(result.Name);
            }
        }
        else
        {
            var thresholdMedian = baseline.Median * (1.0 + thresholdPct / 100.0);

            for (var i = 0; i < successful.Count; i++)
            {
                var result = successful[i];

                if (ReferenceEquals(result, baseline))
                    continue;

                if (result.Median > thresholdMedian)
                    regressed.Add(result.Name);
            }
        }

        if (regressed.Count == 0)
            return (false, Array.Empty<string>());

        regressed.Sort(StringComparer.Ordinal);

        return (true, regressed);
    }
}
