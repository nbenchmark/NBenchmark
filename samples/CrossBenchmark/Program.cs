using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using CrossBenchmark;
using NBenchmark;
using NBenchmark.Reporters;
using NBenchmark.Reporters.Console;

var useBdnThroughput = args.Any(a => string.Equals(a, "--bdn-throughput", StringComparison.OrdinalIgnoreCase));
var bdnRunStrategy = useBdnThroughput ? RunStrategy.Throughput : RunStrategy.Monitoring;

Console.WriteLine("======================================================");
Console.WriteLine("  Cross-Benchmark: NBenchmark vs BenchmarkDotNet");
Console.WriteLine("  Comparable settings (realistic profile, 1 op/sample)");
Console.WriteLine("  25 warmup + 200 measured iterations");
Console.WriteLine($"  BDN mode: {bdnRunStrategy} {(useBdnThroughput ? "(slower)" : "(faster default)")}");
Console.WriteLine("  Tip: pass --bdn-throughput to run BDN in throughput mode");
Console.WriteLine("======================================================");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────
// 1. NBenchmark Suite
// ─────────────────────────────────────────────────────────────────────────
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("  NBenchmark Results");
Console.WriteLine("───────────────────────────────────────────────────────");

var nbResults = await new BenchmarkSuite("cross-benchmark")
    .WithMeasurementProfile(MeasurementProfile.Realistic)
    .WithOpsPerSample(1)
    .Add("CountPrimes", () => Workloads.CountPrimes())
    .Add("SortStrings", () => Workloads.SortStrings())
    .Add("LinqAggregate", () => Workloads.LinqAggregate())
    .Add("StringBuilder", () => Workloads.StringBuilderAppend())
    .Add("DictionaryLookup", () => Workloads.DictionaryLookup())
    .Add("MatrixMultiply", () => Workloads.MatrixMultiply())
    .WithBaseline("CountPrimes")
    .WithWarmup(25)
    .WithIterations(200)
    .WithOutlierMode(OutlierMode.IqrFence)
    .WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────
// 2. BenchmarkDotNet
// ─────────────────────────────────────────────────────────────────────────
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("  BenchmarkDotNet Results");
Console.WriteLine("───────────────────────────────────────────────────────");

var bdnConfig = ManualConfig
    .Create(DefaultConfig.Instance)
    .AddJob(Job.Default
        .WithId($"CrossBench-{bdnRunStrategy}")
        .WithStrategy(bdnRunStrategy)
        .WithLaunchCount(1)
        .WithWarmupCount(25)
        .WithIterationCount(200));

var bdnSummary = BenchmarkRunner.Run<BdnBenchmarks>(bdnConfig);
var bdnMediansByName = bdnSummary.Reports
    .Where(r => r.Success && r.ResultStatistics is not null)
    .ToDictionary(
        r => r.BenchmarkCase.Descriptor.WorkloadMethod.Name,
        r => r.ResultStatistics!.Median,
        StringComparer.Ordinal);

Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────
// 3. Summary Comparison
// ─────────────────────────────────────────────────────────────────────────
Console.WriteLine("───────────────────────────────────────────────────────");
Console.WriteLine("  Comparison (Median ns)");
Console.WriteLine("───────────────────────────────────────────────────────");

if (nbResults.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"  {"Workload",-20} {"NBenchmark",-16} {"BDN",-16} {"Ratio",-10}");
    Console.WriteLine($"  {"─".PadRight(20, '─')} {"─".PadRight(16, '─')} {"─".PadRight(16, '─')} {"─".PadRight(10, '─')}");

    foreach (var r in nbResults)
    {
        var nbMedian = FormatNs(r.Median);
        var hasBdnMedian = bdnMediansByName.TryGetValue(r.Name, out var bdnMedian);
        var bdnMedianText = hasBdnMedian ? FormatNs(bdnMedian) : "(n/a)";
        var ratio = hasBdnMedian && r.Median > 0
            ? $"{bdnMedian / r.Median:F2}x"
            : "-";

        Console.WriteLine($"  {r.Name,-20} {nbMedian,-16} {bdnMedianText,-16} {ratio,-10}");
    }
    Console.WriteLine();
}

// ─────────────────────────────────────────────────────────────────────────
// Helper
// ─────────────────────────────────────────────────────────────────────────
static string FormatNs(double nanoseconds)
{
    return nanoseconds switch
    {
        < 1_000 => $"{nanoseconds:F2} ns",
        < 1_000_000 => $"{nanoseconds / 1_000:F2} us",
        < 1_000_000_000 => $"{nanoseconds / 1_000_000:F2} ms",
        _ => $"{nanoseconds / 1_000_000_000:F2}  s"
    };
}

// ─────────────────────────────────────────────────────────────────────────
// BenchmarkDotNet benchmark class
// ─────────────────────────────────────────────────────────────────────────
public class BdnBenchmarks
{
    [Benchmark(Baseline = true)]
    public int CountPrimes() => Workloads.CountPrimes();

    [Benchmark]
    public void SortStrings() => Workloads.SortStrings();

    [Benchmark]
    public double LinqAggregate() => Workloads.LinqAggregate();

    [Benchmark]
    public string StringBuilder() => Workloads.StringBuilderAppend();

    [Benchmark]
    public int DictionaryLookup() => Workloads.DictionaryLookup();

    [Benchmark]
    public double MatrixMultiply() => Workloads.MatrixMultiply();
}
