using System.Reflection;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class SuiteRunnerTests
{
    [Fact]
    public async Task RunAsync_Executes_Envelopes_In_Order_And_Records_Results()
    {
        var envelopes = new[]
        {
            StaticEnvelope("a", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("b", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        var (results, rawSamples) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 2,
            NullBenchmarkProgress.Instance, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("a", results[0].Name);
        Assert.Equal("b", results[1].Name);
        Assert.Equal(2, rawSamples.Count);
        Assert.True(rawSamples.ContainsKey("a"));
        Assert.True(rawSamples.ContainsKey("b"));
    }

    [Fact]
    public async Task RunAsync_Random_Order_With_Seed_Is_Deterministic()
    {
        var envelopes = new[]
        {
            StaticEnvelope("a", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("b", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("c", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("d", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        var (first, _) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Random, 42, MeasurementOptions.Default,
            0, 4,
            NullBenchmarkProgress.Instance, CancellationToken.None);

        var (second, _) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Random, 42, MeasurementOptions.Default,
            0, 4,
            NullBenchmarkProgress.Instance, CancellationToken.None);

        var firstOrder = first.Select(r => r.Name).ToList();
        var secondOrder = second.Select(r => r.Name).ToList();

        Assert.Equal(firstOrder, secondOrder);
        Assert.NotEqual(new[] { "a", "b", "c", "d" }, firstOrder);
    }

    [Fact]
    public async Task RunAsync_Declaration_Order_Preserves_Order()
    {
        var envelopes = new[]
        {
            StaticEnvelope("first", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("second", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("third", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        var (results, _) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, 99, MeasurementOptions.Default,
            0, 3,
            NullBenchmarkProgress.Instance, CancellationToken.None);

        Assert.Equal(new[] { "first", "second", "third" }, results.Select(r => r.Name).ToList());
    }

    [Fact]
    public async Task RunAsync_Emits_OnBenchmarkStarting_With_Local_Index()
    {
        var progress = new CapturingProgress();

        var envelopes = new[]
        {
            StaticEnvelope("a", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("b", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 2,
            progress, CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, progress.BenchmarkStarts.Select(s => s.Name).ToList());
        Assert.Equal(new[] { 1, 2 }, progress.BenchmarkStarts.Select(s => s.Index).ToList());
        Assert.All(progress.BenchmarkStarts, s => Assert.Equal(2, s.Total));
    }

    [Fact]
    public async Task RunAsync_Emits_OnBenchmarkStarting_With_StartIndex_Offset()
    {
        var progress = new CapturingProgress();

        var envelopes = new[]
        {
            StaticEnvelope("b", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
            StaticEnvelope("c", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            1, 3,
            progress, CancellationToken.None);

        Assert.Equal(new[] { 2, 3 }, progress.BenchmarkStarts.Select(s => s.Index).ToList());
        Assert.All(progress.BenchmarkStarts, s => Assert.Equal(3, s.Total));
    }

    [Fact]
    public async Task RunAsync_Does_Not_Emit_OnSuiteStarting_Or_OnSuiteCompleted()
    {
        var progress = new CapturingProgress();

        var envelopes = new[]
        {
            StaticEnvelope("a", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 1,
            progress, CancellationToken.None);

        Assert.Empty(progress.SuiteStartings);
        Assert.Empty(progress.SuiteCompletions);
    }

    [Fact]
    public async Task RunAsync_Runs_Without_Error_When_Only_One_Envelope()
    {
        var envelopes = new[]
        {
            StaticEnvelope("only", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        var (results, _) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null,
            new MeasurementOptions { Samples = 0, WarmupSamples = 0, ForceGcBetweenBenchmarks = true },
            0, 1,
            NullBenchmarkProgress.Instance, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("only", results[0].Name);
    }

    [Fact]
    public async Task RunAsync_Unexpected_Envelope_Exception_Is_Converted_To_Errored_And_Run_Continues()
    {
        var envelopes = new[]
        {
            new BenchmarkEnvelope(
                "boom",
                "",
                "boom description",
                false,
                [],
                (_, _) => throw new InvalidOperationException("boom")),
            StaticEnvelope("ok", new MeasurementOptions { Samples = 0, WarmupSamples = 0 }),
        };

        var (results, rawSamples) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 2,
            NullBenchmarkProgress.Instance, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Errored);
        Assert.Contains("boom", results[0].ErrorMessage);
        Assert.False(results[1].Errored);
        Assert.Equal("ok", results[1].Name);

        Assert.Equal(2, rawSamples.Count);
        Assert.Contains("boom", rawSamples.Keys);
        Assert.Empty(rawSamples["boom"]);
    }

    /// <summary>
    ///     The canary's readings bracket the benchmarks: one before the first, one at each
    ///     boundary, one after the last. Driven by a scripted clock so the "host" ramps by a
    ///     programmed amount rather than by whatever the machine running the tests did.
    /// </summary>
    [Fact]
    public async Task RunAsync_Stamps_Each_Result_With_Its_Bracketing_Canary_Readings()
    {
        var options = CanaryOptions();

        // Three readings of four samples each: steady across the first benchmark, 40% slower
        // across the second.
        var clock = new ScriptedClock(call => (call / 4) switch
        {
            0 => 100,
            1 => 100,
            _ => 140,
        });

        var (results, _) = await SuiteRunner.RunAsync(
            [StaticEnvelope("a", options), StaticEnvelope("b", options)],
            RunOrder.Declaration, null, options,
            0, 2,
            NullBenchmarkProgress.Instance, CancellationToken.None,
            clock: clock);

        Assert.Equal(100, results[0].HostTimeline!.BeforeNs);
        Assert.Equal(100, results[0].HostTimeline!.AfterNs);
        Assert.Equal(1.0, results[0].HostTimeline!.RelativeToRunStart, 9);

        Assert.Equal(100, results[1].HostTimeline!.BeforeNs);
        Assert.Equal(140, results[1].HostTimeline!.AfterNs);
        Assert.Equal(1.2, results[1].HostTimeline!.RelativeToRunStart, 9);
    }

    [Fact]
    public async Task RunAsync_Leaves_The_Timeline_Unstamped_When_The_Canary_Is_Off()
    {
        var options = CanaryOptions() with { DriftCanary = DriftCanaryOptions.Disabled };

        var (results, _) = await SuiteRunner.RunAsync(
            [StaticEnvelope("a", options), StaticEnvelope("b", options)],
            RunOrder.Declaration, null, options,
            0, 2,
            NullBenchmarkProgress.Instance, CancellationToken.None,
            clock: new ScriptedClock(100));

        Assert.All(results, r => Assert.Null(r.HostTimeline));
    }

    /// <summary>
    ///     A dry-run measures nothing, so it should cost nothing - including the canary. Pinned
    ///     because the canary is on by default and <c>--dry-run</c> is the configuration smoke
    ///     test people run in a loop.
    /// </summary>
    [Fact]
    public async Task RunAsync_Skips_The_Canary_On_A_Dry_Run()
    {
        var options = new MeasurementOptions { Samples = 0, WarmupSamples = 0 };

        var (results, _) = await SuiteRunner.RunAsync(
            [StaticEnvelope("a", options)],
            RunOrder.Declaration, null, options,
            0, 1,
            NullBenchmarkProgress.Instance, CancellationToken.None,
            clock: new ScriptedClock(100));

        Assert.Null(results[0].HostTimeline);
    }

    /// <summary>
    ///     An errored row was not measured anywhere, so there is no measurement point to describe.
    ///     Stamping one would invite a drift comparison against a row that has no number. The rows
    ///     either side keep their stamps, because the reading indices are not compacted.
    /// </summary>
    [Fact]
    public async Task RunAsync_Does_Not_Stamp_An_Errored_Result()
    {
        var options = CanaryOptions();

        var envelopes = new[]
        {
            new BenchmarkEnvelope("boom", "", null, false, [], (_, _) => throw new InvalidOperationException("boom")),
            StaticEnvelope("ok", options),
        };

        var (results, _) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, options,
            0, 2,
            NullBenchmarkProgress.Instance, CancellationToken.None,
            clock: new ScriptedClock(100));

        Assert.True(results[0].Errored);
        Assert.Null(results[0].HostTimeline);
        Assert.NotNull(results[1].HostTimeline);
    }

    /// <summary>
    ///     One measured sample per benchmark and a four-sample canary: enough to exercise the
    ///     bracketing without the test paying for a real adaptive run.
    /// </summary>
    private static MeasurementOptions CanaryOptions() => new()
    {
        Samples = 1,
        WarmupSamples = 0,
        DriftCanary = new DriftCanaryOptions { Samples = 4, WorkPerSample = 64 },
    };

    [Fact]
    public void ShouldForceGcBetweenBenchmarks_Skips_True_DryRun()
    {
        var options = new MeasurementOptions
        {
            Samples = 0,
            WarmupSamples = 0,
            ForceGcBetweenBenchmarks = true,
        };

        var dryRunResult = OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            "dry",
            "",
            null,
            false,
            options,
            TimeSpan.Zero,
            TimeSpan.Zero).Result;

        Assert.False(InvokeShouldForceGcBetweenBenchmarks(options, dryRunResult));
    }

    private static BenchmarkEnvelope StaticEnvelope(string name, MeasurementOptions _) => new(
        name,
        "",
        null,
        false,
        [],
        (spec, ct) =>
        {
            var outcome = BenchmarkRunner.Instance.Run(name, () => { }, spec, ct);
            return Task.FromResult(outcome);
        });

    private static bool InvokeShouldForceGcBetweenBenchmarks(MeasurementOptions options, BenchmarkResult result)
    {
        var method = typeof(SuiteRunner).GetMethod("ShouldForceGcBetweenBenchmarks", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [options, result])!;
    }

    private sealed class CapturingProgress : IBenchmarkProgress
    {
        public List<(string Name, int Index, int Total)> BenchmarkStarts { get; } = [];
        public List<(IReadOnlyList<string> Names, int Total)> SuiteStartings { get; } = [];
        public List<int> SuiteCompletions { get; } = [];

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
        {
            SuiteStartings.Add((benchmarkNames, total));
            return Task.CompletedTask;
        }

        public Task OnWarmupStarting(string name, int totalWarmupSamples) => Task.CompletedTask;

        public Task OnWarmupCompleted(string name) => Task.CompletedTask;

        public Task OnBenchmarkStarting(string name, int index, int total)
        {
            BenchmarkStarts.Add((name, index, total));
            return Task.CompletedTask;
        }

        public Task OnSampleCompleted(string name, int sample, int totalSamples) => Task.CompletedTask;

        public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
        {
            SuiteCompletions.Add(results.Count);
            return Task.CompletedTask;
        }
    }
}
