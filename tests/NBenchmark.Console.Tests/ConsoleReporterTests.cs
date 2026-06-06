using NBenchmark;
using NBenchmark.Console;
using Xunit;

namespace NBenchmark.Console.Tests;

public class ConsoleReporterTests
{
    [Fact]
    public async Task ConsoleReporter_ReportAsync_Does_Not_Throw_For_Empty_Results()
    {
        await CaptureConsoleOutputAsync(async () =>
        {
            var reporter = new ConsoleReporter();
            await reporter.ReportAsync([]);
        });
    }

    [Fact]
    public async Task ConsoleReporter_ReportAsync_Does_Not_Throw_For_Successful_Results()
    {
        await CaptureConsoleOutputAsync(async () =>
        {
            var reporter = new ConsoleReporter();
            var result = MakeResult("test", 100);
            await reporter.ReportAsync([result]);
        });
    }

    [Fact]
    public async Task ConsoleReporter_ReportAsync_Does_Not_Throw_For_Errored_Results()
    {
        await CaptureConsoleOutputAsync(async () =>
        {
            var reporter = new ConsoleReporter();
            var result = BenchmarkResult.CreateErrored("broken", "something went wrong");
            await reporter.ReportAsync([result]);
        });
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

    private static async Task CaptureConsoleOutputAsync(Func<Task> action)
    {
        var sw = new System.IO.StringWriter();
        var original = System.Console.Out;
        System.Console.SetOut(sw);
        try { await action(); }
        finally { System.Console.SetOut(original); }
    }
}