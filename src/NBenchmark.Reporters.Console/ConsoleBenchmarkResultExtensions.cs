namespace NBenchmark.Reporters.Console;

public static class ConsoleBenchmarkResultExtensions
{
    public static async Task<BenchmarkResult> PrintAsync(this BenchmarkResult result)
    {
        var reporter = new ConsoleReporter();
        await reporter.ReportAsync([result]);
        return result;
    }
}
