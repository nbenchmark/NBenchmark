using System.Diagnostics;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class EnvironmentControlTests
{
    [Fact]
    public void Apply_Null_Options_Is_NoOp()
    {
        using var scope = EnvironmentControl.Apply(null);

        // No-op scope: nothing to assert beyond "did not throw". The scope is disposable
        // and restore is a no-op.
        Assert.NotNull(scope);
    }

    [Fact]
    public void Apply_Empty_Options_Is_NoOp()
    {
        using var scope = EnvironmentControl.Apply(new EnvironmentOptions());

        Assert.NotNull(scope);
    }

    [Fact]
    public void Apply_DedicatedHostGuidance_Only_Is_NoOp_Restore()
    {
        // Guidance-only options should not attempt affinity or priority changes, so the
        // restore scope has nothing to restore. The only observable effect is console
        // output, which we do not assert here (it is environment-dependent).
        using var scope = EnvironmentControl.Apply(new EnvironmentOptions { DedicatedHostGuidance = true });

        Assert.NotNull(scope);
    }

    [Fact]
    public void BuildAffinityMask_Single_Core_Produces_Bit0()
    {
        var mask = EnvironmentControl.BuildAffinityMask([0]);

        Assert.Equal(new IntPtr(1), mask);
    }

    [Fact]
    public void BuildAffinityMask_Multiple_Cores_Produces_Bitmask()
    {
        var mask = EnvironmentControl.BuildAffinityMask([0, 2, 3]);

        // Bits 0, 2, 3 set = 1 + 4 + 8 = 13
        Assert.Equal(new IntPtr(13), mask);
    }

    [Fact]
    public void BuildAffinityMask_OutOfRange_Throws()
    {
        var tooHigh = Environment.ProcessorCount;

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentControl.BuildAffinityMask([tooHigh]));
        Assert.Contains($"CPU index {tooHigh}", ex.Message);
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void BuildAffinityMask_Negative_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentControl.BuildAffinityMask([-1]));
        Assert.Contains("CPU index -1", ex.Message);
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void Apply_And_Restore_Affinity_Restores_Prior_Mask_OnLinuxWindows()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            // On macOS the BCL does not expose ProcessorAffinity; the apply path skips it
            // and emits a warning instead. This test only runs where the API is available.
            return;
        }

        var process = Process.GetCurrentProcess();
        var prior = process.ProcessorAffinity;

        try
        {
            // Pin to core 0 only. Use a scope so restore runs on dispose.
            using (EnvironmentControl.Apply(new EnvironmentOptions { CpuAffinity = [0] }))
            {
                // Some CI runners (containers, restricted permissions) refuse affinity
                // changes; we don't assert inside the scope because the apply may not
                // have taken effect.
            }

            // After dispose, the prior mask is restored. When the apply was refused,
            // affinity was never changed so this still holds; when it succeeded, the
            // scope restores the prior value.
            Assert.Equal(prior, process.ProcessorAffinity);
        }
        finally
        {
            // Best-effort restore in case the scope-leaving assertion fails.
            try
            {
                process.ProcessorAffinity = prior;
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Apply_And_Restore_Priority_Restores_Prior_Priority()
    {
        var process = Process.GetCurrentProcess();
        var prior = process.PriorityClass;

        try
        {
            using (EnvironmentControl.Apply(new EnvironmentOptions { ProcessPriority = ProcessPriorityClass.High }))
            {
                // Some CI runners refuse priority elevation; we don't assert inside
                // the scope because the apply may not have taken effect.
            }

            // Restore should bring us back to the original. Some hosts cap priority and
            // the restore may land on a different value; assert it returned to prior.
            Assert.Equal(prior, process.PriorityClass);
        }
        finally
        {
            try
            {
                process.PriorityClass = prior;
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Apply_Affinity_OnMacOS_Emits_Warning_And_Skips()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var stderr = CaptureStderr(() =>
        {
            using var _ = EnvironmentControl.Apply(new EnvironmentOptions { CpuAffinity = [0] });
        });

        Assert.Contains("macOS", stderr);
        Assert.Contains("cpu-affinity", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_DedicatedHostGuidance_LowCoreCount_Warns()
    {
        // This test only asserts when the host actually has < 4 cores; on bigger hosts
        // the guidance probe emits a different (or no) message. Run it regardless so the
        // code path is exercised, but only assert the small-host message there.
        var stderr = CaptureStderr(() =>
        {
            using var _ = EnvironmentControl.Apply(new EnvironmentOptions { DedicatedHostGuidance = true });
        });

        if (Environment.ProcessorCount < 4)
        {
            Assert.Contains("Dedicated-host guidance", stderr);
            Assert.Contains($"{Environment.ProcessorCount} logical CPU", stderr);
        }
    }

    [Fact]
    public void Apply_DedicatedHostGuidance_Suggests_PriorityHigh_OnSuitableHost()
    {
        // On a host with >= 4 cores and no priority set, the probe should actively
        // suggest --priority high. Skipped on small hosts where the suggestion is
        // suppressed in favour of the shared-tenant warning.
        if (Environment.ProcessorCount < 4)
            return;

        var stderr = CaptureStderr(() =>
        {
            using var _ = EnvironmentControl.Apply(new EnvironmentOptions { DedicatedHostGuidance = true });
        });

        Assert.Contains("Dedicated-host guidance", stderr);
        Assert.Contains("--priority high", stderr);
        Assert.Contains("WithProcessPriority", stderr);
    }

    [Fact]
    public void Apply_DedicatedHostGuidance_NoPrioritySuggestion_WhenPriorityApplied()
    {
        // When priority was successfully applied, the suggestion should not appear -
        // the user already took the action. If the host refused the elevation (common
        // on locked-down CI and some macOS configurations), the suggestion correctly
        // fires and we skip the assertion rather than fail on an environment outcome.
        if (Environment.ProcessorCount < 4)
            return;

        var stderr = CaptureStderr(() =>
        {
            using var _ = EnvironmentControl.Apply(
                new EnvironmentOptions
                {
                    DedicatedHostGuidance = true,
                    ProcessPriority = ProcessPriorityClass.High,
                });
        });

        // A "could not raise" warning means the host refused; the suggestion is correct
        // in that case, so there is nothing to assert.
        if (stderr.Contains("could not raise process priority", StringComparison.OrdinalIgnoreCase))
            return;

        Assert.DoesNotContain("--priority high", stderr);
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
}