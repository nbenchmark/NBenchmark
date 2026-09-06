using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class ReporterRegistryTests : IDisposable
{
    public ReporterRegistryTests()
    {
        ReporterRegistry.Reset();
    }

    public void Dispose() => ReporterRegistry.Reset();

    [Fact]
    public void TryCreate_Json_With_Dir_Uses_Dir()
    {
        var dir = MakeSubDir("nb-reg");

        try
        {
            var ok = ReporterRegistry.TryCreate("json", dir, out var reporter);

            Assert.True(ok);
            Assert.IsType<JsonReporter>(reporter);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void TryCreate_Markdown_With_Dir_Passes_Dir()
    {
        var dir = MakeSubDir("nb-reg");

        try
        {
            var ok = ReporterRegistry.TryCreate("markdown", dir, out var reporter);

            Assert.True(ok);
            Assert.IsType<MarkdownReporter>(reporter);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void TryCreate_Csv_With_Dir_Passes_Dir()
    {
        var dir = MakeSubDir("nb-reg");

        try
        {
            var ok = ReporterRegistry.TryCreate("csv", dir, out var reporter);

            Assert.True(ok);
            Assert.IsType<CsvReporter>(reporter);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void TryCreate_Without_Dir_Defaults_To_Cwd()
    {
        var ok = ReporterRegistry.TryCreate("json", null, out var reporter);

        Assert.True(ok);
        Assert.IsType<JsonReporter>(reporter);
    }

    [Fact]
    public void TryCreate_Markdown_Without_Dir_Defaults_To_Cwd()
    {
        var ok = ReporterRegistry.TryCreate("markdown", null, out var reporter);

        Assert.True(ok);
        Assert.IsType<MarkdownReporter>(reporter);
    }

    [Fact]
    public void TryCreate_Unknown_Name_Returns_False()
    {
        var ok = ReporterRegistry.TryCreate("bogus", null, out var reporter);

        Assert.False(ok);
        Assert.Null(reporter);
    }

    [Fact]
    public void TryCreate_Is_Case_Insensitive()
    {
        Assert.True(ReporterRegistry.TryCreate("JSON", null, out _));
        Assert.True(ReporterRegistry.TryCreate("Json", null, out _));
        Assert.True(ReporterRegistry.TryCreate("json", null, out _));
    }

    [Fact]
    public void TryCreate_Null_Name_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReporterRegistry.TryCreate(null!, null, out _));
    }

    [Fact]
    public void Available_Seed_Contains_Json_Markdown_Csv()
    {
        var names = ReporterRegistry.Available.Select(r => r.Name).ToList();
        Assert.Contains("json", names);
        Assert.Contains("markdown", names);
        Assert.Contains("csv", names);
    }

    [Fact]
    public void Register_Adds_Reporter_To_Available_And_TryCreate()
    {
        ReporterRegistry.Register("fake", "Fake reporter for tests", _ => new StubReporter());

        Assert.Contains(ReporterRegistry.Available, r => r.Name == "fake");
        Assert.True(ReporterRegistry.TryCreate("fake", null, out var reporter));
        Assert.IsType<StubReporter>(reporter);
    }

    [Fact]
    public void Register_Throws_On_Duplicate_Name()
    {
        Assert.Throws<BenchmarkConfigurationException>(() =>
            ReporterRegistry.Register("json", "Duplicate", _ => new JsonReporter()));
    }

    [Fact]
    public void Register_Is_Case_Insensitive_For_Duplicate_Check()
    {
        Assert.Throws<BenchmarkConfigurationException>(() =>
            ReporterRegistry.Register("JSON", "Duplicate uppercase", _ => new JsonReporter()));
    }

    [Fact]
    public void Register_Respects_Custom_Factory_For_OutputDir()
    {
        string? captured = null;

        ReporterRegistry.Register("capturing", "Captures outputDir", dir =>
        {
            captured = dir;
            return new StubReporter();
        });

        var dir = MakeSubDir("nb-reg-cap");

        try
        {
            ReporterRegistry.TryCreate("capturing", dir, out _);

            Assert.Equal(dir, captured);
        }
        finally
        {
            Cleanup(dir);
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
    public void Reset_Removes_Post_Reset_Registrations_While_Preserving_Initial_State()
    {
        ReporterRegistry.Register("temp", "Temporary", _ => new StubReporter());
        Assert.Contains(ReporterRegistry.Available, r => r.Name == "temp");

        ReporterRegistry.Reset();

        Assert.DoesNotContain(ReporterRegistry.Available, r => r.Name == "temp");
        Assert.Contains(ReporterRegistry.Available, r => r.Name == "json");
        Assert.Contains(ReporterRegistry.Available, r => r.Name == "markdown");
        Assert.Contains(ReporterRegistry.Available, r => r.Name == "csv");
    }

    private sealed class StubReporter : IReporter
    {
        public string Name => "stub";
        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, ReportContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
