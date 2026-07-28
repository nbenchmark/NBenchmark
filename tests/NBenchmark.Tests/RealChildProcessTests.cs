using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     The only tests in the repo that spawn a real isolated child process. Every other
///     isolation test substitutes <see cref="FakeProcessLauncher" /> and hand-builds the
///     payload, so the real <c>DefaultLauncher</c> had no coverage at all - which is precisely
///     how a defect that emptied <c>RawSamples</c> on every isolated Harness result was able to
///     ship in the library's default mode.
///     <para>
///         These tests drive <c>tests/NBenchmark.Tests.IsolationFixture</c>, a real executable,
///         because isolation re-runs the entry assembly and under <c>dotnet test</c> that is the
///         test host rather than a program containing benchmarks.
///     </para>
/// </summary>
public class RealChildProcessTests
{
    /// <summary>Pinned counts keep the child fast and the sample count exactly predictable.</summary>
    private const int PinnedIterations = 30;

    private const int PinnedWarmup = 2;

    private const string FixtureAssemblyName = "NBenchmark.Tests.IsolationFixture";

    private const string HangingClassFullName = $"{FixtureAssemblyName}.HangingBenchmarks";

    /// <summary>
    ///     The regression test for the composite-key defect. Before the fix the child looked its
    ///     samples up by <c>"{Name}\0{RuntimeMoniker}"</c> while <see cref="SuiteRunner" /> had
    ///     keyed them by plain name, so this returned two results with zero samples each and
    ///     significance testing downstream had nothing to work with.
    ///     <para>
    ///         Both instance lifetimes are covered because they write the payload from different
    ///         methods - <c>RunPerMethodHostChildAsync</c> and <c>RunPerClassHostChildAsync</c> -
    ///         and both carried the defect independently.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("IsolationFixtureBenchmarks")]
    [InlineData("SharedInstanceBenchmarks")]
    public async Task RealChild_ReturnsRawSamples_ForEveryBenchmark(string className)
    {
        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Host,
            DeclaringTypeFullName = $"{FixtureAssemblyName}.{className}",
            DisplayPrefix = className,
            BenchmarkDisplayNames = ["Fast", "Slow"],
            Overrides = new MeasurementOverrides
            {
                Iterations = PinnedIterations,
                WarmupIterations = PinnedWarmup,
            },
            EntryAssemblyPath = FixtureAssemblyPath(),
        };

        var items = await ChildProcessLauncher.LaunchAsync(request, CancellationToken.None);

        Assert.Equal(2, items.Count);

