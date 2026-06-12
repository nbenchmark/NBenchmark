using NBenchmark;
using NBenchmark.Reporters;
using NBenchmark.Reporters.Console;

Console.WriteLine("NBenchmark Suite - Advanced Detail Mode");
Console.WriteLine("========================================");
Console.WriteLine();

var results = await new BenchmarkSuite("advanced-detail")
    .Add("fast", () => Thread.SpinWait(10_000))
    .Add("medium", () => Thread.SpinWait(100_000))
    .Add("slow", () => Thread.SpinWait(500_000))
    .Add("slower", () => Thread.SpinWait(1_000_000))
    .Add("slowest", () => Thread.SpinWait(3_000_000))
    .WithBaseline("fast")
    .WithWarmup(5)
    .WithIterations(30)
    .WithOutlierMode(OutlierMode.IqrFence)
    .WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();