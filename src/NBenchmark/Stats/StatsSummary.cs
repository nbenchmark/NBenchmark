namespace NBenchmark.Stats;

public sealed class StatsSummary
{
    public double Mean { get; init; }
    public double Median { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double StandardDeviation { get; init; }

    /// <summary>Standard error of the mean: <c>StandardDeviation / sqrt(n)</c>.</summary>
    public double StandardError { get; init; }

    /// <summary>
    ///     Half-width of the confidence interval on the mean
    ///     (<c>t* × StandardError</c>) at <see cref="ConfidenceLevel" />.
    /// </summary>
    public double MarginOfError { get; init; }

    /// <summary>The confidence level used to compute <see cref="MarginOfError" /> (e.g. 0.95).</summary>
    public double ConfidenceLevel { get; init; }

    /// <summary>Coefficient of variation: <c>StandardDeviation / Mean</c> (0 when mean is 0).</summary>
    public double CoefficientOfVariation { get; init; }

    /// <param name="samples">Sorted (ascending) measurement samples.</param>
    /// <param name="confidenceLevel">Confidence level for the interval on the mean (0 &lt; level &lt; 1).</param>
    public static StatsSummary Compute(double[] samples, double confidenceLevel = 0.95)
    {
        if (samples.Length == 0)
            return new StatsSummary { ConfidenceLevel = confidenceLevel };

        var n = samples.Length;
        var mean = samples.Average();

        var sumSq = 0.0;

        for (var i = 0; i < n; i++)
        {
            var d = samples[i] - mean;
            sumSq += d * d;
        }

        // Sample standard deviation (Bessel's correction, n-1). Undefined for n < 2,
        // in which case there is no spread to report and the interval collapses to the mean.
        var sampleStdDev = n > 1 ? Math.Sqrt(sumSq / (n - 1)) : 0.0;
        var standardError = n > 1 ? sampleStdDev / Math.Sqrt(n) : 0.0;

        var marginOfError = 0.0;

        if (n > 1)
        {
            var tCritical = StudentT.CriticalValue(confidenceLevel, n - 1);

            if (!double.IsNaN(tCritical))
                marginOfError = tCritical * standardError;
        }

        var cv = mean != 0 ? sampleStdDev / mean : 0.0;

        return new StatsSummary
        {
            Mean = mean,
            Median = Percentile.Compute(samples, 0.50),
            P95 = Percentile.Compute(samples, 0.95),
            P99 = Percentile.Compute(samples, 0.99),
            Min = samples[0],
            Max = samples[^1],
            StandardDeviation = sampleStdDev,
            StandardError = standardError,
            MarginOfError = marginOfError,
            ConfidenceLevel = confidenceLevel,
            CoefficientOfVariation = cv,
        };
    }
}