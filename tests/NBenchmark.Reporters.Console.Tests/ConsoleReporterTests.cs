using Spectre.Console;
using Xunit;

namespace NBenchmark.Reporters.Console.Tests;

public class ConsoleReporterTests
{
    [Fact]
    public async Task ConsoleReporter_ReportAsync_Does_Not_Throw_For_Empty_Results()
    {
        await CaptureConsoleOutputAsyncVoid(async () =>
        {
            var reporter = new ConsoleReporter();
            await reporter.ReportAsync([]);
        });
    }

    [Fact]
    public async Task ConsoleReporter_ReportAsync_Does_Not_Throw_For_Successful_Results()
    {
        await CaptureConsoleOutputAsyncVoid(async () =>
        {
            var reporter = new ConsoleReporter();
            var result = MakeResult("test", 100);
            await reporter.ReportAsync([result]);
        });
    }

    [Fact]
    public async Task ConsoleReporter_ReportAsync_Does_Not_Throw_For_Errored_Results()
    {
        await CaptureConsoleOutputAsyncVoid(async () =>
        {
            var reporter = new ConsoleReporter();

            var result = new BenchmarkResult
            {
                Name = "broken",
                Mean = 0,
                Median = 0,
                Percentiles = [],
                Min = 0,
                Max = 0,
                StandardDeviation = 0,
                Errored = true,
                ErrorMessage = "something went wrong",
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

            await reporter.ReportAsync([result]);
        });
    }

    [Fact]
    public async Task ConsoleReporter_ReportAsync_Errored_First_Row_Renders_Tail_Columns_From_Healthy_Rows()
    {
        await CaptureConsoleOutputAsyncVoid(async () =>
        {
            var reporter = new ConsoleReporter();

            var errored = new BenchmarkResult
            {
                Name = "broken",
                Mean = 0,
                Median = 0,
                Percentiles = [],
                Min = 0,
                Max = 0,
                StandardDeviation = 0,
                Errored = true,
                ErrorMessage = "something went wrong",
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

            var healthy = MakeResult("healthy", 100) with
            {
                Percentiles = [new PercentileEntry(0.95, 110), new PercentileEntry(0.99, 120)],
            };

            await reporter.ReportAsync([errored, healthy]);
        });
    }

    [Fact]
    public async Task ConsoleReporter_TimingDetail_Includes_Runtime_Column_When_MultiRuntime()
    {
        var reporter = new ConsoleReporter(ReportDetail.Standard);

        var net8 = MakeResult("alpha", 100) with
        {
            RuntimeMoniker = "net8.0",
            Percentiles = [new PercentileEntry(0.95, 110)],
        };

        var net9 = MakeResult("alpha", 80) with
        {
            RuntimeMoniker = "net9.0",
            Percentiles = [new PercentileEntry(0.95, 95)],
        };

        AnsiConsole.Record();

        await reporter.ReportAsync([net8, net9]);

        var output = AnsiConsole.ExportText();

        var tailIndex = output.IndexOf("Precision & Tail Latency", StringComparison.Ordinal);
        Assert.True(tailIndex >= 0);

        var tailSection = output[tailIndex..];
        Assert.Contains("Runtime", tailSection);
        Assert.Contains("net8.0", tailSection);
        Assert.Contains("net9.0", tailSection);
        Assert.Contains("runtime-scoped in multi-runtime runs; combined summary omitted.", output);
    }

    [Fact]
    public void IReporter_Name_Property_Returns_Console() => Assert.Equal("console", new ConsoleReporter().Name);

    [Fact]
    public void Module_Initializer_Registers_Console_With_Global_Registry()
    {
        var ok = ReporterRegistry.TryCreate("console", null, ReportDetail.Simple, out var reporter);

        Assert.True(ok);
        Assert.IsType<ConsoleReporter>(reporter);
    }

    [Fact]
    public async Task ConsoleReporter_Advanced_Accepts_Results_With_Categories()
    {
        var reporter = new ConsoleReporter(ReportDetail.Advanced);
        var result = MakeResult("tagged", 100) with { Categories = ["String", "Fast"] };

        // Does not throw; categories are surfaced only in advanced detail.
        await reporter.ReportAsync([result]);
    }

    [Fact]
    public async Task ConsoleReporter_Simple_Accepts_Results_With_Categories()
    {
        var reporter = new ConsoleReporter();
        var result = MakeResult("tagged", 100) with { Categories = ["String"] };

        // Does not throw; categories are hidden in simple detail to keep the table narrow.
        await reporter.ReportAsync([result]);
    }

    private static BenchmarkResult MakeResult(string name, double median)
    {
        return new BenchmarkResult
        {
            Name = name,
            Mean = median,
            Median = median,
            Percentiles = [],
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

    private static async Task<string> CaptureConsoleOutputAsync(Func<Task> action)
    {
        var sw = new StringWriter();
        var original = System.Console.Out;
        System.Console.SetOut(sw);

        try
        {
            await action();
        }
        finally
        {
            System.Console.SetOut(original);
        }

        return sw.ToString();
    }

    private static async Task CaptureConsoleOutputAsyncVoid(Func<Task> action)
    {
        var sw = new StringWriter();
        var original = System.Console.Out;
        System.Console.SetOut(sw);

        try
        {
            await action();
        }
        finally
        {
            System.Console.SetOut(original);
        }
    }
}
