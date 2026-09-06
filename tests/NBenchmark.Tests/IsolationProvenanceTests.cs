using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Whether a result's <see cref="IsolationStatus" /> can be believed.
/// </summary>
/// <remarks>
///     Every gate, column and footer keys on this stamp, so a stamp that is wrong is worse than one
///     that is missing - a reader has no way to tell the two apart. These pin the cases where it used
///     to be wrong: a row that was never measured claiming to have run in the host, a table
///     suppressing the column for exactly the run that needed it, and the classifier that tells "you
///     asked for the host" apart from "you asked for a worker and did not get one".
/// </remarks>
public sealed class IsolationProvenanceTests
{
    private static BenchmarkResult Result(string name, IsolationStatus status, bool errored = false) =>
        new()
        {
            Name = name,
            MeanNs = 10,
            MedianNs = 10,
            Percentiles = [],
            MinNs = 10,
            MaxNs = 10,
            StandardDeviationNs = 0,
            Q1Ns = 10,
            Q3Ns = 10,
            InterquartileRangeNs = 0,
            OutliersRemoved = 0,
            SampleCount = 30,
            Skewness = 0,
            Kurtosis = 0,
            MedianAbsoluteDeviationNs = 0,
            AllocatedBytesMedian = null,
            AllocatedBytesP95 = null,
            AllocatedBytesMax = null,
            Errored = errored,
            ErrorMessage = errored ? "boom" : null,
            IsolationStatus = status,
        };

    /// <summary>
    ///     The four refusals are the cases the user asked for isolation and did not get it. The two
    ///     non-refusals are the user getting what they asked for.
    /// </summary>
    [Theory]
    [InlineData(IsolationStatus.InProcessCapturedState, true)]
    [InlineData(IsolationStatus.InProcessLiveFixture, true)]
    [InlineData(IsolationStatus.InProcessUnaddressablePlan, true)]
    [InlineData(IsolationStatus.InProcessNoWorker, true)]
    [InlineData(IsolationStatus.InProcessRequested, false)]
    [InlineData(IsolationStatus.Isolated, false)]
    public void IsRefusal_Separates_Refusals_From_Deliberate_Choices(IsolationStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsRefusal());
    }

    /// <summary>
    ///     Every status with a remedy is a refusal, and vice versa. The two answer the same question -
    ///     "is there something for the user to act on" - so they must not drift apart.
    /// </summary>
    [Fact]
    public void IsRefusal_Agrees_With_ToRemedy()
    {
        foreach (var status in Enum.GetValues<IsolationStatus>())
        {
            Assert.Equal(status.IsRefusal(), status.ToRemedy() is not null);
        }
    }

    /// <summary>
    ///     A benchmark that threw was not measured in this process - it was not measured anywhere - so
    ///     <c>--strict-isolation</c> must not tell the user its numbers carry the host's configuration.
    ///     There are no numbers.
    /// </summary>
    [Fact]
    public void Enforce_Ignores_Errored_Rows()
    {
        using var error = new StringWriter();

        var passed = IsolationAudit.Enforce(
            [Result("ok", IsolationStatus.Isolated), Result("broken", IsolationStatus.InProcessRequested, errored: true)],
            error);

        Assert.True(passed);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Enforce_Still_Fails_On_A_Real_Offender()
    {
        using var error = new StringWriter();

        var passed = IsolationAudit.Enforce(
            [Result("ok", IsolationStatus.Isolated), Result("host", IsolationStatus.InProcessCapturedState)],
            error);

        Assert.False(passed);
        Assert.Contains("host", error.ToString());
    }

    /// <summary>
    ///     The denominator counts the same population as the numerator. "1 of 3" while excluding the
    ///     errored row from the 1 describes two different sets in one sentence.
    /// </summary>
    [Fact]
    public void Enforce_Counts_Only_Measured_Benchmarks()
    {
        using var error = new StringWriter();

        IsolationAudit.Enforce(
            [
                Result("a", IsolationStatus.Isolated),
                Result("b", IsolationStatus.InProcessCapturedState),
                Result("c", IsolationStatus.InProcessRequested, errored: true),
            ],
            error);

        Assert.Contains("1 of 2", error.ToString());
    }

    /// <summary>
    ///     The case the column existed for and did not cover: a table where <i>nothing</i> was
    ///     isolated has one distinct status, so the old mixed-statuses rule suppressed it.
    /// </summary>
    [Fact]
    public void A_Uniformly_Refused_Table_Shows_The_Isolation_Column()
    {
        var table = BenchmarkTable.Build(
            [
                Result("a", IsolationStatus.InProcessCapturedState),
                Result("b", IsolationStatus.InProcessCapturedState),
            ]);

        Assert.True(table.MixedIsolationStatuses);
    }

    /// <summary>
    ///     A deliberately in-process run does not, though. Every row reading "no" says nothing the
    ///     footer does not, and reporters trade this column against the bar column.
    /// </summary>
    [Fact]
    public void A_Deliberately_InProcess_Table_Does_Not()
    {
        var table = BenchmarkTable.Build(
            [
                Result("a", IsolationStatus.InProcessRequested),
                Result("b", IsolationStatus.InProcessRequested),
            ]);

        Assert.False(table.MixedIsolationStatuses);
    }

    /// <summary>
    ///     One errored row in an otherwise isolated run used to flip the flag - adding a spurious
    ///     column and, because reporters trade the two, removing the bar column.
    /// </summary>
    [Fact]
    public void An_Errored_Row_Does_Not_Flip_The_Isolation_Column()
    {
        var table = BenchmarkTable.Build(
            [
                Result("a", IsolationStatus.Isolated),
                Result("b", IsolationStatus.Isolated),
                Result("broken", IsolationStatus.InProcessRequested, errored: true),
            ]);

        Assert.False(table.MixedIsolationStatuses);
        Assert.Empty(table.InProcessReasons);
    }
}
