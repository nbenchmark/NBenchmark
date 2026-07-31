using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The two commands that make isolation checkable rather than claimed.
/// </summary>
public sealed class IsolationAuditTests
{
    private static BenchmarkResult Result(string name, IsolationStatus status, double median) =>
        new()
        {
            Name = name,
            Mean = median,
            Median = median,
            Min = median,
            Max = median,
            StandardDeviation = 0,
            Q1 = median,
            Q3 = median,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 30,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
            IsolationStatus = status,
        };

    /// <summary>
    ///     A cross-runtime run is refused with a reason rather than compared.
    /// </summary>
    /// <remarks>
    ///     <see cref="IsolationAudit.Render" /> keys the host side by name, and a moniker is the only
    ///     thing distinguishing multi-runtime rows - so comparing would put every runtime against the
    ///     same unlabelled host row and print a table that looks like a finding. The refusal has to name
    ///     the runtimes and the remedy, or it is just a message saying nothing happened.
    /// </remarks>
    [Fact]
    public void RefuseCrossRuntimeComparison_NamesTheRuntimesAndTheRemedy()
    {
        using var output = new StringWriter();

        IsolationAudit.RefuseCrossRuntimeComparison(["net8.0", "net9.0"], output);

        var text = output.ToString();

        Assert.Contains("net8.0", text);
        Assert.Contains("net9.0", text);
        Assert.Contains("--runtimes", text);
    }

    /// <summary>
    ///     The same refusal is reachable from <see cref="IsolationAudit.Render" /> itself, so no future
    ///     caller can produce the misleading table by handing it multi-runtime results directly.
    /// </summary>
    [Fact]
    public void Render_WithCrossRuntimeResults_RefusesInsteadOfComparing()
    {
        using var output = new StringWriter();

        var isolated = new[]
        {
            Result("Bench.Body", IsolationStatus.Isolated, 100) with { RuntimeMoniker = "net8.0" },
            Result("Bench.Body", IsolationStatus.Isolated, 80) with { RuntimeMoniker = "net10.0" },
        };

        IsolationAudit.Render(
            isolated,
            [Result("Bench.Body", IsolationStatus.InProcessRequested, 300)],
            output);

        var text = output.ToString();

        Assert.Contains("more than one runtime", text);
        Assert.DoesNotContain("Isolation verification", text);
    }

    /// <summary>An all-isolated run passes and says nothing.</summary>
    [Fact]
    public void Enforce_AllIsolated_Passes()
    {
        using var error = new StringWriter();

        var passed = IsolationAudit.Enforce(
            [Result("a", IsolationStatus.Isolated, 10), Result("b", IsolationStatus.Isolated, 20)],
            error);

        Assert.True(passed);
        Assert.Empty(error.ToString());
    }

    /// <summary>
    ///     A single un-isolated benchmark fails the whole run, and the message names it. A gate that
    ///     reports only a count leaves the user to hunt for which row it meant.
    /// </summary>
    [Fact]
    public void Enforce_AnyNotIsolated_FailsAndNamesTheBenchmark()
    {
        using var error = new StringWriter();

        var passed = IsolationAudit.Enforce(
            [
                Result("clean", IsolationStatus.Isolated, 10),
                Result("captured", IsolationStatus.InProcessCapturedState, 20),
            ],
            error);

        var message = error.ToString();

        Assert.False(passed);
        Assert.Contains("captured", message);
        Assert.Contains("1 of 2", message);

        // The remedy, not just the diagnosis.
        Assert.Contains(IsolationStatus.InProcessCapturedState.ToRemedy()!, message);

        // A passing benchmark must not be listed as an offender.
        Assert.DoesNotContain("clean", message);
    }

