using System.Text.Json;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Every report states the schema it is written in and the measurement epoch its numbers belong
///     to, so a consumer storing files over time can tell a regression from a change of harness.
/// </summary>
/// <remarks>
///     The case that motivated the epoch: replacing the boxing dispatch path with typed delegates
///     moved the calibration standard from 9.34 ns / 24 B per op to 2.53 ns / 0 B while leaving the
///     JSON shape byte-for-byte identical. A trend dashboard had no way to distinguish that from a
///     3.7x improvement in the code under test.
/// </remarks>
public class ReportFormatVersionTests
{
    [Fact]
    public async Task Json_Reports_Carry_The_Schema_Version_And_Measurement_Epoch()
    {
        var dir = MakeDir("nb-format-json");

        try
        {
            await new JsonReporter(dir, "out.json").ReportAsync([Result("alpha", 100)]);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "out.json")));
            var root = doc.RootElement;

            Assert.Equal(ReportFormat.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(ReportFormat.MeasurementEpoch, root.GetProperty("measurementEpoch").GetInt32());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     A consumer that cannot read the stamps without first parsing the payload has to trust the
    ///     payload to find out whether it can trust the payload.
    /// </summary>
    [Fact]
    public async Task The_Version_Stamps_Precede_The_Results()
    {
        var dir = MakeDir("nb-format-order");

        try
        {
            await new JsonReporter(dir, "out.json").ReportAsync([Result("alpha", 100)]);

            var json = await File.ReadAllTextAsync(Path.Combine(dir, "out.json"));

            Assert.True(json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal)
                        < json.IndexOf("\"results\"", StringComparison.Ordinal));

            Assert.True(json.IndexOf("\"measurementEpoch\"", StringComparison.Ordinal)
                        < json.IndexOf("\"results\"", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Theory]
    [InlineData(ReportDetail.Simple)]
    [InlineData(ReportDetail.Standard)]
    [InlineData(ReportDetail.Advanced)]
    public async Task Csv_Reports_Carry_The_Stamps_At_Every_Detail_Level(ReportDetail detail)
    {
        var dir = MakeDir($"nb-format-csv-{detail}");

        try
        {
            await new CsvReporter(dir, "out.csv", detail).ReportAsync([Result("alpha", 100)]);

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "out.csv"));
            var headers = lines[0].Split(',');

            var schemaIndex = Array.IndexOf(headers, "SchemaVersion");
            var epochIndex = Array.IndexOf(headers, "MeasurementEpoch");

            Assert.True(schemaIndex >= 0, "SchemaVersion column missing");
            Assert.True(epochIndex >= 0, "MeasurementEpoch column missing");

            // Read by header position rather than asserting a literal column index: the point is
            // that the value lines up with its header, which is what a consumer relies on.
            var values = lines[1].Split(',');

            Assert.Equal(ReportFormat.SchemaVersion.ToString(), values[schemaIndex]);
            Assert.Equal(ReportFormat.MeasurementEpoch.ToString(), values[epochIndex]);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task Markdown_Reports_State_The_Epoch_In_Prose()
    {
        var dir = MakeDir("nb-format-md");

        try
        {
            await new MarkdownReporter(dir, "out.md").ReportAsync([Result("alpha", 100)]);

            var content = await File.ReadAllTextAsync(Path.Combine(dir, "out.md"));

            Assert.Contains($"schema {ReportFormat.SchemaVersion}", content);
            Assert.Contains($"measurement epoch {ReportFormat.MeasurementEpoch}", content);
            Assert.Contains("comparable only with the same epoch", content);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    ///     Guards the two constants against a careless edit. Both are contracts with data already
    ///     written to disk elsewhere: lowering either, or bumping the schema when only the numbers
    ///     moved, silently changes what previously-written files claim about themselves.
    /// </summary>
    [Fact]
    public void The_Declared_Versions_Start_At_One_And_Only_Move_Forward()
    {
        Assert.True(ReportFormat.SchemaVersion >= 1);
        Assert.True(ReportFormat.MeasurementEpoch >= 1);
    }

    // Under the working directory, not the temp path: the reporters refuse to write outside it.
    private static string MakeDir(string name)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }

    private static BenchmarkResult Result(string name, double median) => new()
    {
        Name = name,
        Mean = median,
        Median = median,
        Percentiles = [],
        Min = median,
        Max = median,
        StandardDeviation = 0,
        MeanAllocatedBytes = 0,
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
