namespace NBenchmark;

public record BenchmarkResult
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    public required double Mean { get; init; }
    public required double Median { get; init; }
    public required double P95 { get; init; }
    public required double P99 { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public required double StandardDeviation { get; init; }

    public double StandardError { get; init; }

    public double MarginOfError { get; init; }

    public double ConfidenceLevel { get; init; } = 0.95;

    public double CoefficientOfVariation { get; init; }

    public required double Q1 { get; init; }
    public required double Q3 { get; init; }
    public required double InterquartileRange { get; init; }

    public double? LowerFence { get; init; }
    public double? UpperFence { get; init; }

    public required int OutliersRemoved { get; init; }
    public required int N { get; init; }

    public required double Skewness { get; init; }
    public required double Kurtosis { get; init; }
    public required double Mad { get; init; }

    public required long? AllocMedian { get; init; }
    public required long? AllocP95 { get; init; }
    public required long? AllocMax { get; init; }

    public long? MeanAllocatedBytes { get; init; }

    public double? PValue { get; init; }
    public SignificanceVerdict SignificanceVerdict { get; init; }

    public double SignificanceLevel { get; init; } = 0.05;

    public bool Errored { get; init; }
    public string? ErrorMessage { get; init; }

    public int MeasuredIterations { get; init; }
    public int WarmupIterations { get; init; }
    public DateTimeOffset RunAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public TimeSpan TotalDuration { get; init; } = TimeSpan.Zero;

    public TimeSpan MeasuredDuration { get; init; } = TimeSpan.Zero;

    public bool IsBaseline { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public double ConfidenceIntervalLower => Mean - MarginOfError;
    public double ConfidenceIntervalUpper => Mean + MarginOfError;
    public double Range => Max - Min;
    public double StandardErrorPercent => Mean > 0 ? StandardError / Mean * 100 : 0;
    public double MarginPercent => Mean > 0 ? MarginOfError / Mean * 100 : 0;
    public double CoefficientOfVariationPercent => CoefficientOfVariation * 100;
}