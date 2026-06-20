using NBenchmark.Attributes;
using NBenchmark.Discovery;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class BenchmarkHostCliTests
{
    [Fact]
    public void Help_Flag_Returns_Empty_And_Prints_Help()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            var host = BenchmarkHost.Create(["--help"]);
            host.RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("Usage:", stdout);
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
                    var host = BenchmarkHost.Create(["--bogus-flag"]);
                    host.RunAsync().GetAwaiter().GetResult();
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
                var host = BenchmarkHost.Create([
                    "--filter", "TestBenchmarks.*",
                    "--threshold-pct", "999999",
                    "--iterations", "20",
                    "--warmup", "3",
                ]);

                host.AddFromAssembly<TestBenchmarks>().WithRunOrder(RunOrder.Declaration).WithIsolation(false)
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
                    var host = BenchmarkHost.Create([
                        "--filter", "SlowVsBaselineBenchmarks.*",
                        "--threshold-pct", "1",
                        "--iterations", "20",
                        "--warmup", "3",
                    ]);

                    host.AddFromAssembly<SlowVsBaselineBenchmarks>().WithRunOrder(RunOrder.Declaration).WithIsolation(false)
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
            await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));
    }

    [Fact]
    public async Task RunAsync_Category_Filter_Includes_Only_Matching_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "CategoryBenchmarks.*", "--category", "String", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));
        Assert.Contains(results, r => r.Name == "CategoryBenchmarks.Concat");
        Assert.Contains(results, r => r.Name == "CategoryBenchmarks.ManyConcat");
    }

    [Fact]
    public async Task RunAsync_ExcludeCategory_Removes_Matching_Benchmarks()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "CategoryBenchmarks.*", "--exclude-category", "Slow", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored));
        Assert.DoesNotContain(results, r => r.Name == "CategoryBenchmarks.ManyConcat");
        Assert.Contains(results, r => r.Name == "CategoryBenchmarks.Compute");
    }

    [Fact]
    public async Task RunAsync_Category_Filter_And_Glob_Combine()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create([
                    "--filter", "CategoryBenchmarks.*", "--category", "String", "--exclude-category", "Slow", "--iterations", "5", "--warmup", "2",
                ])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal("CategoryBenchmarks.Concat", results[0].Name);
    }

    [Fact]
    public async Task RunAsync_WithCategoryFilter_Programmatic_And_CLI_Compose()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "CategoryBenchmarks.*", "--category", "String", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithCategoryFilter(["Fast"])
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Single(results);
        Assert.Equal("CategoryBenchmarks.Concat", results[0].Name);
    }

    [Fact]
    public void WithCategoryFilter_WithBlankCategory_Throws()
    {
        var host = BenchmarkHost.Create([]);
        Assert.Throws<ArgumentException>(() => host.WithCategoryFilter([" "]));
    }

    [Fact]
    public void RunAsync_List_Output_Shows_Categories()
    {
        var stdout = CaptureConsoleOutput(() =>
        {
            BenchmarkHost.Create(["--filter", "CategoryBenchmarks.*", "--list"])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithIsolation(false)
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
            await BenchmarkHost.Create(["--filter", "CategoryBenchmarks.*", "--category", "String", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<CategoryBenchmarks>()
                .WithCategoryFilter(["Number"])
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync()
        );

        Assert.Empty(results);
    }

    [Fact]
    public async Task RunAsync_With_Random_Order_Shuffles_PerMethod_Benchmarks()
    {
        var ordered = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "HostOrderBenchmarks.*", "--seed", "7"])
                .AddFromAssembly<HostOrderBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync());

        var randomized = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "HostOrderBenchmarks.*", "--seed", "7"])
                .AddFromAssembly<HostOrderBenchmarks>()
                .WithRunOrder(RunOrder.Random)
                .WithInstanceLifetime(InstanceLifetime.PerMethod)
                .WithIsolation(false)
                .RunAsync());

        Assert.Equal(2, ordered.Count);
        Assert.Equal(2, randomized.Count);
        Assert.Equal("HostOrderBenchmarks.A", ordered[0].Name);
        Assert.Equal("HostOrderBenchmarks.B", ordered[1].Name);
        Assert.Equal("HostOrderBenchmarks.B", randomized[0].Name);
        Assert.Equal("HostOrderBenchmarks.A", randomized[1].Name);
    }

    [Fact]
    public async Task RunAsync_Emits_OnSuiteStarting_Before_Per_Class_Setup_And_OnSuiteCompleted_After_Per_Class_Teardown()
    {
        var events = new List<string>();

        await CaptureConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithProgress(new OrderingProgress(
                    () => events.Add("onSuiteStarting"),
                    () => events.Add("onSuiteCompleted")))
                .WithIsolation(false)
                .RunAsync();
        });

        Assert.Equal(2, events.Count);
        Assert.Equal("onSuiteStarting", events[0]);
        Assert.Equal("onSuiteCompleted", events[1]);
    }

    [Fact]
    public async Task RunAsync_Applies_Output_Directory_To_File_Reporters()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-host-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithIsolation(false)
                    .RunAsync();
            });

            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*"])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithReporter(new JsonReporter(tempDir))
                    .WithIsolation(false)
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
            var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-host-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                await CaptureConsoleOutputAsync(async () =>
                {
                    await BenchmarkHost.Create(["--filter", "TestBenchmarks.*", "--reporter", name, "--output", tempDir])
                        .AddFromAssembly<TestBenchmarks>()
                        .WithRunOrder(RunOrder.Declaration)
                        .WithIsolation(false)
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
                    var host = BenchmarkHost.Create(["--filter", "TestBenchmarks.*", "--reporter", "bogus"]);
                    host.RunAsync().GetAwaiter().GetResult();
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
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-host-custom-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*", "--output", tempDir])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithReporter(customReporter)
                    .WithIsolation(false)
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
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-host-detail-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*", "--reporter", "csv", "--detail", "advanced", "--output", tempDir])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithIsolation(false)
                    .RunAsync();
            });

            var csvFile = Assert.Single(Directory.GetFiles(tempDir, "*.csv"));
            var lines = await File.ReadAllLinesAsync(csvFile);
            Assert.Contains("Q1", lines[0]);
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
            await BenchmarkHost.Create(["--filter", "TestBenchmarks.*", "--detail", "advanced"])
                .AddFromAssembly<TestBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithReporter(customReporter)
                .WithIsolation(false)
                .RunAsync();
        });

        Assert.Equal(1, customReporter.ReportCount);
        Assert.Equal(ReportDetail.Advanced, customReporter.CapturedDetail);
    }

    [Fact]
    public async Task Detail_Flag_Simple_Is_The_Default()
    {
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-host-detail-default-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*", "--reporter", "csv", "--output", tempDir])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithIsolation(false)
                    .RunAsync();
            });

            var csvFile = Assert.Single(Directory.GetFiles(tempDir, "*.csv"));
            var lines = await File.ReadAllLinesAsync(csvFile);
            Assert.DoesNotContain("Q1", lines[0]);
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
                    var host = BenchmarkHost.Create(["--detail", "bogus"]);
                    host.RunAsync().GetAwaiter().GetResult();
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
        var tempDir = Path.Combine(Directory.GetCurrentDirectory(), $"nb-host-withdetail-{Guid.NewGuid():N}");

        try
        {
            await CaptureConsoleOutputAsync(async () =>
            {
                await BenchmarkHost.Create(["--filter", "TestBenchmarks.*", "--output", tempDir])
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithReporter(customReporter)
                    .WithDetail(ReportDetail.Advanced)
                    .WithIsolation(false)
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
            new(typeof(RuntimeAttributedHostBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8, RuntimeMoniker.Net9] },
            new(typeof(CliOverrideHostBenchmarks), []) { Runtimes = [RuntimeMoniker.Net10] },
        };

        var result = BenchmarkHost.AggregateRuntimes(suites);

        Assert.Equal([RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10], result);
    }

    [Fact]
    public void AggregateRuntimes_Deduplicates_Duplicates()
    {
        var suites = new List<BenchmarkSuiteDefinition>
        {
            new(typeof(RuntimeAttributedHostBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8, RuntimeMoniker.Net9] },
            new(typeof(CliOverrideHostBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8, RuntimeMoniker.Net10] },
        };

        var result = BenchmarkHost.AggregateRuntimes(suites);

        Assert.Equal([RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10], result);
    }

    [Fact]
    public void AggregateRuntimes_Empty_When_No_Attribute()
    {
        var suites = new List<BenchmarkSuiteDefinition>
        {
            new(typeof(NoRuntimeAttributeBenchmarks), []) { Runtimes = [] },
        };

        var result = BenchmarkHost.AggregateRuntimes(suites);

        Assert.Empty(result);
    }

    [Fact]
    public void AggregateRuntimes_Preserves_Declaration_Order()
    {
        var suites = new List<BenchmarkSuiteDefinition>
        {
            new(typeof(RuntimeAttributedHostBenchmarks), []) { Runtimes = [RuntimeMoniker.Net10] },
            new(typeof(CliOverrideHostBenchmarks), []) { Runtimes = [RuntimeMoniker.Net8] },
        };

        var result = BenchmarkHost.AggregateRuntimes(suites);

        Assert.Equal([RuntimeMoniker.Net10, RuntimeMoniker.Net8], result);
    }

    [Fact]
    public async Task No_Attribute_No_Cli_Flag_Runs_Single_Runtime()
    {
        var results = await CaptureConsoleOutputAsync(async () =>
            await BenchmarkHost.Create(["--filter", "NoRuntimeAttributeBenchmarks.*", "--iterations", "5", "--warmup", "2"])
                .AddFromAssembly<NoRuntimeAttributeBenchmarks>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
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
            BenchmarkHost.Create(["--help"])
                .AddFromAssembly<RuntimeAttributedHostBenchmarks>()
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
            BenchmarkHost.Create(["--filter", "RuntimeAttributedHostBenchmarks.*", "--list"])
                .AddFromAssembly<RuntimeAttributedHostBenchmarks>()
                .WithIsolation(false)
                .RunAsync().GetAwaiter().GetResult();
        });

        Assert.Contains("RuntimeAttributedHostBenchmarks", stdout);
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

        public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;
        public Task OnWarmupCompleted(string name) => Task.CompletedTask;
        public Task OnBenchmarkStarting(string name, int index, int total) => Task.CompletedTask;
        public Task OnIterationCompleted(string name, int iteration, int totalIterations) => Task.CompletedTask;
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

public class HostOrderBenchmarks
{
    [Benchmark]
    public int A() => 1;

    [Benchmark]
    public int B() => 2;
}

[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9)]
public class RuntimeAttributedHostBenchmarks
{
    [Benchmark]
    public int A() => 1;
}

[Runtimes(RuntimeMoniker.Net10)]
public class CliOverrideHostBenchmarks
{
    [Benchmark]
    public int A() => 1;
}

public class NoRuntimeAttributeBenchmarks
{
    [Benchmark]
    public int A() => 1;
}
