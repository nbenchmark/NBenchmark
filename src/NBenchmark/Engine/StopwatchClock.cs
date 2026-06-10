using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NBenchmark.Engine;

/// <summary>
///     The concrete clock used on the per-iteration hot path. The runner's
///     private field is typed as <see cref="StopwatchClock" /> (not
///     <see cref="IClock" />) so the JIT devirtualizes <see cref="GetTimestamp" />
///     and <see cref="GetElapsedTime" /> on every measured iteration. The
///     class also implements <see cref="IClock" /> so the public constructor
///     of <c>BenchmarkRunner</c> can pass <see cref="WallClock" /> into the
///     internal seam that takes an <see cref="IClock" />.
/// </summary>
internal sealed class StopwatchClock : IClock
{
    /// <summary>The default real-time clock; used by <see cref="BenchmarkRunner.Instance" />.</summary>
    public static StopwatchClock WallClock { get; } = new();

    private readonly IClock? _inner;

    private StopwatchClock()
    {
    }

    private StopwatchClock(IClock inner)
    {
        _inner = inner;
    }

    /// <summary>
    ///     Wraps an arbitrary <see cref="IClock" /> (typically <c>FakeClock</c> from
    ///     the test assembly) in a concrete <see cref="StopwatchClock" /> so the
    ///     hot path stays on the concrete type. Wall-clock calls remain direct
    ///     <see cref="Stopwatch" /> reads; wrapped clocks pay interface dispatch.
    /// </summary>
    internal static StopwatchClock Wrap(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock as StopwatchClock ?? new StopwatchClock(clock);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetTimestamp()
        => _inner?.GetTimestamp() ?? Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan GetElapsedTime(long startTimestamp)
        => _inner?.GetElapsedTime(startTimestamp) ?? Stopwatch.GetElapsedTime(startTimestamp);
}
