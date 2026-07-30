using System.Security.Cryptography;
using NBenchmark;
using NBenchmark.Reporters.Console;
using NBenchmark.Stats;

// ---------------------------------------------------------------------------
// NBenchmark - Extensible statistics
//
// This sample shows three things the engine lets you customize:
//   1. The built-in Median Absolute Deviation (MAD) outlier detector.
//   2. The omnibus Kruskal-Wallis test, used automatically for 3+ benchmarks.
//   3. Plugging in your OWN outlier detector and significance test.
// ---------------------------------------------------------------------------

Console.WriteLine("NBenchmark - Extensible Statistics");
Console.WriteLine("==================================");
Console.WriteLine();

// 1 + 2: Comparing three implementations. Because there are three groups, the
// engine runs the Kruskal-Wallis omnibus test by default (Mann-Whitney U is
// only used for a two-way comparison). MAD trims the heavy tail more robustly
// than the default IQR fence on skewed latency data.
Console.WriteLine(">> Built-in: MAD trimming + Kruskal-Wallis (3 groups)");
Console.WriteLine();

await new BenchmarkSuite("hashing")
    .Add("sha256", () => SHA256.HashData(Payload))
    .Add("sha1", () => SHA1.HashData(Payload))
    .Add("md5", () => MD5.HashData(Payload))
    .WithBaseline("md5")
    .WithWarmup(5)
    .WithIterations(60)
    .WithOutlierMode(OutlierMode.MedianAbsoluteDeviation)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

// 3: The same comparison, but with a custom trimming strategy and a custom
// significance rule supplied through the fluent builder.
//
// Both are passed as *factories* rather than as built instances, and that choice decides whether
// this suite is isolated. A strategy object cannot be sent to a measurement worker; only its type
// name can, and a type name reaches a parameterless constructor and nothing else. Neither of these
// has one - `0.90` and `25` are constructor arguments - so handing over the instances would leave
// the worker with no way to rebuild them. Rather than score the results under a silently
// substituted method, NBenchmark declines to isolate at all and measures in this process.
//
// A static factory is addressable, so the worker runs it and gets these exact objects with these
// exact arguments.
Console.WriteLine();
Console.WriteLine(">> Custom: KeepFastest detector + median-ratio significance");
Console.WriteLine();

var custom = await new BenchmarkSuite("hashing-custom")
    .Add("sha256", () => SHA256.HashData(Payload))
    .Add("sha1", () => SHA1.HashData(Payload))
    .Add("md5", () => MD5.HashData(Payload))
    .WithBaseline("md5")
    .WithWarmup(5)
    .WithIterations(60)
    .WithOutlierDetector(static () => new KeepFastestDetector(0.90))
    .WithSignificanceTest(static () => new MedianRatioSignificanceTest(25))
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

// Printed so the sample asserts its own fidelity: if a custom strategy ever stops being isolatable,
// this line says so rather than leaving it to be noticed in the header.
Console.WriteLine();

foreach (var result in custom)
{
    Console.WriteLine(
        $"  {result.Name}: {result.IsolationStatus} under '{result.RuntimeProfileName}', "
        + $"trimmed by '{result.OutlierDetector}'");
}

internal static partial class Program
{
    // 64 KiB of data to hash on every iteration.
    private static readonly byte[] Payload = CreatePayload();

    private static byte[] CreatePayload()
    {
        var payload = new byte[64 * 1024];
        new Random(20260610).NextBytes(payload);
        return payload;
    }
}

/// <summary>
///     A custom outlier detector that keeps only the fastest <c>fraction</c> of samples.
///     Useful for throughput work where the slow tail is environmental noise (GC, context
///     switches) rather than part of the operation being measured.
/// </summary>
internal sealed class KeepFastestDetector(double fraction) : IOutlierDetector
{
    public string Name => $"keep fastest {fraction * 100:0.#}%";

    public OutlierClassification Classify(double[] sortedSamples)
    {
        // sortedSamples is provided sorted ascending and must not be mutated.
        var keep = (int)Math.Floor(sortedSamples.Length * fraction);

        if (keep <= 0 || keep >= sortedSamples.Length)
            return OutlierClassification.KeepAll(sortedSamples);

        return new OutlierClassification
        {
            Kept = sortedSamples[..keep],
            Discarded = sortedSamples[keep..],
            UpperFence = sortedSamples[keep],
        };
    }
}

/// <summary>
///     A custom significance test that flags a candidate as "significant" when its median
///     differs from the baseline's by more than a fixed percentage - a simple, explainable
///     business rule rather than a statistical hypothesis test.
/// </summary>
internal sealed class MedianRatioSignificanceTest(double thresholdPercent) : ISignificanceTest
{
    public string Name => $"median ratio (>{thresholdPercent:0.#}%)";

    public SignificanceReport Analyze(SignificanceContext context)
    {
        var baselineMedian = Median(context.Baseline.Samples);
        var pairwise = new List<PairwiseComparison>();

        foreach (var candidate in context.Candidates)
        {
            var ratio = Median(candidate.Samples) / baselineMedian;
            var deltaPercent = Math.Abs(ratio - 1.0) * 100.0;

            var verdict = deltaPercent > thresholdPercent
                ? SignificanceVerdict.Significant
                : SignificanceVerdict.NotSignificant;

            // This rule has no p-value, so we report null for it.
            pairwise.Add(new PairwiseComparison(candidate.Name, null, verdict));
        }

        return new SignificanceReport { Pairwise = pairwise };
    }

    private static double Median(double[] samples)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;

        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
}
