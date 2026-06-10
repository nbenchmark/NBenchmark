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
