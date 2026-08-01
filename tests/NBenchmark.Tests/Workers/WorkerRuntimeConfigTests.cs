using System.Text.Json;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The rules for extending the worker's framework set from the assembly under test.
/// </summary>
/// <remarks>
///     Every case runs in its own temporary directory. <see cref="WorkerRuntimeConfig.ResolveFor" />
///     memoizes on the two paths, so reusing a path across cases with different content would have
///     one test answer another test's question.
/// </remarks>
public sealed class WorkerRuntimeConfigTests : IDisposable
{
    private const string WorkerConfig = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "rollForward": "LatestMinor",
            "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
            "configProperties": { "System.GC.Server": false }
          }
        }
        """;

    private const string WebTargetConfig = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "frameworks": [
              { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
              { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
            ],
            "configProperties": { "System.GC.Server": true }
          }
        }
        """;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"nbenchmark-runtimeconfig-{Guid.NewGuid():N}");

    public WorkerRuntimeConfigTests() => Directory.CreateDirectory(_directory);

    /// <summary>
    ///     The case the whole file exists for: an ASP.NET Core target gets its extra framework added
    ///     to the worker's own, and nothing else about the worker's configuration moves.
    /// </summary>
    /// <remarks>
    ///     The <c>configProperties</c> assertion is the one that is easy to get wrong in the
    ///     permissive direction. The worker's runtime configuration is chosen by
    ///     <see cref="RuntimeProfile" /> - importing the application's <c>System.GC.Server</c> would
    ///     quietly undo the thing the process boundary exists for, and it would do so without
    ///     changing a single reported field.
    /// </remarks>
    [Fact]
    public void SharedFrameworkTarget_AddsTheFrameworkAndKeepsEverythingElseFromTheWorker()
    {
        var (worker, target) = Configs(WorkerConfig, WebTargetConfig);

        var merged = WorkerRuntimeConfig.ResolveFor(worker, target);

        Assert.NotNull(merged);

        using var document = JsonDocument.Parse(File.ReadAllBytes(merged));
        var options = document.RootElement.GetProperty("runtimeOptions");

        var frameworks = options.GetProperty("frameworks")
            .EnumerateArray()
            .Select(f => f.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"], frameworks);

        // The singular form must be gone, not merely joined by the plural one - hostfxr reads both.
        Assert.False(options.TryGetProperty("framework", out _));

        Assert.Equal("net10.0", options.GetProperty("tfm").GetString());
        Assert.Equal("LatestMinor", options.GetProperty("rollForward").GetString());
        Assert.False(options.GetProperty("configProperties").GetProperty("System.GC.Server").GetBoolean());
    }

    /// <summary>The added framework keeps the version the target asked for.</summary>
    [Fact]
    public void AddedFramework_CarriesTheTargetsVersion()
    {
        var (worker, target) = Configs(WorkerConfig, WebTargetConfig);

        using var document = JsonDocument.Parse(File.ReadAllBytes(WorkerRuntimeConfig.ResolveFor(worker, target)!));

        var aspNet = document.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("frameworks")
            .EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Microsoft.AspNetCore.App");

        Assert.Equal("10.0.0", aspNet.GetProperty("version").GetString());
    }

    /// <summary>
    ///     An ordinary console target needs nothing the worker does not already have, so nothing is
    ///     synthesized and the launch command line stays exactly as it was.
    /// </summary>
    [Fact]
    public void PlainTarget_NeedsNothing()
    {
        var (worker, target) = Configs(
            WorkerConfig,
            """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
              }
            }
            """);

        Assert.Null(WorkerRuntimeConfig.ResolveFor(worker, target));
    }

    /// <summary>
    ///     A self-contained target carries its framework in its own output directory, where its
    ///     <c>deps.json</c> already resolves it. There is no shared framework to ask the host for, and
    ///     naming one would name a version the machine may not have.
    /// </summary>
    [Fact]
    public void SelfContainedTarget_IsLeftAlone()
    {
        var (worker, target) = Configs(
            WorkerConfig,
            """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" } ]
              }
            }
            """);

        Assert.Null(WorkerRuntimeConfig.ResolveFor(worker, target));
    }

    /// <summary>
    ///     A missing or unparseable configuration falls back to the worker's own rather than failing
    ///     the launch. The run it would have broken is one that may never have needed this at all.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{ this is not json")]
    [InlineData("{ }")]
    public void UnusableTargetConfig_FallsBackToTheWorkersOwn(string? targetContent)
    {
        var worker = WriteConfig("nbworker.dll", WorkerConfig);
        var target = Path.Combine(_directory, "Target.dll");

        if (targetContent is not null)
            File.WriteAllText(Path.ChangeExtension(target, ".runtimeconfig.json"), targetContent);

        Assert.Null(WorkerRuntimeConfig.ResolveFor(worker, target));
    }

    /// <summary>
    ///     The same question twice gives the same file. Replicates, groups and concurrent runs all
    ///     ask it, and a path per launch would litter the temp directory for the life of the machine.
    /// </summary>
    [Fact]
    public void RepeatedResolution_ConvergesOnOneFile()
    {
        var (worker, target) = Configs(WorkerConfig, WebTargetConfig);

        Assert.Equal(
            WorkerRuntimeConfig.ResolveFor(worker, target),
            WorkerRuntimeConfig.ResolveFor(worker, target));
    }

    private (string Worker, string Target) Configs(string workerContent, string targetContent)
        => (WriteConfig("nbworker.dll", workerContent), WriteConfig("Target.dll", targetContent));

    private string WriteConfig(string assemblyFileName, string content)
    {
        var assemblyPath = Path.Combine(_directory, assemblyFileName);

        File.WriteAllText(Path.ChangeExtension(assemblyPath, ".runtimeconfig.json"), content);

        return assemblyPath;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory is not worth failing a passing test over.
        }
    }
}
