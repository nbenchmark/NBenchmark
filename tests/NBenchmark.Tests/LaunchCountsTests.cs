using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Where the launch count's bounds are enforced, now that it is not a field on
///     <see cref="MeasurementOptions" />.
/// </summary>
/// <remarks>
///     <para>
///         It used to be one, and the record's <c>init</c> accessor validated every path into it by
///         construction. Moving the count onto the coordinators that spend it moved the enforcement
///         point too: each entry has to reject or clamp for itself, and a missed one would silently
///         accept a zero (measure nothing) or a five-figure count (launch that many processes). So the
///         entries are pinned here rather than left to whichever integration test happens to pass a
///         valid number.
///     </para>
///     <para>
///         The split is deliberate. A fluent builder and the CLI parser can report the mistake to the
///         caller, so they reject. An attribute argument is a compile-time constant and the only report
///         available is failing the test or benchmark with a configuration error instead of measuring
///         it, so those clamp.
///     </para>
/// </remarks>
public class LaunchCountsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void IsValid_Accepts_Exactly_One_Through_The_Maximum(int count, bool expected)
        => Assert.Equal(expected, LaunchCounts.IsValid(count));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-7, 1)]
    [InlineData(3, 3)]
    [InlineData(10_000, 100)]
    public void Clamp_Brings_A_Value_Into_Range(int count, int expected)
        => Assert.Equal(expected, LaunchCounts.Clamp(count));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Suite_WithLaunchCount_Rejects_An_Out_Of_Range_Count(int count)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BenchmarkSuite("bounds").WithLaunchCount(count));

        Assert.Contains("between 1 and 100", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Harness_WithLaunchCount_Rejects_An_Out_Of_Range_Count(int count)
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => harness.WithLaunchCount(count));

        Assert.Contains("between 1 and 100", ex.Message);
    }

    /// <summary>
    ///     The flag reports the range rather than silently taking the nearest legal value: a command
    ///     line is typed, so the typo is worth naming.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("three")]
    public void LaunchCount_Flag_Rejects_An_Out_Of_Range_Value(string value)
    {
        var (args, errors) = CliArgs.ParseCore(["--launch-count", value]);

        Assert.Null(args.LaunchCount);
        Assert.Contains(errors, e => e.Contains("--launch-count"));
    }

    [Fact]
    public void LaunchCount_Flag_Accepts_The_Boundaries()
    {
        Assert.Equal(1, CliArgs.ParseCore(["--launch-count", "1"]).Args.LaunchCount);
        Assert.Equal(LaunchCounts.Max, CliArgs.ParseCore(["--launch-count", "100"]).Args.LaunchCount);
    }
}
