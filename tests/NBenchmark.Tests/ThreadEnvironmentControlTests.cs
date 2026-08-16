using System.Diagnostics;
using NBenchmark.Engine;
using NBenchmark.Interop;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Thread-level environment control. CI is Ubuntu-only, so two of the three platform paths are
///     never exercised there - which is why the <i>unavailable</i> path is asserted explicitly
///     rather than left as the untested remainder. A test that only passes because the call
///     silently did nothing is the failure mode these guard against.
/// </summary>
public class ThreadEnvironmentControlTests
{
    [Fact]
    public void Apply_Null_Options_Returns_A_Scope()
    {
        // Null is not a no-op here, unlike the process scope: the macOS quality-of-service
        // elevation is on by default and needs no configuration to apply.
        using var scope = ThreadEnvironmentControl.Apply(null);

        Assert.NotNull(scope);
    }

    [Fact]
    public void Apply_ThreadControl_Disabled_Applies_Nothing()
    {
        var priorPriority = Thread.CurrentThread.Priority;

        using (ThreadEnvironmentControl.Apply(new EnvironmentOptions
               {
                   ThreadControl = false,
                   CpuAffinity = [0],
                   ProcessPriority = ProcessPriorityClass.High,
               }))
        {
            Assert.Equal(priorPriority, Thread.CurrentThread.Priority);

            if (OperatingSystem.IsMacOS())
            {
                // A test runs on a thread-pool thread, whose class the runtime has already made
                // immutable at QOS_CLASS_UNSPECIFIED - so this asserts that opting out changed
                // nothing, which is all it can assert here.
                Assert.True(NativeThreadControl.TryReadQos(out var qos));
                Assert.NotEqual(NativeThreadControl.UserInteractiveQosClass, qos);
            }
        }

        Assert.Equal(priorPriority, Thread.CurrentThread.Priority);
    }

    [Fact]
    public void Apply_Dispose_Is_Idempotent()
    {
        var scope = ThreadEnvironmentControl.Apply(new EnvironmentOptions());

        scope.Dispose();
        scope.Dispose();
    }

    [Fact]
    public void Apply_Restores_Thread_Priority()
    {
        // Thread priority is only applied on Windows: under SCHED_OTHER it is a no-op, and
        // applying a control that does nothing is worse than declining to.
        if (!OperatingSystem.IsWindows())
            return;

        var priorPriority = Thread.CurrentThread.Priority;

        using (ThreadEnvironmentControl.Apply(new EnvironmentOptions
               {
                   ProcessPriority = ProcessPriorityClass.High,
               }))
        {
            Assert.Equal(ThreadPriority.Highest, Thread.CurrentThread.Priority);
        }

        Assert.Equal(priorPriority, Thread.CurrentThread.Priority);
    }

    [Fact]
    public void Apply_Leaves_Thread_Priority_Alone_Off_Windows()
    {
        if (OperatingSystem.IsWindows())
            return;

        var priorPriority = Thread.CurrentThread.Priority;

        using var scope = ThreadEnvironmentControl.Apply(new EnvironmentOptions
        {
            ProcessPriority = ProcessPriorityClass.High,
        });

        Assert.Equal(priorPriority, Thread.CurrentThread.Priority);
    }

    /// <summary>
    ///     The macOS elevation either lands or is refused, and both outcomes have to be correct:
    ///     a success has to be readable back, and a refusal has to leave the class where it was.
    ///     Which one happens is a property of the thread, not of the code - see
    ///     <see cref="TrySetUserInteractiveQos_Is_Refused_On_A_Runtime_Created_Thread" /> - so the
    ///     test asserts the invariant rather than picking a side.
    /// </summary>
    [Fact]
    public void Apply_Either_Raises_Qos_Or_Leaves_It_Untouched_On_MacOS()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Assert.True(NativeThreadControl.TryReadQos(out var before));

        using (ThreadEnvironmentControl.Apply(null))
        {
            Assert.True(NativeThreadControl.TryReadQos(out var during));

            Assert.True(
                during == NativeThreadControl.UserInteractiveQosClass || during == before,
                $"quality of service moved to an unrequested class: 0x{during:x}");
        }

