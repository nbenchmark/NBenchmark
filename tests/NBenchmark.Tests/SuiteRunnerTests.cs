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
            StaticEnvelope("a", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("b", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
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
            StaticEnvelope("a", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("b", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("c", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("d", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
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
            StaticEnvelope("first", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("second", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("third", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
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
            StaticEnvelope("a", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("b", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
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
            StaticEnvelope("b", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
            StaticEnvelope("c", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
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
            StaticEnvelope("a", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
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
            StaticEnvelope("only", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
        };

        var (results, _) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null,
            new MeasurementOptions { Iterations = 0, WarmupIterations = 0, ForceGcBetweenBenchmarksOverride = true },
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
            StaticEnvelope("ok", new MeasurementOptions { Iterations = 0, WarmupIterations = 0 }),
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

    [Fact]
    public void ShouldForceGcBetweenBenchmarks_Skips_True_DryRun()
    {
        var options = new MeasurementOptions
        {
            Iterations = 0,
            WarmupIterations = 0,
            ForceGcBetweenBenchmarksOverride = true,
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

        public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;

        public Task OnWarmupCompleted(string name) => Task.CompletedTask;

        public Task OnBenchmarkStarting(string name, int index, int total)
        {
            BenchmarkStarts.Add((name, index, total));
            return Task.CompletedTask;
        }

        public Task OnIterationCompleted(string name, int iteration, int totalIterations) => Task.CompletedTask;

        public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
        {
            SuiteCompletions.Add(results.Count);
            return Task.CompletedTask;
        }
    }
}
