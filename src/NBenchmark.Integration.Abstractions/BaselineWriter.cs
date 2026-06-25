using System.Text.Json;

namespace NBenchmark.Integration.Abstractions;

public static class BaselineWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Task WriteAsync(MeasurementOutcome outcome, string path, CancellationToken cancellationToken = default)
    {
        return WriteAsync([outcome], path, cancellationToken);
    }

    public static async Task WriteAsync(
        IReadOnlyList<MeasurementOutcome> outcomes,
        string path,
        CancellationToken cancellationToken = default)
    {
        var envelope = new BaselineEnvelope
        {
            Results = outcomes.Select(o => new BaselineEntry
            {
                Name = o.Result.Name,
                Mean = o.Result.Mean,
                Median = o.Result.Median,
                Samples = o.RawSamples,
            }).ToList(),
        };

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken);
    }
}
