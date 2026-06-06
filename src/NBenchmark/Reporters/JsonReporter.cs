using System.Text.Json;

namespace NBenchmark.Reporters;

public sealed class JsonReporter : IReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static int _jsonFileCounter;
    private readonly string _outputDirectory;

    public JsonReporter(string outputDirectory = ".")
    {
        _outputDirectory = PathValidation.ValidateOutputPath(outputDirectory);
    }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var counter = Interlocked.Increment(ref _jsonFileCounter);
        var fileName = $"benchmarks-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{counter:D3}.json";
        var filePath = Path.Combine(_outputDirectory, fileName);

        var envelope = new ResultEnvelope
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Results = results,
        };

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, envelope, Options, cancellationToken);
    }

    private sealed class ResultEnvelope
    {
        public DateTimeOffset GeneratedAt { get; init; }
        public IReadOnlyList<BenchmarkResult> Results { get; init; } = [];
    }
}