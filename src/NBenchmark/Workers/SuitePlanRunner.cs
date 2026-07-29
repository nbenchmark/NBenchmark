using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Runs a <c>[BenchmarkPlan]</c> factory's suite in measurement workers, then aggregates,
///     scores and reports it in the coordinator.
/// </summary>
/// <remarks>
///     <para>
///         The factory - not the suite - is what crosses the process boundary, and that single
///         choice removes the entire class of problems the previous design fought. A suite object is
///         full of live delegates: benchmark bodies, setup and teardown, custom detectors and
///         significance tests, instance factories. None of that can be serialized honestly. A
///         <i>factory</i> is one static method, addressable by metadata token, and the worker runs it
///         to obtain all of those as real objects in its own process.
///     </para>
///     <para>
///         It also removes the old model's worst property. Previously an isolated suite re-executed
///         the user's entire <c>Main</c>, so <i>M</i> isolated suites in one program did <i>M²</i>
///         measurement work and every side effect in <c>Main</c> - a file write, an HTTP call,
///         database seeding - happened once per child. A worker calls one factory and nothing else.
///     </para>
/// </remarks>
internal static class SuitePlanRunner
{
    /// <summary>
    ///     Measures the plan's suite, once per replicate, and returns the aggregated results with
    ///     their pooled samples and the status describing where they were measured.
    /// </summary>
    public static async Task<PlanOutcome> RunAsync(
        Func<BenchmarkSuite> plan,
        BenchmarkSuite localSuite,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        int? sessionSeed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(localSuite);

        var options = localSuite.ResolvedOptions;
        var names = localSuite.BenchmarkNames();

        if (!TryPlan(plan, out var planRef, out var status, out var refusal))
            return PlanOutcome.Declined(status, refusal);

        var replicates = Math.Max(1, options.LaunchCount);
        var timeout = MeasurementBudget.For(options, names.Count);

        var perReplicate = new List<IReadOnlyList<BenchmarkResult>>(replicates);
        var perReplicateSamples = new List<Dictionary<string, double[]>>(replicates);
        var faults = new List<FaultPayload>();

        for (var replicate = 0; replicate < replicates; replicate++)
        {
            var request = new RunGroupPayload
            {
                GroupId = $"plan:{planRef.DisplayName}#{replicate}",
                Kind = WorkGroupKind.Plan,
                TargetAssemblyPath = planRef.AssemblyPath,
                Bodies = [planRef],

                // Sent so the coordinator and worker agree on the runtime profile to launch under.
                // The worker measures with the options its own factory produced, which is the point
                // of the design - see WorkerSession.RunPlanAsync.
                Options = options with { LaunchCount = 1 },
                Seed = WorkerRunPlan.DeriveSeed(sessionSeed, replicate),
                TotalBenchmarks = names.Count,
            };

            // Only the first replicate's telemetry is forwarded. Later replicates measure the same
            // benchmarks again, so replaying their lifecycle events would make a progress bar
            // appear to run backwards.
            var group = await WorkerLauncher.Current.RunGroupAsync(
                    request,
                    replicate == 0 ? progress : NullBenchmarkProgress.Instance,
                    replicate == 0 ? observer : NullMeasurementObserver.Instance,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            faults.AddRange(group.Faults);

            if (group.Results.Count > 0)
            {
                perReplicate.Add(group.Results);
                perReplicateSamples.Add(group.RawSamples);
            }
        }

        if (perReplicate.Count == 0)
        {
            // Every replicate failed. Falling back to in-process measurement is better than
            // returning nothing, but the caller must know it is not getting what it asked for.
            return PlanOutcome.Declined(
                IsolationStatus.InProcessNoWorker,
                faults.FirstOrDefault()?.Message ?? "no measurement worker produced a result.");
        }

        var (results, samples) = Combine(perReplicate, perReplicateSamples);

        // Benchmarks no worker reported become errored rows naming the reason, so a failure is
        // visible in the table rather than a silently missing line.
        foreach (var fault in faults.Where(f => f.BenchmarkName is { Length: > 0 }))
        {
            if (results.All(r => r.Name != fault.BenchmarkName))
                results.Add(WorkerGroupRunner.ErroredResult(fault.BenchmarkName!, fault.Message));
        }

        return new PlanOutcome
        {
            Status = IsolationStatus.Isolated,
            Results = results,
            RawSamples = samples,
            Faults = faults,
        };
    }

    /// <summary>
    ///     Whether the factory can be invoked in a worker. A capturing factory is refused for the
    ///     same reason a capturing benchmark body is: the captured state lives only here, and
    ///     reconstructing it was measured to return plausible, wrong numbers rather than failing.
    /// </summary>
    private static bool TryPlan(
        Func<BenchmarkSuite> plan,
        out BodyRef planRef,
        out IsolationStatus status,
        out string? refusal)
    {
        planRef = null!;

        if (!WorkerLauncher.Current.IsAvailable)
        {
            status = IsolationStatus.InProcessNoWorker;

            refusal = "the measurement worker (nbworker) is not deployed alongside this application. "
                      + $"Looked in {WorkerLocator.DescribeSearch()}.";

            return false;
        }

        if (!BodyRef.TryCreate(plan, plan.Method.Name, out planRef, out refusal))
        {
            status = refusal is not null && refusal.Contains("captures", StringComparison.Ordinal)
                ? IsolationStatus.InProcessCapturedState
                : IsolationStatus.InProcessUnaddressablePlan;

            return false;
        }

        status = IsolationStatus.Isolated;
        refusal = null;

        return true;
    }

    /// <summary>
    ///     Combines the replicates. With one replicate the results pass through unchanged; with
    ///     several, each benchmark gets between-worker launch statistics - which is the estimate a
    ///     regression gate should read, because it describes reproducibility rather than the
    ///     precision of a single process.
    /// </summary>
    public static (List<BenchmarkResult> Results, Dictionary<string, double[]> RawSamples) Combine(
        List<IReadOnlyList<BenchmarkResult>> perReplicate,
        List<Dictionary<string, double[]>> perReplicateSamples)
    {
        if (perReplicate.Count == 1)
        {
            // Re-attach each result's own samples. They travelled beside the result rather than
            // inside it, so this restores the shape an in-process run would have produced - and
            // omitting it is precisely the defect that silently emptied RawSamples on every isolated
            // result and disabled significance testing for a whole release.
            var single = perReplicate[0]
                .Select(r => r with { RawSamples = perReplicateSamples[0].GetValueOrDefault(r.Name, []) })
                .ToList();

            return (single, RawSampleKey.ToComposite(single, perReplicateSamples[0]));
        }

        var results = new List<BenchmarkResult>();
        var samples = new Dictionary<string, double[]>(StringComparer.Ordinal);

        foreach (var name in perReplicate[0].Select(r => r.Name))
        {
            var launches = perReplicate
                .Select(replicate => replicate.FirstOrDefault(r => r.Name == name))
                .OfType<BenchmarkResult>()
                .ToList();

            if (launches.Count == 0)
                continue;

            var bestIndex = IndexOfBestLaunch(launches);
            var best = launches[bestIndex];
            var aggregated = LaunchAggregator.Apply(best, LaunchAggregator.Aggregate(launches));

            // The displayed result keeps the representative launch's own samples, so its statistical
            // fields and trimmed-sample ordinals stay aligned with the distribution it describes.
            // The pooled samples travel separately, for significance across all replicates.
            aggregated = aggregated with
            {
                RawSamples = perReplicateSamples[bestIndex].GetValueOrDefault(name, []),
            };

            results.Add(aggregated);

            samples[RawSampleKey.For(aggregated)] = perReplicateSamples
                .SelectMany(replicate => replicate.GetValueOrDefault(name, []))
                .ToArray();
        }

        return (results, samples);
    }

    /// <summary>
    ///     The position of the lowest-median successful launch. <see cref="LaunchAggregator.BestLaunch" />
    ///     returns the result itself, but the samples live in a parallel list addressed by position,
    ///     so the index is what pairs them.
    /// </summary>
    private static int IndexOfBestLaunch(IReadOnlyList<BenchmarkResult> launches)
    {
        var bestIndex = 0;
        var bestMedian = double.MaxValue;

        for (var i = 0; i < launches.Count; i++)
        {
            if (launches[i].Errored || launches[i].Median >= bestMedian)
                continue;

            bestMedian = launches[i].Median;
            bestIndex = i;
        }

        return bestIndex;
    }
}

/// <summary>What running a plan produced, and where.</summary>
internal sealed record PlanOutcome
{
    public required IsolationStatus Status { get; init; }
    public IReadOnlyList<BenchmarkResult> Results { get; init; } = [];
    public IReadOnlyDictionary<string, double[]> RawSamples { get; init; } = new Dictionary<string, double[]>();
    public IReadOnlyList<FaultPayload> Faults { get; init; } = [];

    /// <summary>Why the plan was not measured in a worker, when it was not.</summary>
    public string? Refusal { get; init; }

    public bool WasIsolated => Status.IsIsolated();

    public static PlanOutcome Declined(IsolationStatus status, string? refusal)
        => new() { Status = status, Refusal = refusal };
}
