using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NBenchmark.Engine;

/// <summary>
///     The concrete clock used on the per-iteration hot path. The runner's
///     private field is typed as <see cref="StopwatchClock" /> (not
///     <see cref="IClock" />) so the JIT devirtualizes <see cref="GetTimestamp" />
///     and <see cref="GetElapsedNanoseconds" /> on every measured iteration. The
///     class also implements <see cref="IClock" /> so the public constructor
///     of <c>BenchmarkRunner</c> can pass <see cref="WallClock" /> into the
///     internal seam that takes an <see cref="IClock" />.
/// </summary>
internal sealed class StopwatchClock : IClock
{
    /// <summary>
    ///     Nanoseconds per raw <see cref="Stopwatch" /> tick. On Windows the counter
    ///     typically runs at 10 MHz (100 ns per tick); on macOS/Linux at 1 GHz
    ///     (1 ns per tick). Converting raw tick deltas directly - instead of through
    ///     <see cref="TimeSpan" />, whose ticks are always 100 ns - preserves the
    ///     platform's full timer resolution for per-iteration measurements.
    /// </summary>
    private static readonly double NanosecondsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    private readonly IClock? _inner;

    private StopwatchClock()
    {
    }

    private StopwatchClock(IClock inner)
    {
        _inner = inner;
    }

    /// <summary>The default real-time clock; used by <see cref="BenchmarkRunner.Instance" />.</summary>
    public static StopwatchClock WallClock { get; } = new();

    /// <summary>
    ///     Whether this instance reads the real hardware counter rather than delegating to a wrapped
    ///     <see cref="IClock" /> (in practice a test fake).
    ///     <para>
    ///         Consulted by <see cref="Detectors.ClockResolutionProbe" />, which must not probe an
    ///         injected clock. Probing means calling <see cref="GetTimestamp" /> repeatedly, and a fake
    ///         typically serves a finite scripted sequence - so a probe would consume the readings the
    ///         test scheduled for the measurement itself. A fake also has no meaningful "resolution" to
    ///         report: its timings are whatever the test chose.
    ///     </para>
    /// </summary>
    internal bool IsRealTime => _inner is null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetTimestamp()
        => _inner?.GetTimestamp() ?? Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TimeSpan GetElapsedTime(long startTimestamp)
        => _inner?.GetElapsedTime(startTimestamp) ?? Stopwatch.GetElapsedTime(startTimestamp);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetElapsedNanoseconds(long startTimestamp)
        => _inner is not null
            ? _inner.GetElapsedNanoseconds(startTimestamp)
            : (Stopwatch.GetTimestamp() - startTimestamp) * NanosecondsPerTick;

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
}
