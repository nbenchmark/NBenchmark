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
                Name = "A", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
            },
        };

        var table = BenchmarkTable.Build(results);

        var row = Assert.Single(table.Rows);
        Assert.Equal("A", row.Name);
        Assert.Equal(1.0, row.Ratio);
        Assert.False(row.Errored);
    }

    [Fact]
    public void Build_MultipleBenchmarks_FindsExplicitBaseline()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Fast", Mean = 50, Median = 50, P95 = 50, P99 = 50, Min = 50, Max = 50,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Slow", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow, IsBaseline = true,
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
                Name = "Fast", Mean = 50, Median = 50, P95 = 50, P99 = 50, Min = 50, Max = 50,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Slow", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
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
                Name = "A", Mean = 0, Median = 0, P95 = 0, P99 = 0, Min = 0, Max = 0,
                StandardDeviation = 0, Errored = true, ErrorMessage = "fail",
                MeasuredIterations = 0, WarmupIterations = 0, RunAt = DateTimeOffset.UtcNow,
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
                Name = "Base", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "TwoX", Mean = 200, Median = 200, P95 = 200, P99 = 200, Min = 200, Max = 200,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Half", Mean = 50, Median = 50, P95 = 50, P99 = 50, Min = 50, Max = 50,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
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
                Name = "Base", Mean = 0, Median = 0, P95 = 0, P99 = 0, Min = 0, Max = 0,
                StandardDeviation = 0, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Other", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
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
                Name = "Base", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Fail", Mean = 0, Median = 0, P95 = 0, P99 = 0, Min = 0, Max = 0,
                StandardDeviation = 0, Errored = true, ErrorMessage = "crash",
                MeasuredIterations = 0, WarmupIterations = 0,
                RunAt = DateTimeOffset.UtcNow,
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
                Name = "Base", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                IsSignificant = false,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Sig", Mean = 200, Median = 200, P95 = 200, P99 = 200, Min = 200, Max = 200,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                IsSignificant = true,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "NotSig", Mean = 150, Median = 150, P95 = 150, P99 = 150, Min = 150, Max = 150,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                IsSignificant = false,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "NoSig", Mean = 120, Median = 120, P95 = 120, P99 = 120, Min = 120, Max = 120,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                IsSignificant = null,
                RunAt = DateTimeOffset.UtcNow,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal("", table.Rows.First(r => r.Name == "Base").SignificanceLabel);
        Assert.Equal("✓", table.Rows.First(r => r.Name == "Sig").SignificanceLabel);
        Assert.Equal("~", table.Rows.First(r => r.Name == "NotSig").SignificanceLabel);
        Assert.Equal("", table.Rows.First(r => r.Name == "NoSig").SignificanceLabel);
    }

    [Fact]
    public void Build_SingleBenchmark_NoSignificanceLabel()
    {
        var results = new[]
        {
            new BenchmarkResult
            {
                Name = "Only", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5, IsBaseline = true,
                IsSignificant = true,
                RunAt = DateTimeOffset.UtcNow,
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
                Name = "A", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = now, ConfidenceLevel = 0.99,
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
                Name = "A", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                TotalDuration = TimeSpan.FromSeconds(1),
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "B", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                TotalDuration = TimeSpan.FromSeconds(2),
                RunAt = DateTimeOffset.UtcNow,
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
                Name = "Slowest", Mean = 300, Median = 300, P95 = 300, P99 = 300, Min = 300, Max = 300,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Fastest", Mean = 100, Median = 100, P95 = 100, P99 = 100, Min = 100, Max = 100,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
            },
            new BenchmarkResult
            {
                Name = "Middle", Mean = 200, Median = 200, P95 = 200, P99 = 200, Min = 200, Max = 200,
                StandardDeviation = 1, MeasuredIterations = 10, WarmupIterations = 5,
                RunAt = DateTimeOffset.UtcNow,
            },
        };

        var table = BenchmarkTable.Build(results);

        Assert.Equal(["Fastest", "Middle", "Slowest"], table.Rows.Select(r => r.Name));
    }
}