        Assert.True(NativeThreadControl.TryReadQos(out var after));
        Assert.Equal(before, after);
    }

    /// <summary>
    ///     The finding that decides what the macOS story can honestly claim. Darwin refuses a
    ///     quality-of-service change on any thread that carries an explicit scheduling priority,
    ///     and the runtime gives one to every thread it creates - a <see cref="Thread" /> and a
    ///     thread-pool thread alike. Only the kernel-created process main thread accepts the call.
    ///     If a future runtime stops setting that priority, this test fails and the docs that
    ///     describe the limitation need revisiting.
    /// </summary>
    [Fact]
    public void TrySetUserInteractiveQos_Is_Refused_On_A_Runtime_Created_Thread()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var applied = false;
        var error = 0;

        var thread = new Thread(() => applied = NativeThreadControl.TrySetUserInteractiveQos(out _, out error));
        thread.Start();
        thread.Join();

        Assert.False(applied);
        Assert.Equal(NativeThreadControl.Eperm, error);
    }

    [Fact]
    public void Apply_Pins_The_Calling_Thread_Where_Affinity_Is_Supported()
    {
        if (!ThreadEnvironmentControl.ThreadAffinitySupported())
            return;

        // Pinning to core 0 and reading the mask back through the setter's own capture: setting
        // the same mask twice reports the first one as the prior value.
        using var scope = ThreadEnvironmentControl.Apply(new EnvironmentOptions { CpuAffinity = [0] });

        Assert.True(NativeThreadControl.TrySetThreadAffinity(1, out var current));
        Assert.Equal(1UL, current);
    }

    [Fact]
    public void TrySetThreadAffinity_Is_Unavailable_On_MacOS()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Assert.False(NativeThreadControl.TrySetThreadAffinity(1, out var previous));
        Assert.Equal(0UL, previous);
    }

    [Fact]
    public void TrySetUserInteractiveQos_Is_Unavailable_Off_MacOS()
    {
        if (OperatingSystem.IsMacOS())
            return;

        Assert.False(NativeThreadControl.TrySetUserInteractiveQos(out var previous, out var error));
        Assert.Equal(0U, previous);
        Assert.Equal(0, error);
        Assert.False(NativeThreadControl.TryReadQos(out _));
        Assert.False(NativeThreadControl.TryRestoreQos(0));
    }

    [Fact]
    public void TrySetThreadAffinity_Rejects_An_Empty_Mask()
    {
        // A zero mask means "no cores", which the OS would refuse anyway - caught before the call
        // so the answer is the same on every platform.
        Assert.False(NativeThreadControl.TrySetThreadAffinity(0, out _));
        Assert.False(NativeThreadControl.TryRestoreThreadAffinity(0));
    }

    [Fact]
    public void ThreadAffinitySupported_Matches_The_Platform()
    {
        Assert.Equal(
            OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
            ThreadEnvironmentControl.ThreadAffinitySupported());
    }

    [Theory]
    [InlineData(ProcessPriorityClass.Idle, ThreadPriority.Lowest)]
    [InlineData(ProcessPriorityClass.BelowNormal, ThreadPriority.BelowNormal)]
    [InlineData(ProcessPriorityClass.Normal, ThreadPriority.Normal)]
    [InlineData(ProcessPriorityClass.AboveNormal, ThreadPriority.AboveNormal)]
    [InlineData(ProcessPriorityClass.High, ThreadPriority.Highest)]
    [InlineData(ProcessPriorityClass.RealTime, ThreadPriority.Highest)]
    public void ToThreadPriority_Maps_Every_Process_Class(ProcessPriorityClass input, ThreadPriority expected)
    {
        Assert.Equal(expected, ThreadEnvironmentControl.ToThreadPriority(input));
    }

    [Fact]
    public void Apply_Out_Of_Range_Affinity_Warns_And_Proceeds()
    {
        var priorError = Console.Error;
        var writer = new StringWriter();

        try
        {
            Console.SetError(writer);

            // Never throws is the contract: a benchmark run must not fail because a scheduler
            // hint was refused.
            using var scope = ThreadEnvironmentControl.Apply(new EnvironmentOptions
            {
                CpuAffinity = [Environment.ProcessorCount + 64],
            });

            Assert.NotNull(scope);
        }
        finally
        {
            Console.SetError(priorError);
        }

        if (ThreadEnvironmentControl.ThreadAffinitySupported())
            Assert.Contains("measurement thread", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