        foreach (var item in items)
        {
            Assert.False(
                item.Result.Errored,
                $"'{item.Result.Name}' errored in the child: {item.Result.ErrorMessage}");

            Assert.Equal(PinnedIterations, item.RawSamples.Length);
            Assert.All(item.RawSamples, sample => Assert.True(sample > 0));
            Assert.True(item.Result.Median > 0);
        }
    }

    /// <summary>
    ///     End to end through the real parent: a real program spawns real children, folds their
    ///     results back, and computes significance over the returned samples. This is the
    ///     user-visible half of the same defect - a null p-value in the library's default mode.
    /// </summary>
    [Fact]
    public async Task RealHarnessRun_ProducesSamplesAndPValue_InJsonOutput()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("nbench-e2e-").FullName;

        try
        {
            var exitCode = await RunFixtureAsync(
                workingDirectory,
                [
                    // Exclude HangingBenchmarks, which never returns.
                    "--filter", "IsolationFixtureBenchmarks.*",
                    "--iterations", PinnedIterations.ToString(),
                    "--warmup", PinnedWarmup.ToString(),
                    "--launch-count", "1",
                    "--reporter", "json",
                    "--output", ".",
                ],
                CancellationToken.None);

            Assert.Equal(0, exitCode);

            var reportPath = Assert.Single(Directory.GetFiles(workingDirectory, "*.json"));
            var results = ParseResults(await File.ReadAllTextAsync(reportPath, CancellationToken.None));

            Assert.Equal(2, results.Count);

            foreach (var result in results)
            {
                Assert.False(result.Errored, $"'{result.Name}' errored: {result.ErrorMessage}");
                Assert.Equal(PinnedIterations, result.RawSampleCount);
            }

            // Significance is computed on the samples the children returned, so a non-null
            // p-value on the non-baseline row is the end-to-end proof they survived the boundary.
            var candidate = Assert.Single(results, r => !r.IsBaseline);
            Assert.NotNull(candidate.PValue);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>
    ///     A wedged child must be killed and reported, not waited on forever. Before this change
    ///     the launcher awaited a bare <c>WaitForExitAsync</c> with no timeout, so a benchmark
    ///     that blocked hung the whole run with no diagnostic.
    /// </summary>
    [Fact]
    public async Task RealChild_ThatNeverReturns_IsKilledAndReportedAsTimedOut()
    {
        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Host,
            DeclaringTypeFullName = HangingClassFullName,
            DisplayPrefix = "HangingBenchmarks",
            BenchmarkDisplayNames = ["Hang"],
            Overrides = new MeasurementOverrides
            {
                Iterations = PinnedIterations,
                WarmupIterations = 0,
            },
            EntryAssemblyPath = FixtureAssemblyPath(),
            Timeout = TimeSpan.FromSeconds(3),
        };

        var stopwatch = Stopwatch.StartNew();
        var items = await ChildProcessLauncher.LaunchAsync(request, CancellationToken.None);
        stopwatch.Stop();

        var item = Assert.Single(items);

        Assert.True(item.Result.Errored);
        Assert.Contains("timeout", item.Result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        // Generous, but far below the 10-minute default the request overrode: the point is that
        // the bound came from the request rather than from the child deciding to exit.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(90),
            $"Timed-out child took {stopwatch.Elapsed.TotalSeconds:0.#}s to be reaped.");

        // The launcher must not leave the killed child registered for the reaper to find.
        Assert.Equal(0, ChildProcessReaper.TrackedCount);
    }

    /// <summary>
    ///     Cancelling a run must take the child with it rather than orphan it. The launcher
    ///     deliberately lets <see cref="OperationCanceledException" /> escape, so the assertion is
    ///     that it escapes <em>and</em> the child is gone.
    /// </summary>
    [Fact]
    public async Task RealChild_IsKilled_WhenTheRunIsCancelled()
    {
        using var cts = new CancellationTokenSource();

        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Host,
            DeclaringTypeFullName = HangingClassFullName,
            DisplayPrefix = "HangingBenchmarks",
            BenchmarkDisplayNames = ["Hang"],
            Overrides = new MeasurementOverrides { Iterations = PinnedIterations, WarmupIterations = 0 },
            EntryAssemblyPath = FixtureAssemblyPath(),
        };

        var launch = ChildProcessLauncher.LaunchAsync(request, cts.Token);

        // Give the child time to actually start before cancelling, otherwise the cancellation
        // races the process launch and never exercises the kill path.
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launch);

        Assert.Equal(0, ChildProcessReaper.TrackedCount);
    }

    [Fact]
    public void ComputeTimeout_ScalesWithBenchmarkCountAndStaysWithinBounds()
    {
        var options = MeasurementOptions.Default;

        var one = ChildProcessLauncher.ComputeTimeout(options, 1);
        var ten = ChildProcessLauncher.ComputeTimeout(options, 10);

        Assert.True(one >= ChildProcessLauncher.MinChildTimeout);
        Assert.True(ten > one);
        Assert.True(ten <= ChildProcessLauncher.MaxChildTimeout);

        // A zero or negative count must not collapse the budget below the floor.
        Assert.True(ChildProcessLauncher.ComputeTimeout(options, 0) >= ChildProcessLauncher.MinChildTimeout);
    }

    /// <summary>
    ///     Resolves the fixture executable from the path baked into this assembly at build time
    ///     by <c>NBenchmark.Tests.csproj</c>, so the tests never guess at relative bin layouts.
    /// </summary>
    private static string FixtureAssemblyPath()
    {
        var directory = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "IsolationFixtureDirectory")
            ?.Value;

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The IsolationFixtureDirectory assembly metadata is missing. It is set by an "
                + "AssemblyMetadata item in NBenchmark.Tests.csproj.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, $"{FixtureAssemblyName}.dll"));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The isolation fixture was not found at '{path}'. It should have been built by the "
                + "ProjectReference in NBenchmark.Tests.csproj.");
        }

        return path;
    }

    /// <summary>
    ///     Runs the fixture as a full parent process. <c>--output</c> is validated against the
    ///     current working directory, so the report directory is set through
    ///     <see cref="ProcessStartInfo.WorkingDirectory" /> and passed as a relative path.
    /// </summary>
    private static async Task<int> RunFixtureAsync(
        string workingDirectory,
        string[] args,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(FixtureAssemblyPath());

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start the isolation fixture.");

        // Drain both pipes concurrently so a chatty child cannot fill one and deadlock.
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The isolation fixture exited with {process.ExitCode}. "
                + $"stdout: {await stdout} stderr: {await stderr}");
        }

        return process.ExitCode;
    }

    private static List<ReportedResult> ParseResults(string json)
    {
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;

        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("results", out var results)
                ? results
                : throw new InvalidOperationException($"Unrecognised report shape: {root.ValueKind}.");

        return array.EnumerateArray().Select(ReportedResult.From).ToList();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    /// <summary>
    ///     The subset of the JSON report these tests assert on. Deliberately hand-parsed rather
    ///     than deserialized into <see cref="BenchmarkResult" />, so the assertions describe the
    ///     report a user actually reads instead of trusting the same round-trip under test.
    /// </summary>
    private sealed record ReportedResult(
        string Name,
        bool Errored,
        string? ErrorMessage,
        bool IsBaseline,
        int RawSampleCount,
        double? PValue)
    {
        public static ReportedResult From(JsonElement element) => new(
            Name: element.GetProperty("name").GetString() ?? "",
            Errored: element.TryGetProperty("errored", out var errored) && errored.GetBoolean(),
            ErrorMessage: element.TryGetProperty("errorMessage", out var message)
                          && message.ValueKind is not JsonValueKind.Null
                ? message.GetString()
                : null,
            IsBaseline: element.TryGetProperty("isBaseline", out var baseline) && baseline.GetBoolean(),
            RawSampleCount: element.TryGetProperty("rawSamples", out var samples)
                            && samples.ValueKind == JsonValueKind.Array
                ? samples.GetArrayLength()
                : 0,
            PValue: element.TryGetProperty("pValue", out var pValue)
                    && pValue.ValueKind is not JsonValueKind.Null
                ? pValue.GetDouble()
                : null);
    }
}
