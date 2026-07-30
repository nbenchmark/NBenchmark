using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Stats;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     What a test-framework gate asks a worker for when the test wants replicates: how many workers,
///     and what goes in each one.
/// </summary>
/// <remarks>
///     <para>
///         These are the two claims a paired test-integration ratio rests on, and both are properties
///         of the <i>request</i> rather than of any measurement, so they are pinned here where a reader
///         can check them without spawning a process. The end-to-end path is covered separately by
///         <see cref="TestMethodIsolationTests" />, which spawns real workers - a planning seam alone
///         is how the defect that emptied raw samples on every isolated result managed to ship.
///     </para>
///     <para>
///         The claims: a candidate and its reference are measured <b>together</b> in each replicate, so
///         their per-replicate ratio has that worker's core draw and address-space layout divided out;
///         and the replicate count is spent by launching workers rather than passed down to one, which
///         would repeat the measurement inside a single process and report precision as reproducibility.
///     </para>
/// </remarks>
public sealed class TestMethodReplicateTests
{
    private static MethodInfo Method(string name)
        => typeof(Subject).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    private static TestMethodRunner.Subject Candidate()
        => new(Method(nameof(Subject.Candidate)), [], "Subject.Candidate");

    private static TestMethodRunner.Subject Reference()
        => new(Method(nameof(Subject.Reference)), [], "Subject.Reference");

    /// <summary>
    ///     Answers every requested name with a result whose median is <paramref name="medians" /> for
    ///     that replicate, so a test can dictate the exact per-launch numbers a ratio is formed from.
    /// </summary>
    private static Func<RunGroupPayload, WorkerGroupRunner.GroupResult> Answering(
        Dictionary<string, double[]> medians)
    {
        var replicate = 0;

        return request =>
        {
            var index = replicate++;
            var results = new List<BenchmarkResult>();
            var samples = new Dictionary<string, double[]>(StringComparer.Ordinal);

            foreach (var method in request.TestMethods)
            {
                if (!medians.TryGetValue(method.DisplayName, out var perLaunch) || index >= perLaunch.Length)
                    continue;

                // A NaN entry stands for a launch that measured nothing, so the coordinator has to fill
                // the gap itself rather than shortening one subject's list and not the other's.
                if (double.IsNaN(perLaunch[index]))
                    continue;

                results.Add(Measured(method.DisplayName, perLaunch[index]));
                samples[method.DisplayName] = [perLaunch[index], perLaunch[index] + 1];
            }

            return new WorkerGroupRunner.GroupResult
            {
                Results = results,
                RawSamples = samples,
                Faults = results.Count == request.TestMethods.Count
                    ? []
                    : [new FaultPayload { Message = "this replicate measured nothing" }],
            };
        };
    }

