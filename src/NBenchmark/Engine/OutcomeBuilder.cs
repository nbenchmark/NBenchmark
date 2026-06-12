using System.Reflection;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

internal static class OutcomeBuilder
{
    public static MeasurementOutcome Build(
        RunOutcome input,
        string name,
        string? description,
        bool isBaseline,
        MeasurementOptions options,
        TimeSpan totalDuration,
        TimeSpan measuredDuration)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input switch
        {
            RunOutcome.Success s => Build(
                name, description, isBaseline, options,
                s.Result.Stats,
                s.Result.MeasuredIterations,
                s.Result.MeanAllocatedBytes,
                s.Result.Q1,
                s.Result.Q3,
                s.Result.InterquartileRange,
                s.Result.LowerFence,
                s.Result.UpperFence,
                s.Result.OutliersRemoved,
                s.RawSamples,
                s.Result.RawAllocations,
                false,
                null,
                totalDuration,
                measuredDuration,
                s.Result.Warnings),

            RunOutcome.DryRun => Build(
                name, description, isBaseline, options,
                null,
                0,
                null,
                0, 0, 0, null, null, 0,
                [],
                null,
                false,
                null,
                totalDuration,
                TimeSpan.Zero,
                []),

            RunOutcome.Errored e => Build(
                name, description, isBaseline, options,
                null,
                0,
                null,
                0, 0, 0, null, null, 0,
                [],
                null,
                true,
                e.ErrorMessageOverride ?? Unwrap(e.Error).ToString(),
                totalDuration,
                measuredDuration,
                []),

            _ => throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown RunOutcome case."),
        };
    }

    private static MeasurementOutcome Build(
        string name,
        string? description,
        bool isBaseline,
        MeasurementOptions options,
        StatsSummary? stats,
        int measuredIterations,
        long? meanAllocatedBytes,
        double q1,
        double q3,
        double interquartileRange,
        double? lowerFence,
        double? upperFence,
        int outliersRemoved,
        double[] rawSamples,
        long[]? rawAllocations,
        bool errored,
        string? errorMessage,
        TimeSpan totalDuration,
        TimeSpan measuredDuration,
        IReadOnlyList<string> warnings)
    {
        var allocStats = (stats is not null && rawAllocations is not null)
            ? StatsSummary.ComputeAllocations(rawAllocations)
            : (AllocationStats?)null;

        return new MeasurementOutcome
        {
            RawSamples = rawSamples,
            Result = new BenchmarkResult
            {
                Name = name,
                Description = description,
                Mean = stats?.Mean ?? 0,
                Median = stats?.Median ?? 0,
                P95 = stats?.P95 ?? 0,
                P99 = stats?.P99 ?? 0,
                Min = stats?.Min ?? 0,
                Max = stats?.Max ?? 0,
                StandardDeviation = stats?.StandardDeviation ?? 0,
                StandardError = stats?.StandardError ?? 0,
                MarginOfError = stats?.MarginOfError ?? 0,
                ConfidenceLevel = options.ConfidenceLevel,
                CoefficientOfVariation = stats?.CoefficientOfVariation ?? 0,
                Q1 = q1,
                Q3 = q3,
                InterquartileRange = interquartileRange,
                LowerFence = lowerFence,
                UpperFence = upperFence,
                OutliersRemoved = outliersRemoved,
                N = measuredIterations,
                Skewness = stats?.Skewness ?? 0,
                Kurtosis = stats?.Kurtosis ?? 0,
                Mad = stats?.Mad ?? 0,
                MeanAllocatedBytes = meanAllocatedBytes,
                AllocMedian = allocStats?.P50,
                AllocP95 = allocStats?.P95,
                AllocMax = allocStats?.Max,
                PValue = null,
                SignificanceVerdict = SignificanceVerdict.NotTested,
                Errored = errored,
                ErrorMessage = errorMessage,
                MeasuredIterations = measuredIterations,
                WarmupIterations = options.WarmupIterations,
                RunAtUtc = DateTimeOffset.UtcNow,
                TotalDuration = totalDuration,
                MeasuredDuration = measuredDuration,
                IsBaseline = isBaseline,
                OutlierMode = options.OutlierMode,
                SignificanceLevel = options.SignificanceLevel,
                Warnings = warnings,
            },
        };
    }

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException tiex ? tiex.InnerException ?? tiex : ex;
}
