using NBenchmark;
using NBenchmark.Reporters;
using NBenchmark.Reporters.Console;

Console.WriteLine("NBenchmark Suite - Advanced Detail Mode");
Console.WriteLine("========================================");
Console.WriteLine();

var results = await new BenchmarkSuite("advanced-detail")
    .Add("parse-int", () =>
    {
        _ = int.Parse("12345");
    })
    .Add("parse-guid", () =>
    {
        _ = Guid.Parse("a3b7c8d9-e1f2-4a5b-8c7d-9e0f1a2b3c4d");
    })
    .Add("split-string", () =>
    {
        _ = "one,two,three,four,five,six,seven,eight,nine,ten".Split(',');
    })
    .Add("string-concat", () =>
    {
        _ = string.Concat("The quick brown ", "fox jumps over ", "the lazy dog.");
    })
    .Add("string-builder", () =>
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 10; i++)
            sb.Append("hello").Append(i);
        _ = sb.ToString();
    })
    .WithBaseline("parse-int")
    .WithWarmup(5)
    .WithIterations(100)
    .WithOutlierMode(OutlierMode.IqrFence)
    .WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress(50, 5))
    .RunAsync();