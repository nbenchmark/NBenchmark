namespace NBenchmark.Engine.Detectors;

/// <summary>
///     The pre-flight environment probe. Runs a deterministic, allocation-free busy-weight
///     loop <c>N</c> times, timing each sample, and derives a robust jitter metric: the ratio
///     of the median absolute deviation to the median (MAD / median) of the per-sample
///     timings. The metric characterises the host's baseline interruption rate before the
///     real benchmark starts: a quiet dedicated host reports well below 0.05, a shared-tenant
///     CI runner typically reports 0.10-0.30.
///     <para>
///         The busy-weight body is a multiply-accumulate over a private accumulator. It is
///         CPU-bound, allocation-free, and not optimised away (the accumulator escapes via
///         <see cref="Accumulator" />). The work is deliberately not the benchmark body -
///         the point is to measure the host, not the code under test.
///     </para>
/// </summary>
/// <remarks>
///     Pure computation; no hot-path allocation. The <see cref="Run" /> method takes the
///     clock and cancellation token so it slots into the adaptive loop's existing seams.
/// </remarks>
internal static class JitterCalibrator
{
    /// <summary>
    ///     Runs the jitter probe and returns a robust jitter metric: the ratio of the median
    ///     absolute deviation to the median (<c>MAD / median</c>) of the per-sample timings.
    ///     Returns <c>null</c> when the probe produced fewer than two samples, the median was
    ///     zero, or any sample was non-finite.
    /// </summary>
    /// <param name="sampleCount">How many timed samples to collect.</param>
    /// <param name="workPerSample">How many busy-weight iterations each sample performs.</param>
    /// <param name="clock">The monotonic clock used by the adaptive loop.</param>
    /// <remarks>
    ///     The metric is a robust counterpart to the coefficient of variation: the median and
    ///     MAD each have a ~50% breakdown point, so a single JIT spike or one-off preemption
    ///     cannot distort the metric the way stddev/mean can. A quiet dedicated host reports
    ///     well below 0.05; a shared-tenant CI runner typically reports 0.10-0.30.
    /// </remarks>
    public static double? Run(int sampleCount, int workPerSample, IClock clock)
    {
        if (sampleCount < 2 || workPerSample < 1)
            return null;

        // Warm up the probe path before timing: the first call to BusyWeight pays one-off JIT
        // compilation for BusyWeight, Run, and the clock methods, and the first cache miss for
        // the code. Without this warmup those costs land inside the first timed sample and -
        // because they are 10-100x the steady-state timing - inflate a stddev-based metric to
        // ~1.0+ and trigger spurious detector switches. This mirrors the ops-per-sample
        // calibrator's approach (AcquireSample several times, feed the fastest).
        var warmAcc = BusyWeight(workPerSample);
        Volatile.Write(ref Accumulator, warmAcc);

        var samples = new double[sampleCount];
        var accumulator = 0L;

        for (var i = 0; i < sampleCount; i++)
        {
            var start = clock.GetTimestamp();
            accumulator += BusyWeight(workPerSample);
            samples[i] = clock.GetElapsedNanoseconds(start);
        }

        // Escaped so the JIT cannot elide the loop. Volatile write is enough; the value is
        // informational, not a synchronization primitive.
        Volatile.Write(ref Accumulator, accumulator);

        return RobustJitterMetric(samples);
    }

    /// <summary>Escaped accumulator state, so the busy-weight loop is not dead-code-eliminated.</summary>
    public static long Accumulator;

    /// <summary>
    ///     A deterministic, allocation-free CPU-bound loop. The multiply-accumulate pattern
    ///     keeps the dependency chain short so the loop is throughput-bound rather than
    ///     latency-bound, making it sensitive to scheduling preemption rather than to
    ///     microarchitectural quirks.
    /// </summary>
    private static long BusyWeight(int iterations)
    {
        long acc = 1;

        for (var i = 0; i < iterations; i++)
        {
            // 0x9E3779B97F4A7C15 is the golden-ratio constant; keeps the accumulator drifting
            // without overflowing to zero, and the multiply-add is a single instruction on
            // most modern CPUs.
            acc = unchecked(acc * (long)0x9E3779B97F4A7C15UL + i);
        }

        return acc;
    }

    /// <summary>
    ///     Computes a robust jitter metric: the ratio of the median absolute deviation to the
    ///     median (<c>MAD / median</c>). Both the median and MAD have a ~50% breakdown point,
    ///     so a single JIT spike or one-off preemption cannot distort the metric the way
    ///     stddev/mean can. Returns <c>null</c> when the median is zero or any sample is
    ///     non-finite.
    /// </summary>
    private static double? RobustJitterMetric(double[] samples)
    {
        var n = samples.Length;

        for (var i = 0; i < n; i++)
        {
            if (!double.IsFinite(samples[i]))
                return null;
        }

        Array.Sort(samples);

        var median = Median(samples);

        if (median <= 0)
            return null;

        // MAD = median(|x_i - median|). Compute on a second sorted array of absolute deviations.
        var absDeviations = new double[n];

        for (var i = 0; i < n; i++)
        {
            absDeviations[i] = Math.Abs(samples[i] - median);
        }

        Array.Sort(absDeviations);

        var mad = Median(absDeviations);

        return mad / median;
    }

    /// <summary>
    ///     Computes the median of a pre-sorted array. For even-length arrays, returns the
    ///     average of the two middle elements. The same routine is used for both the central
    ///     median and the MAD, so the metric is internally consistent.
    /// </summary>
    private static double Median(double[] sorted)
    {
        var n = sorted.Length;
        var mid = n / 2;

        if ((n & 1) == 1)
            return sorted[mid];

        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}