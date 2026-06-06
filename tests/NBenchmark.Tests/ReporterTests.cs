using NBenchmark;
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
        finally { Cleanup(tempDir); }
    }

    [Fact]
    public async Task MarkdownReporter_Writes_Table_Containing_Results()
    {
        var tempPath = Path.Combine(MakeSubDir("nb-md"), "out.md");
        try
        {
            var reporter = new MarkdownReporter(tempPath);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            Assert.True(File.Exists(tempPath));
            var content = await File.ReadAllTextAsync(tempPath);
            Assert.Contains("alpha", content);
            Assert.Contains("| Benchmark | Median |", content);
        }
        finally { Cleanup(Path.GetDirectoryName(tempPath)!); }
    }

    [Fact]
    public async Task CsvReporter_Writes_Header_And_Row()
    {
        var tempPath = Path.Combine(MakeSubDir("nb-csv"), "out.csv");
        try
        {
            var reporter = new CsvReporter(tempPath);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            Assert.True(File.Exists(tempPath));
            var content = await File.ReadAllTextAsync(tempPath);
            Assert.Contains("Name,Median,Mean", content);
            Assert.Contains("\"alpha\"", content);
        }
        finally { Cleanup(Path.GetDirectoryName(tempPath)!); }
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
            Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void PathValidation_Rejects_Path_Traversal()
    {
        Assert.Throws<ArgumentException>(() => new JsonReporter("../escaped"));
    }

    [Fact]
    public void PathValidation_Accepts_Subdirectory()
    {
        var sub = Path.Combine(Directory.GetCurrentDirectory(), "sub-out");
        var reporter = new JsonReporter(sub);
        Assert.NotNull(reporter);
    }

    private static BenchmarkResult MakeResult(string name, double median) => new()
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
    };
}
