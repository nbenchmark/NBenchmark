using System.Diagnostics;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Covers the runtime-startup configuration layer: the knob mapping, its delivery to a child
///     process, the honest stamping of what was actually applied, and the refusal to compare
///     results measured under different configurations.
/// </summary>
public class RuntimeProfileTests
{
    [Fact]
    public void SteadyState_DisablesTieringPgoAndReadyToRun()
    {
        var env = RuntimeProfile.SteadyState.ToEnvironment();

        Assert.Equal("0", env["DOTNET_TieredCompilation"]);
        Assert.Equal("0", env["DOTNET_TieredPGO"]);
        Assert.Equal("0", env["DOTNET_ReadyToRun"]);

        // Nothing about the GC: SteadyState is a JIT profile, and silently switching a user's GC
        // would change allocation-heavy results for reasons unrelated to its stated purpose.
        Assert.False(env.ContainsKey("DOTNET_gcServer"));
    }

    [Fact]
    public void Production_EnablesTheRuntimeDefaultsExplicitly()
    {
        var env = RuntimeProfile.Production.ToEnvironment();

        // Set rather than inherited, so the run reproduces regardless of the host's environment.
        Assert.Equal("1", env["DOTNET_TieredCompilation"]);
        Assert.Equal("1", env["DOTNET_TieredPGO"]);
        Assert.Equal("1", env["DOTNET_ReadyToRun"]);
    }

    [Fact]
    public void ServerGc_AddsNonConcurrentServerGcToSteadyState()
    {
        var env = RuntimeProfile.ServerGc.ToEnvironment();

        Assert.Equal("0", env["DOTNET_TieredCompilation"]);
        Assert.Equal("1", env["DOTNET_gcServer"]);
        Assert.Equal("0", env["DOTNET_gcConcurrent"]);
    }

    [Fact]
    public void Host_SetsNothingAndInheritsEverything()
    {
        Assert.True(RuntimeProfile.Host.InheritsEverything);
        Assert.Empty(RuntimeProfile.Host.ToEnvironment());
        Assert.Equal("", RuntimeProfile.Host.Describe());
    }

    [Fact]
    public void ExtraEnvironment_IsForwardedAndWinsOverModelledKnobs()
    {
        var profile = RuntimeProfile.SteadyState with
        {
            Name = "custom",
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["DOTNET_TieredCompilation"] = "1",
                ["DOTNET_GCgen0size"] = "1E00000",
            },
        };

        var env = profile.ToEnvironment();

