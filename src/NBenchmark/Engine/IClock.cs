namespace NBenchmark.Engine;

/// <summary>
///     Abstraction for monotonic benchmark timing. Enables deterministic tests
///     without relying on wall-clock behavior.
/// </summary>
internal interface IClock
{
    public long GetTimestamp();

    public TimeSpan GetElapsedTime(long startTimestamp);

    /// <summary>
    ///     Elapsed nanoseconds since <paramref name="startTimestamp" /> at the clock's
    ///     full native resolution. The default implementation round-trips through
    ///     <see cref="GetElapsedTime" /> (100 ns ticks); real clocks should override it
    ///     to avoid quantizing sub-100 ns measurements.
    /// </summary>
    public double GetElapsedNanoseconds(long startTimestamp)
        => GetElapsedTime(startTimestamp).Ticks * 100.0;
}
