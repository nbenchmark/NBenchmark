using System.Reflection;
using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

/// <summary>
///     A ratio gate is a claim about two bodies. It is only enforced when both were measured the
///     same way, because otherwise it is mostly a claim about two processes.
/// </summary>
/// <remarks>
///     The evidence: on four benchmark bodies of provably identical cost, in-host measurement
///     produced a 2.80x ratio with a tight confidence interval on each side, and the isolated /
///     in-host difference alone moved a median by ~3.3x. A gate reading either number reports an
///     effect that does not exist - and a CI gate, unlike a human, does not read the caveat printed
///     beside it.
/// </remarks>
public sealed class PerformanceGateIsolationTests
{
    [Fact]
    public void Ratio_Is_Gated_When_Both_Sides_Were_Isolated()
    {
        var outcome = Evaluate(
            Result("candidate", 600, IsolationStatus.Isolated),
            Result("reference", 100, IsolationStatus.Isolated));

        Assert.Contains(outcome.Violations, v => v.Contains("Regression detected"));
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void Ratio_Is_Not_Gated_When_Both_Sides_Ran_In_The_Test_Host()
    {
        var outcome = Evaluate(
            Result("candidate", 600, IsolationStatus.InProcessLiveFixture),
            Result("reference", 100, IsolationStatus.InProcessLiveFixture));

        Assert.Empty(outcome.Violations);

        // Silence here would be the real defect: the gate stops failing, and nothing says so.
        var note = Assert.Single(outcome.Notes);
        Assert.Contains("was not enforced", note);
        Assert.Contains("[AllowInProcessGate]", note);
    }

    [Fact]
    public void Ratio_Is_Gated_In_The_Test_Host_When_The_Test_Opts_In()
    {
        var outcome = Evaluate(
            Result("candidate", 600, IsolationStatus.InProcessLiveFixture),
            Result("reference", 100, IsolationStatus.InProcessLiveFixture),
            allowInProcessGate: true);

        Assert.Contains(outcome.Violations, v => v.Contains("Regression detected"));
        Assert.Contains(outcome.Notes, n => n.Contains("[AllowInProcessGate] is present"));
    }

    /// <summary>
    ///     Opting in accepts a noisy comparison, not a meaningless one. A ratio spanning a process
    ///     boundary is dominated by the runtime configuration difference, so no opt-in enables it.
    /// </summary>
    [Fact]
    public void Opting_In_Does_Not_Enable_A_Cross_Process_Ratio()
    {
        var outcome = Evaluate(
            Result("candidate", 600, IsolationStatus.Isolated),
            Result("reference", 100, IsolationStatus.InProcessLiveFixture),
            allowInProcessGate: true);

        Assert.Empty(outcome.Violations);
        Assert.Contains(outcome.Notes, n => n.Contains("measured in different processes"));
    }

    /// <summary>
    ///     The defect this replaced: the adapters withheld the reference when isolation differed and
    ///     then fell through to the calibration comparison - swapping one cross-process ratio for a
    ///     worse one, while printing a note claiming the gate had been skipped.
    /// </summary>
    [Fact]
    public void A_Withheld_Reference_Does_Not_Fall_Through_To_The_Calibration_Ratio()
    {
        var outcome = Evaluate(
            Result("candidate", 600, IsolationStatus.Isolated),
            Result("reference", 100, IsolationStatus.InProcessRequested));

        Assert.Empty(outcome.Violations);
        Assert.DoesNotContain(outcome.Violations, v => v.Contains("calibration"));
    }

    [Fact]
    public void RequireIsolation_Fails_A_Result_Measured_In_The_Test_Host()
    {
        var outcome = PerformanceGate.Evaluate(
            Result("candidate", 100, IsolationStatus.InProcessLiveFixture),
            Samples(100),
            null,
            null,
            new Thresholds { RequireIsolation = true });

        var violation = Assert.Single(outcome.Violations);
        Assert.Contains("RequireIsolation", violation);
        Assert.Contains("in-process (fixture)", violation);

        // The reason carries its own remedy; a failure that does not say what to do about it just
        // relocates the problem to whoever reads the CI log.
        Assert.Contains("a worker cannot reproduce it", violation);
    }

    [Fact]
    public void RequireIsolation_Passes_A_Result_Measured_In_A_Worker()
    {
        var outcome = PerformanceGate.Evaluate(
            Result("candidate", 100, IsolationStatus.Isolated),
            Samples(100),
            null,
            null,
            new Thresholds { RequireIsolation = true });

        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void RequireIsolation_Defaults_To_Off()
    {
        var outcome = PerformanceGate.Evaluate(
            Result("candidate", 100, IsolationStatus.InProcessNoWorker),
            Samples(100),
            null,
            null,
            new Thresholds());

        Assert.Empty(outcome.Violations);
    }

    [Fact]
    public void AllowInProcessGate_Is_Read_From_The_Method()
        => Assert.True(PerformanceGate.AllowsInProcessGate(MethodOf<Marked>(nameof(Marked.OnTheMethod))));

    [Fact]
    public void AllowInProcessGate_Is_Read_From_The_Declaring_Class()
        => Assert.True(PerformanceGate.AllowsInProcessGate(MethodOf<Marked>(nameof(Marked.Unmarked))));

    [Fact]
    public void AllowInProcessGate_Is_Absent_When_Nothing_Declares_It()
        => Assert.False(PerformanceGate.AllowsInProcessGate(MethodOf<Bare>(nameof(Bare.Unmarked))));

    private static MethodInfo MethodOf<T>(string name)
        => typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    private static PerformanceGate.Outcome Evaluate(
        BenchmarkResult candidate,
        BenchmarkResult reference,
        bool allowInProcessGate = false)
        => PerformanceGate.Evaluate(
            candidate,
            Samples(candidate.Mean),
            reference,
            Samples(reference.Mean),
            new Thresholds { MaxSlowdownRatio = 1.2 },
            allowInProcessGate);

    private static double[] Samples(double mean)
    {
        var samples = new double[50];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = mean + (i % 10 - 5) * 0.02 * mean;
        }

        return samples;
    }

    private static BenchmarkResult Result(string name, double mean, IsolationStatus status) => new()
    {
        Name = name,
        IsolationStatus = status,
        Mean = mean,
        Median = mean,
        Percentiles = [],
        Min = mean,
        Max = mean,
        StandardDeviation = 0,
        MeasuredIterations = 50,
        WarmupIterations = 10,
        Q1 = 0,
        Q3 = 0,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 50,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
    };

    private sealed class Thresholds : IPerformanceThresholds
    {
        public double MaxMeanNs => -1;
        public double MaxP95Ns => -1;
        public long MaxAllocatedBytes => -1;
        public string? ReferenceMethod => null;
        public double MaxSlowdownRatio { get; init; }
        public int Iterations => 0;
        public int WarmupIterations => 0;
        public bool MeasureAllocations => false;
        public OutlierMode OutlierMode => OutlierMode.IqrFence;
        public double ConfidenceLevel => 0.95;
        public double MaxAbsoluteThresholdTolerance => 1.0;
        public bool RequireIsolation { get; init; }
    }

    [AllowInProcessGate]
    public sealed class Marked
    {
        [AllowInProcessGate]
        public void OnTheMethod()
        {
        }

        public void Unmarked()
        {
        }
    }

    public sealed class Bare
    {
        public void Unmarked()
        {
        }
    }
}
