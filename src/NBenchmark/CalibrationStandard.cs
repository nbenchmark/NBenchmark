using System.Diagnostics;

namespace NBenchmark;

/// <summary>What one run of the calibration standard measured.</summary>
public sealed record CalibrationResult(double Mean, double Median, double[] Samples);

/// <summary>
///     A fixed synthetic workload used to normalise a threshold for machine speed.
/// </summary>
/// <remarks>
///     <para>
///         A test that gates on <c>MaxSlowdownRatio</c> without naming a reference method is asking
///         "is this slower than it should be <i>on this machine</i>". Answering that needs a second
///         measurement of known cost to divide by, and this is it: an integer-multiply chain with a
///         loop-carried dependency, chosen because its cost is dominated by arithmetic latency rather
///         than by memory, branch prediction or the allocator, so it tracks core speed and little
///         else.
///     </para>
///     <para>
///         This lives in the core assembly, not beside the gate that uses it, because <b>both sides of
///         the ratio have to run under the same runtime configuration for the ratio to mean
///         anything</b>. The candidate is measured in a worker process launched with tiering and
///         ReadyToRun disabled; a calibration measured in the test host runs under the opposite
///         configuration, and that difference alone moved a body of provably identical cost by ~3.3x.
///         Putting the standard here lets the worker measure it in the same process as the candidate,
///         so the configuration cancels out of the ratio instead of hiding in it.
///     </para>
///     <para>
///         The numbers this produces are not comparable across runtime profiles and are not meant to
///         be. It is a divisor, not a benchmark.
///     </para>
/// </remarks>
public static class CalibrationStandard
{
    private const int SampleCount = 32;
    private const int WorkPerSample = 4096;

    /// <summary>
    ///     Kept alive across the measurement so the JIT cannot delete the loop it is timing - the
    ///     same hazard the engine's own elision sink exists for, and one this workload would be
    ///     especially prone to, since nothing else reads the result.
    /// </summary>
    private static long Accumulator;

    /// <summary>Runs the standard and returns its samples in nanoseconds.</summary>
    public static CalibrationResult Measure()
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

        return new CalibrationResult(samples.Sum() / samples.Length, Median(samples), samples);
    }

    /// <summary>Presents a measurement as a <see cref="BenchmarkResult" /> for comparison APIs.</summary>
    public static BenchmarkResult ToBenchmarkResult(CalibrationResult calibration, IsolationStatus isolationStatus)
    {
        ArgumentNullException.ThrowIfNull(calibration);

        return BenchmarkResult.FromCalibration(
                "calibration", calibration.Mean, calibration.Median, calibration.Samples)
            with
            {
                IsolationStatus = isolationStatus,
            };
    }

    private static long BusyWeight(int iterations)
    {
        long acc = 1;

        for (var i = 0; i < iterations; i++)
        {
            acc = unchecked(acc * (long)0x9E3779B97F4A7C15UL + i);
        }

        return acc;
    }

    private static double Median(double[] values)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        var mid = sorted.Length / 2;

        return (sorted.Length & 1) == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
