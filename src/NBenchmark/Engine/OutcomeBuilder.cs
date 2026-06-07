using System.Reflection;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

/// <summary>
///     Single owner of the 22-field <see cref="BenchmarkResult" /> literal. Replaces
///     the three duplicated literals that previously lived in
///     <c>BenchmarkRunner.DryRunOutcome</c>, <c>BenchmarkRunner.BuildOutcome</c>, and
///     <c>BenchmarkRunner.ErroredOutcome</c>, plus the <c>CreateErrored</c> factory
///     on <see cref="BenchmarkResult" />. Concentrates result construction so the
///     shape can evolve in one place. See ADR 0001 (status update).
/// </summary>
internal static class OutcomeBuilder
{
    public static MeasurementOutcome Build(
        OutcomeInput input,
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
            OutcomeInput.Success s => Build(
                name, description, isBaseline, options,
                stats: s.Result.Stats,
                measuredIterations: s.Result.MeasuredIterations,
                meanAllocatedBytes: s.Result.MeanAllocatedBytes,
                rawSamples: s.RawTimings,
                errored: false,
                errorMessage: null,
                totalDuration,
                measuredDuration),

            OutcomeInput.DryRun => Build(
                name, description, isBaseline, options,
                stats: null,
                measuredIterations: 0,
                meanAllocatedBytes: null,
                rawSamples: [],
                errored: false,
                errorMessage: null,
                totalDuration,
                measuredDuration: TimeSpan.Zero),

            OutcomeInput.Errored e => Build(
                name, description, isBaseline, options,
                stats: null,
                measuredIterations: 0,
                meanAllocatedBytes: null,
                rawSamples: [],
                errored: true,
                errorMessage: e.ErrorMessageOverride ?? Unwrap(e.Error).ToString(),
                totalDuration,
                measuredDuration),

            _ => throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown OutcomeInput case."),
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
        double[] rawSamples,
        bool errored,
        string? errorMessage,
        TimeSpan totalDuration,
        TimeSpan measuredDuration)
    {
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
                MeanAllocatedBytes = meanAllocatedBytes,
                PValue = null,
                IsSignificant = null,
                Errored = errored,
                ErrorMessage = errorMessage,
                MeasuredIterations = measuredIterations,
                WarmupIterations = options.WarmupIterations,
                RunAt = DateTimeOffset.UtcNow,
                TotalDuration = totalDuration,
                MeasuredDuration = measuredDuration,
                IsBaseline = isBaseline,
                OutlierMode = options.OutlierMode,
            },
        };
    }

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException tiex ? (tiex.InnerException ?? tiex) : ex;
}
