using NBenchmark.Engine;
using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class ClockResolutionProbeTests
{
    /// <summary>
    ///     A clock that advances in fixed steps, like real hardware counters do. Each
    ///     <see cref="GetElapsedNanoseconds" /> call costs one "read"; the reported elapsed time stays
    ///     at zero until enough reads have accumulated to cross a step boundary, then jumps a whole
    ///     step. This is the behaviour the probe exists to detect.
    /// </summary>
    private sealed class SteppedClock(double stepNs, int readsPerStep) : IClock
    {
        private int _reads;

        public long GetTimestamp()
        {
            _reads = 0;
            return 1;
        }

        public TimeSpan GetElapsedTime(long startTimestamp)
            => TimeSpan.FromTicks((long)(GetElapsedNanoseconds(startTimestamp) / 100.0));

        public double GetElapsedNanoseconds(long startTimestamp)
        {
            _reads++;
            return _reads / Math.Max(1, readsPerStep) * stepNs;
        }
    }

    /// <summary>A clock that never advances - the degenerate case the probe must not hang on.</summary>
    private sealed class FrozenClock : IClock
    {
        public long GetTimestamp() => 1;

        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.Zero;

        public double GetElapsedNanoseconds(long startTimestamp) => 0.0;
    }

    private sealed class NonFiniteClock : IClock
    {
        public long GetTimestamp() => 1;

        public TimeSpan GetElapsedTime(long startTimestamp) => TimeSpan.Zero;

        public double GetElapsedNanoseconds(long startTimestamp) => double.NaN;
    }

    [Theory]
    [InlineData(41.6667, 3)]
    [InlineData(100.0, 5)]
    [InlineData(1.0, 1)]
    public void Measure_ReportsTheStepSize_NotTheAdvertisedResolution(double stepNs, int readsPerStep)
    {
        var resolution = ClockResolutionProbe.Measure(new SteppedClock(stepNs, readsPerStep), attempts: 8);

        Assert.Equal(stepNs, resolution, precision: 6);
    }

    [Fact]
    public void Measure_FrozenClock_ReturnsZeroRatherThanHanging()
        => Assert.Equal(0.0, ClockResolutionProbe.Measure(new FrozenClock(), attempts: 4));

    [Fact]
    public void Measure_NonFiniteClock_ReturnsZero()
        => Assert.Equal(0.0, ClockResolutionProbe.Measure(new NonFiniteClock(), attempts: 4));

    /// <summary>
    ///     The regression guard for the defect this probe introduced on first wiring: probing calls
    ///     <see cref="IClock.GetTimestamp" /> in a loop, and an injected clock generally serves a finite
    ///     scripted sequence, so probing one consumes the readings a test scheduled for the measurement
    ///     itself. Every injected-clock test in this assembly failed with empty sample arrays until the
    ///     probe learned to report a non-real-time clock as unknown.
    /// </summary>
    [Fact]
    public void ResolutionNs_InjectedClock_ReportsUnknownWithoutConsumingIt()
    {
        var fake = new FakeClock([TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)]);

        Assert.Equal(0.0, ClockResolutionProbe.ResolutionNs(StopwatchClock.Wrap(fake)));
        Assert.Equal(0.0, ClockResolutionProbe.ResolutionNs(fake));

        // Nothing was drained: the scheduled readings are still there for the measurement.
        Assert.Equal(2, fake.PendingElapsedCount);
    }

    [Fact]
    public void ResolutionNs_WallClock_ReportsAPositiveResolution()
    {
        var resolution = ClockResolutionProbe.ResolutionNs(StopwatchClock.WallClock);

        Assert.True(resolution > 0, $"expected a positive measured resolution, got {resolution}");

        // Sanity bound rather than an exact value: this is real hardware and varies by host. Anything
        // past a microsecond would mean the probe measured something other than the counter step.
        Assert.True(resolution < 1_000, $"implausible clock resolution: {resolution} ns");
    }

    [Fact]
    public void ResolveTargetSampleDurationNs_RaisesTargetToClearTheResolutionFloor()
    {
        // Apple Silicon: 41.667 ns steps, 512 required -> ~21.3 µs, above the configured 10 µs.
        var resolved = ClockResolutionProbe.ResolveTargetSampleDurationNs(10_000, 41.6667, 512);

        Assert.Equal(41.6667 * 512, resolved, precision: 3);
        Assert.True(resolved > 10_000);
    }

    [Fact]
    public void ResolveTargetSampleDurationNs_LeavesAFineClockAlone()
    {
        // A 1 ns clock clears 512 steps in 512 ns, well inside the configured target.
        var resolved = ClockResolutionProbe.ResolveTargetSampleDurationNs(10_000, 1.0, 512);

        Assert.Equal(10_000, resolved);
    }

    [Fact]
    public void ResolveTargetSampleDurationNs_NeverLowersTheConfiguredTarget()
    {
        // The Thorough preset asks for 50 µs; a coarse clock's floor of ~21 µs must not pull it down.
        var resolved = ClockResolutionProbe.ResolveTargetSampleDurationNs(50_000, 41.6667, 512);

        Assert.Equal(50_000, resolved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveTargetSampleDurationNs_DisabledFloor_KeepsConfiguredTarget(int minQuanta)
        => Assert.Equal(10_000, ClockResolutionProbe.ResolveTargetSampleDurationNs(10_000, 41.6667, minQuanta));

    [Fact]
    public void ResolveTargetSampleDurationNs_UnknownResolution_KeepsConfiguredTarget()
        => Assert.Equal(10_000, ClockResolutionProbe.ResolveTargetSampleDurationNs(10_000, 0, 512));

    [Fact]
    public void QuantizationFraction_IsOneStepOverTheSampleDuration()
    {
        // The measured configuration behind the run-to-run drift this probe was added for: a 2.53 ns
        // body at K = 4096 spans ~10.4 µs, or ~250 steps of a 41.667 ns clock - so one step is 0.4% of
        // the sample, against a reported error margin of ~0.03%.
        var fraction = ClockResolutionProbe.QuantizationFraction(41.6667, 10_375);

        Assert.Equal(0.004016, fraction, precision: 5);
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(41.6667, 0)]
    [InlineData(double.NaN, 10_000)]
    [InlineData(41.6667, double.PositiveInfinity)]
    public void QuantizationFraction_UnusableInputs_ReturnZero(double resolutionNs, double sampleDurationNs)
        => Assert.Equal(0.0, ClockResolutionProbe.QuantizationFraction(resolutionNs, sampleDurationNs));
}
