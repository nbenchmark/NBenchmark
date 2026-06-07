using System.Diagnostics;

namespace NBenchmark.Engine;

/// <summary>
///     Abstraction for monotonic benchmark timing. Enables deterministic tests
///     without relying on wall-clock behavior.
/// </summary>
internal interface IClock
{
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startTimestamp);
}

internal sealed class StopwatchClock : IClock
{
    public static StopwatchClock Instance { get; } = new();

    private StopwatchClock() { }

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
}
