namespace NBenchmark.Engine.Detectors;

/// <summary>
///     Records the warmup curve - the per-op mean of each warmup batch, in order - in a fixed amount
///     of memory regardless of how long warmup runs.
///     <para>
///         This is the shape of tiered compilation landing. A body starts in tier-0 (or quick-jitted)
///         code, the runtime promotes it after the call-counting delay, dynamic PGO may then
///         instrument and re-optimise it, and each transition shows up as a step down in per-op time.
///         The engine already computes a batch mean per warmup batch for its plateau rule and throws
///         each one away; retaining them costs nothing and is the only record of that decay, since raw
///         warmup timings are never persisted.
///     </para>
///     <para>
///         Batch means are deliberately preferred over raw warmup samples: they are already computed,
///         and the averaging suppresses the per-sample jitter that would otherwise bury a
///         two-or-three-step decay curve.
///     </para>
///     <para>
///         Warmup can span tens of thousands of samples on a fast body, so once the buffer fills every
///         other retained point is dropped and the stride doubles. Retained points are always exact
///         multiples of the current stride, so they stay evenly spaced and the curve keeps its shape at
///         progressively coarser resolution. <see cref="SampleInterval" /> reports the spacing so a
///         caller can label a real x-axis.
///     </para>
/// </summary>
internal sealed class WarmupCurveRecorder
{
    /// <summary>
    ///     Retained points. 512 renders a smooth decay curve at any sensible chart width and costs
    ///     4 KiB per benchmark.
    /// </summary>
    internal const int Capacity = 512;

    private readonly double[] _values = new double[Capacity];
    private readonly int _batchSize;

    /// <summary>Batches between retained points. Doubles on each decimation pass.</summary>
    private int _stride = 1;

    /// <summary>Batches recorded so far, retained or not.</summary>
    private long _seen;

    private int _count;

    public WarmupCurveRecorder(int batchSize)
    {
        _batchSize = Math.Max(1, batchSize);
    }

    /// <summary>Warmup samples between consecutive retained points.</summary>
    public int SampleInterval => _batchSize * _stride;

    /// <summary>Records one completed batch's mean per-op nanoseconds.</summary>
    public void Add(double batchMeanPerOpNs)
    {
        if (_seen % _stride == 0)
        {
            if (_count == Capacity)
            {
                Decimate();

                // Decimation doubles the stride, so this batch may no longer sit on a boundary.
                if (_seen % _stride != 0)
                {
                    _seen++;
                    return;
                }
            }

            _values[_count++] = batchMeanPerOpNs;
        }

        _seen++;
    }

    /// <summary>
    ///     Keeps every other retained point and doubles the stride. Retained points were multiples of
    ///     the old stride, so keeping the even-indexed ones leaves multiples of the new one.
    /// </summary>
    private void Decimate()
    {
        for (var i = 0; i < Capacity / 2; i++)
            _values[i] = _values[i * 2];

        _count = Capacity / 2;
        _stride *= 2;
    }

    /// <summary>The recorded curve, oldest first. Empty when no batch completed.</summary>
    public double[] ToArray()
    {
        if (_count == 0)
            return [];

        var result = new double[_count];
        Array.Copy(_values, result, _count);
        return result;
    }
}
