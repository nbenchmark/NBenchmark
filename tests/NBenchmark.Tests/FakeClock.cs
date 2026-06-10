using NBenchmark.Engine;

namespace NBenchmark.Tests;

internal sealed class FakeClock : IClock
{
    private readonly Dictionary<long, TimeSpan> _elapsedByTimestamp = [];
    private readonly Queue<TimeSpan> _scheduledElapsed;
    private long _nextTimestamp = 1;

    public FakeClock(IEnumerable<TimeSpan> scheduledElapsed)
    {
        _scheduledElapsed = new Queue<TimeSpan>(scheduledElapsed);
    }

    public int PendingElapsedCount => _scheduledElapsed.Count;

    public long GetTimestamp()
    {
        if (_scheduledElapsed.Count == 0)
            throw new InvalidOperationException("FakeClock has no scheduled elapsed values remaining.");

        var timestamp = _nextTimestamp++;
        _elapsedByTimestamp[timestamp] = _scheduledElapsed.Dequeue();
        return timestamp;
    }

    public TimeSpan GetElapsedTime(long startTimestamp)
    {
        if (_elapsedByTimestamp.TryGetValue(startTimestamp, out var elapsed))
            return elapsed;

        throw new InvalidOperationException($"FakeClock received unknown timestamp '{startTimestamp}'.");
    }
}
