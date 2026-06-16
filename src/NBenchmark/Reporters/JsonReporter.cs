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

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var fileName = name
                       ?? $"benchmarks-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _fileCounter):D3}.json";

        var filePath = Path.Combine(_outputDirectory, fileName);

        var envelope = new ResultEnvelope
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Detail = Detail,
            Profile = results.FirstOrDefault()?.Profile ?? MeasurementProfile.Realistic,
            Results = results,
        };

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, envelope, Options, cancellationToken);
    }

    private sealed class ResultEnvelope
    {
        public DateTimeOffset GeneratedAt { get; init; }
        public ReportDetail Detail { get; init; }
        public MeasurementProfile Profile { get; init; }
        public IReadOnlyList<BenchmarkResult> Results { get; init; } = [];
    }
}
