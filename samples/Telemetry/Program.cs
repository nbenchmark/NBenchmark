using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Exporters.OpenTelemetry;
using NBenchmark.Reporters.Console;

// One call, and it is only here to supply a local default. Referencing the exporter package and
// passing --otlp-endpoint (or setting OTEL_EXPORTER_OTLP_ENDPOINT) would be enough on its own: the
// package self-registers and attaches to any run that has an endpoint to export to, in the harness
// and in every nbworker child.
await BenchmarkHarness.Create(args)
    .AddFromAssembly<TelemetryBenchmarks>()
    .WithOpenTelemetry(o =>
    {
        o.Endpoint = "http://localhost:4317";
        o.ServiceName = "nbenchmark-telemetry-sample";
    })
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();
Console.WriteLine("Open Grafana at http://localhost:3000/d/nbenchmark-run.");

/// <summary>
///     Four bodies chosen to make the telemetry interesting rather than to compare anything:
///     they span four orders of magnitude, one allocates, and one has data-dependent cost so the
///     confidence-interval gauge and the outlier counter have something to say.
/// </summary>
public class TelemetryBenchmarks
{
    private readonly byte[] _payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
    private readonly string[] _words = ["alpha", "beta", "gamma", "delta", "epsilon"];
    private readonly int[] _lengths = [1, 3, 2, 64, 1, 1, 8, 1, 256, 2, 1, 16, 1, 1, 4, 1];

    /// <summary>Microseconds, low variance - the stable reference point on every chart.</summary>
    [Benchmark(Baseline = true)]
    public byte[] Sha256() => SHA256.HashData(_payload);

    /// <summary>Allocates on every call, so `nbenchmark.alloc.bytes_per_op` is non-zero.</summary>
    [Benchmark]
    public string ConcatStrings()
    {
        var builder = new StringBuilder();
        foreach (var word in _words)
            builder.Append(word).Append(' ');

        return builder.ToString();
    }

    /// <summary>
    ///     Single-digit nanoseconds and zero allocation. Far below the timer's resolution per
    ///     call, so the calibration phase resolves a large ops-per-sample count - visible as
    ///     `nbenchmark.resolved_k` on the calibration span.
    /// </summary>
    [Benchmark]
    public int SpanFormat()
    {
        Span<char> buffer = stackalloc char[16];
        return 1234567.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture) ? written : 0;
    }

    /// <summary>
    ///     Cost depends on which entry of the length table the call lands on, so the sample
    ///     distribution is heavy-tailed: a wide CI half-width gauge, a slow-converging
    ///     measurement phase, and outliers for the detector to remove.
    /// </summary>
    [Benchmark]
    public long VariableWork()
    {
        long total = 0;
        foreach (var length in _lengths)
        for (var i = 0; i < length; i++)
            total += _payload[i];

        return total;
    }
}
