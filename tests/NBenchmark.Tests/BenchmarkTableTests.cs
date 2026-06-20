using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkTableTests
{
    [Fact]
    public void Build_EmptyResults_ReturnsEmptyTable()
    {
        var table = BenchmarkTable.Build([]);

        Assert.Empty(table.Rows);
        Assert.Equal("", table.RunAtUtc);
        Assert.Equal(0, table.WarmupIterations);
        Assert.Equal(0, table.MeasuredIterations);
    }

    [Fact]
    public void Build_SingleBenchmark_BaselineIsThatBenchmark()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "A", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        var row = Assert.Single(table.Rows);
        Assert.Equal("A", row.Name);
        Assert.Equal(1.0, row.Ratio);
        Assert.False(row.Errored);
    }

    [Fact]
    public void Build_Does_Not_Mutate_Benchmark_Name_When_Outliers_Were_Removed()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "A", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                OutliersRemoved = 2,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal("A", Assert.Single(table.Rows).Name);
    }

    [Fact]
    public void Build_MultipleBenchmarks_FindsExplicitBaseline()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Fast", Mean = 50, Median = 50, Percentiles = [], Min = 50, Max = 50,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Slow", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
                IsBaseline = true,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal(2, table.Rows.Count);
        var slowRow = table.Rows.First(r => r.Name == "Slow");
        var fastRow = table.Rows.First(r => r.Name == "Fast");
        Assert.True(slowRow.IsBaseline);
        Assert.Equal(1.0, slowRow.Ratio);
        Assert.Equal(0.5, fastRow.Ratio);
    }

    [Fact]
    public void Build_MultipleBenchmarks_NoExplicitBaseline_UsesMinByMedian()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Fast", Mean = 50, Median = 50, Percentiles = [], Min = 50, Max = 50,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Slow", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal(2, table.Rows.Count);
        var fastRow = table.Rows.First(r => r.Name == "Fast");
        var slowRow = table.Rows.First(r => r.Name == "Slow");
        Assert.False(fastRow.IsBaseline);
        Assert.False(slowRow.IsBaseline);
        Assert.Equal(1.0, fastRow.Ratio);
        Assert.Equal(2.0, slowRow.Ratio);
    }

    [Fact]
    public void Build_AllErrored_NoBaseline()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "A", Mean = 0, Median = 0, Percentiles = [], Min = 0, Max = 0,
                StandardDeviation = 0, Errored = true, ErrorMessage = "fail",
                MeasuredIterations = 0, WarmupIterations = 0, RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        var row = Assert.Single(table.Rows);
        Assert.True(row.Errored);
        Assert.Equal("fail", row.ErrorMessage);
        Assert.True(double.IsNaN(row.Ratio));
    }

    [Fact]
    public void Build_ComputesRatioCorrectly()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Base", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "TwoX", Mean = 200, Median = 200, Percentiles = [], Min = 200, Max = 200,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Half", Mean = 50, Median = 50, Percentiles = [], Min = 50, Max = 50,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal(1.0, table.Rows.First(r => r.Name == "Base").Ratio);
        Assert.Equal(2.0, table.Rows.First(r => r.Name == "TwoX").Ratio);
        Assert.Equal(0.5, table.Rows.First(r => r.Name == "Half").Ratio);
    }

    [Fact]
    public void Build_ZeroMedian_ReturnsNanRatio()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Base", Mean = 0, Median = 0, Percentiles = [], Min = 0, Max = 0,
                StandardDeviation = 0, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Other", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.True(double.IsNaN(table.Rows.First(r => r.Name == "Other").Ratio));
    }

    [Fact]
    public void Build_ErroredBenchmark_HasNanRatio()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Base", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Fail", Mean = 0, Median = 0, Percentiles = [], Min = 0, Max = 0,
                StandardDeviation = 0, Errored = true, ErrorMessage = "crash",
                MeasuredIterations = 0, WarmupIterations = 0,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.True(double.IsNaN(table.Rows.First(r => r.Name == "Fail").Ratio));
    }

    [Fact]
    public void Build_SignificanceLabel_VariesByContext()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Base", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                SignificanceVerdict = SignificanceVerdict.NotSignificant,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Sig", Mean = 200, Median = 200, Percentiles = [], Min = 200, Max = 200,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                SignificanceVerdict = SignificanceVerdict.Significant,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "NotSig", Mean = 150, Median = 150, Percentiles = [], Min = 150, Max = 150,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                SignificanceVerdict = SignificanceVerdict.NotSignificant,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "NoSig", Mean = 120, Median = 120, Percentiles = [], Min = 120, Max = 120,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                SignificanceVerdict = SignificanceVerdict.NotTested,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal("", table.Rows.First(r => r.Name == "Base").SignificanceLabel);
        Assert.Equal("✓", table.Rows.First(r => r.Name == "Sig").SignificanceLabel);
        Assert.Equal("✗", table.Rows.First(r => r.Name == "NotSig").SignificanceLabel);
        Assert.Equal("", table.Rows.First(r => r.Name == "NoSig").SignificanceLabel);
    }

    [Fact]
    public void Build_SingleBenchmark_NoSignificanceLabel()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Only", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                SignificanceVerdict = SignificanceVerdict.Significant,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal("", Assert.Single(table.Rows).SignificanceLabel);
    }

    [Fact]
    public void Build_HeaderMetadata_CopiedFromFirstSuccessful()
    {
        var now = DateTimeOffset.UtcNow;

        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "A", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = now, ConfidenceLevel = 0.99,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal(now.ToString("yyyy-MM-dd HH:mm:ss"), table.RunAtUtc);
        Assert.Equal(5, table.WarmupIterations);
        Assert.Equal(10, table.MeasuredIterations);
        Assert.Equal(0.99, table.ConfidenceLevel);
    }

    [Fact]
    public void Build_TotalDuration_SumsAllEntries()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "A", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                TotalDuration = TimeSpan.FromSeconds(1),
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "B", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                TotalDuration = TimeSpan.FromSeconds(2),
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal(TimeSpan.FromSeconds(3), table.TotalDuration);
    }

    [Fact]
    public void Build_RowsOrderedByMedian()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Slowest", Mean = 300, Median = 300, Percentiles = [], Min = 300, Max = 300,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Fastest", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
            new BenchmarkResult
            {
                Name = "Middle", Mean = 200, Median = 200, Percentiles = [], Min = 200, Max = 200,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0,
                Q3 = 0,
                InterquartileRange = 0,
                OutliersRemoved = 0,
                N = 0,
                Skewness = 0,
                Kurtosis = 0,
                Mad = 0,
                AllocMedian = null,
                AllocP95 = null,
                AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal(["Fastest", "Middle", "Slowest"], table.Rows.Select(r => r.Name));
    }

    [Fact]
    public void Build_CopiesOutlierDetectorName_FromResults()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "A", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                OutlierDetector = "MAD (3×)",
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal("MAD (3×)", table.OutlierDetector);
    }

    [Fact]
    public void Build_SurfacesOmnibus_WhenPresentOnResults()
    {
        var omnibus = new OmnibusComparison
        {
            TestName = "Kruskal-Wallis",
            Statistic = 7.2,
            PValue = 0.027,
            DegreesOfFreedom = 2,
            GroupCount = 3,
            Verdict = SignificanceVerdict.Significant,
        };

        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "A", Mean = 100, Median = 100, Percentiles = [], Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                Omnibus = omnibus,
                RunAtUtc = DateTimeOffset.UtcNow,
                Q1 = 0, Q3 = 0, InterquartileRange = 0, OutliersRemoved = 0, N = 0,
                Skewness = 0, Kurtosis = 0, Mad = 0, AllocMedian = null, AllocP95 = null, AllocMax = null,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.NotNull(table.Omnibus);
        Assert.Equal("Kruskal-Wallis", table.Omnibus!.TestName);
        Assert.Equal(3, table.Omnibus.GroupCount);
    }

    [Fact]
    public void BuildPerClass_NonParameterised_ReturnsOneTablePerClass()
    {
        var results = new[]
        {
            R("ClassA", "M1", 100),
            R("ClassA", "M2", 200),
            R("ClassB", "M1", 150),
        };

        var tables = BenchmarkTable.BuildPerClass(results);

        Assert.Equal(2, tables.Count);
        Assert.All(tables, t => Assert.Empty(t.ParameterNames));
        Assert.Equal(2, tables[0].Rows.Count);
        Assert.Single(tables[1].Rows);
    }

    [Fact]
    public void BuildPerClass_ParameterisedClass_RendersSingleTableWithParameterColumns()
    {
        var results = new[]
        {
            R("Sweep", "Sort", 100, [P("size", 10)], true),
            R("Sweep", "Sort", 200, [P("size", 100)]),
            R("Sweep", "Sort", 300, [P("size", 1000)]),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        Assert.Equal(["size"], table.ParameterNames);
        Assert.Equal(3, table.Rows.Count);
        Assert.All(table.Rows, r => Assert.Equal("Sort", r.BaseName));

        // A single method swept across parameter values is ranked against its reference point
        // (here the explicit baseline at size=10), so the Ratio column shows the scaling factor.
        var small = Row(table, "Sort", 10);
        var medium = Row(table, "Sort", 100);
        var large = Row(table, "Sort", 1000);

        Assert.True(small.IsBaseline);
        Assert.Equal(1.0, small.Ratio, 6);
        Assert.False(medium.IsBaseline);
        Assert.Equal(2.0, medium.Ratio, 6);
        Assert.False(large.IsBaseline);
        Assert.Equal(3.0, large.Ratio, 6);
    }

    [Fact]
    public void BuildPerClass_SingleMethodSweep_WithoutExplicitBaseline_RanksAgainstFastestPoint()
    {
        var results = new[]
        {
            R("Sweep", "Hash", 300, [P("size", 1000)]),
            R("Sweep", "Hash", 100, [P("size", 10)]),
            R("Sweep", "Hash", 200, [P("size", 100)]),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        // No explicit baseline and no within-group comparison: the fastest point becomes the
        // reference (1.00x) and the remaining points report their scaling ratio, regardless of
        // the order results were supplied in.
        var fastest = Row(table, "Hash", 10);
        var mid = Row(table, "Hash", 100);
        var slowest = Row(table, "Hash", 1000);

        Assert.True(fastest.IsBaseline);
        Assert.Equal(1.0, fastest.Ratio, 6);
        Assert.False(mid.IsBaseline);
        Assert.Equal(2.0, mid.Ratio, 6);
        Assert.False(slowest.IsBaseline);
        Assert.Equal(3.0, slowest.Ratio, 6);

        // Significance stays unreported because the engine does not test different workloads
        // across parameter values against one another.
        Assert.All(table.Rows, r => Assert.Equal("", r.SignificanceLabel));
    }

    [Fact]
    public void BuildPerClass_CollectsParameterNames_InFirstAppearanceOrder()
    {
        var results = new[]
        {
            R("C", "First", 100, [P("size", 1)]),
            R("C", "Second", 200, [P("size", 1), P("order", 2)]),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        Assert.Equal(["size", "order"], table.ParameterNames);
    }

    [Fact]
    public void BuildPerClass_ComputesBaselineAndRatio_PerParameterGroup()
    {
        var results = new[]
        {
            R("Search", "Binary", 100, [P("size", 10)], true),
            R("Search", "Linear", 120, [P("size", 10)], significance: SignificanceVerdict.Significant),
            R("Search", "Binary", 250, [P("size", 100)], true),
            R("Search", "Linear", 300, [P("size", 100)], significance: SignificanceVerdict.Significant),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        var binarySmall = Row(table, "Binary", 10);
        var linearSmall = Row(table, "Linear", 10);
        var binaryLarge = Row(table, "Binary", 100);
        var linearLarge = Row(table, "Linear", 100);

        Assert.True(binarySmall.IsBaseline);
        Assert.Equal(1.0, binarySmall.Ratio, 6);
        Assert.Equal(1.2, linearSmall.Ratio, 6);

        Assert.True(binaryLarge.IsBaseline);
        Assert.Equal(1.0, binaryLarge.Ratio, 6);
        Assert.Equal(1.2, linearLarge.Ratio, 6);

        // Significance applies within the multi-benchmark group, not to the baseline.
        Assert.Equal("✓", linearSmall.SignificanceLabel);
        Assert.Equal("", binarySmall.SignificanceLabel);
    }

    [Fact]
    public void BuildPerClass_MultiRuntime_UsesRuntimeScopedBaselineForRatios()
    {
        var results = new[]
        {
            R("Compare", "Base", 100, baseline: true, runtimeMoniker: "net8.0"),
            R("Compare", "Alt", 150, runtimeMoniker: "net8.0"),
            R("Compare", "Base", 50, baseline: true, runtimeMoniker: "net9.0"),
            R("Compare", "Alt", 125, runtimeMoniker: "net9.0"),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        var baseNet8 = table.Rows.Single(r => r.BaseName == "Base" && r.RuntimeMoniker == "net8.0");
        var altNet8 = table.Rows.Single(r => r.BaseName == "Alt" && r.RuntimeMoniker == "net8.0");
        var baseNet9 = table.Rows.Single(r => r.BaseName == "Base" && r.RuntimeMoniker == "net9.0");
        var altNet9 = table.Rows.Single(r => r.BaseName == "Alt" && r.RuntimeMoniker == "net9.0");

        Assert.True(baseNet8.IsBaseline);
        Assert.Equal(1.0, baseNet8.Ratio, 6);
        Assert.Equal(1.5, altNet8.Ratio, 6);

        Assert.True(baseNet9.IsBaseline);
        Assert.Equal(1.0, baseNet9.Ratio, 6);
        Assert.Equal(2.5, altNet9.Ratio, 6);
    }

    [Fact]
    public void BuildPerClass_Parameterised_MultiRuntime_UsesRuntimeScopedBaselineForRatios()
    {
        var results = new[]
        {
            R("Search", "Binary", 100, [P("size", 10)], true, runtimeMoniker: "net8.0"),
            R("Search", "Linear", 200, [P("size", 10)], significance: SignificanceVerdict.Significant, runtimeMoniker: "net8.0"),
            R("Search", "Binary", 40, [P("size", 10)], true, runtimeMoniker: "net9.0"),
            R("Search", "Linear", 120, [P("size", 10)], significance: SignificanceVerdict.Significant, runtimeMoniker: "net9.0"),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        var binaryNet8 = table.Rows.Single(r => r.BaseName == "Binary" && r.RuntimeMoniker == "net8.0");
        var linearNet8 = table.Rows.Single(r => r.BaseName == "Linear" && r.RuntimeMoniker == "net8.0");
        var binaryNet9 = table.Rows.Single(r => r.BaseName == "Binary" && r.RuntimeMoniker == "net9.0");
        var linearNet9 = table.Rows.Single(r => r.BaseName == "Linear" && r.RuntimeMoniker == "net9.0");

        Assert.Equal(1.0, binaryNet8.Ratio, 6);
        Assert.Equal(2.0, linearNet8.Ratio, 6);

        Assert.Equal(1.0, binaryNet9.Ratio, 6);
        Assert.Equal(3.0, linearNet9.Ratio, 6);
    }

    [Fact]
    public void BuildPerClass_OrdersGroupsByFirstAppearance_ThenMedianWithinGroup()
    {
        var results = new[]
        {
            R("Search", "Linear", 120, [P("size", 10)]),
            R("Search", "Linear", 300, [P("size", 100)]),
            R("Search", "Binary", 100, [P("size", 10)], true),
            R("Search", "Binary", 250, [P("size", 100)], true),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        var ordering = table.Rows
            .Select(r => (r.BaseName, Size: (int)r.ParameterSet.Single(p => p.Name == "size").Value!))
            .ToArray();

        Assert.Equal(
            [("Binary", 10), ("Linear", 10), ("Binary", 100), ("Linear", 100)],
            ordering);
    }

    [Fact]
    public void BuildPerClass_MixedPlainAndParameterised_PlainRowHasEmptyParameterSet()
    {
        var results = new[]
        {
            R("Mix", "Constant", 100, []),
            R("Mix", "Variable", 200, [P("count", 10)]),
            R("Mix", "Variable", 400, [P("count", 100)]),
        };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));

        Assert.Equal(["count"], table.ParameterNames);

        var plain = Assert.Single(table.Rows, r => r.BaseName == "Constant");
        Assert.Empty(plain.ParameterSet);

        Assert.All(
            table.Rows.Where(r => r.BaseName == "Variable"),
            r => Assert.Single(r.ParameterSet, p => p.Name == "count"));
    }

    [Fact]
    public void BuildPerClass_Row_StripsParameterSuffixIntoBaseName_AndCarriesParameterSet()
    {
        var results = new[] { R("C", "Sort", 100, [P("size", 10)]) };

        var table = Assert.Single(BenchmarkTable.BuildPerClass(results));
        var row = Assert.Single(table.Rows);

        Assert.Equal("Sort(size=10)", row.Name);
        Assert.Equal("Sort", row.BaseName);
        var parameter = Assert.Single(row.ParameterSet);
        Assert.Equal("size", parameter.Name);
        Assert.Equal(10, parameter.Value);
    }

    private static BenchmarkRow Row(BenchmarkTable table, string baseName, int size)
        => table.Rows.Single(r =>
            r.BaseName == baseName
            && r.ParameterSet.Any(p => p.Name == "size" && (int)p.Value! == size));

    private static BenchmarkParameter P(string name, object? value) => new(name, value);

    private static BenchmarkResult R(
        string className,
        string baseName,
        double median,
        BenchmarkParameter[]? parameters = null,
        bool baseline = false,
        SignificanceVerdict significance = SignificanceVerdict.NotTested,
        string runtimeMoniker = "")
    {
        parameters ??= [];

        return new BenchmarkResult
        {
            Name = BenchmarkParameter.FormatDisplayName(baseName, parameters),
            ClassName = className,
            ParameterSet = parameters,
            Mean = median,
            Median = median,
            Percentiles = [],
            Min = median,
            Max = median,
            StandardDeviation = 1,
            MeasuredIterations = 10,
            WarmupIterations = 5,
            IsBaseline = baseline,
            SignificanceVerdict = significance,
            RuntimeMoniker = runtimeMoniker,
            RunAtUtc = DateTimeOffset.UtcNow,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 0,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };
    }
}
