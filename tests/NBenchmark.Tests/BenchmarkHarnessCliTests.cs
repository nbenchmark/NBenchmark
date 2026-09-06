using NBenchmark;
using NBenchmark.Discovery;
using NBenchmark.Observers;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class BenchmarkHarnessCliTests
{
    private const string OtlpEndpointEnvVar = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string NBenchmarkOtelEndpointEnvVar = "NBENCHMARK_OTEL_ENDPOINT";

    private static readonly string TestBenchmarksClassName = typeof(TestBenchmarks).FullName!;
    private static readonly string CategoryBenchmarksClassName = typeof(CategoryBenchmarks).FullName!;
    private static readonly string HarnessOrderBenchmarksClassName = typeof(HarnessOrderBenchmarks).FullName!;

    private static readonly string[] ManagedTelemetryEnvVars =
    [
        OtlpEndpointEnvVar,
        NBenchmarkOtelEndpointEnvVar,
    ];

    [Fact]
    public void Help_Flag_Returns_Empty_And_Prints_Help()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            var harness = BenchmarkHarness.Create(["--help"]);
            harness.RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("Usage:", stdout);
    }

    [Fact]
    public async Task RunAsync_OtlpEndpoint_Does_Not_Leak_Environment_Variables_When_Unset()
    {
        using var _ = WithTelemetryEnv();

        await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--help", "--otlp-endpoint", "http://collector:4317"])
                .RunAsync());

        Assert.Null(Environment.GetEnvironmentVariable(NBenchmarkOtelEndpointEnvVar));
        Assert.Null(Environment.GetEnvironmentVariable(OtlpEndpointEnvVar));
    }

    [Fact]
    public async Task RunAsync_OtlpEndpoint_Preserves_Explicit_Otel_Endpoint_And_Restores_NBenchmark_Endpoint()
    {
        using var _ = WithTelemetryEnv((OtlpEndpointEnvVar, "http://explicit:4317"));

        await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--help", "--otlp-endpoint", "http://collector:4318"])
                .RunAsync());

        Assert.Equal("http://explicit:4317", Environment.GetEnvironmentVariable(OtlpEndpointEnvVar));
        Assert.Null(Environment.GetEnvironmentVariable(NBenchmarkOtelEndpointEnvVar));
    }

    [Fact]
    public void Unknown_Flag_Sets_ExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CaptureConsoleOutput(() =>
            {
                CaptureConsoleError(() =>
                {
                    var harness = BenchmarkHarness.Create(["--bogus-flag"]);
                    harness.RunAsync().GetAwaiter().GetResult();
                });
            });

            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Threshold_Pct_No_Regression_With_Real_Benchmarks()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CaptureConsoleOutput(() =>
            {
                var harness = BenchmarkHarness.Create([
                    "--filter", "TestBenchmarks.*",
                    "--threshold-pct", "999999",
                    "--samples", "20",
                    "--warmup-samples", "3",
                    "--launch-count", "1",
                ]);

                harness.AddFromAssembly<TestBenchmarks>().WithRunOrder(RunOrder.Declaration).WithIsolation(Isolation.Off)
                    .RunAsync().GetAwaiter().GetResult();
            });

            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Threshold_Pct_Regression_Sets_ExitCode_One()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = string.Empty;

            CaptureConsoleOutput(() =>
            {
                stderr = CaptureConsoleError(() =>
                {
                    var harness = BenchmarkHarness.Create([
                        "--filter", "SlowVsBaselineBenchmarks.*",
                        "--threshold-pct", "1",
                        "--samples", "20",
                        "--warmup-samples", "3",
                        "--launch-count", "1",
                    ]);

                    harness.AddFromAssembly<SlowVsBaselineBenchmarks>().WithRunOrder(RunOrder.Declaration).WithIsolation(Isolation.Off)
                        .RunAsync().GetAwaiter().GetResult();
                });
            });

            Assert.Equal(1, Environment.ExitCode);
            Assert.Contains("Regression threshold exceeded", stderr);
            Assert.Contains("SlowVsBaselineBenchmarks.Slow", stderr);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public async Task RunAsync_Discovers_And_Executes_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));
    }

    [Fact]
    public async Task RunAsync_Without_LaunchCount_Uses_Harness_Default_MultiLaunch()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--samples", "1", "--warmup-samples", "0"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);

        Assert.All(results, r =>
        {
            Assert.NotNull(r.LaunchStatistics);
            Assert.Equal(LaunchCounts.HarnessDefault, r.LaunchStatistics!.LaunchCount);
        });
    }

    /// <summary>
    ///     <c>WithOptions</c> says nothing about how many launches to spend, so the harness default
    ///     still applies. Only <c>WithLaunchCount</c> - or the flag - suppresses it.
    /// </summary>
    /// <remarks>
    ///     This used to go the other way: a launch count lived on <see cref="MeasurementOptions" />, so
    ///     handing the harness any options object was read as choosing one, and
    ///     <c>WithRuntimeProfile</c> silently did the same thing for the same reason. Neither call has
    ///     an opinion on replicates, and now neither is treated as having one.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WithOptions_Keeps_The_Harness_Default_MultiLaunch()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*"])
                .AddFromAssembly<TestBenchmarks>()
                .WithOptions(MeasurementOptions.Default with { Samples = 1, WarmupSamples = 0 })
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);

        Assert.All(results, r =>
        {
            Assert.NotNull(r.LaunchStatistics);
            Assert.Equal(LaunchCounts.HarnessDefault, r.LaunchStatistics!.LaunchCount);
        });
    }

    /// <summary>
    ///     <c>WithLaunchCount(1)</c> is how a caller opts out of the harness default.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithLaunchCount_One_Suppresses_Harness_Default_MultiLaunch()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*"])
                .AddFromAssembly<TestBenchmarks>()
                .WithOptions(MeasurementOptions.Default with { Samples = 1, WarmupSamples = 0 })
                .WithLaunchCount(LaunchCounts.Single)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Null(r.LaunchStatistics));
    }

    [Fact]
    public async Task RunAsync_DryRun_Ignores_LaunchCount_And_Uses_Single_Launch()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--dry-run", "--launch-count", "9"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Null(r.LaunchStatistics));
    }

    [Fact]
    public async Task RunAsync_Observer_Flag_Attaches_Registry_Built_Observer()
    {
        // Register a capturing observer factory into ObserverRegistry, then activate it via
        // --observer from the CLI. The harness should build the observer via the registry and
        // attach it; OnResult fires for every benchmark.
        var observer = new CapturingObserver();
        ObserverRegistry.Reset();
        ObserverRegistry.Register("capturing", "Captures results for tests", () => observer);

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create([
                        "--filter", "TestBenchmarks.*", "--in-process", "--warmup-samples", "0", "--samples", "1", "--launch-count", "1", "--observer", "capturing",
                    ])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .RunAsync();
            });

            // TestBenchmarks has two methods; the registry-built observer receives both results.
            Assert.Equal(2, observer.Results.Count);
            Assert.Contains(observer.Results, r => r.Name == $"{TestBenchmarksClassName}.Fast");
            Assert.Contains(observer.Results, r => r.Name == $"{TestBenchmarksClassName}.FastBaseline");
        }
        finally
        {
            ObserverRegistry.Reset();
        }
    }

    [Fact]
    public async Task RunAsync_Observer_Flag_Unknown_Name_Writes_Error_And_Sets_ExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = string.Empty;

            await CaptureConsoleOutputAsync(async () =>
            {
                stderr = CaptureConsoleError(() =>
                {
                    var harness = BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--observer", "bogus-observer"]);
                    harness.RunAsync().GetAwaiter().GetResult();
                });
            });

            Assert.Contains("Unknown observer", stderr);
            Assert.Contains("bogus-observer", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
            ObserverRegistry.Reset();
        }
    }

    [Fact]
    public async Task RunAsync_Multiple_Observer_Flags_Compose_Observers()
    {
        var a = new CapturingObserver();
        var b = new CapturingObserver();
        ObserverRegistry.Reset();
        ObserverRegistry.Register("obs-a", "First test observer", () => a);
        ObserverRegistry.Register("obs-b", "Second test observer", () => b);

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create([
                        "--filter", "TestBenchmarks.*", "--in-process", "--warmup-samples", "0", "--samples", "1", "--launch-count", "1", "--observer", "obs-a",
                        "--observer", "obs-b",
                    ])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .RunAsync();
            });

            // Both registry-built observers receive every result (composite fan-out from CLI).
            Assert.Equal(2, a.Results.Count);
            Assert.Equal(2, b.Results.Count);
        }
        finally
        {
            ObserverRegistry.Reset();
        }
    }

    [Fact]
    public async Task RunAsync_Category_Filter_Includes_Only_Matching_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create([
                    "--filter", "CategoryBenchmarks.*", "--category", "String", "--samples", "5", "--warmup-samples", "2", "--launch-count", "1",
                ])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));
        Assert.Contains(results, r => r.Name == $"{CategoryBenchmarksClassName}.Concat");
        Assert.Contains(results, r => r.Name == $"{CategoryBenchmarksClassName}.ManyConcat");
    }

    [Fact]
    public async Task RunAsync_ExcludeCategory_Removes_Matching_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create([
                    "--filter", "CategoryBenchmarks.*", "--exclude-category", "Slow", "--samples", "5", "--warmup-samples", "2", "--launch-count", "1",
                ])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));
        Assert.DoesNotContain(results, r => r.Name == $"{CategoryBenchmarksClassName}.ManyConcat");
        Assert.Contains(results, r => r.Name == $"{CategoryBenchmarksClassName}.Compute");
    }

    [Fact]
    public async Task RunAsync_Category_Filter_And_Glob_Combine()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create([
                    "--filter", "CategoryBenchmarks.*", "--category", "String", "--exclude-category", "Slow", "--samples", "5", "--warmup-samples", "2",
                    "--launch-count", "1",
                ])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal($"{CategoryBenchmarksClassName}.Concat", results[0].Name);
    }

    [Fact]
    public async Task RunAsync_WithCategoryFilter_Programmatic_And_CLI_Compose()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create([
                    "--filter", "CategoryBenchmarks.*", "--category", "String", "--samples", "5", "--warmup-samples", "2", "--launch-count", "1",
                ])
                .AddFromAssembly<CategoryBenchmarks>()
                .FilterCategories(["Fast"])
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal($"{CategoryBenchmarksClassName}.Concat", results[0].Name);
    }

    [Fact]
    public void WithCategoryFilter_WithBlankCategory_Throws()
    {
        var harness = BenchmarkHarness.Create([]);
        Assert.Throws<ArgumentException>(() => harness.FilterCategories([" "]));
    }

    [Fact]
    public void RunAsync_List_Output_Shows_Categories()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHarness.Create(["--filter", "CategoryBenchmarks.*", "--list"])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithIsolation(Isolation.Off)
                .RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("[String, Fast]", stdout);
        Assert.Contains("[String, Slow]", stdout);
        Assert.Contains("[Number]", stdout);
    }

    [Fact]
    public async Task RunAsync_CategoryFilter_Empty_Intersection_Returns_No_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create([
                    "--filter", "CategoryBenchmarks.*", "--category", "String", "--samples", "5", "--warmup-samples", "2", "--launch-count", "1",
                ])
                .AddFromAssembly<CategoryBenchmarks>()
                .FilterCategories(["Number"])
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Empty(results);
    }

    [Fact]
    public async Task RunAsync_With_Random_Order_Shuffles_PerMethod_Benchmarks()
    {
        var ordered = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "HarnessOrderBenchmarks.*", "--seed", "7", "--launch-count", "1"])
                .AddFromAssembly<HarnessOrderBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync());

        var randomized = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "HarnessOrderBenchmarks.*", "--seed", "7", "--launch-count", "1"])
                .AddFromAssembly<HarnessOrderBenchmarks>()
                .WithRunOrder(RunOrder.Random)
                .WithInstanceLifetime(InstanceLifetime.PerMethod)
                .WithIsolation(Isolation.Off)
                .RunAsync());

        Assert.Equal(2, ordered.Count);
        Assert.Equal(2, randomized.Count);
        Assert.Equal($"{HarnessOrderBenchmarksClassName}.A", ordered[0].Name);
        Assert.Equal($"{HarnessOrderBenchmarksClassName}.B", ordered[1].Name);
        Assert.Equal($"{HarnessOrderBenchmarksClassName}.B", randomized[0].Name);
        Assert.Equal($"{HarnessOrderBenchmarksClassName}.A", randomized[1].Name);
    }

    [Fact]
    public async Task RunAsync_Emits_OnSuiteStarting_Before_Per_Class_Setup_And_OnSuiteCompleted_After_Per_Class_Teardown()
    {
        var events = new List<string>();

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithProgress(new OrderingProgress(
                    () => events.Add("onSuiteStarting"),
                    () => events.Add("onSuiteCompleted")))
                .WithIsolation(Isolation.Off)
                .RunAsync();
        });

        Assert.Equal(2, events.Count);
        Assert.Equal("onSuiteStarting", events[0]);
        Assert.Equal("onSuiteCompleted", events[1]);
    }

    [Fact]
    public async Task RunAsync_Applies_Output_Directory_To_File_Reporters()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-harness-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithIsolation(Isolation.Off)
                    .RunAsync();
            });

            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--launch-count", "1"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithReporter(new JsonReporter(tempDir))
                    .WithIsolation(Isolation.Off)
                    .RunAsync();
            });

            Assert.True(Directory.Exists(tempDir));
            Assert.NotEmpty(Directory.GetFiles(tempDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RunAsync_Output_Dir_Rebuilds_All_Seed_Reporters()
    {
        foreach (var name in ReporterRegistry.Available.Select(r => r.Name))
        {
            var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-harness-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                await CaptureConsoleOutputAsync(async () =>
                {
                    await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--reporter", name, "--output", tempDir, "--launch-count", "1"])
                        .AddFromAssembly<TestBenchmarks>()
                        .WithRunOrder(RunOrder.Declaration)
                        .WithIsolation(Isolation.Off)
                        .RunAsync();
                });

                Assert.True(Directory.Exists(tempDir), $"directory not created for reporter '{name}'");
                Assert.NotEmpty(Directory.GetFiles(tempDir));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void RunAsync_Unknown_Reporter_Prints_Available_List_And_Console_Hint()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = string.Empty;

            CaptureConsoleOutput(() =>
            {
                stderr = CaptureConsoleError(() =>
                {
                    var harness = BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--reporter", "bogus"]);
                    harness.RunAsync().GetAwaiter().GetResult();
                });
            });

            Assert.Contains("bogus", stderr);
            Assert.Contains("json", stderr);
            Assert.Contains("markdown", stderr);
            Assert.Contains("csv", stderr);
            Assert.Contains("NBenchmark.Reporters.Console", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public async Task RunAsync_Output_Dir_Leaves_Custom_Named_Reporter_Alone()
    {
        var customReporter = new CustomNamedReporter();
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-harness-custom-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--output", tempDir, "--launch-count", "1"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithReporter(customReporter)
                    .WithIsolation(Isolation.Off)
                    .RunAsync();
            });

            Assert.Equal(1, customReporter.ReportCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Detail_Flag_Advanced_Propagates_To_Reporter()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-harness-detail-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create([
                        "--filter", "TestBenchmarks.*", "--reporter", "csv", "--detail", "advanced", "--output", tempDir, "--launch-count", "1",
                    ])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithIsolation(Isolation.Off)
                    .RunAsync();
            });

            var csvFile = Assert.Single(Directory.GetFiles(tempDir, "*.csv"));
            var lines = await File.ReadAllLinesAsync(csvFile);
            Assert.Contains("Q1Ns", lines[0]);
            Assert.Contains("Skewness", lines[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Detail_Flag_Advanced_Applies_To_Custom_Reporter_Added_Programmatically()
    {
        var customReporter = new CustomNamedReporter();

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--detail", "advanced", "--launch-count", "1"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithReporter(customReporter)
                .WithIsolation(Isolation.Off)
                .RunAsync();
        });

        Assert.Equal(1, customReporter.ReportCount);
        Assert.Equal(ReportDetail.Advanced, customReporter.CapturedDetail);
    }

    [Fact]
    public async Task Detail_Flag_Simple_Is_The_Default()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-harness-detail-default-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--reporter", "csv", "--output", tempDir, "--launch-count", "1"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithIsolation(Isolation.Off)
                    .RunAsync();
            });

            var csvFile = Assert.Single(Directory.GetFiles(tempDir, "*.csv"));
            var lines = await File.ReadAllLinesAsync(csvFile);
            Assert.DoesNotContain("Q1Ns", lines[0]);
            Assert.DoesNotContain("Skewness", lines[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Detail_Flag_Invalid_Sets_ExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CaptureConsoleOutput(() =>
            {
                CaptureConsoleError(() =>
                {
                    var harness = BenchmarkHarness.Create(["--detail", "bogus"]);
                    harness.RunAsync().GetAwaiter().GetResult();
                });
            });

            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public async Task WithDetail_Advanced_Propagates_To_Reporter_Without_Rebuilding()
    {
        var customReporter = new CustomNamedReporter();
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-harness-withdetail-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--output", tempDir, "--launch-count", "1"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithReporter(customReporter)
                    .WithDetail(ReportDetail.Advanced)
                    .WithIsolation(Isolation.Off)
                    .RunAsync();
            });

            Assert.Equal(1, customReporter.ReportCount);
            Assert.Equal(ReportDetail.Advanced, customReporter.CapturedDetail);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AggregateRuntimes_Unions_Across_Suites()
    {
        var suites = new List<BenchmarkSuiteDefinition>
        {
            new(typeof(RuntimeAttributedHarnessBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8, RuntimeMoniker.Net9] },
            new(typeof(CliOverrideHarnessBenchmarks), []) { Runtimes = [RuntimeMoniker.Net10] },
        };

        var result = BenchmarkHarness.AggregateRuntimes(suites);

        Assert.Equal([RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10], result);
    }

    [Fact]
    public void AggregateRuntimes_Deduplicates_Duplicates()
    {
        var suites = new List<BenchmarkSuiteDefinition>
        {
            new(typeof(RuntimeAttributedHarnessBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8, RuntimeMoniker.Net9] },
            new(typeof(CliOverrideHarnessBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8, RuntimeMoniker.Net10] },
        };

        var result = BenchmarkHarness.AggregateRuntimes(suites);

        Assert.Equal([RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10], result);
    }

    [Fact]
    public void AggregateRuntimes_Empty_When_No_Attribute()
    {
        var suites = new List<BenchmarkSuiteDefinition>
        {
            new(typeof(NoRuntimeAttributeBenchmarks), []) { Runtimes = [] },
        };

        var result = BenchmarkHarness.AggregateRuntimes(suites);

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateRuntimes_Preserves_Declaration_Order()
    {
        var suites = new List<BenchmarkSuiteDefinition>
        {
            new(typeof(RuntimeAttributedHarnessBenchmarks), []) { Runtimes = [RuntimeMoniker.Net10] },
            new(typeof(CliOverrideHarnessBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8] },
        };

        var result = BenchmarkHarness.AggregateRuntimes(suites);

        Assert.Equal([RuntimeMoniker.Net10, RuntimeMoniker.Net8], result);
    }

    [Fact]
    public async Task No_Attribute_No_Cli_Flag_Runs_Single_Runtime()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHarness.Create(["--filter", "NoRuntimeAttributeBenchmarks.*", "--samples", "5", "--warmup-samples", "2", "--launch-count", "1"])
                .AddFromAssembly<NoRuntimeAttributeBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(Isolation.Off)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.False(results[0].Errored);
    }

    [Fact]
    public void Help_Flag_Works_With_Runtimes_Attribute_Present()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHarness.Create(["--help"])
                .AddFromAssembly<RuntimeAttributedHarnessBenchmarks>()
                .RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("Usage:", stdout);
        Assert.DoesNotContain("Building for runtimes:", stdout);
    }

    [Fact]
    public void List_Flag_Works_With_Runtimes_Attribute_Present()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHarness.Create(["--filter", "RuntimeAttributedHarnessBenchmarks.*", "--list"])
                .AddFromAssembly<RuntimeAttributedHarnessBenchmarks>()
                .WithIsolation(Isolation.Off)
                .RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("RuntimeAttributedHarnessBenchmarks", stdout);
        Assert.DoesNotContain("Building for runtimes:", stdout);
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return sw.ToString();
    }

    private static string CaptureConsoleError(Action action)
    {
        var sw = new StringWriter();
        var original = Console.Error;
        Console.SetError(sw);

        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return sw.ToString();
    }

    private static async Task<T> CaptureConsoleOutputAsync<T>(Func<Task<T>> action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            return await action();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static async Task CaptureConsoleOutputAsync(Func<Task> action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static IDisposable WithTelemetryEnv(IEnumerable<(string Name, string? Value)> vars)
    {
        var saved = new Dictionary<string, string?>();

        foreach (var name in ManagedTelemetryEnvVars)
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
        }

        foreach (var name in ManagedTelemetryEnvVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var (name, value) in vars)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return new EnvVarScope(saved);
    }

    private static IDisposable WithTelemetryEnv(params (string Name, string Value)[] vars)
        => WithTelemetryEnv(vars.Select(v => (v.Name, (string?)v.Value)));

    private sealed class EnvVarScope : IDisposable
    {
        private readonly Dictionary<string, string?> _saved;

        public EnvVarScope(Dictionary<string, string?> saved)
        {
            _saved = saved;
        }

        public void Dispose()
        {
            foreach (var (name, value) in _saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed class CustomNamedReporter : IReporter
    {
        public int ReportCount { get; private set; }
        public ReportDetail CapturedDetail { get; private set; }
        public string Name => "custom";

        public ReportDetail Detail
        {
            get => CapturedDetail;
            set => CapturedDetail = value;
        }

        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default)
        {
            ReportCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingObserver : IMeasurementObserver
    {
        public List<BenchmarkResult> Results { get; } = [];

        public void OnPhase(in MeasurementPhaseEvent e)
        {
        }

        public void OnSample(in SampleEvent e)
        {
        }

        public void OnDetector(in DetectorStateEvent e)
        {
        }

        public void OnResult(BenchmarkResult result) => Results.Add(result);
    }

    private sealed class OrderingProgress : IBenchmarkProgress
    {
        private readonly Action _onSuiteCompleted;
        private readonly Action _onSuiteStarting;

        public OrderingProgress(Action onSuiteStarting, Action onSuiteCompleted)
        {
            _onSuiteStarting = onSuiteStarting;
            _onSuiteCompleted = onSuiteCompleted;
        }

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
        {
            _onSuiteStarting();
            return Task.CompletedTask;
        }

        public Task OnWarmupStarting(string name, int totalWarmupSamples) => Task.CompletedTask;
        public Task OnWarmupCompleted(string name) => Task.CompletedTask;
        public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;
        public Task OnSampleCompleted(string name, int sample, int totalSamples) => Task.CompletedTask;
        public Task OnBenchmarkCompleted(BenchmarkResult result) => Task.CompletedTask;

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
        {
            _onSuiteCompleted();
            return Task.CompletedTask;
        }
    }
}

public class CategoryBenchmarks
{
    [Benchmark]
    [BenchmarkCategory("String")]
    [BenchmarkCategory("Fast")]
    public int Concat() => 1;

    [Benchmark]
    [BenchmarkCategory("String")]
    [BenchmarkCategory("Slow")]
    public int ManyConcat() => 2;

    [Benchmark]
    [BenchmarkCategory("Number")]
    public int Compute() => 3;
}

public class TestBenchmarks
{
    [Benchmark]
    public int Fast() => 1 + 1;

    [Benchmark(Baseline = true)]
    public int FastBaseline() => 2 + 2;
}

public class SlowVsBaselineBenchmarks
{
    [Benchmark(Baseline = true)]
    public int FastBaseline()
    {
        Thread.SpinWait(5_000);
        return 1;
    }

    [Benchmark]
    public int Slow()
    {
        Thread.SpinWait(20_000);
        return 1;
    }
}

public class HarnessOrderBenchmarks
{
    [Benchmark]
    public int A() => 1;

    [Benchmark]
    public int B() => 2;
}

[Runtimes("net8.0", "net9.0")]
public class RuntimeAttributedHarnessBenchmarks
{
    [Benchmark]
    public int A() => 1;
}

[Runtimes("net10.0")]
public class CliOverrideHarnessBenchmarks
{
    [Benchmark]
    public int A() => 1;
}

public class NoRuntimeAttributeBenchmarks
{
    [Benchmark]
    public int A() => 1;
}
