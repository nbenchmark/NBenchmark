using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     A ratio may only be formed between two results measured the same way.
/// </summary>
/// <remarks>
///     <para>
///         The runtime configuration a process is launched with is the dominant term in a
///         nanosecond-scale measurement - worth roughly 3.3x on bodies of provably identical cost.
///         Dividing an in-process median by an isolated one therefore reports that configuration
///         difference under the name of a speedup, with a confidence interval on each side.
///     </para>
///     <para>
///         Significance testing has always partitioned on this key. The ratio column did not, and
///         the default <c>samples/Harness</c> run printed <c>0.38x</c> next to its one
///         <c>[Isolation(Isolation.Off)]</c> benchmark - a fabricated 2.6x "win" that was purely the profile.
///     </para>
/// </remarks>
public class MixedIsolationTableTests
{
    [Fact]
    public void Ratio_Is_Withheld_From_A_Row_Not_Measured_Like_The_Baseline()
    {
        var table = BenchmarkTable.Build([
            Isolated("Baseline", 100, isBaseline: true),
            Isolated("Candidate", 200),
            InProcess("InHarness", 40),
        ]);

        var candidate = table.Rows.Single(r => r.Result.Name == "Candidate");
        Assert.Equal(2.0, candidate.Ratio, 3);
        Assert.False(candidate.RatioSuppressed);

        var inProcess = table.Rows.Single(r => r.Result.Name == "InHarness");
        Assert.True(double.IsNaN(inProcess.Ratio));
        Assert.True(inProcess.RatioSuppressed);
    }

    [Fact]
    public void Ratios_Are_Kept_When_Every_Row_Shares_The_Configuration()
    {
        var table = BenchmarkTable.Build([
            Isolated("Baseline", 100, isBaseline: true),
            Isolated("Candidate", 250),
        ]);

        Assert.All(table.Rows, r => Assert.False(r.RatioSuppressed));
        Assert.Equal(2.5, table.Rows.Single(r => r.Result.Name == "Candidate").Ratio, 3);
        Assert.False(table.MixedIsolationStatuses);
    }

    /// <summary>
    ///     Without an explicit baseline the fastest row wins - but the fastest row in a mixed table
    ///     is usually the in-process one, because it inherits whatever tiering state the host had.
    ///     Letting it take the baseline would withhold every remaining ratio and leave the table
    ///     with no comparison at all, which is a worse answer than the one the user asked for.
    /// </summary>
    [Fact]
    public void Implicit_Baseline_Comes_From_The_Largest_Comparable_Group()
    {
        var table = BenchmarkTable.Build([
            Isolated("A", 100),
            Isolated("B", 200),
            InProcess("Fastest", 10),
        ]);

        // An implicit baseline is not flagged on the row (only a declared one is), so it is read
        // off the ratios: A is the reference at 1.00x. Had the in-process row been picked instead,
        // A would read 10.00x and both isolated rows would have lost their ratio entirely.
        Assert.Equal(1.0, table.Rows.Single(r => r.Result.Name == "A").Ratio, 3);
        Assert.Equal(2.0, table.Rows.Single(r => r.Result.Name == "B").Ratio, 3);
        Assert.True(table.Rows.Single(r => r.Result.Name == "Fastest").RatioSuppressed);
    }

    [Fact]
    public void Mixed_Statuses_Are_Flagged_So_Reporters_Can_Add_A_Status_Column()
    {
        var mixed = BenchmarkTable.Build([Isolated("A", 100, isBaseline: true), InProcess("B", 100)]);
        var uniform = BenchmarkTable.Build([Isolated("A", 100, isBaseline: true), Isolated("B", 100)]);

        Assert.True(mixed.MixedIsolationStatuses);
        Assert.False(uniform.MixedIsolationStatuses);

        Assert.Equal(IsolationStatus.InProcessRequested, mixed.Rows.Single(r => r.Result.Name == "B").Result.IsolationStatus);
        Assert.Equal(IsolationStatus.Isolated, mixed.Rows.Single(r => r.Result.Name == "A").Result.IsolationStatus);
    }

    /// <summary>
    ///     Two rows refused isolation for different reasons still ran in the same host process under
    ///     the same configuration, so comparing them is legitimate. Suppressing on the status label
    ///     rather than on the configuration would withhold a ratio that is perfectly sound.
    /// </summary>
    [Fact]
    public void Two_In_Process_Rows_Are_Still_Comparable_With_Each_Other()
    {
        var table = BenchmarkTable.Build([
            InProcess("Requested", 100, IsolationStatus.InProcessRequested, isBaseline: true),
            InProcess("Fixture", 300, IsolationStatus.InProcessLiveFixture),
        ]);

        var fixtureRow = table.Rows.Single(r => r.Result.Name == "Fixture");

        Assert.False(fixtureRow.RatioSuppressed);
        Assert.Equal(3.0, fixtureRow.Ratio, 3);
    }

    private static BenchmarkResult Isolated(string name, double median, bool isBaseline = false)
        => Make(name, median, IsolationStatus.Isolated, RuntimeProfile.SteadyState.Name, isBaseline);

    private static BenchmarkResult InProcess(
        string name,
        double median,
        IsolationStatus status = IsolationStatus.InProcessRequested,
        bool isBaseline = false)
        => Make(name, median, status, RuntimeProfile.Host.Name, isBaseline);

    private static BenchmarkResult Make(
        string name,
        double median,
        IsolationStatus status,
        string profileName,
        bool isBaseline) => new()
    {
        Name = name,
        MeanNs = median,
        MedianNs = median,
        Percentiles = [],
        MinNs = median,
        MaxNs = median,
        StandardDeviationNs = 0,
        SampleCount = 10,
        WarmupSamples = 1,
        RunAtUtc = DateTimeOffset.UtcNow,
        IsBaseline = isBaseline,
        IsolationStatus = status,
        RuntimeProfileName = profileName,
        Q1Ns = 0,
        Q3Ns = 0,
        InterquartileRangeNs = 0,
        OutliersRemoved = 0,
        Skewness = 0,
        Kurtosis = 0,
        MedianAbsoluteDeviationNs = 0,
        AllocatedBytesMedian = null,
        AllocatedBytesP95 = null,
        AllocatedBytesMax = null,
    };
}
