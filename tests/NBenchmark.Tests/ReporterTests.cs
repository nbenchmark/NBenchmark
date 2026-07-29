using System.Text.Json;
using NBenchmark.Reporters;
using NBenchmark.Stats;
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
    public async Task JsonReporter_Includes_RawSamples_By_Default()
    {
        var tempDir = MakeSubDir("nb-json-samples");

        try
        {
            var reporter = new JsonReporter(tempDir);
            var result = MakeResult("alpha", 100) with { RawSamples = [777.0, 888.0, 999.0] };

            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmarks-*.json");
            Assert.Single(files);

            var samples = await ReadRawSamplesAsync(files[0]);
            Assert.Equal([777.0, 888.0, 999.0], samples);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task JsonReporter_IncludeSamples_False_Omits_RawSamples()
    {
        var tempDir = MakeSubDir("nb-json-no-samples");

        try
        {
            var reporter = new JsonReporter(tempDir) { IncludeSamples = false };
            var result = MakeResult("alpha", 100) with { RawSamples = [777.0, 888.0, 999.0] };

            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmarks-*.json");
            Assert.Single(files);

            // The property is still emitted, but empty - the schema stays stable so a consumer
            // does not have to distinguish "no samples" from "older file without the field".
            Assert.Empty(await ReadRawSamplesAsync(files[0]));
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
    public async Task MarkdownReporter_TimingDetail_Does_Not_Render_Empty_Tail_Columns_When_No_Percentiles()
    {
        var tempDir = MakeSubDir("nb-md-no-tail");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md", ReportDetail.Standard);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.md");
            var content = await File.ReadAllTextAsync(filePath);

            Assert.Contains("### Precision & Tail Latency", content);
            Assert.Contains("| Benchmark | Error (±CI) | StdDev | CV |", content);
            Assert.DoesNotContain("| Benchmark | Error (±CI) | StdDev | CV |  |", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_TimingDetail_Includes_Runtime_Column_When_MultiRuntime()
    {
        var tempDir = MakeSubDir("nb-md-tail-runtime");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md", ReportDetail.Standard);
            var net8 = MakeResult("alpha", 100, "net8.0", [new PercentileEntry(0.95, 110)]);
            var net9 = MakeResult("alpha", 80, "net9.0", [new PercentileEntry(0.95, 95)]);

            await reporter.ReportAsync([net8, net9]);

            var filePath = Path.Combine(tempDir, "out.md");
            var content = await File.ReadAllTextAsync(filePath);

            Assert.Contains("| Benchmark | Runtime | Error (±CI) | StdDev | CV | P95 |", content);
            Assert.Contains("| alpha | net8.0 |", content);
            Assert.Contains("| alpha | net9.0 |", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Diagnostics_Leaves_Blanks_For_Uncollected_Metrics()
    {
        var tempDir = MakeSubDir("nb-md-diagnostics-blanks");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md", ReportDetail.Standard);

            var gcOnly = MakeResult("gc", 100) with
            {
                Diagnostics = new DiagnosticsResult
                {
                    Gen0Collections = 1,
                    Gen1Collections = 0,
                    Gen2Collections = 0,
                    Mode = DiagnosticsMode.Gc,
                },
            };

            var cpuOnly = MakeResult("cpu", 100) with
            {
                Diagnostics = new DiagnosticsResult
                {
                    CpuWallRatio = 0.42,
                    Mode = DiagnosticsMode.CpuTime,
                },
            };

            await reporter.ReportAsync([gcOnly, cpuOnly]);

            var filePath = Path.Combine(tempDir, "out.md");
            var content = await File.ReadAllTextAsync(filePath);

            Assert.Contains("| Benchmark | Gen0 | Gen1 | Gen2 | CPU% |", content);
            Assert.Contains("| gc | 1 | 0 | 0 |  |", content);
            Assert.Contains("| cpu |  |  |  | 42% |", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Interpretation_Uses_RuntimeScoped_Omnibus_Note_For_MultiRuntime()
    {
        var tempDir = MakeSubDir("nb-md-omnibus-runtime");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md", ReportDetail.Standard);
            var net8 = MakeResult("alpha", 100, "net8.0");
            var net9 = MakeResult("alpha", 80, "net9.0");

            await reporter.ReportAsync([net8, net9]);

            var filePath = Path.Combine(tempDir, "out.md");
            var content = await File.ReadAllTextAsync(filePath);

            Assert.Contains("**Omnibus**: runtime-scoped in multi-runtime runs; combined summary omitted.", content);
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

    [Theory]
    [InlineData(ReportDetail.Standard)]
    [InlineData(ReportDetail.Advanced)]
    public async Task MarkdownReporter_StandardAndAdvanced_HeaderHasWellFormedColumns(ReportDetail detail)
    {
        var tempDir = MakeSubDir($"nb-md-header-{detail}");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md", detail);
            var first = MakeResult("alpha", 100);
            var second = MakeResult("beta", 150);

            await reporter.ReportAsync([first, second]);

            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, "out.md"));

            Assert.Contains(
                "| | Benchmark | Median | Mean | Ops/s | Ratio | Scale | Sig | Magnitude | Alloc/op |",
                content);

            Assert.Contains("|:---:|---|---:|---:|---:|:---:|---|---:|---:|---:|", content);
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
            var reporter = new CsvReporter(tempDir, "out.csv", ReportDetail.Standard);
            var result = MakeResult("alpha", 100);

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.csv");
            Assert.True(File.Exists(filePath));
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Name,Median,Mean,OpsPerSecond", content);
            Assert.Contains("EffectMetric,EffectValue,Magnitude", content);
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
    public async Task CsvReporter_Writes_Generic_Effect_Columns()
    {
        var tempDir = MakeSubDir("nb-csv-effect");

        try
        {
            var reporter = new CsvReporter(tempDir, "out.csv", ReportDetail.Standard);

            var result = MakeResult("alpha", 100) with
            {
                Effect = new EffectSize(
                    "median-ratio",
                    0.42,
                    "small",
                    EffectDirection.CandidateHigher,
                    0.42),
            };

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.csv");
            var lines = await File.ReadAllLinesAsync(filePath);

            Assert.Contains("EffectMetric,EffectValue,Magnitude", lines[0]);
            Assert.Contains("\"median-ratio\",0.4200,\"small\"", lines[1]);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Advanced_Quotes_DiagnosticsMode_For_Combined_Flags()
    {
        var tempDir = MakeSubDir("nb-csv-diag-mode");

        try
        {
            var reporter = new CsvReporter(tempDir, "out.csv", ReportDetail.Advanced);

            var result = MakeResult("alpha", 100) with
            {
                Diagnostics = new DiagnosticsResult
                {
                    Mode = DiagnosticsMode.GcHeapInfo | DiagnosticsMode.Exceptions,
                },
            };

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.csv");
            var line = (await File.ReadAllLinesAsync(filePath))[1];

            Assert.Contains("\"GcHeapInfo, Exceptions\"", line);
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

    /// <summary>
    ///     Reads the single result's raw samples out of a reporter's JSON file.
    ///     <para>
    ///         Parsed rather than substring-matched. These assertions used to search the whole
    ///         document for "777", which also matches the <c>generatedAt</c> timestamp - a run at
    ///         <c>…50.967779+00:00</c> fails the negative test, and, far worse, a timestamp
    ///         collision could make the positive test pass while samples were being dropped.
    ///     </para>
    /// </summary>
    private static async Task<double[]> ReadRawSamplesAsync(string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        var result = document.RootElement.GetProperty("results").EnumerateArray().Single();

        // Asserted rather than assumed: the property must exist even when it is empty.
        Assert.True(
            result.TryGetProperty("rawSamples", out var rawSamples),
            "the result carried no 'rawSamples' property at all");

        return [.. rawSamples.EnumerateArray().Select(e => e.GetDouble())];
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
    public async Task JsonReporter_Emits_Categories()
    {
        var tempDir = MakeSubDir("nb-json-categories");

        try
        {
            var reporter = new JsonReporter(tempDir);
            var result = MakeResult("alpha", 100) with { Categories = ["String", "Fast"] };

            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "benchmarks-*.json");
            var content = await File.ReadAllTextAsync(files[0]);
            Assert.Contains("\"categories\":", content);
            Assert.Contains("\"String\"", content);
            Assert.Contains("\"Fast\"", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task CsvReporter_Advanced_Includes_Categories_Column()
    {
        var tempDir = MakeSubDir("nb-csv-categories");

        try
        {
            var reporter = new CsvReporter(tempDir, detail: ReportDetail.Advanced);
            var result = MakeResult("alpha", 100) with { Categories = ["String", "Fast"] };

            await reporter.ReportAsync([result]);

            var files = Directory.GetFiles(tempDir, "*.csv");
            var lines = await File.ReadAllLinesAsync(files[0]);
            Assert.Contains("Categories", lines[0]);
            Assert.Contains("String; Fast", lines[1]);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Advanced_Includes_Categories_Column_When_Present()
    {
        var tempDir = MakeSubDir("nb-md-categories");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md", ReportDetail.Advanced);
            var result = MakeResult("alpha", 100) with { Categories = ["String", "Fast"] };

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.md");
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Categories", content);
            Assert.Contains("String, Fast", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task MarkdownReporter_Simple_Does_Not_Show_Categories_Column()
    {
        var tempDir = MakeSubDir("nb-md-simple-no-categories");

        try
        {
            var reporter = new MarkdownReporter(tempDir, "out.md");
            var result = MakeResult("alpha", 100) with { Categories = ["String"] };

            await reporter.ReportAsync([result]);

            var filePath = Path.Combine(tempDir, "out.md");
            var content = await File.ReadAllTextAsync(filePath);
            Assert.DoesNotContain("Categories", content);
        }
        finally
        {
            Cleanup(tempDir);
        }
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

    private static BenchmarkResult MakeResult(
        string name,
        double median,
        string runtimeMoniker = "",
        IReadOnlyList<PercentileEntry>? percentiles = null)
    {
        percentiles ??= [];

        return new BenchmarkResult
        {
            Name = name,
            Mean = median,
            Median = median,
            Percentiles = percentiles,
            Min = median * 0.8,
            Max = median * 1.3,
            StandardDeviation = median * 0.05,
            MeanAllocatedBytes = 64,
            RuntimeMoniker = runtimeMoniker,
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