        Assert.Equal("1", env["DOTNET_TieredCompilation"]);
        Assert.Equal("1E00000", env["DOTNET_GCgen0size"]);
        Assert.False(profile.InheritsEverything);
    }

    [Theory]
    [InlineData("steady-state", "steady-state")]
    [InlineData("steadystate", "steady-state")]
    [InlineData("STEADY-STATE", "steady-state")]
    [InlineData("production", "production")]
    [InlineData("server-gc", "server-gc")]
    [InlineData("host", "host")]
    public void TryParse_AcceptsKnownNamesCaseAndHyphenInsensitively(string input, string expected)
    {
        Assert.True(RuntimeProfile.TryParse(input, out var profile));
        Assert.Equal(expected, profile.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("steady state")]
    public void TryParse_RejectsUnknownNames(string? input)
        => Assert.False(RuntimeProfile.TryParse(input, out _));

    [Fact]
    public void Describe_RendersKnobsCompactly()
    {
        Assert.Equal("tiered=off pgo=off r2r=off", RuntimeProfile.SteadyState.Describe());
        Assert.Equal("tiered=on pgo=on r2r=on", RuntimeProfile.Production.Describe());
        Assert.Contains("gc=server", RuntimeProfile.ServerGc.Describe());
    }

    [Fact]
    public void ApplyRuntimeProfile_PutsKnobsAndTheNameMarkerOnTheChildEnvironment()
    {
        var psi = new ProcessStartInfo();

        ChildProcessLauncher.ApplyRuntimeProfile(psi, RuntimeProfile.SteadyState);

        Assert.Equal("0", psi.Environment["DOTNET_TieredCompilation"]);
        Assert.Equal("0", psi.Environment["DOTNET_ReadyToRun"]);

        // The marker is how the child reports its profile by name: the runtime exposes no managed
        // read-back for tiering, so it cannot work this out for itself.
        Assert.Equal("steady-state", psi.Environment[RuntimeProfile.ProfileNameEnvVar]);
    }

    [Fact]
    public void ApplyRuntimeProfile_LeavesTheEnvironmentAloneForHostAndNull()
    {
        var forHost = new ProcessStartInfo();
        ChildProcessLauncher.ApplyRuntimeProfile(forHost, RuntimeProfile.Host);

        var forNull = new ProcessStartInfo();
        ChildProcessLauncher.ApplyRuntimeProfile(forNull, null);

        foreach (var psi in (ProcessStartInfo[])[forHost, forNull])
        {
            Assert.False(psi.Environment.ContainsKey("DOTNET_TieredCompilation"));
            Assert.False(psi.Environment.ContainsKey(RuntimeProfile.ProfileNameEnvVar));
        }
    }

    [Fact]
    public void MeasurementOptions_DefaultToSteadyState()
        => Assert.Equal(RuntimeProfile.SteadyState.Name, MeasurementOptions.Default.RuntimeProfile.Name);

    [Fact]
    public void CliArgs_ParsesRuntimeProfile()
    {
        var (args, errors) = CliArgs.ParseCore(["--runtime-profile", "production"]);

        Assert.Empty(errors);
        Assert.Equal("production", args.RuntimeProfile?.Name);
    }

    [Fact]
    public void CliArgs_ReportsAnErrorForAnUnknownRuntimeProfile()
    {
        var (_, errors) = CliArgs.ParseCore(["--runtime-profile", "turbo"]);

        var error = Assert.Single(errors);
        Assert.Contains("turbo", error);

        // The message must list the valid values; an error that does not is a dead end.
        Assert.Contains("steady-state", error);
    }

    [Fact]
    public void CliArgs_RuntimeProfileOverridesTheProgrammaticOption()
    {
        var (args, _) = CliArgs.ParseCore(["--runtime-profile", "host"]);
        var options = MeasurementOverrides.FromCliArgs(args).Apply(MeasurementOptions.Default);

        Assert.Equal("host", options.RuntimeProfile.Name);
    }

    /// <summary>
    ///     Results measured under different runtime configurations must never land in the same
    ///     comparison group. The configuration difference alone moved a measured value by roughly
    ///     3.3x, so comparing across it manufactures an effect far larger than most real ones.
    /// </summary>
    [Fact]
    public void ComparisonGroup_SeparatesResultsByRuntimeProfileAndMoniker()
    {
        var isolated = Result("A", profileName: "steady-state");
        var inProcess = Result("B", profileName: "host");
        var sameProfileOtherRuntime = Result("C", profileName: "steady-state", moniker: "net8.0");

        Assert.NotEqual(ComparisonGroup.KeyFor(isolated), ComparisonGroup.KeyFor(inProcess));
        Assert.NotEqual(ComparisonGroup.KeyFor(isolated), ComparisonGroup.KeyFor(sameProfileOtherRuntime));
        Assert.Equal(ComparisonGroup.KeyFor(isolated), ComparisonGroup.KeyFor(Result("D", "steady-state")));
    }

    [Fact]
    public void BenchmarkTable_FlagsMixedRuntimeProfiles()
    {
        var mixed = BenchmarkTable.BuildPerClass([
            Result("A", profileName: "steady-state"),
            Result("B", profileName: "host"),
        ]);

        Assert.True(Assert.Single(mixed).MixedRuntimeProfiles);

        var uniform = BenchmarkTable.BuildPerClass([
            Result("A", profileName: "steady-state"),
            Result("B", profileName: "steady-state"),
        ]);

        var table = Assert.Single(uniform);
        Assert.False(table.MixedRuntimeProfiles);
        Assert.Equal("steady-state", table.RuntimeProfileName);
    }

    /// <summary>
    ///     The end-to-end proof: a real child process, launched under a real profile, reports back
    ///     what it was actually running under. This is the only assertion that covers the whole
    ///     mechanism - env-block delivery, the runtime honouring the knobs at startup, and the
    ///     child reading its own environment to stamp the result.
    /// </summary>
    [Theory]
    [InlineData("steady-state", "tiered=off pgo=off r2r=off")]
    [InlineData("production", "tiered=on pgo=on r2r=on")]
    public async Task RealChild_ReportsTheProfileItWasLaunchedUnder(string profileName, string expectedKnobs)
    {
        Assert.True(RuntimeProfile.TryParse(profileName, out var profile));

        var items = await ChildProcessLauncher.LaunchAsync(
            ChildRequest(profile),
            CancellationToken.None);

        Assert.NotEmpty(items);

        foreach (var item in items)
        {
            Assert.False(item.Result.Errored, item.Result.ErrorMessage);
            Assert.Equal(profileName, item.Result.RuntimeProfileName);
            Assert.Equal(expectedKnobs, item.Result.RuntimeKnobs);
        }
    }

    /// <summary>
    ///     With no profile the child inherits the parent's environment, so it must report
    ///     <c>host</c> rather than claiming the default profile was applied. Stamping the
    ///     requested profile here would make every result overstate its own fidelity.
    /// </summary>
    [Fact]
    public async Task RealChild_LaunchedWithoutAProfile_ReportsHost()
    {
        var items = await ChildProcessLauncher.LaunchAsync(
            ChildRequest(RuntimeProfile.Host),
            CancellationToken.None);

        foreach (var item in items)
        {
            Assert.False(item.Result.Errored, item.Result.ErrorMessage);
            Assert.Equal(RuntimeProfile.Host.Name, item.Result.RuntimeProfileName);
        }
    }

    /// <summary>
    ///     The honesty property, and the one most easily got wrong: an in-process measurement
    ///     requests <see cref="RuntimeProfile.SteadyState" /> by default and <b>cannot</b> honour it,
    ///     because the runtime fixes these knobs at startup. It must therefore stamp
    ///     <c>host</c>.
    ///     <para>
    ///         Stamping <c>options.RuntimeProfile</c> instead - the obvious implementation - would
    ///         make every in-process result claim steady-state fidelity it does not have, and would
    ///         also defeat <see cref="ComparisonGroup" />, silently allowing in-process rows to be
    ///         compared against isolated ones. That is why the stamp is read from the measuring
    ///         process's own environment rather than from its configuration.
    ///     </para>
    /// </summary>
    [Fact]
    public void InProcessMeasurement_StampsHost_EvenThoughSteadyStateWasRequested()
    {
        var options = new MeasurementOptions
        {
            Iterations = 5,
            WarmupIterations = 0,
            SuppressRuntimeProfileWarning = true,
        };

        Assert.Equal(RuntimeProfile.SteadyState.Name, options.RuntimeProfile.Name);

        var result = Benchmark.Run(() => Thread.SpinWait(50), options, "in-process-stamp");

        Assert.Equal(RuntimeProfile.Host.Name, result.RuntimeProfileName);
    }

    private static IsolatedRunRequest ChildRequest(RuntimeProfile profile) => new()
    {
        Kind = IsolatedRunKind.Host,
        DeclaringTypeFullName = IsolationFixtureLocator.ClassFullName("IsolationFixtureBenchmarks"),
        DisplayPrefix = "IsolationFixtureBenchmarks",
        BenchmarkDisplayNames = ["Fast"],
        Overrides = new MeasurementOverrides { Iterations = 20, WarmupIterations = 1 },
        EntryAssemblyPath = IsolationFixtureLocator.AssemblyPath(),
        RuntimeProfile = profile,
    };

    [Fact]
    public void NotAppliedGuidance_IsEmittedOnceAndIsSuppressible()
    {
        var original = Console.Error;

        try
        {
            // Fires: the test host was not launched with a profile, so SteadyState cannot apply.
            var first = CaptureStdErr(() =>
            {
                RuntimeProfileEnvironment.ResetGuidanceGuardForTesting();
                RuntimeProfileEnvironment.EmitNotAppliedGuidanceOnce(MeasurementOptions.Default);
            });

            // Assert on the load-bearing content rather than the exact prose: the profile that
            // could not be applied, and the actionable remedy.
            Assert.Contains("steady-state", first);
            Assert.Contains("child process", first);
            Assert.Contains(RuntimeProfileEnvironment.SuppressWarningEnvVar, first);

            // Once per process: a suite of ten benchmarks must not print this ten times.
            var second = CaptureStdErr(
                () => RuntimeProfileEnvironment.EmitNotAppliedGuidanceOnce(MeasurementOptions.Default));

            Assert.Equal("", second);

            // Explicitly asking for the host's configuration is not a problem worth reporting.
            var forHost = CaptureStdErr(() =>
            {
                RuntimeProfileEnvironment.ResetGuidanceGuardForTesting();

                RuntimeProfileEnvironment.EmitNotAppliedGuidanceOnce(
                    MeasurementOptions.Default with { RuntimeProfile = RuntimeProfile.Host });
            });

            Assert.Equal("", forHost);

            var suppressed = CaptureStdErr(() =>
            {
                RuntimeProfileEnvironment.ResetGuidanceGuardForTesting();

                RuntimeProfileEnvironment.EmitNotAppliedGuidanceOnce(
                    MeasurementOptions.Default with { SuppressRuntimeProfileWarning = true });
            });

            Assert.Equal("", suppressed);
        }
        finally
        {
            Console.SetError(original);
            RuntimeProfileEnvironment.ResetGuidanceGuardForTesting();
        }
    }

    private static string CaptureStdErr(Action action)
    {
        var writer = new StringWriter();
        Console.SetError(writer);
        action();

        return writer.ToString();
    }

    private static BenchmarkResult Result(
        string name,
        string profileName,
        string moniker = "") => new()
    {
        Name = name,
        ClassName = "Fixture",
        Mean = 10,
        Median = 10,
        Min = 9,
        Max = 11,
        Percentiles = [],
        StandardDeviation = 1,
        Q1 = 9,
        Q3 = 11,
        InterquartileRange = 2,
        OutliersRemoved = 0,
        N = 10,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = 0,
        AllocP95 = 0,
        AllocMax = 0,
        RuntimeMoniker = moniker,
        RuntimeProfileName = profileName,
    };
}
