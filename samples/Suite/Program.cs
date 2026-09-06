using NBenchmark;
using NBenchmark.Reporters.Console;

var results = await new BenchmarkSuite("sorting")
    .Add("bubble", () =>
    {
        var arr = Enumerable.Range(0, 100).Reverse().ToArray();
        Array.Sort(arr);
    })
    .Add("linq", () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmupSamples(3)
    .WithSamples(50)
    .WithOutlierMode(OutlierMode.RemoveTop5Percent)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
