using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkRunnerTests
{
    // ---------- Warmup & progress lifecycle ----------

    [Fact]
    public async Task RunAsync_Emits_OnWarmupStarting_With_Configured_Count()
    {
        var seen = new CapturingProgress();
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 4, Iterations = 2, OutlierMode = OutlierMode.None },
            Progress = seen,
        };

        await BenchmarkRunner.Instance.RunAsync("a", () => Task.CompletedTask, spec);

        Assert.Equal(1, seen.WarmupStartingCount);
        Assert.Equal("a", seen.WarmupStartingName);
        Assert.Equal(4, seen.WarmupStartingTotal);
    }

    [Fact]
    public async Task RunAsync_Emits_OnWarmupCompleted_After_Measurement()
    {
        var seen = new CapturingProgress();
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 2, OutlierMode = OutlierMode.None },
            Progress = seen,
        };

        await BenchmarkRunner.Instance.RunAsync("a", () => Task.CompletedTask, spec);

        Assert.Equal(1, seen.WarmupCompletedCount);
    }

    [Fact]
    public async Task RunAsync_Does_Not_Emit_Suite_Level_Callbacks()
    {
        var seen = new CapturingProgress();
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 1, OutlierMode = OutlierMode.None },
            Progress = seen,
        };

        await BenchmarkRunner.Instance.RunAsync("a", () => Task.CompletedTask, spec);

        Assert.Equal(0, seen.SuiteStartingCount);
        Assert.Equal(0, seen.BenchmarkStartingCount);
        Assert.Equal(0, seen.BenchmarkCompletedCount);
        Assert.Equal(0, seen.SuiteCompletedCount);
    }

    // ---------- Error translation ----------

    [Fact]
    public async Task RunAsync_Translates_Action_Exception_To_Errored_Result()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 2, OutlierMode = OutlierMode.None },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("bad",
            () => throw new InvalidOperationException("nope"), spec);

        Assert.True(outcome.Result.Errored);
        Assert.Contains("nope", outcome.Result.ErrorMessage);
        Assert.Equal(0, outcome.Result.MeasuredIterations);
        Assert.Empty(outcome.RawSamples);
    }

    [Fact]
    public async Task RunAsync_Propagates_OperationCanceledException_Untouched()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 1, OutlierMode = OutlierMode.None },
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await BenchmarkRunner.Instance.RunAsync("cancelled", () => Task.CompletedTask, spec, cts.Token));
    }

    // ---------- JIT-elision Consume wrap ----------

    [Fact]
    public void Run_With_Func_Int_Wraps_With_Consume_Internally()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None },
        };

        var outcome = BenchmarkRunner.Instance.Run("int-bench", () => 42, spec);

        Assert.True(outcome.Result.Mean > 0);
        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public void Run_With_Func_String_Wraps_With_Consume_Internally()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None },
        };

        var outcome = BenchmarkRunner.Instance.Run("string-bench", () => "hello", spec);

        Assert.True(outcome.Result.Mean > 0);
        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public async Task RunAsync_With_Func_Task_Of_Int_Wraps_With_Consume_Internally()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("async-int",
            () => Task.FromResult(7), spec);

        Assert.True(outcome.Result.Mean > 0);
        Assert.False(outcome.Result.Errored);
    }

    [Fact]
    public async Task RunAsync_With_Func_Task_Of_String_Wraps_With_Consume_Internally()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("async-string",
            () => Task.FromResult("world"), spec);

        Assert.True(outcome.Result.Mean > 0);
        Assert.False(outcome.Result.Errored);
    }

    // ---------- Dry-run shape ----------

    [Fact]
    public void Run_With_Zero_Iterations_And_Zero_Warmup_Returns_Zeroed_Result_Without_Invoking_Body()
    {
        var invoked = 0;
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 0, Iterations = 0 },
        };

        var outcome = BenchmarkRunner.Instance.Run("dry", () => invoked++, spec);

        Assert.Equal(0, invoked);
        Assert.Equal(0, outcome.Result.MeasuredIterations);
        Assert.Equal(0, outcome.Result.Mean);
        Assert.Equal(0, outcome.Result.Median);
        Assert.False(outcome.Result.Errored);
        Assert.Empty(outcome.RawSamples);
    }

    [Fact]
    public void Run_With_NonZero_Warmup_And_Zero_Iterations_Runs_Warmup_Only()
    {
        var invoked = 0;
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 3, Iterations = 0, OutlierMode = OutlierMode.None },
        };

        var outcome = BenchmarkRunner.Instance.Run("warmup-only", () => invoked++, spec);

        Assert.Equal(3, invoked);
        Assert.Equal(0, outcome.Result.MeasuredIterations);
        Assert.Equal(0, outcome.Result.Mean);
    }

    // ---------- Outlier mode + confidence level plumbing ----------

    [Fact]
    public void Run_Passes_Outlier_Mode_Through_To_Result()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 50, OutlierMode = OutlierMode.None },
        };

        var outcome = BenchmarkRunner.Instance.Run("a", () => Thread.SpinWait(100), spec);

        Assert.Equal(OutlierMode.None, outcome.Result.OutlierMode);
    }

    [Fact]
    public void Run_Passes_Confidence_Level_Through_To_Stats()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions
            {
                WarmupIterations = 1,
                Iterations = 20,
                OutlierMode = OutlierMode.None,
                ConfidenceLevel = 0.99,
            },
        };

        var outcome = BenchmarkRunner.Instance.Run("a", () => Thread.SpinWait(100), spec);

        Assert.Equal(0.99, outcome.Result.ConfidenceLevel);
        Assert.True(outcome.Result.MarginOfError >= 0);
    }

    // ---------- Allocation tracking ----------

    [Fact]
    public async Task RunAsync_With_MeasureAllocations_Records_Deltas_And_Sets_MeanAllocatedBytes()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions
            {
                WarmupIterations = 1,
                Iterations = 10,
                MeasureAllocations = true,
                OutlierMode = OutlierMode.None,
            },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("alloc",
            () =>
            {
                _ = new byte[64 * 1024];
                return Task.CompletedTask;
            }, spec);

        Assert.NotNull(outcome.Result.MeanAllocatedBytes);
        Assert.True(outcome.Result.MeanAllocatedBytes >= 1024);
    }

    // ---------- IsBaseline plumbing ----------

    [Fact]
    public void Run_With_IsBaseline_True_Sets_IsBaseline_On_Result()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 5, OutlierMode = OutlierMode.None },
            IsBaseline = true,
        };

        var outcome = BenchmarkRunner.Instance.Run("baseline", () => Thread.SpinWait(100), spec);

        Assert.True(outcome.Result.IsBaseline);
    }

    // ---------- Cancellation propagation ----------

    [Fact]
    public async Task RunAsync_Cancellation_Throws_Before_Starting_Next_Iteration()
    {
        var invoked = 0;
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 100, OutlierMode = OutlierMode.None },
        };

        using var cts = new CancellationTokenSource();

        var task = BenchmarkRunner.Instance.RunAsync("cancellable", () =>
        {
            invoked++;
            if (invoked == 5) cts.Cancel();
            return Task.CompletedTask;
        }, spec, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(invoked >= 4);
        Assert.True(invoked < 100);
    }

    // ---------- Hot-loop timing invariant (replaces TimingSanityTests at the new seam) ----------

    [Fact]
    public void HotLoop_MinimumSample_Is_Near_Known_BusyWait_Floor()
    {
        const double targetMicros = 5_000.0;
        const double targetNanos = targetMicros * 1_000.0;

        var outcome = BenchmarkRunner.Instance.Run("busywait",
            () => BusyWait(targetMicros),
            new RunSpec
            {
                Options = new MeasurementOptions
                {
                    WarmupIterations = 3,
                    Iterations = 15,
                    OutlierMode = OutlierMode.None,
                    MeasureAllocations = false,
                },
            });

        Assert.InRange(outcome.Result.Min, targetNanos * 0.9, targetNanos * 3.0);
    }

    private static void BusyWait(double microseconds)
    {
        var ticks = (long)(microseconds * System.Diagnostics.Stopwatch.Frequency / 1_000_000.0);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetTimestamp() - start < ticks)
        {
        }
    }

    private sealed class CapturingProgress : IBenchmarkProgress
    {
        public int SuiteStartingCount;
        public int WarmupStartingCount;
        public string? WarmupStartingName;
        public int WarmupStartingTotal;
        public int WarmupCompletedCount;
        public int BenchmarkStartingCount;
        public int BenchmarkCompletedCount;
        public int SuiteCompletedCount;

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
        {
            SuiteStartingCount++;
            return Task.CompletedTask;
        }

        public Task OnWarmupStarting(string name, int totalWarmupIterations)
        {
            WarmupStartingCount++;
            WarmupStartingName = name;
            WarmupStartingTotal = totalWarmupIterations;
            return Task.CompletedTask;
        }

        public Task OnWarmupCompleted(string name)
        {
            WarmupCompletedCount++;
            return Task.CompletedTask;
        }

        public Task OnBenchmarkStarting(string name, int index, int total)
        {
            BenchmarkStartingCount++;
            return Task.CompletedTask;
        }

        public Task OnBenchmarkCompleted(BenchmarkResult result)
        {
            BenchmarkCompletedCount++;
            return Task.CompletedTask;
        }

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
        {
            SuiteCompletedCount++;
            return Task.CompletedTask;
        }
    }
}