    /// <summary>
    ///     Offenders are grouped by reason. Twenty rows sharing one cause should print one remedy,
    ///     not twenty.
    /// </summary>
    [Fact]
    public void Enforce_GroupsOffendersByReason()
    {
        using var error = new StringWriter();

        IsolationAudit.Enforce(
            [
                Result("a", IsolationStatus.InProcessCapturedState, 10),
                Result("b", IsolationStatus.InProcessCapturedState, 10),
                Result("c", IsolationStatus.InProcessNoWorker, 10),
            ],
            error);

        var message = error.ToString();
        var remedy = IsolationStatus.InProcessCapturedState.ToRemedy()!;

        Assert.Equal(1, message.Split(remedy).Length - 1);
        Assert.Contains("a, b", message);
    }

    /// <summary>
    ///     The comparison reports a ratio per benchmark and leads with the worst, because the finding
    ///     is that host measurement is <i>unpredictable</i> - one row at 21x beside another at 1.0x
    ///     is the point, and an average would erase it.
    /// </summary>
    [Fact]
    public void Render_ReportsPerBenchmarkRatio_WorstFirst()
    {
        using var output = new StringWriter();

        IsolationAudit.Render(
            [Result("mild", IsolationStatus.Isolated, 100), Result("severe", IsolationStatus.Isolated, 100)],
            [Result("mild", IsolationStatus.InProcessRequested, 105), Result("severe", IsolationStatus.InProcessRequested, 2_100)],
            output);

        var text = output.ToString();

        Assert.Contains("21.00x", text);
        Assert.Contains("1.05x", text);

        // Worst first: the severe row must precede the mild one.
        Assert.True(
            text.IndexOf("severe", StringComparison.Ordinal) < text.IndexOf("mild", StringComparison.Ordinal),
            "the largest discrepancy should be listed first");

        Assert.Contains("off by up to 21.0x", text);
    }

    /// <summary>
    ///     A host reading at <i>half</i> the isolated one is as wrong as one at double, so ranking
    ///     must use distance from parity rather than the raw ratio - otherwise every
    ///     under-measurement sorts below every trivial over-measurement.
    /// </summary>
    [Fact]
    public void Render_RanksUnderAndOverMeasurementAlike()
    {
        using var output = new StringWriter();

        IsolationAudit.Render(
            [Result("half", IsolationStatus.Isolated, 100), Result("slightly-over", IsolationStatus.Isolated, 100)],
            [Result("half", IsolationStatus.InProcessRequested, 50), Result("slightly-over", IsolationStatus.InProcessRequested, 110)],
            output);

        var text = output.ToString();

        Assert.True(
            text.IndexOf("half", StringComparison.Ordinal) < text.IndexOf("slightly-over", StringComparison.Ordinal),
            "a 0.5x reading is a 2x error and must outrank a 1.1x one");
    }

    /// <summary>
    ///     When the two agree, say so explicitly. A user who runs this and sees nothing cannot tell
    ///     agreement from a check that never ran.
    /// </summary>
    [Fact]
    public void Render_WhenBothAgree_SaysSoRatherThanStayingSilent()
    {
        using var output = new StringWriter();

        IsolationAudit.Render(
            [Result("a", IsolationStatus.Isolated, 100)],
            [Result("a", IsolationStatus.InProcessRequested, 101)],
            output);

        var text = output.ToString();

        Assert.Contains("agree closely", text);
        Assert.DoesNotContain("off by up to", text);
    }

    /// <summary>
    ///     A benchmark that never isolated is marked as such, not compared against itself - which
    ///     would print a meaningless 1.00x implying the host is fine.
    /// </summary>
    [Fact]
    public void Render_BenchmarkThatCouldNotIsolate_IsMarkedNotCompared()
    {
        using var output = new StringWriter();

        IsolationAudit.Render(
            [Result("captured", IsolationStatus.InProcessCapturedState, 100)],
            [Result("captured", IsolationStatus.InProcessRequested, 100)],
            output);

        var text = output.ToString();

        Assert.Contains("not isolated", text);
        Assert.DoesNotContain("1.00x", text);
    }
}
