using System.Diagnostics;

namespace NBenchmark.Integration.Abstractions;

public sealed record CalibrationResult(double Mean, double Median, double[] Samples);

public static class PerformanceCalibration
{
    private const int SampleCount = 32;
    private const int WorkPerSample = 4096;

    private static readonly Lazy<CalibrationResult> Cached = new(RunCalibration);

    public static CalibrationResult Run() => Cached.Value;

    public static BenchmarkResult CreateBenchmarkResult()
    {
        var c = Run();
        return BenchmarkResult.FromCalibration("calibration", c.Mean, c.Median, c.Samples);
    }

    private static CalibrationResult RunCalibration()
    {
        var nanosecondsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

        var warmAcc = BusyWeight(WorkPerSample);
        Volatile.Write(ref Accumulator, warmAcc);

        var samples = new double[SampleCount];
        var accumulator = 0L;

        for (var i = 0; i < SampleCount; i++)
        {
            var start = Stopwatch.GetTimestamp();
            accumulator += BusyWeight(WorkPerSample);
            samples[i] = (Stopwatch.GetTimestamp() - start) * nanosecondsPerTick;
        }

        Volatile.Write(ref Accumulator, accumulator);

        var mean = samples.Sum() / samples.Length;
        var median = ComputeMedian(samples);

        return new CalibrationResult(mean, median, samples);
    }

    private static long Accumulator;

    private static long BusyWeight(int iterations)
    {
        long acc = 1;

        for (var i = 0; i < iterations; i++)
        {
            acc = unchecked(acc * (long)0x9E3779B97F4A7C15UL + i);
        }

        return acc;
    }

    private static double ComputeMedian(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var mid = sorted.Length / 2;

        if ((sorted.Length & 1) == 1)
            return sorted[mid];

        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}