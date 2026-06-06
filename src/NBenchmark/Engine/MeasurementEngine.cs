using System.Diagnostics;
using System.Runtime.CompilerServices;
using NBenchmark.Stats;

namespace NBenchmark.Engine;

public static class MeasurementEngine
{
    public static MeasurementOutcome MeasureSync(
        string name,
        Action action,
        MeasurementOptions? options = null,
        string? description = null,
        bool isBaseline = false,
        Action? iterationSetup = null,
        Action? iterationTeardown = null,
        CancellationToken cancellationToken = default)
    {
        options ??= MeasurementOptions.Default;
        var totalTimer = Stopwatch.StartNew();

        for (var i = 0; i < options.WarmupIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterationSetup?.Invoke();
            action();
            iterationTeardown?.Invoke();
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        var timings = new double[options.Iterations];
        var allocations = options.MeasureAllocations ? new long[options.Iterations] : null;

        for (var i = 0; i < options.Iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            iterationSetup?.Invoke();

            long allocBefore = 0;

            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes();

            var timestamp = Stopwatch.GetTimestamp();
            action();
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes();
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            iterationTeardown?.Invoke();

            timings[i] = elapsed.TotalNanoseconds;
        }

        totalTimer.Stop();

        var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
        var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);

        long? meanAllocs = allocations is not null
            ? (long)allocations.Average()
            : null;

        return new MeasurementOutcome
        {
            RawSamples = timings,
            Result = new BenchmarkResult
            {
                Name = name,
                Description = description,
                Mean = stats.Mean,
                Median = stats.Median,
                P95 = stats.P95,
                P99 = stats.P99,
                Min = stats.Min,
                Max = stats.Max,
                StandardDeviation = stats.StandardDeviation,
                StandardError = stats.StandardError,
                MarginOfError = stats.MarginOfError,
                ConfidenceLevel = stats.ConfidenceLevel,
                CoefficientOfVariation = stats.CoefficientOfVariation,
                MeanAllocatedBytes = meanAllocs,
                PValue = null,
                IsSignificant = null,
                Errored = false,
                ErrorMessage = null,
                MeasuredIterations = trimmed.Length,
                WarmupIterations = options.WarmupIterations,
                RunAt = DateTimeOffset.UtcNow,
                TotalDuration = totalTimer.Elapsed,
                IsBaseline = isBaseline,
                OutlierMode = options.OutlierMode,
            },
        };
    }

    public static async Task<MeasurementOutcome> MeasureAsync(
        string name,
        Func<Task> action,
        MeasurementOptions? options = null,
        string? description = null,
        bool isBaseline = false,
        Action? iterationSetup = null,
        Action? iterationTeardown = null,
        CancellationToken cancellationToken = default)
    {
        options ??= MeasurementOptions.Default;

        var totalTimer = Stopwatch.StartNew();

        await RunWarmupAsync(action, iterationSetup, iterationTeardown, options.WarmupIterations, cancellationToken);

        var (timings, allocations) = await CollectSamplesAsync(action, iterationSetup, iterationTeardown, options, cancellationToken);

        totalTimer.Stop();

        var trimmed = ApplyOutlierMode(timings, options.OutlierMode);
        var stats = StatsSummary.Compute(trimmed, options.ConfidenceLevel);

        long? meanAllocs = allocations is not null
            ? (long)allocations.Average()
            : null;

        return new MeasurementOutcome
        {
            RawSamples = timings,
            Result = new BenchmarkResult
            {
                Name = name,
                Description = description,
                Mean = stats.Mean,
                Median = stats.Median,
                P95 = stats.P95,
                P99 = stats.P99,
                Min = stats.Min,
                Max = stats.Max,
                StandardDeviation = stats.StandardDeviation,
                StandardError = stats.StandardError,
                MarginOfError = stats.MarginOfError,
                ConfidenceLevel = stats.ConfidenceLevel,
                CoefficientOfVariation = stats.CoefficientOfVariation,
                MeanAllocatedBytes = meanAllocs,
                PValue = null,
                IsSignificant = null,
                Errored = false,
                ErrorMessage = null,
                MeasuredIterations = trimmed.Length,
                WarmupIterations = options.WarmupIterations,
                RunAt = DateTimeOffset.UtcNow,
                TotalDuration = totalTimer.Elapsed,
                IsBaseline = isBaseline,
                OutlierMode = options.OutlierMode,
            },
        };
    }

    public static Task<MeasurementOutcome> MeasureAsync(
        string name,
        Action action,
        MeasurementOptions? options = null,
        string? description = null,
        bool isBaseline = false,
        Action? iterationSetup = null,
        Action? iterationTeardown = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = MeasureSync(name, action, options, description, isBaseline,
            iterationSetup, iterationTeardown, cancellationToken);

        return Task.FromResult(outcome);
    }

    private static async Task RunWarmupAsync(
        Func<Task> action,
        Action? iterationSetup,
        Action? iterationTeardown,
        int iterations,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterationSetup?.Invoke();
            await action();
            iterationTeardown?.Invoke();
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    private static async Task<(double[] Timings, long[]? Allocations)> CollectSamplesAsync(
        Func<Task> action,
        Action? iterationSetup,
        Action? iterationTeardown,
        MeasurementOptions options,
        CancellationToken cancellationToken)
    {
        var timings = new double[options.Iterations];
        var allocations = options.MeasureAllocations ? new long[options.Iterations] : null;

        for (var i = 0; i < options.Iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.ForceGcBeforeEachIteration)
                ForceGen0Collection();

            iterationSetup?.Invoke();

            long allocBefore = 0;

            if (options.MeasureAllocations)
                allocBefore = GC.GetTotalAllocatedBytes();

            var timestamp = Stopwatch.GetTimestamp();
            await action();
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            if (options.MeasureAllocations && allocations is not null)
            {
                var allocAfter = GC.GetTotalAllocatedBytes();
                allocations[i] = Math.Max(0, allocAfter - allocBefore);
            }

            iterationTeardown?.Invoke();

            timings[i] = elapsed.TotalNanoseconds;
        }

        return (timings, allocations);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGen0Collection()
    {
        GC.Collect(0, GCCollectionMode.Forced, true);
    }

    private static double[] ApplyOutlierMode(double[] timings, OutlierMode mode)
    {
        return mode switch
        {
            OutlierMode.None => SortAndReturn(timings),
            OutlierMode.RemoveTop5Percent => RemoveTopPercent(timings, 0.05),
            OutlierMode.RemoveTop5PercentAndBottom5Percent => RemoveBothPercent(timings, 0.05),
            OutlierMode.IqrFence => RemoveIqrOutliers(timings),
            _ => timings,
        };
    }

    private static double[] SortAndReturn(double[] values)
    {
        Array.Sort(values);
        return values;
    }

    private static double[] RemoveTopPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var keep = (int)Math.Floor(values.Length * (1.0 - fraction));
        return values[..keep];
    }

    private static double[] RemoveBothPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var trimEach = (int)Math.Floor(values.Length * fraction);
        return values[trimEach..(values.Length - trimEach)];
    }

    private static double[] RemoveIqrOutliers(double[] values)
    {
        Array.Sort(values);
        var q1 = Percentile.Compute(values, 0.25);
        var q3 = Percentile.Compute(values, 0.75);
        var iqr = q3 - q1;
        var lower = q1 - 1.5 * iqr;
        var upper = q3 + 1.5 * iqr;
        var filtered = values.Where(v => v >= lower && v <= upper).ToArray();

        return filtered.Length > 0 ? filtered : values;
    }
}