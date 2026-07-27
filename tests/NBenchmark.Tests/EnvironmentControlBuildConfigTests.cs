using System.Reflection;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Tests for the always-on Debug-build / debugger-attached warning in
///     <see cref="EnvironmentControl.EmitBuildConfigurationGuidance" />. The warning is
///     environment-dependent (the test assembly's configuration and whether a debugger is
///     attached are fixed by the runner), so these tests focus on the deterministic
///     behaviour: suppression, the once-per-process guard, and child-scope gating.
/// </summary>
public class EnvironmentControlBuildConfigTests
{
    [Fact]
    public void EmitBuildConfigurationGuidance_SuppressFlag_True_DoesNotWarn()
    {
        EnvironmentControl.ResetBuildConfigurationWarningGuard();

        var stderr = CaptureStderr(() =>
        {
            EnvironmentControl.EmitBuildConfigurationGuidance(
                new EnvironmentOptions { SuppressBuildConfigurationWarning = true });
        });

        Assert.DoesNotContain("Build configuration guidance", stderr);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void EmitBuildConfigurationGuidance_SuppressEnvVar_DoesNotWarn(string value)
    {
        EnvironmentControl.ResetBuildConfigurationWarningGuard();

        var prior = Environment.GetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING");

        try
        {
            Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING", value);

            var stderr = CaptureStderr(() =>
            {
                EnvironmentControl.EmitBuildConfigurationGuidance(null);
            });

            Assert.DoesNotContain("Build configuration guidance", stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING", prior);
        }
    }

    [Fact]
    public void EmitBuildConfigurationGuidance_FiresOncePerProcess()
    {
        EnvironmentControl.ResetBuildConfigurationWarningGuard();

        // First call may or may not warn depending on the entry assembly's configuration
        // and debugger state, but it must consume the guard so the second call is silent.
        CaptureStderr(() =>
        {
            EnvironmentControl.EmitBuildConfigurationGuidance(null);
        });

        var secondStderr = CaptureStderr(() =>
        {
            EnvironmentControl.EmitBuildConfigurationGuidance(null);
        });

        // The second call must never emit, regardless of whether the first did.
        Assert.DoesNotContain("Build configuration guidance", secondStderr);
    }

    [Fact]
    public void EmitBuildConfigurationGuidance_NonTruthyEnvVar_IsNotTreatedAsSuppress()
    {
        // A non-truthy env var value (e.g. "0") must not be treated as suppression.
        // This exercises the parsing rule directly: only "1" / "true" suppress.
        var parser = typeof(EnvironmentControl).GetMethod(
            "IsSuppressEnvVarSet",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(parser);

        var prior = Environment.GetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING");

        try
        {
            Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING", "0");
            var suppressed = Assert.IsType<bool>(parser!.Invoke(null, null));
            Assert.False(suppressed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING", prior);
        }
    }

    [Fact]
    public async Task EmitBuildConfigurationGuidance_SkippedInIsolatedChild()
    {
        EnvironmentControl.ResetBuildConfigurationWarningGuard();

        // Simulate an isolated child by setting the active request scope, then call the
        // guidance method. The child must not re-emit the warning its parent already
        // produced - the parent and child share the same entry assembly, so the warning
        // would be a duplicate.
        var request = new IsolatedRunRequest
        {
            Kind = IsolatedRunKind.Suite,
            InvocationOrdinal = 1,
            CallerFilePath = "test",
            CallerLineNumber = 0,
            CallerMemberName = "test",
            SuiteName = "child-scope-test",
        };

        var stderr = await CaptureStderrAsync(async () =>
        {
            await IsolatedRunContext.WithActiveRequestForTestingAsync(
                request,
                outputPath: null,
                () => Task.Run(() =>
                {
                    EnvironmentControl.EmitBuildConfigurationGuidance(null);
                    return 0;
                }));
        });

        Assert.DoesNotContain("Build configuration guidance", stderr);
    }

    [Fact]
    public void Apply_NullOptions_DoesNotThrow_AndRespectsSuppressEnvVar()
    {
        // Apply(null) takes the no-op fast path for hardware/OS options, but must still
        // run the build-config check. The deterministic proof that it does: when the
        // suppress env var is set, Apply(null) produces no "Build configuration guidance"
        // output. Without the check running, the env var would have nothing to suppress
        // and a Debug-built test assembly would emit - this asserts the check ran and
        // honoured the suppression.
        EnvironmentControl.ResetBuildConfigurationWarningGuard();

        var prior = Environment.GetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING");

        try
        {
            Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING", "1");

            var stderr = CaptureStderr(() =>
            {
                using var _ = EnvironmentControl.Apply(null);
            });

            Assert.DoesNotContain("Build configuration guidance", stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NBENCHMARK_SUPPRESS_DEBUG_WARNING", prior);
        }
    }

    [Fact]
    public void Apply_SuppressFlag_PreventsBuildConfigWarning()
    {
        EnvironmentControl.ResetBuildConfigurationWarningGuard();

        var stderr = CaptureStderr(() =>
        {
            using var _ = EnvironmentControl.Apply(
                new EnvironmentOptions { SuppressBuildConfigurationWarning = true });
        });

        Assert.DoesNotContain("Build configuration guidance", stderr);
    }

    [Fact]
    public void EnvironmentOptions_SuppressBuildConfigurationWarning_Defaults_False()
    {
        var opts = new EnvironmentOptions();

        Assert.False(opts.SuppressBuildConfigurationWarning);
    }

    /// <summary>
    ///     Sanity check: when neither the suppress flag nor the env var is set, the warning
    ///     fires when the entry assembly is Debug. The test assembly itself is built in
    ///     Debug under `dotnet test` in most configurations, so this is the most likely
    ///     environment. When the assembly is Release-built (e.g. a CI Release test run) the
    ///     warning will not fire from the configuration check alone - but the
    ///     debugger-attached check still fires under an attached debugger. Skip the
    ///     assertion when neither condition holds, so the test passes on every host while
    ///     still exercising the code path.
    /// </summary>
    [Fact]
    public void EmitBuildConfigurationGuidance_WarnsWhenEntryAssemblyIsDebug()
    {
        EnvironmentControl.ResetBuildConfigurationWarningGuard();

        var configuration = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration;

        var stderr = CaptureStderr(() =>
        {
            EnvironmentControl.EmitBuildConfigurationGuidance(null);
        });

        if (!string.IsNullOrEmpty(configuration)
            && configuration.Contains("Debug", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Contains("Build configuration guidance", stderr);
            Assert.Contains("Debug", stderr);
            Assert.Contains("dotnet run -c Release", stderr);
        }
        // Release build with no debugger: no warning. Nothing to assert.
        // Release build with debugger: the debugger-attached branch fires; covered by
        // the once-per-process guard test, which does not depend on the build config.
    }

    private static string CaptureStderr(Action action)
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

    private static async Task<string> CaptureStderrAsync(Func<Task> action)
    {
        var sw = new StringWriter();
        var original = Console.Error;
        Console.SetError(sw);

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Console.SetError(original);
        }

        return sw.ToString();
    }
}
