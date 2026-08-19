using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class OutcomeBuilderTests
{
    // ---------- Success path ----------

    [Fact]
    public void Build_Success_Pins_All_22_Fields()
    {
        var stats = new StatsSummary
        {
            Mean = 100,
            Median = 99,
            Percentiles = [new PercentileEntry(0.95, 110), new PercentileEntry(0.99, 120)],
            Min = 80,
            Max = 130,
            StandardDeviation = 5,
            StandardError = 1,
            MarginOfError = 2,
            ConfidenceLevel = 0.95,
            CoefficientOfVariation = 0.05,
        };

        var allocations = new long[] { 1024, 2048, 4096 };
        var rawTimings = new double[] { 90, 100, 110 };

        var options = new MeasurementOptions
        {
            Iterations = 3,
            WarmupIterations = 5,
            OutlierMode = OutlierMode.RemoveTop5Percent,
            ConfidenceLevel = 0.95,
        };

        var total = TimeSpan.FromMilliseconds(42);
        var measured = TimeSpan.FromMilliseconds(7);

        var diagnostic = new AutoTuneDiagnostic
        {
            ResolvedWarmup = 5,
            ResolvedSamples = 3,
            OpsPerSample = 1,
            TotalBodyInvocations = 8,
            WarmupStop = WarmupStopReason.Settled,
            SampleStop = SampleStopReason.CiTargetMet,
            AchievedRelativeCiWidth = 0.018,
            TuningWallClock = TimeSpan.FromMilliseconds(33),
        };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 3, (long)allocations.Average(), 0, 0, 0, null, null, 0, null, []),
                rawTimings),
            "bench", "",
            "desc",
            true,
            options,
            total,
            measured,
            5,
            diagnostic);

        Assert.Equal(rawTimings, outcome.RawSamples);
        var r = outcome.Result;
        Assert.Equal("bench", r.Name);
        Assert.Equal("desc", r.Description);
        Assert.Equal(100, r.Mean);
        Assert.Equal(99, r.Median);
        Assert.Equal(110, r.GetPercentile(0.95) ?? 0);
        Assert.Equal(120, r.GetPercentile(0.99) ?? 0);
        Assert.Equal(80, r.Min);
        Assert.Equal(130, r.Max);
        Assert.Equal(5, r.StandardDeviation);
        Assert.Equal(1, r.StandardError);
        Assert.Equal(2, r.MarginOfError);
        Assert.Equal(0.95, r.ConfidenceLevel);
        Assert.Equal(0.05, r.CoefficientOfVariation);
        Assert.Equal((long)((1024 + 2048 + 4096) / 3.0), r.MeanAllocatedBytes);
        Assert.Null(r.PValue);
        Assert.Equal(SignificanceVerdict.NotTested, r.SignificanceVerdict);
        Assert.False(r.Errored);
        Assert.Null(r.ErrorMessage);
        Assert.Equal(3, r.MeasuredIterations);
        Assert.Equal(5, r.WarmupIterations);
        Assert.Equal(total, r.TotalDuration);
        Assert.Equal(measured, r.MeasuredDuration);
        Assert.True(r.IsBaseline);
        Assert.Equal(OutlierMode.RemoveTop5Percent, r.OutlierMode);
        Assert.Equal(10_000_000.0, r.OperationsPerSecond, 1);
        Assert.Equal(10_101_010.101010101, r.MedianOperationsPerSecond, 1);
        Assert.Equal(8, r.TotalOperations);
        Assert.Equal(100.0, r.NanosecondsPerOperation);
    }

    [Fact]
    public void Build_Success_With_Allocations_Averages_Into_MeanAllocatedBytes()
    {
        var stats = new StatsSummary { Mean = 1 };
        var allocations = new long[] { 100, 200, 300, 400 };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 4, (long)allocations.Average(), 0, 0, 0, null, null, 0, null, []),
                [1, 2, 3, 4]),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(250, outcome.Result.MeanAllocatedBytes);
    }

    [Fact]
    public void Build_Success_Without_Allocations_Sets_MeanAllocatedBytes_Null()
    {
        var stats = new StatsSummary { Mean = 1 };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 3, null, 0, 0, 0, null, null, 0, null, []),
                [1, 2, 3]),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Null(outcome.Result.MeanAllocatedBytes);
    }

    [Fact]
    public void Build_Success_ConfidenceLevel_Comes_From_Options()
    {
        var stats = new StatsSummary { Mean = 1, ConfidenceLevel = 0.99 };
        var options = new MeasurementOptions { ConfidenceLevel = 0.99 };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 1, null, 0, 0, 0, null, null, 0, null, []),
                [1]),
            "b", "", null, false,
            options,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(0.99, outcome.Result.ConfidenceLevel);
    }

    [Fact]
    public void Build_Success_RunAt_Is_Recent_UtcNow()
    {
        var stats = new StatsSummary { Mean = 1 };
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 1, null, 0, 0, 0, null, null, 0, null, []),
                [1]),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        var after = DateTimeOffset.UtcNow.AddSeconds(5);
        Assert.InRange(outcome.Result.RunAtUtc, before, after);
    }

    [Fact]
    public void Build_Success_Flows_ResolvedWarmup_And_AutoTune_Diagnostic()
    {
        var stats = new StatsSummary { Mean = 1 };

        var diagnostic = new AutoTuneDiagnostic
        {
            ResolvedWarmup = 12,
            ResolvedSamples = 47,
            OpsPerSample = 8,
            TotalBodyInvocations = (12 + 47) * 8,
            WarmupStop = WarmupStopReason.Settled,
            SampleStop = SampleStopReason.CiTargetMet,
            AchievedRelativeCiWidth = 0.018,
            TuningWallClock = TimeSpan.FromMilliseconds(33),
        };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 47, null, 0, 0, 0, null, null, 0, null, []),
                [1, 2, 3]),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            12,
            diagnostic);

        Assert.Equal(12, outcome.Result.WarmupIterations);
        Assert.Equal(diagnostic, outcome.Result.AutoTune);
    }

    [Fact]
    public void Build_DryRun_Leaves_AutoTune_Null()
    {
        var outcome = OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            "dry", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Null(outcome.Result.AutoTune);
    }

    // ---------- Dry-run path ----------

    [Fact]
    public void Build_DryRun_Returns_All_Zero_Stats()
    {
        var options = new MeasurementOptions
        {
            Iterations = 0,
            WarmupIterations = 0,
            OutlierMode = OutlierMode.None,
            ConfidenceLevel = 0.95,
        };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            "dry", "", null, false,
            options,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        var r = outcome.Result;
        Assert.Equal(0, r.Mean);
        Assert.Equal(0, r.Median);
        Assert.Equal(0, r.GetPercentile(0.95) ?? 0);
        Assert.Equal(0, r.GetPercentile(0.99) ?? 0);
        Assert.Equal(0, r.Min);
        Assert.Equal(0, r.Max);
        Assert.Equal(0, r.StandardDeviation);
        Assert.Equal(0, r.StandardError);
        Assert.Equal(0, r.MarginOfError);
        Assert.Equal(0, r.CoefficientOfVariation);
        Assert.Equal(0.95, r.ConfidenceLevel);
        Assert.False(r.Errored);
        Assert.Null(r.ErrorMessage);
        Assert.Equal(0, r.MeasuredIterations);
        Assert.Empty(outcome.RawSamples);
    }

    [Fact]
    public void Build_DryRun_Has_Zero_MeasuredIterations_And_Empty_RawSamples()
    {
        var outcome = OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            "dry", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(0, outcome.Result.MeasuredIterations);
        Assert.Empty(outcome.RawSamples);
    }

    [Fact]
    public void Build_DryRun_MeasuredDuration_Is_Zero_Regardless_Of_Caller()
    {
        var outcome = OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            "dry", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.Zero, outcome.Result.MeasuredDuration);
    }

    [Fact]
    public void Build_DryRun_TotalDuration_Is_Preserved()
    {
        var total = TimeSpan.FromMilliseconds(123);

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            "dry", "", null, false,
            new MeasurementOptions(),
            total,
            TimeSpan.FromMilliseconds(99));

        Assert.Equal(total, outcome.Result.TotalDuration);
    }

    // ---------- Errored path ----------

    [Fact]
    public void Build_Errored_Pins_All_22_Fields_With_ErroredTrue()
    {
        var options = new MeasurementOptions
        {
            WarmupIterations = 4,
            OutlierMode = OutlierMode.IqrFence,
            ConfidenceLevel = 0.99,
        };

        var ex = new InvalidOperationException("nope");
        var total = TimeSpan.FromMilliseconds(50);
        var measured = TimeSpan.Zero;

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Errored(ex),
            "bad", "", "with desc", true,
            options,
            total,
            measured,
            4);

        Assert.Empty(outcome.RawSamples);
        var r = outcome.Result;
        Assert.Equal("bad", r.Name);
        Assert.Equal("with desc", r.Description);
        Assert.Equal(0, r.Mean);
        Assert.Equal(0, r.Median);
        Assert.Equal(0, r.GetPercentile(0.95) ?? 0);
        Assert.Equal(0, r.GetPercentile(0.99) ?? 0);
        Assert.Equal(0, r.Min);
        Assert.Equal(0, r.Max);
        Assert.Equal(0, r.StandardDeviation);
        Assert.Equal(0, r.StandardError);
        Assert.Equal(0, r.MarginOfError);
        Assert.Equal(0.99, r.ConfidenceLevel);
        Assert.Equal(0, r.CoefficientOfVariation);
        Assert.Null(r.MeanAllocatedBytes);
        Assert.Null(r.PValue);
        Assert.Equal(SignificanceVerdict.NotTested, r.SignificanceVerdict);
        Assert.True(r.Errored);
        Assert.NotNull(r.ErrorMessage);
        Assert.Contains("nope", r.ErrorMessage);
        Assert.Equal(0, r.MeasuredIterations);
        Assert.Equal(4, r.WarmupIterations);
        Assert.Equal(total, r.TotalDuration);
        Assert.Equal(measured, r.MeasuredDuration);
        Assert.True(r.IsBaseline);
        Assert.Equal(OutlierMode.IqrFence, r.OutlierMode);
    }

    [Fact]
    public void Build_Errored_Unwraps_TargetInvocationException()
    {
        var inner = new InvalidOperationException("inner-cause");
        var tiex = new TargetInvocationException(inner);

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Errored(tiex),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Contains("inner-cause", outcome.Result.ErrorMessage);
        Assert.DoesNotContain("TargetInvocation", outcome.Result.ErrorMessage);
    }

    [Fact]
    public void Build_Errored_Uses_Explicit_Message_Override_When_Provided()
    {
        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Errored(new Exception("inner"), "setup failed"),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal("setup failed", outcome.Result.ErrorMessage);
    }

    [Fact]
    public void Build_Errored_Falls_Back_To_Outer_When_Inner_Is_Null()
    {
        var tiex = new TargetInvocationException("outer", null);

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Errored(tiex),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.NotNull(outcome.Result.ErrorMessage);
    }

    [Fact]
    public void Build_Errored_Preserves_Durations_From_Caller()
    {
        var total = TimeSpan.FromMilliseconds(50);
        var measured = TimeSpan.FromMilliseconds(7);

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Errored(new Exception("x")),
            "b", "", null, false,
            new MeasurementOptions(),
            total,
            measured);

        Assert.Equal(total, outcome.Result.TotalDuration);
        Assert.Equal(measured, outcome.Result.MeasuredDuration);
    }

    // ---------- ThreadControl / InterferenceFilter sourcing ----------

    [Fact]
    public void Build_Success_SetsThreadControlEnabled_FromOptions()
    {
        var stats = new StatsSummary { Mean = 1 };
        var options = new MeasurementOptions
        {
            Environment = new EnvironmentOptions { ThreadControl = false },
        };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 1, null, 0, 0, 0, null, null, 0, null, []),
                [1]),
            "b", "", null, false,
            options,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.False(outcome.Result.ThreadControlEnabled);
    }

    [Fact]
    public void Build_Success_SetsThreadControlEnabled_DefaultTrue_WhenEnvironmentNull()
    {
        var stats = new StatsSummary { Mean = 1 };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 1, null, 0, 0, 0, null, null, 0, null, []),
                [1]),
            "b", "", null, false,
            new MeasurementOptions { Environment = null },
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.True(outcome.Result.ThreadControlEnabled);
    }

    [Fact]
    public void Build_Success_SetsInterferenceFilterEnabled_FromOptions()
    {
        var stats = new StatsSummary { Mean = 1 };
        var options = new MeasurementOptions
        {
            Interference = InterferenceOptions.Disabled,
        };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 1, null, 0, 0, 0, null, null, 0, null, []),
                [1]),
            "b", "", null, false,
            options,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.False(outcome.Result.InterferenceFilterEnabled);
    }

    [Fact]
    public void Build_Success_SetsInterferenceFilterEnabled_DefaultTrue()
    {
        var stats = new StatsSummary { Mean = 1 };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Success(
                new ProcessedMeasurements(stats, 1, null, 0, 0, 0, null, null, 0, null, []),
                [1]),
            "b", "", null, false,
            new MeasurementOptions(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.True(outcome.Result.InterferenceFilterEnabled);
    }

    [Fact]
    public void Build_DryRun_PreservesThreadControlAndInterferenceFilterSettings()
    {
        var options = new MeasurementOptions
        {
            Environment = new EnvironmentOptions { ThreadControl = false },
            Interference = InterferenceOptions.Disabled,
        };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.DryRun(),
            "dry", "", null, false,
            options,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.False(outcome.Result.ThreadControlEnabled);
        Assert.False(outcome.Result.InterferenceFilterEnabled);
    }

    [Fact]
    public void Build_Errored_PreservesThreadControlAndInterferenceFilterSettings()
    {
        var options = new MeasurementOptions
        {
            Environment = new EnvironmentOptions { ThreadControl = false },
            Interference = InterferenceOptions.Disabled,
        };

        var outcome = OutcomeBuilder.Build(
            new RunOutcome.Errored(new Exception("x")),
            "b", "", null, false,
            options,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.False(outcome.Result.ThreadControlEnabled);
        Assert.False(outcome.Result.InterferenceFilterEnabled);
    }

    [Fact]
    public void Build_Null_Input_Throws_ArgumentNullException()
    {
        RunOutcome bogus = null!;

        Assert.Throws<ArgumentNullException>(() =>
            OutcomeBuilder.Build(
                bogus,
                "b", "", null, false,
                new MeasurementOptions(),
                TimeSpan.Zero,
                TimeSpan.Zero));
    }
}