    private static BenchmarkResult Measured(string name, double median) => new()
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
        N = 2,
        MeasuredIterations = 2,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
        IsolationStatus = IsolationStatus.Isolated,
    };

    private static MeasurementOptions Options(int launchCount) => MeasurementOptions.Default with
    {
        Iterations = 2,
        WarmupIterations = 0,
        LaunchCount = launchCount,
    };

    /// <summary>
    ///     The co-residency claim: one worker per replicate, both methods inside it.
    /// </summary>
    [Fact]
    public async Task AComparisonPair_SharesOneWorkerPerReplicate()
    {
        using var scope = FakeWorkerLauncher.Install(Answering(new Dictionary<string, double[]>
        {
            ["Subject.Candidate"] = [120, 130, 125],
            ["Subject.Reference"] = [100, 110, 105],
        }));

        var outcome = await TestMethodRunner.RunAsync(
            [Candidate(), Reference()], Options(3));

        Assert.True(outcome.Measured, outcome.Refusal);

        // Three workers, not six. Measuring the two sides separately would double the wall clock and
        // buy a worse ratio.
        Assert.Equal(3, scope.Launcher.Requests.Count);

        foreach (var request in scope.Launcher.Requests)
        {
            Assert.Equal(
                new[] { "Subject.Candidate", "Subject.Reference" },
                request.TestMethods.Select(m => m.DisplayName));

            // The replicate count is spent here, by launching workers. A worker handed LaunchCount > 1
            // would repeat the measurement internally and report within-process precision as though it
            // were reproducibility.
            Assert.Equal(1, request.Options.LaunchCount);
        }
    }

    /// <summary>
    ///     Two bodies in one worker have an order, and a fixed one is a confound: whichever runs first
    ///     pays to warm whatever they share. Each replicate shuffles independently, which turns that
    ///     into a nuisance factor rather than a term in the ratio.
    /// </summary>
    [Fact]
    public async Task APair_IsMeasuredInARandomizedOrder()
    {
        using var scope = FakeWorkerLauncher.Install(Answering(new Dictionary<string, double[]>
        {
            ["Subject.Candidate"] = [120, 130],
            ["Subject.Reference"] = [100, 110],
        }));

        await TestMethodRunner.RunAsync([Candidate(), Reference()], Options(2));

        Assert.All(scope.Launcher.Requests, r => Assert.Equal(RunOrder.Random, r.Order));
    }

    /// <summary>
    ///     A lone subject has no order to randomize, and is left in declaration order so that opting
    ///     out of a comparison changes nothing about how it is measured.
    /// </summary>
    [Fact]
    public async Task ASingleSubject_IsNotShuffled()
    {
        using var scope = FakeWorkerLauncher.Install(Answering(new Dictionary<string, double[]>
        {
            ["Subject.Candidate"] = [120],
        }));

        await TestMethodRunner.RunAsync([Candidate()], Options(1));

        Assert.Equal(RunOrder.Declaration, Assert.Single(scope.Launcher.Requests).Order);
    }

    /// <summary>
    ///     The default is one launch, and it produces exactly what it produced before replicates
    ///     existed - including no <see cref="BenchmarkResult.LaunchStatistics" />, which is what tells
    ///     every downstream gate there is nothing to pair.
    /// </summary>
    [Fact]
    public async Task OneLaunch_IsTheDefaultAndCarriesNoLaunchStatistics()
    {
        using var scope = FakeWorkerLauncher.Install(Answering(new Dictionary<string, double[]>
        {
            ["Subject.Candidate"] = [120],
        }));

        var outcome = await TestMethodRunner.RunAsync([Candidate()], MeasurementOptions.Default with
        {
            Iterations = 2,
            WarmupIterations = 0,
        });

        Assert.Single(scope.Launcher.Requests);
        Assert.Null(outcome.Result!.LaunchStatistics);
        Assert.Null(LogRatio.Estimate(outcome.Result, outcome.Result));
    }

    /// <summary>
    ///     Replicates produce the paired estimate the gate reads, over per-launch ratios rather than a
    ///     quotient of aggregates.
    /// </summary>
    /// <remarks>
    ///     The numbers are the point. The three launches disagree with each other by 5x, and every one
    ///     of them measured the candidate at exactly 1.20x its reference. A paired estimate reports
    ///     1.20x with a zero-width interval; dividing the two averaged medians reports 1.20x too, but
    ///     with the 5x spread left in the numerator and denominator independently and therefore no
    ///     honest interval at all.
    /// </remarks>
    [Fact]
    public async Task Replicates_ProduceAPairedRatioOverPerLaunchRatios()
    {
        using var scope = FakeWorkerLauncher.Install(Answering(new Dictionary<string, double[]>
        {
            ["Subject.Candidate"] = [120, 600, 240],
            ["Subject.Reference"] = [100, 500, 200],
        }));

        var outcome = await TestMethodRunner.RunAsync([Candidate(), Reference()], Options(3));

        var candidate = outcome.Measurements[0].Result;
        var reference = outcome.Measurements[1].Result;

        Assert.Equal(3, candidate.LaunchStatistics!.LaunchCount);
        Assert.Equal(3, reference.LaunchStatistics!.LaunchCount);

        var estimate = LogRatio.Estimate(candidate, reference);

        Assert.NotNull(estimate);
        Assert.Equal(3, estimate.Replicates);
        Assert.Equal(1.20, estimate.Value, 6);
        Assert.Equal(1.20, estimate.Lower, 6);
        Assert.Equal(1.20, estimate.Upper, 6);
    }

    /// <summary>
    ///     A replicate that measured nothing is recorded as an errored launch at its own index, not
    ///     dropped.
    /// </summary>
    /// <remarks>
    ///     This is the defect shape the pairing is most exposed to. Here the candidate misses launch 1
    ///     and the reference misses launch 2. Dropping the gaps would leave two two-entry lists whose
    ///     first entries are different replicates, and the ratio would then compare the candidate's
    ///     launch 0 against the reference's launch 0 and its launch 2 against the reference's launch 1 -
    ///     a difference between two processes reported as a property of the code. Recorded by index,
    ///     only launch 0 pairs, which is one pair and correctly not an estimate.
    /// </remarks>
    [Fact]
    public async Task AFailedReplicate_KeepsTheLaunchIndexMeaningTheLaunch()
    {
        using var scope = FakeWorkerLauncher.Install(Answering(new Dictionary<string, double[]>
        {
            ["Subject.Candidate"] = [120, double.NaN, 240],
            ["Subject.Reference"] = [100, 500, double.NaN],
        }));

        var outcome = await TestMethodRunner.RunAsync([Candidate(), Reference()], Options(3));

        var candidateLaunches = outcome.Measurements[0].Result.LaunchStatistics!.Launches;
        var referenceLaunches = outcome.Measurements[1].Result.LaunchStatistics!.Launches;

        Assert.Equal(3, candidateLaunches.Count);
        Assert.Equal(3, referenceLaunches.Count);

        Assert.Collection(
            candidateLaunches,
            l => Assert.False(l.Errored),
            l => Assert.True(l.Errored),
            l => Assert.False(l.Errored));

        Assert.Collection(
            referenceLaunches,
            l => Assert.False(l.Errored),
            l => Assert.False(l.Errored),
            l => Assert.True(l.Errored));

        // One survivable pair, which is a ratio and not an estimate of one.
        Assert.Null(LogRatio.Estimate(outcome.Measurements[0].Result, outcome.Measurements[1].Result));
    }

    /// <summary>
    ///     A pair whose methods come from different classes is rejected rather than measured in two
    ///     workers, because one worker builds one test-class instance and the pairing is the reason the
    ///     call exists.
    /// </summary>
    /// <remarks>
    ///     Thrown rather than refused: a refusal sends the measurement to the test host, and measuring
    ///     this pair there would produce a ratio for a comparison that was never coherent. The caller
    ///     made a mistake, and the honest response is to say which two methods.
    /// </remarks>
    [Fact]
    public async Task MethodsFromDifferentClasses_AreRejected()
    {
        using var scope = FakeWorkerLauncher.Install(Answering([]));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => TestMethodRunner.RunAsync(
            [
                Candidate(),
                new TestMethodRunner.Subject(
                    typeof(OtherSubject).GetMethod(nameof(OtherSubject.Elsewhere))!, [], "Other.Elsewhere"),
            ],
            Options(2)));

        Assert.Contains("same class", error.Message);
        Assert.Empty(scope.Launcher.Requests);
    }

    /// <summary>
    ///     Two subjects under one name are rejected too - in practice a reference method that resolved to
    ///     the method under test, which would compare a body against itself and always report 1.00x.
    /// </summary>
    /// <remarks>
    ///     Also the shape that would break the group mechanically: results are matched back to subjects by
    ///     name, so two subjects sharing one leaves no way to say which is which.
    /// </remarks>
    [Fact]
    public async Task TwoSubjectsWithTheSameName_AreRejected()
    {
        using var scope = FakeWorkerLauncher.Install(Answering([]));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => TestMethodRunner.RunAsync(
            [Candidate(), Candidate()], Options(2)));

        Assert.Contains("comparison against itself", error.Message);
        Assert.Empty(scope.Launcher.Requests);
    }

    public class Subject
    {
        public void Candidate() => Thread.SpinWait(120);

        public void Reference() => Thread.SpinWait(100);
    }

    public class OtherSubject
    {
        public void Elsewhere() => Thread.SpinWait(100);
    }
}
