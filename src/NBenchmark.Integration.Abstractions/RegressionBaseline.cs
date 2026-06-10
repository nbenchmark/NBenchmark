using System.Text.Json;

namespace NBenchmark.Integration.Abstractions;

public static class RegressionBaseline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IReadOnlyList<string> Check(
        BenchmarkResult result,
        string baselinePath,
        double maxSlowdownRatio)
    {
        var violations = new List<string>();

        if (!File.Exists(baselinePath))
        {
            violations.Add($"Baseline file not found: {baselinePath}");
            return violations;
        }

        var json = File.ReadAllText(baselinePath);

        BaselineEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BaselineEnvelope>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            violations.Add($"Failed to parse baseline file '{baselinePath}': {ex.Message}");
            return violations;
        }

        if (envelope?.Results is null || envelope.Results.Count == 0)
        {
            violations.Add($"Baseline file '{baselinePath}' contains no results.");
            return violations;
        }

        var baseline = envelope.Results.FirstOrDefault(r =>
            string.Equals(r.Name, result.Name, StringComparison.OrdinalIgnoreCase));

        if (baseline is null)
        {
            violations.Add(
                $"Benchmark '{result.Name}' not found in baseline file '{baselinePath}'. " +
                $"Available: {string.Join(", ", envelope.Results.Select(r => r.Name))}");
            return violations;
        }

        if (baseline.Mean <= 0)
        {
            if (result.Mean > 0)
            {
                violations.Add(
                    $"Regression detected: mean {result.Mean:F2} ns exceeds non-positive baseline {baseline.Mean:F2} ns.");
            }

            return violations;
        }

        var ratio = result.Mean / baseline.Mean;

        if (ratio > maxSlowdownRatio)
        {
            violations.Add(
                $"Regression detected: mean {result.Mean:F2} ns vs baseline {baseline.Mean:F2} ns " +
                $"(ratio {ratio:F2}x exceeds max {maxSlowdownRatio:F2}x)");
        }

        return violations;
    }

    private sealed class BaselineEnvelope
    {
        public List<BaselineEntry> Results { get; init; } = [];
    }

    private sealed class BaselineEntry
    {
        public string Name { get; init; } = string.Empty;
        public double Mean { get; init; }
    }
}
