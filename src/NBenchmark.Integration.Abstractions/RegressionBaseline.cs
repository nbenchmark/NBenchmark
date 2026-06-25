using System.Text.Json;
using NBenchmark.Stats;

namespace NBenchmark.Integration.Abstractions;

public sealed class BaselineEnvelope
{
    public List<BaselineEntry> Results { get; init; } = [];
}

public sealed class BaselineEntry
{
    public required string Name { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public required double[] Samples { get; init; }
}

public static class RegressionBaseline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IReadOnlyList<string> Check(
        BenchmarkResult result,
        double[] currentSamples,
        string baselinePath,
        double maxSlowdownRatio,
        double significanceLevel = 0.05)
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

        if (currentSamples is null || currentSamples.Length == 0)
        {
            violations.Add(
                "Current run produced no raw samples; cannot run significance test. " +
                "Ensure the benchmark completed successfully with measurement iterations > 0.");
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

        var mwu = MannWhitneyU.Test(baseline.Samples, currentSamples);
        var statisticallySignificant = !double.IsNaN(mwu.PValue) && mwu.PValue < significanceLevel;
        var ratio = result.Mean / baseline.Mean;
        var practicallySignificant = ratio > maxSlowdownRatio;

        if (statisticallySignificant && practicallySignificant)
        {
            violations.Add(
                $"Regression detected: mean {result.Mean:F2} ns vs baseline {baseline.Mean:F2} ns " +
                $"(ratio {ratio:F2}x, p={mwu.PValue:F4}, Cliff's delta={mwu.CliffsDelta:F3}). " +
                $"Significant slowdown exceeding {maxSlowdownRatio:F2}x ratio gate.");
        }

        return violations;
    }
}