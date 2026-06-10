namespace NBenchmark.Engine;

/// <summary>
///     Abstraction for monotonic benchmark timing. Enables deterministic tests
///     without relying on wall-clock behavior.
/// </summary>
internal interface IClock
{
    public long GetTimestamp();

    public TimeSpan GetElapsedTime(long startTimestamp);
}
