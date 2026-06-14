using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class ReporterTests
{
    [Fact]
    public async Task JsonReporter_Writes_File_Containing_Results()
    {
        var tempDir = MakeSubDir("nb-json");

        try
        {
            var reporter = new JsonReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmarks-*.json");
            Assert.Single(files);

            var content = await File.ReadAllTextAsync(files[0]);
            Assert.Contains("alpha", content);
            Assert.Contains("\"median\"", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task JsonReporter_Uses_Explicit_FileName()
    {
        var tempDir = MakeSubDir("nb-json-explicit");

        try
        {
            var reporter = new JsonReporter(tempDir, "custom.json");
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "custom.json");
            Assert.True(File.Exists(filePath));

            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("alpha", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task JsonReporter_Creates_Directory()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-json-dir-{Guid.NewGuid():N}");

        try
        {
            Assert.False(Directory.Exists(tempDir));

            var reporter = new JsonReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            Assert.True(Directory.Exists(tempDir));
            Assert.NotEmpty(Directory.GetFiles(tempDir));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Writes_Table_Containing_Results()
    {
        var tempDir = MakeSubDir("nb-md");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md");
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.md");
            Assert.True(File.Exists(filePath));
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("alpha", content);
            Assert.Contains("| Benchmark | Median |", content);
            Assert.Contains("| Sig |", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Advanced_Appends_Details_Without_Breaking_Table()
    {
        var tempDir = MakeSubDir("nb-md-advanced");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md", ReportDetail.Advanced);
            var first = MakeResult("alpha", 100);
            var second = MakeResult("beta", 150);

            await reporter.ReportAsync([first, second]);

            var filePath = Path.Combine(tempDir, "out.md");
            Assert.True(File.Exists(filePath));

            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("| alpha", content);
            Assert.Contains("| beta", content);
            Assert.Contains("### Distribution Details", content);
            Assert.Contains("<summary><strong>alpha</strong></summary>", content);
            Assert.Contains("<summary><strong>beta</strong></summary>", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Auto_Names_File_When_No_FileName()
    {
        var tempDir = MakeSubDir("nb-md-auto");

        try
        {
            var reporter = new MarkdownReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmark-results-*.md");
            Assert.Single(files);

            var content = await File.ReadAllTextAsync(files[0]);
            Assert.Contains("alpha", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Creates_Directory()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-md-dir-{Guid.NewGuid():N}");

        try
        {
            Assert.False(Directory.Exists(tempDir));

            var reporter = new MarkdownReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            Assert.True(Directory.Exists(tempDir));
            Assert.NotEmpty(Directory.GetFiles(tempDir));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Writes_Header_And_Row()
    {
        var tempDir = MakeSubDir("nb-csv");

        try
        {
            var reporter = new CsvReporter(tempDir, "out.csv");
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.csv");
            Assert.True(File.Exists(filePath));
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Name,Median,Mean", content);
            Assert.Contains("\"alpha\"", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Writes_Lowercase_Detail_Value()
    {
        var tempDir = MakeSubDir("nb-csv-detail");

        try
        {
            var reporter = new CsvReporter(tempDir, "out.csv", ReportDetail.Advanced);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.csv");
            var lines = await File.ReadAllLinesAsync(filePath);
            Assert.Contains(",advanced,", lines[1]);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Auto_Names_File_When_No_FileName()
    {
        var tempDir = MakeSubDir("nb-csv-auto");

        try
        {
            var reporter = new CsvReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmark-results-*.csv");
            Assert.Single(files);

            var content = await File.ReadAllTextAsync(files[0]);
            Assert.Contains("alpha", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Creates_Directory()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-csv-dir-{Guid.NewGuid():N}");

        try
        {
            Assert.False(Directory.Exists(tempDir));

            var reporter = new CsvReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            Assert.True(Directory.Exists(tempDir));
            Assert.NotEmpty(Directory.GetFiles(tempDir));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task Counter_Increments_Across_Calls()
    {
        var tempDir = MakeSubDir("nb-counter");

        try
        {
            var reporter = new JsonReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);
            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmarks-*.json");
            Assert.Equal(2, files.Length);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Counter_Increments_Across_Calls()
    {
        var tempDir = MakeSubDir("nb-md-counter");

        try
        {
            var reporter = new MarkdownReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);
            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmark-results-*.md");
            Assert.Equal(2, files.Length);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Counter_Increments_Across_Calls()
    {
        var tempDir = MakeSubDir("nb-csv-counter");

        try
        {
            var reporter = new CsvReporter(tempDir);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);
            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmark-results-*.csv");
            Assert.Equal(2, files.Length);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Uses_Explicit_FileName()
    {
        var tempDir = MakeSubDir("nb-md-explicit");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "custom.md");
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "custom.md");
            Assert.True(File.Exists(filePath));

            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("alpha", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Uses_Explicit_FileName()
    {
        var tempDir = MakeSubDir("nb-csv-explicit");

        try
        {
            var reporter = new CsvReporter(tempDir, "custom.csv");
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "custom.csv");
            Assert.True(File.Exists(filePath));

            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("alpha", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    private static string MakeSubDir(string name)
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

    [Fact]
    public void PathValidation_Rejects_Path_Traversal() => Assert.Throws<ArgumentException>(() => new JsonReporter("../escaped"));

    [Fact]
    public void PathValidation_Accepts_Subdirectory()
    {
        var sub = Path.Combine(Directory.GetCurrentDirectory(), "sub-out");
        var reporter = new JsonReporter(sub);
        Assert.NotNull(reporter);
    }

    [Fact]
    public void IReporter_Name_Property_Returns_Canonical_Name_For_Seed_Reporters()
    {
        Assert.Equal("json", new JsonReporter().Name);
        Assert.Equal("markdown", new MarkdownReporter().Name);
        Assert.Equal("csv", new CsvReporter().Name);
    }

    private static BenchmarkResult MakeResult(string name, double median)
    {
        return new BenchmarkResult
        {
            Name = name,
            Mean = median,
            Median = median,
            P95 = median * 1.1,
            P99 = median * 1.2,
            Min = median * 0.8,
            Max = median * 1.3,
            StandardDeviation = median * 0.05,
            MeanAllocatedBytes = 64,
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
