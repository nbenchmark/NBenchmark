using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The two ceilings a worker runs under, and why they are different numbers.
/// </summary>
public class MeasurementBudgetTests
{
    /// <summary>
    ///     The point of the idle timeout. The group ceiling grows with the benchmark count, so on a
    ///     large group it is useless as a wedge detector - it would let a hung worker sit there for
    ///     the better part of an hour. The idle timeout does not grow, because a silent worker means
    ///     the same thing whether it had one benchmark left or fifty.
    /// </summary>
    [Fact]
    public void The_Idle_Timeout_Does_Not_Grow_With_The_Benchmark_Count()
    {
        var options = MeasurementOptions.Default;

        var idle = MeasurementBudget.IdleFrame(options);
        var oneBenchmark = MeasurementBudget.For(options, 1);
        var fiftyBenchmarks = MeasurementBudget.For(options, 50);

        Assert.Equal(idle, MeasurementBudget.IdleFrame(options));
        Assert.True(fiftyBenchmarks > oneBenchmark);
        Assert.True(idle < fiftyBenchmarks);
    }

    /// <summary>
    ///     A single benchmark may legitimately produce no frames for a whole tuning budget - the
    ///     engine reports a sample only when one finishes, so a body with one long iteration is silent
    ///     throughout. The idle timeout has to clear that, or it kills the slow-but-honest benchmarks
    ///     the engine explicitly permits.
    /// </summary>
    [Fact]
    public void The_Idle_Timeout_Clears_One_Full_Benchmark_Of_Legitimate_Silence()
    {
        var options = MeasurementOptions.Default;

        Assert.True(MeasurementBudget.IdleFrame(options) > MeasurementBudget.PerBenchmark(options));
    }

    [Fact]
    public void The_Idle_Timeout_Scales_With_The_Tuning_Budget()
    {
        var brisk = new MeasurementOptions
        {
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromSeconds(5) },
        };

        var patient = new MeasurementOptions
        {
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromMinutes(5) },
        };

        Assert.True(MeasurementBudget.IdleFrame(patient) > MeasurementBudget.IdleFrame(brisk));
    }

    [Fact]
    public void Both_Ceilings_Respect_The_Floor_And_The_Cap()
    {
        var tiny = new MeasurementOptions
        {
            AutoTune = AutoTuneOptions.Default with
            {
                MaxTuningTime = TimeSpan.FromMilliseconds(1),
                MinWarmupTime = TimeSpan.Zero,
            },
        };

        Assert.Equal(MeasurementBudget.MinTimeout, MeasurementBudget.IdleFrame(tiny));
        Assert.Equal(MeasurementBudget.MinTimeout, MeasurementBudget.For(tiny, 1));

        var huge = new MeasurementOptions
        {
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromHours(10) },
        };

        Assert.Equal(MeasurementBudget.MaxTimeout, MeasurementBudget.IdleFrame(huge));
        Assert.Equal(MeasurementBudget.MaxTimeout, MeasurementBudget.For(huge, 1));
    }
}
