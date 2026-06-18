using NBenchmark.Engine;

namespace NBenchmark.Tests;

/// <summary>
///     A lenient, fully deterministic <see cref="IClock" /> for tests that need to drive the
///     adaptive loop with scripted per-sample timings. Each timed sample reads a scripted
///     nanosecond value by call index, and the clock never throws on exhaustion.
///     Elapsed-time queries return a tick count derived from the timestamp counter,
///     so measured/total durations stay monotonic without constraining the sample script.
/// </summary>
internal sealed class ScriptedClock : IClock
{
    private readonly Func<int, double> _sampleNs;
    private int _nsCall;
    private long _timestamp;

    public ScriptedClock(double constantNs)
    {
        _sampleNs = _ => constantNs;
    }

    public ScriptedClock(Func<int, double> sampleNs)
    {
        _sampleNs = sampleNs;
    }

    public long GetTimestamp() => ++_timestamp;

    public TimeSpan GetElapsedTime(long startTimestamp)
        => TimeSpan.FromTicks(Math.Max(0, _timestamp - startTimestamp));

    public double GetElapsedNanoseconds(long startTimestamp) => _sampleNs(_nsCall++);
}
