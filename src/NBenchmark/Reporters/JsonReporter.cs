using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark.Reporters;

public sealed class JsonReporter(string outputDirectory = ".", string? name = null, ReportDetail detail = ReportDetail.Simple) : IReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static int _fileCounter;
    private readonly string _outputDirectory = PathValidation.ValidateOutputPath(outputDirectory);

    public string Name => "json";

    public ReportDetail Detail { get; set; } = detail;

    /// <summary>
    ///     When <c>false</c>, raw per-sample arrays are omitted from the JSON output (serialized as
    ///     empty arrays). Samples are still collected for significance and the Console histogram;
    ///     this only controls whether they are written to the file. Default <c>true</c>.
    /// </summary>
    public bool IncludeSamples { get; set; } = true;

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var fileName = name
                       ?? $"benchmarks-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _fileCounter):D3}.json";

        var filePath = Path.Combine(_outputDirectory, fileName);

        var serializedResults = IncludeSamples
            ? results
            : results.Select(r => r with { RawSamples = [] }).ToList();

        var envelope = new ResultEnvelope
        {
            SchemaVersion = ReportFormat.SchemaVersion,
            MeasurementEpoch = ReportFormat.MeasurementEpoch,
            GeneratedAt = DateTimeOffset.UtcNow,
            Detail = Detail,
            Profile = results.FirstOrDefault()?.Profile ?? MeasurementProfile.Realistic,
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
        public MeasurementProfile Profile { get; init; }
        public IReadOnlyList<BenchmarkResult> Results { get; init; } = [];
    }
}
