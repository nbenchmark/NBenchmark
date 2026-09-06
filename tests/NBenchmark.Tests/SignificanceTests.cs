using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class SignificanceTests
{
    [Fact]
    public void ComputeSignificance_Sets_PValue_And_SignificanceVerdict()
    {
        var rng = new Random(42);
        var baselineSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var fasterSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(40, 60)).ToArray();

        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "faster", MeanNs = 50, MedianNs = 50, Percentiles = [],
                MinNs = 40, MaxNs = 60, StandardDeviationNs = 3, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["faster"] = fasterSamples,
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.NotNull(results[1].PValue);
        Assert.NotEqual(SignificanceVerdict.NotTested, results[1].SignificanceVerdict);
        Assert.True(results[1].PValue < 0.05);
        Assert.Equal(SignificanceVerdict.Significant, results[1].SignificanceVerdict);
    }

    /// <summary>
    ///     <see cref="Significance.ComputeSignificance(System.Collections.Generic.List{BenchmarkResult}, System.Collections.Generic.Dictionary{string, double[]}, double, double?, double?, double?)" /> is public, so a caller can hand it a list
    ///     with two successful results sharing a <see cref="BenchmarkResult.Name" />. The sample
    ///     lookup, group construction and write-back all key on <c>Name</c>, so a collision either
    ///     collapses two benchmarks onto one sample set (silent corruption) or surfaces later as an
    ///     opaque <c>"An item with the same key has already been added"</c> from the write-back
    ///     dictionary. The entry point must refuse the input with a message that names the duplicate
    ///     and points at the remedy, rather than corrupting the comparison or throwing opaquely.
    /// </summary>
    [Fact]
    public void ComputeSignificance_Throws_On_Duplicate_Names()
    {
        var firstSamples = Enumerable.Range(0, 50).Select(_ => (double)100).ToArray();
        var secondSamples = Enumerable.Range(0, 50).Select(_ => (double)80).ToArray();

        // Two successful results share the name "Dupe" - the collision the guard must catch.
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "Dupe", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "Dupe", MeanNs = 80, MedianNs = 80, Percentiles = [],
                MinNs = 70, MaxNs = 100, StandardDeviationNs = 5, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["Dupe"] = firstSamples,
            // A caller that has already collapsed the two benchmarks into one keyed entry is the
            // corruption case; the guard must fire before the sample lookup can paper over it.
            ["Dupe#2"] = secondSamples,
        };

        var ex = Assert.Throws<ArgumentException>(
            () => Significance.ComputeSignificance(results, rawSamples));

        // The message must name the colliding benchmark and steer the user at the remedy, rather
        // than the framework's generic duplicate-key text.
        Assert.Contains("Dupe", ex.Message);
        Assert.Contains("unique", ex.Message);
    }

    [Fact]
    public void ComputeSignificance_Populates_Median_Shift_On_Candidate()
    {
        var rng = new Random(42);
        var baselineSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var fasterSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(40, 60)).ToArray();

        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "faster", MeanNs = 50, MedianNs = 50, Percentiles = [],
                MinNs = 40, MaxNs = 60, StandardDeviationNs = 3, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["faster"] = fasterSamples,
        };

        Significance.ComputeSignificance(results, rawSamples);

        // Baseline carries no shift; the candidate is clearly faster, so the shift is negative and
        // its interval excludes zero.
        Assert.Null(results[0].MedianShift);
        Assert.NotNull(results[1].MedianShift);
        Assert.True(results[1].MedianShift!.Value.Value < 0);
        Assert.True(results[1].MedianShift!.Value.Upper < 0);
    }

    [Fact]
    public void ComputeSignificance_Does_Not_Set_Baseline_PValue()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "other", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Enumerable.Range(0, 50).Select(_ => 100.0).ToArray(),
            ["other"] = Enumerable.Range(0, 50).Select(_ => 100.0).ToArray(),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
        Assert.Equal(SignificanceVerdict.NotTested, results[0].SignificanceVerdict);
    }

    [Fact]
    public void ComputeSignificance_Skips_Errored_Results()
    {
        var results = new List<BenchmarkResult>
        {
            ErroredResult("broken", "error"),
            new()
            {
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = Enumerable.Range(0, 50).Select(i => (double)i).ToArray(),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
    }

    [Fact]
    public void ComputeSignificance_With_Only_One_Result_Does_Nothing()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "solo", MeanNs = 100, MedianNs = 100, Percentiles = [],
                MinNs = 85, MaxNs = 120, StandardDeviationNs = 5,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var rawSamples = new Dictionary<string, double[]>
        {
            ["solo"] = Enumerable.Range(0, 50).Select(i => (double)i).ToArray(),
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
    }

    [Fact]
    public void ComputeSignificance_Uses_MinBy_Median_When_No_Baseline()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "fast", MeanNs = 50, MedianNs = 50, Percentiles = [],
                MinNs = 40, MaxNs = 60, StandardDeviationNs = 3, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "slow", MeanNs = 200, MedianNs = 200, Percentiles = [],
                MinNs = 180, MaxNs = 260, StandardDeviationNs = 10, IsBaseline = false,
                Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var rng = new Random(42);
        var fastSamples = Enumerable.Range(0, 30).Select(_ => 50.0 + (rng.NextDouble() - 0.5) * 10).ToArray();
        var slowSamples = Enumerable.Range(0, 30).Select(_ => 200.0 + (rng.NextDouble() - 0.5) * 20).ToArray();

        var rawSamples = new Dictionary<string, double[]>
        {
            ["fast"] = fastSamples,
            ["slow"] = slowSamples,
        };

        Significance.ComputeSignificance(results, rawSamples);

        Assert.Null(results[0].PValue);
        Assert.NotNull(results[1].PValue);
    }

    [Fact]
    public void ApplyIfEnabled_Skips_When_Disabled()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "a", MeanNs = 100, MedianNs = 100, Percentiles = [], MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true, Q1Ns = 0, Q3Ns = 0,
                InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0, Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
            new()
            {
                Name = "b", MeanNs = 50, MedianNs = 50, Percentiles = [], MinNs = 40, MaxNs = 60, StandardDeviationNs = 3, Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0,
                OutliersRemoved = 0, SampleCount = 0, Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var raw = new Dictionary<string, double[]> { ["a"] = [10, 11, 12], ["b"] = [1, 2, 3] };

        Significance.ApplyIfEnabled(results, raw, new MeasurementOptions { EnableSignificance = false });

        Assert.All(results, r => Assert.Null(r.PValue));
    }

    [Fact]
    public void ApplyIfEnabled_Skips_When_Fewer_Than_Two_Results()
    {
        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "a", MeanNs = 100, MedianNs = 100, Percentiles = [], MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true, Q1Ns = 0, Q3Ns = 0,
                InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0, Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var raw = new Dictionary<string, double[]> { ["a"] = [10, 11, 12] };

        Significance.ApplyIfEnabled(results, raw, MeasurementOptions.Default);

        Assert.Null(results[0].PValue);
    }

    [Fact]
    public void ApplyIfEnabled_Runs_When_Two_Or_More_Successful()
    {
        var rng = new Random(42);
        var baselineSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var fasterSamples = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(40, 60)).ToArray();

        var results = new List<BenchmarkResult>
        {
            new()
            {
                Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [], MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true, Q1Ns = 0,
                Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0, Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null,
                AllocatedBytesMax = null,
            },
            new()
            {
                Name = "faster", MeanNs = 50, MedianNs = 50, Percentiles = [], MinNs = 40, MaxNs = 60, StandardDeviationNs = 3, Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0,
                OutliersRemoved = 0, SampleCount = 0, Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
            },
        };

        var raw = new Dictionary<string, double[]> { ["baseline"] = baselineSamples, ["faster"] = fasterSamples };

        Significance.ApplyIfEnabled(results, raw, MeasurementOptions.Default);

        Assert.NotNull(results[1].PValue);
    }

    [Fact]
    public void ComputeSignificance_Respects_Configurable_SignificanceLevel()
    {
        var rng = new Random(7);
        var baselineSamples = Enumerable.Range(0, 40).Select(_ => 100.0 + (rng.NextDouble() - 0.5) * 30).ToArray();
        var candidateSamples = Enumerable.Range(0, 40).Select(_ => 92.0 + (rng.NextDouble() - 0.5) * 30).ToArray();

        var rawSamples = new Dictionary<string, double[]>
        {
            ["baseline"] = baselineSamples,
            ["candidate"] = candidateSamples,
        };

        // First measure the actual p-value at the default level.
        var probe = NewPair();
        Significance.ComputeSignificance(probe, rawSamples);
        var pValue = probe[1].PValue;

        Assert.NotNull(pValue);

        // A threshold below the observed p makes the same difference NOT significant.
        var strict = NewPair();
        Significance.ComputeSignificance(strict, rawSamples, pValue!.Value / 2);
        Assert.Equal(SignificanceVerdict.NotSignificant, strict[1].SignificanceVerdict);

        // A threshold above the observed p makes it significant.
        var lenient = NewPair();
        Significance.ComputeSignificance(lenient, rawSamples, Math.Min(1.0, pValue.Value * 2));
        Assert.Equal(SignificanceVerdict.Significant, lenient[1].SignificanceVerdict);

        static List<BenchmarkResult> NewPair()
        {
            return
            [
                new BenchmarkResult
                {
                    Name = "baseline", MeanNs = 100, MedianNs = 100, Percentiles = [],
                    MinNs = 85, MaxNs = 120, StandardDeviationNs = 5, IsBaseline = true,
                    Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                    Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
                },
                new BenchmarkResult
                {
                    Name = "candidate", MeanNs = 92, MedianNs = 92, Percentiles = [],
                    MinNs = 80, MaxNs = 110, StandardDeviationNs = 5, IsBaseline = false,
                    Q1Ns = 0, Q3Ns = 0, InterquartileRangeNs = 0, OutliersRemoved = 0, SampleCount = 0,
                    Skewness = 0, Kurtosis = 0, MedianAbsoluteDeviationNs = 0, AllocatedBytesMedian = null, AllocatedBytesP95 = null, AllocatedBytesMax = null,
                },
            ];
        }
    }

    private static BenchmarkResult ErroredResult(string name, string error) =>
        new()
        {
            Name = name,
            MeanNs = 0,
            MedianNs = 0,
            Percentiles = [],
            MinNs = 0,
            MaxNs = 0,
            StandardDeviationNs = 0,
            Errored = true,
            ErrorMessage = error,
            Q1Ns = 0,
            Q3Ns = 0,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            SampleCount = 0,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
        };
}
