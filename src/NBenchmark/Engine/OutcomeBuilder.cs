using System.Reflection;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

internal static class OutcomeBuilder
{
    public static MeasurementOutcome Build(
        RunOutcome input,
        string name,
        string className,
        string? description,
        bool isBaseline,
        MeasurementOptions options,
        TimeSpan totalDuration,
        TimeSpan measuredDuration,
        int resolvedWarmup = 0,
        AutoTuneDiagnostic? autoTune = null,
        IReadOnlyList<string>? categories = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input switch
        {
            RunOutcome.Success s => Build(
                name, className, description, isBaseline, options,
                s.Result.Stats,
                s.Result.MeasuredIterations,
                s.Result.MeanAllocatedBytes,
                s.Result.Q1,
                s.Result.Q3,
                s.Result.InterquartileRange,
                s.Result.LowerFence,
                s.Result.UpperFence,
                s.Result.OutliersRemoved,
                s.Result.TrimmedOrdinals,
                s.RawSamples,
                s.Result.RawAllocations,
                s.Result.DiagnosticsResult,
                false,
                null,
                totalDuration,
                measuredDuration,
                s.Result.Warnings,
                resolvedWarmup,
                autoTune,
                categories),

            RunOutcome.DryRun => Build(
                name, className, description, isBaseline, options,
                null,
                0,
                null,
                0, 0, 0, null, null, 0,
                [],
                [],
                null,
                null,
                false,
                null,
                totalDuration,
                TimeSpan.Zero,
                [],
                resolvedWarmup,
                null,
                categories),

            RunOutcome.Errored e => Build(
                name, className, description, isBaseline, options,
                null,
                0,
                null,
                0, 0, 0, null, null, 0,
                [],
                [],
                null,
                null,
                true,
                e.ErrorMessageOverride ?? Unwrap(e.Error).ToString(),
                totalDuration,
                measuredDuration,
                [],
                resolvedWarmup,
                null,
                categories),

            _ => throw new ArgumentOutOfRangeException(nameof(input), input, "Unknown RunOutcome case."),
        };
    }

    private static MeasurementOutcome Build(
        string name,
        string className,
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
        IReadOnlyList<int> trimmedOrdinals,
        double[] rawSamples,
        long[]? rawAllocations,
        DiagnosticsResult? diagnosticsResult,
        bool errored,
        string? errorMessage,
        TimeSpan totalDuration,
        TimeSpan measuredDuration,
        IReadOnlyList<string> warnings,
        int resolvedWarmup,
        AutoTuneDiagnostic? autoTune,
        IReadOnlyList<string>? categories = null)
    {
        var allocStats = stats is not null && rawAllocations is not null
            ? StatsSummary.ComputeAllocations(rawAllocations)
            : (AllocationStats?)null;

        var opsPerSecond = stats is not null ? ThroughputFromNs(stats.Mean) : double.NaN;
        var medianOpsPerSecond = stats is not null ? ThroughputFromNs(stats.Median) : double.NaN;

        var totalOperations = autoTune?.TotalBodyInvocations
                              ?? (long)measuredIterations + resolvedWarmup;

        // Share one sample array between MeasurementOutcome.RawSamples and Result.RawSamples
        // to avoid an extra O(n) copy on every measured benchmark.
        var samples = rawSamples.Length == 0 ? Array.Empty<double>() : rawSamples;

        return new MeasurementOutcome
        {
            RawSamples = samples,
            Result = new BenchmarkResult
            {
                Name = name,
                ClassName = className,
                Description = description,
                Mean = stats?.Mean ?? 0,
                Median = stats?.Median ?? 0,
                Percentiles = stats?.Percentiles ?? [],
                Histogram = stats?.Histogram,
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
                TrimmedOrdinals = trimmedOrdinals,
                RawSamples = samples,
                Skewness = stats?.Skewness ?? 0,
                Kurtosis = stats?.Kurtosis ?? 0,
                Mad = stats?.Mad ?? 0,
                MedianCiLower = stats?.MedianCiLower,
                MedianCiUpper = stats?.MedianCiUpper,
                MeanAllocatedBytes = meanAllocatedBytes,
                AllocMedian = allocStats?.P50,
                AllocP95 = allocStats?.P95,
                AllocMax = allocStats?.Max,
                OperationsPerSecond = opsPerSecond,
                MedianOperationsPerSecond = medianOpsPerSecond,
                TotalOperations = totalOperations,
                PValue = null,
                SignificanceVerdict = SignificanceVerdict.NotTested,
                Errored = errored,
                ErrorMessage = errorMessage,
                MeasuredIterations = measuredIterations,
                WarmupIterations = resolvedWarmup,
                RunAtUtc = DateTimeOffset.UtcNow,
                TotalDuration = totalDuration,
                MeasuredDuration = measuredDuration,
                IsBaseline = isBaseline,
                OutlierMode = options.OutlierMode,
                OutlierDetector = options.ResolveOutlierDetector().Name,
                TailMetricsBasis = options.TailMetricsBasis,
                SignificanceTestName = options.ResolveSignificanceTest().Name,
                SignificanceLevel = options.SignificanceLevel,
                Profile = options.Profile,

                // Read from the measuring process's own environment, never from
                // options.RuntimeProfile: that is what the caller asked for, and an in-process run
                // cannot honour it because these knobs are fixed at startup. Stamping intent would
                // make every in-process result claim a fidelity it does not have.
                RuntimeProfileName = RuntimeProfileEnvironment.Current.Name,
                RuntimeKnobs = RuntimeProfileEnvironment.Current.Knobs,
                Warnings = warnings,
                AutoTune = autoTune,
                Diagnostics = diagnosticsResult,
                Categories = categories ?? [],
            },
        };
    }

    private static double ThroughputFromNs(double nsPerOp) =>
        nsPerOp > 0 ? 1_000_000_000.0 / nsPerOp : double.NaN;

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException tiex ? tiex.InnerException ?? tiex : ex;
}
