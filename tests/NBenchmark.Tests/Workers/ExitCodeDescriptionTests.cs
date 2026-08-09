using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The exit code is the only evidence left when a worker dies hard, and a fault message that prints
///     a bare number sends the user nowhere. These tests pin the code-to-cause table that turns the
///     common crash exits into a named cause.
/// </summary>
/// <remarks>
///     <para>
///         The defect this replaces: <c>WorkerHost.ExitDescription</c> rendered a negative exit code as
///         "killed by signal {-code}". That branch is inverted on both platforms. On Unix .NET reports
///         a signal death as a <b>positive</b> <c>128 + signum</c> (SIGKILL 137, SIGSEGV 139, SIGABRT
///         134), so the branch never fired where signals occur. On Windows the NTSTATUS codes are
///         large unsigned values that read as negative <c>int</c>s, so the branch <i>did</i> fire and
///         rendered <c>STATUS_STACK_OVERFLOW</c> (0xC00000FD) as the nonsense
///         "killed by signal 1073741571".
///     </para>
///     <para>
///         An OOM kill - the case the plan calls out specifically - produced a bare
///         "(exit code 137)" with no hint that the kernel killed the worker for memory; the table names
///         it.
///     </para>
/// </remarks>
public sealed class ExitCodeDescriptionTests
{
    [Fact]
    public void Zero_ReportsAsExitCode()
    {
        Assert.Equal("exit code 0", ExitCodeDescription.Describe(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(70)]
    [InlineData(71)]
    public void UnknownPositiveCode_ReportsAsExitCode(int code)
    {
        // 71 is WorkerExitCode.CoordinatorLost - the orphaned-worker test asserts it renders as
        // "exit code 71", so a known-coordinator exit must not be reinterpreted as a crash cause.
        Assert.Equal($"exit code {code}", ExitCodeDescription.Describe(code));
    }

    [Fact]
    public void Sigkill_137_IsNamedAsTheOutOfMemoryKill()
    {
        var description = ExitCodeDescription.Describe(137);

        Assert.Contains("137", description);
        Assert.Contains("SIGKILL", description);

        // The actionable hint: a bare 137 is undiagnosable; the kernel's OOM killer is the usual cause.
        Assert.Contains("memory", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sigsegv_139_IsNamedAsASegmentationFault()
    {
        var description = ExitCodeDescription.Describe(139);

        Assert.Contains("139", description);
        Assert.Contains("SIGSEGV", description);
        Assert.Contains("segment", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sigabrt_134_IsNamedAsAbortOrStackOverflow()
    {
        var description = ExitCodeDescription.Describe(134);

        Assert.Contains("134", description);
        Assert.Contains("SIGABRT", description);
        Assert.Contains("stack", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsStatusStackOverflow_IsNamedWithTheHexCode()
    {
        // 0xC00000FD as a signed int is negative; this is the case the old branch rendered as
        // "killed by signal 1073741571".
        var description = ExitCodeDescription.Describe(unchecked((int)0xC00000FD));

        Assert.Contains("STATUS_STACK_OVERFLOW", description);
        Assert.Contains("0xC00000FD", description);
        Assert.Contains("stack", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsStatusAccessViolation_IsNamedWithTheHexCode()
    {
        var description = ExitCodeDescription.Describe(unchecked((int)0xC0000005));

        Assert.Contains("STATUS_ACCESS_VIOLATION", description);
        Assert.Contains("0xC0000005", description);
    }

    [Fact]
    public void UnknownNegativeCode_IsRenderedAsAWindowsStatusHex_NotAsASignal()
    {
        // An NTSTATUS we do not name explicitly: 0xC0000017 is STATUS_NO_MEMORY. It must not render as
        // "killed by signal ..." - that was the defect - but as a hex Windows status the user can look
        // up, alongside the raw exit code.
        var description = ExitCodeDescription.Describe(unchecked((int)0xC0000017));

        Assert.DoesNotContain("signal", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0xC0000017", description);
        Assert.Contains("-1073741801", description);
    }
}