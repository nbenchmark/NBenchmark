using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark.Reporters;

public sealed class JsonReporter(string? outputDirectory = null, string? fileName = null) : IReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static int _fileCounter;
    private readonly string? _outputDirectory =
        outputDirectory is null ? null : PathValidation.ValidateOutputPath(outputDirectory);

    public string Name => "json";

    /// <summary>
    ///     When <c>false</c>, raw per-sample arrays are omitted from the JSON output (serialized as
    ///     empty arrays). Samples are still collected for significance and the Console histogram;
    ///     this only controls whether they are written to the file. Default <c>true</c>.
    /// </summary>
    public bool IncludeSamples { get; set; } = true;

    /// <summary>
    ///     The path of the file the last <see cref="ReportAsync" /> wrote.
    /// </summary>
    /// <remarks>
    ///     Internal: the extension methods that wrap this reporter for a single result return the path
    ///     they wrote, and the name is generated inside <see cref="ReportAsync" /> from a timestamp and
    ///     a counter, so it cannot be predicted from outside.
    /// </remarks>
    internal string? LastWrittenPath { get; private set; }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        ReportContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var directory = _outputDirectory ?? PathValidation.ValidateOutputPath(context.OutputDirectory ?? ".");
        Directory.CreateDirectory(directory);

        var resolvedName = fileName
                       ?? context.FileName
                       ?? $"benchmarks-{context.StartedUtc.UtcDateTime:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _fileCounter):D3}.json";

        var filePath = Path.Combine(directory, resolvedName);
        LastWrittenPath = filePath;

        var serializedResults = IncludeSamples
            ? results
            : results.Select(r => r with { RawSamples = [] }).ToList();

        var envelope = new ResultEnvelope
        {
            SchemaVersion = ReportFormat.SchemaVersion,
            MeasurementEpoch = ReportFormat.MeasurementEpoch,
            GeneratedAt = DateTimeOffset.UtcNow,
            Detail = context.Detail,
            GcBehavior = results.FirstOrDefault()?.GcBehavior ?? GcBehavior.Natural,
            Results = serializedResults,
        };

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, envelope, Options, cancellationToken);
    }

    private sealed class ResultEnvelope
    {
        // First two fields on purpose: a consumer deciding whether it can read the rest should not
        // have to parse the rest to find out.
        public int SchemaVersion { get; init; }
        public int MeasurementEpoch { get; init; }
        public DateTimeOffset GeneratedAt { get; init; }
        public ReportDetail Detail { get; init; }
        public GcBehavior GcBehavior { get; init; }
        public IReadOnlyList<BenchmarkResult> Results { get; init; } = [];
    }
}
