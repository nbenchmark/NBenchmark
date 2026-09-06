using NBenchmark;
using NBenchmark.Reporters.Console;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<CategorizedBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

[BenchmarkCategory("String")]
public class CategorizedBenchmarks
{
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Fast")]
    public int Concat() => "hello".Length + "world".Length;

    [Benchmark]
    [BenchmarkCategory("Fast")]
    public int Interpolate() => $"hello {"world"}".Length;

    [Benchmark]
    [BenchmarkCategory("Slow")]
    public int ManyConcat()
    {
        var s = "";

        for (var i = 0; i < 100; i++)
        {
            s += (char)('a' + i % 26);
        }

        return s.Length;
    }
}
