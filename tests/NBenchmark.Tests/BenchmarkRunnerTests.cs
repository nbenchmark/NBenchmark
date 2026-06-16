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
    public async Task RunAsync_Errored_Result_Records_Positive_TotalDuration()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 2, OutlierMode = OutlierMode.None },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("bad",
            () => throw new InvalidOperationException("nope"), spec);

        Assert.True(outcome.Result.Errored);

        Assert.True(outcome.Result.TotalDuration > TimeSpan.Zero,
            $"Expected TotalDuration > 0 on errored path, got {outcome.Result.TotalDuration}");

        Assert.Equal(TimeSpan.Zero, outcome.Result.MeasuredDuration);
    }

    [Fact]
    public async Task RunAsync_Errored_Result_Reports_Pinned_Warmup_Count()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 7, Iterations = 2, OutlierMode = OutlierMode.None },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("bad",
            () => throw new InvalidOperationException("nope"), spec);

        Assert.True(outcome.Result.Errored);
        Assert.Equal(7, outcome.Result.WarmupIterations);
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

    [Fact]
    public void Run_UserCode_OperationCanceledException_Becomes_Errored_Result()
    {
        // A body-thrown OCE (e.g. an HttpClient timeout) must not abort the whole
        // run when the runner's own token was never cancelled.
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 2, OutlierMode = OutlierMode.None },
        };

        var outcome = BenchmarkRunner.Instance.Run("user-oce",
            () => throw new OperationCanceledException("internal timeout"), spec);

        Assert.True(outcome.Result.Errored);
        Assert.Contains("internal timeout", outcome.Result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_UserCode_TaskCanceledException_Becomes_Errored_Result()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions { WarmupIterations = 1, Iterations = 2, OutlierMode = OutlierMode.None },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("user-tce",
            () => Task.FromException(new TaskCanceledException("client timeout")), spec);

        Assert.True(outcome.Result.Errored);
        Assert.Contains("client timeout", outcome.Result.ErrorMessage);
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

        Assert.True(outcome.Result.TotalDuration > TimeSpan.Zero,
            "Dry-run path should still record wall-clock cost of the runner lifecycle");

        Assert.Equal(TimeSpan.Zero, outcome.Result.MeasuredDuration);
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

        Assert.True(outcome.Result.TotalDuration > TimeSpan.Zero,
            "Dry-run-with-warmup path should record wall-clock cost including warmup");

        Assert.Equal(TimeSpan.Zero, outcome.Result.MeasuredDuration);
    }

    // ---------- Deterministic clock seam ----------

    [Fact]
    public void Run_With_InjectedClock_DryRun_Uses_Scheduled_TotalDuration()
    {
        var clock = new FakeClock([TimeSpan.FromTicks(42)]);
        var runner = new BenchmarkRunner(clock);

        var outcome = runner.Run("dry", () => { }, new RunSpec
        {
            Options = new MeasurementOptions
            {
                WarmupIterations = 0,
                Iterations = 0,
                OutlierMode = OutlierMode.None,
            },
        });

        Assert.Equal(TimeSpan.FromTicks(42), outcome.Result.TotalDuration);
        Assert.Equal(TimeSpan.Zero, outcome.Result.MeasuredDuration);
        Assert.Equal(0, clock.PendingElapsedCount);
    }

    [Fact]
    public void Run_With_InjectedClock_SyncVoid_Success_Uses_Scheduled_Durations()
    {
        var clock = new FakeClock([
            TimeSpan.FromTicks(120), // total
            TimeSpan.FromTicks(100), // tuning span
            TimeSpan.FromTicks(80), // measured loop
            TimeSpan.FromTicks(10), // sample 1
            TimeSpan.FromTicks(30), // sample 2
        ]);

        var runner = new BenchmarkRunner(clock);

        var outcome = runner.Run("sync-void", () => { }, DeterministicSuccessSpec());

        Assert.Equal([1_000.0, 3_000.0], outcome.RawSamples);
        Assert.Equal(2_000.0, outcome.Result.Mean);
        Assert.Equal(TimeSpan.FromTicks(80), outcome.Result.MeasuredDuration);
        Assert.Equal(TimeSpan.FromTicks(120), outcome.Result.TotalDuration);
        Assert.Equal(0, clock.PendingElapsedCount);
    }

    [Fact]
    public void Run_With_InjectedClock_Preserves_Raw_Sample_Order_Before_Trim()
    {
        var clock = new FakeClock([
            TimeSpan.FromTicks(520), // total
            TimeSpan.FromTicks(500), // tuning span
            TimeSpan.FromTicks(480), // measured loop
            TimeSpan.FromTicks(30), // sample 1
            TimeSpan.FromTicks(10), // sample 2
        ]);

        var runner = new BenchmarkRunner(clock);

        var outcome = runner.Run("raw-order", () => { }, DeterministicSuccessSpec());

        Assert.Equal([3_000.0, 1_000.0], outcome.RawSamples);
        Assert.Equal(1_000.0, outcome.Result.Min);
        Assert.Equal(3_000.0, outcome.Result.Max);
        Assert.Equal(0, clock.PendingElapsedCount);
    }

    [Fact]
    public void Run_With_InjectedClock_SyncReturning_Success_Uses_Scheduled_Durations()
    {
        var clock = new FakeClock([
            TimeSpan.FromTicks(220), // total
            TimeSpan.FromTicks(200), // tuning span
            TimeSpan.FromTicks(180), // measured loop
            TimeSpan.FromTicks(40), // sample 1
            TimeSpan.FromTicks(20), // sample 2
        ]);

        var runner = new BenchmarkRunner(clock);

        var outcome = runner.Run("sync-returning", () => 123, DeterministicSuccessSpec());

        Assert.Equal([4_000.0, 2_000.0], outcome.RawSamples);
        Assert.Equal(3_000.0, outcome.Result.Mean);
        Assert.Equal(TimeSpan.FromTicks(180), outcome.Result.MeasuredDuration);
        Assert.Equal(TimeSpan.FromTicks(220), outcome.Result.TotalDuration);
        Assert.Equal(0, clock.PendingElapsedCount);
    }

    [Fact]
    public async Task RunAsync_With_InjectedClock_AsyncVoid_Success_Uses_Scheduled_Durations()
    {
        var clock = new FakeClock([
            TimeSpan.FromTicks(320), // total
            TimeSpan.FromTicks(300), // tuning span
            TimeSpan.FromTicks(280), // measured loop
            TimeSpan.FromTicks(15), // sample 1
            TimeSpan.FromTicks(25), // sample 2
        ]);

        var runner = new BenchmarkRunner(clock);

        var outcome = await runner.RunAsync("async-void", () => Task.CompletedTask, DeterministicSuccessSpec());

        Assert.Equal([1_500.0, 2_500.0], outcome.RawSamples);
        Assert.Equal(2_000.0, outcome.Result.Mean);
        Assert.Equal(TimeSpan.FromTicks(280), outcome.Result.MeasuredDuration);
        Assert.Equal(TimeSpan.FromTicks(320), outcome.Result.TotalDuration);
        Assert.Equal(0, clock.PendingElapsedCount);
    }

    [Fact]
    public async Task RunAsync_With_InjectedClock_AsyncReturning_Success_Uses_Scheduled_Durations()
    {
        var clock = new FakeClock([
            TimeSpan.FromTicks(420), // total
            TimeSpan.FromTicks(400), // tuning span
            TimeSpan.FromTicks(380), // measured loop
            TimeSpan.FromTicks(55), // sample 1
            TimeSpan.FromTicks(35), // sample 2
        ]);

        var runner = new BenchmarkRunner(clock);

        var outcome = await runner.RunAsync("async-returning", () => Task.FromResult("ok"), DeterministicSuccessSpec());

        Assert.Equal([5_500.0, 3_500.0], outcome.RawSamples);
        Assert.Equal(4_500.0, outcome.Result.Mean);
        Assert.Equal(TimeSpan.FromTicks(380), outcome.Result.MeasuredDuration);
        Assert.Equal(TimeSpan.FromTicks(420), outcome.Result.TotalDuration);
        Assert.Equal(0, clock.PendingElapsedCount);
    }

    [Fact]
    public async Task RunAsync_With_InjectedClock_Errored_Path_Uses_Scheduled_TotalDuration()
    {
        var clock = new FakeClock([TimeSpan.FromTicks(77)]);
        var runner = new BenchmarkRunner(clock);

        var outcome = await runner.RunAsync("boom", () => throw new InvalidOperationException("boom"),
            new RunSpec
            {
                Options = new MeasurementOptions
                {
                    WarmupIterations = 1,
                    Iterations = 2,
                    OutlierMode = OutlierMode.None,
                },
            });

        Assert.True(outcome.Result.Errored);
        Assert.Equal(TimeSpan.FromTicks(77), outcome.Result.TotalDuration);
        Assert.Equal(TimeSpan.Zero, outcome.Result.MeasuredDuration);
        Assert.Equal(0, clock.PendingElapsedCount);
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
                MeasureAllocationsOverride = true,
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

    [Fact]
    public async Task Run_With_MeasureAllocations_Sync_Path_Ignores_Background_Thread_Noise()
    {
        using var cts = new CancellationTokenSource();

        var backgroundAllocator = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                _ = new byte[1024];
            }
        }, cts.Token);

        try
        {
            var spec = new RunSpec
            {
                Options = new MeasurementOptions
                {
                    WarmupIterations = 1,
                    Iterations = 30,
                    MeasureAllocationsOverride = true,
                    ForceGcBeforeEachIterationOverride = false,
                    OutlierMode = OutlierMode.None,
                },
            };

            var outcome = BenchmarkRunner.Instance.Run("alloc-sync-noise", () => Thread.SpinWait(20_000), spec);

            Assert.NotNull(outcome.Result.MeanAllocatedBytes);

            Assert.True(outcome.Result.MeanAllocatedBytes < 64 * 1024,
                $"Expected sync allocation measurement to stay near zero despite background allocator; got {outcome.Result.MeanAllocatedBytes} bytes/op.");
        }
        finally
        {
            cts.Cancel();

            try
            {
                await backgroundAllocator;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation.
            }
        }
    }

    [Fact]
    public async Task RunAsync_With_MeasureAllocations_Thread_Hop_Reports_NonZero_Allocations()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions
            {
                WarmupIterations = 1,
                Iterations = 10,
                MeasureAllocationsOverride = true,
                OutlierMode = OutlierMode.None,
            },
        };

        var outcome = await BenchmarkRunner.Instance.RunAsync("alloc-thread-hop",
            async () =>
            {
                await Task.Yield();
                _ = new byte[2048];
            }, spec);

        Assert.NotNull(outcome.Result.MeanAllocatedBytes);

        Assert.True(outcome.Result.MeanAllocatedBytes >= 512,
            $"Expected async thread-hop benchmark to report at least 512 bytes/op; got {outcome.Result.MeanAllocatedBytes}");
    }

    [Fact]
    public async Task RunAsync_With_MeasureAllocations_Thread_Hop_With_Background_Noise_Still_Reports_Body_Allocations()
    {
        // Allocation tracking for async benchmarks that thread-hop falls back to
        // process-wide GetTotalAllocatedBytes when the continuation resumes on a
        // different thread.  When the continuation happens to resume on the original
        // thread the per-thread counter is used instead, which can miss allocations
        // made on the intermediate thread.  Combined with a noisy background allocator
        // this makes the per-iteration delta highly variable across CI environments,
        // so we only assert that the mechanism produces a non-zero result rather than
        // checking a specific threshold.
        using var cts = new CancellationTokenSource();

        var backgroundAllocator = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                _ = new byte[1024];
            }
        }, cts.Token);

        try
        {
            var spec = new RunSpec
            {
                Options = new MeasurementOptions
                {
                    WarmupIterations = 1,
                    Iterations = 30,
                    MeasureAllocationsOverride = true,
                    ForceGcBeforeEachIterationOverride = false,
                    OutlierMode = OutlierMode.RemoveTop5Percent,
                },
            };

            var outcome = await BenchmarkRunner.Instance.RunAsync("alloc-async-noise",
                async () =>
                {
                    await Task.Yield();
                    _ = new byte[4096];
                }, spec);

            Assert.NotNull(outcome.Result.MeanAllocatedBytes);

            Assert.True(outcome.Result.MeanAllocatedBytes > 0,
                $"Expected async thread-hop benchmark with background noise to report body allocations; got {outcome.Result.MeanAllocatedBytes}");
        }
        finally
        {
            cts.Cancel();

            try
            {
                await backgroundAllocator;
            }
            catch (OperationCanceledException)
            {
            }
        }
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

            if (invoked == 5)
                cts.Cancel();

            return Task.CompletedTask;
        }, spec, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.True(invoked >= 4);
        Assert.True(invoked < 100);
    }

    // ---------- TotalDuration / MeasuredDuration contract ----------

    [Fact]
    public void Run_Success_Result_Has_TotalDuration_At_Least_MeasuredDuration()
    {
        var spec = new RunSpec
        {
            Options = new MeasurementOptions
            {
                WarmupIterations = 3,
                Iterations = 10,
                OutlierMode = OutlierMode.None,
            },
        };

        var outcome = BenchmarkRunner.Instance.Run("success-durations",
            () => Thread.SpinWait(50), spec);

        Assert.False(outcome.Result.Errored);

        Assert.True(outcome.Result.TotalDuration > TimeSpan.Zero,
            $"Expected TotalDuration > 0 on success, got {outcome.Result.TotalDuration}");

        Assert.True(outcome.Result.MeasuredDuration > TimeSpan.Zero,
            $"Expected MeasuredDuration > 0 on success, got {outcome.Result.MeasuredDuration}");

        Assert.True(outcome.Result.TotalDuration >= outcome.Result.MeasuredDuration,
            $"TotalDuration ({outcome.Result.TotalDuration}) must be >= MeasuredDuration ({outcome.Result.MeasuredDuration})");
    }

    private static RunSpec DeterministicSuccessSpec()
    {
        return new RunSpec
        {
            Options = new MeasurementOptions
            {
                WarmupIterations = 0,
                Iterations = 2,
                OpsPerSample = 1,
                OutlierMode = OutlierMode.None,
                MeasureAllocationsOverride = false,
            },
        };
    }

    private sealed class CapturingProgress : IBenchmarkProgress
    {
        public int BenchmarkCompletedCount;
        public int BenchmarkStartingCount;
        public int SuiteCompletedCount;
        public int SuiteStartingCount;
        public int WarmupCompletedCount;
        public int WarmupStartingCount;
        public string? WarmupStartingName;
        public int WarmupStartingTotal;

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

        public Task OnIterationCompleted(string name, int iteration, int totalIterations) => Task.CompletedTask;

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
