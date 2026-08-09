namespace NBenchmark.Workers;

/// <summary>
///     Turns a worker process exit code into a phrase that names the cause, for a fault message.
/// </summary>
/// <remarks>
///     <para>
///         The exit code is the only evidence left when a worker dies hard - its stderr may be empty
///         and the pipe is gone - so a bare number sends the user nowhere. The common crash exits are
///         a closed set on both platforms, and naming them turns an undiagnosable vanishing process
///         into an actionable message.
///     </para>
///     <para>
///         This replaces a branch in <see cref="WorkerHost.ExitDescription" /> that rendered a negative
///         code as <c>"killed by signal {-code}"</c>. That branch was inverted on both platforms. On
///         Unix, .NET reports a signal death as a <b>positive</b> <c>128 + signum</c> (SIGKILL 137,
///         SIGSEGV 139, SIGABRT 134), so the negative branch never fired where signals actually
///         occur. On Windows, NTSTATUS codes are large unsigned values that read as negative
///         <see cref="int" />s, so the branch <i>did</i> fire and rendered
///         <c>STATUS_STACK_OVERFLOW</c> (0xC00000FD) as the nonsense "killed by signal 1073741571".
///         An out-of-memory kill produced a bare "(exit code 137)" with no hint that the kernel
///         ended the process for memory.
///     </para>
///     <para>
///         Pure and separate from <see cref="WorkerHost" /> so the table is testable without a real
///         process: the codes are facts about the operating system, not about any one worker.
///     </para>
/// </remarks>
internal static class ExitCodeDescription
{
    /// <summary>STATUS_STACK_OVERFLOW (0xC00000FD), as a signed <see cref="int" />.</summary>
    private const int StatusStackOverflow = unchecked((int)0xC00000FD);

    /// <summary>STATUS_ACCESS_VIOLATION (0xC0000005), as a signed <see cref="int" />.</summary>
    private const int StatusAccessViolation = unchecked((int)0xC0000005);

    public static string Describe(int code) => code switch
    {
        0 => "exit code 0",

        // Unix: the shell convention for "killed by signal N" is 128 + signum, and .NET reports
        // these as positive exit codes. The three that a benchmark worker can actually produce are
        // named with their actionable cause.
        134 => "killed by SIGABRT (exit code 134) - abort, often a stack overflow or an unhandled "
               + "exception",
        137 => "killed by SIGKILL (exit code 137) - the process was killed, most commonly by the "
               + "operating system's out-of-memory killer",
        139 => "killed by SIGSEGV (exit code 139) - a segmentation fault, usually native code or a "
               + "corrupted heap",

        // Windows: the runtime reports NTSTATUS codes, which are large unsigned values that read as
        // negative ints. The two a worker is most likely to die with are named with their hex so a
        // reader can search for them.
        StatusStackOverflow => "STATUS_STACK_OVERFLOW (0xC00000FD) - a stack overflow",
        StatusAccessViolation => "STATUS_ACCESS_VIOLATION (0xC0000005) - an access violation",

        // Any other negative code is an unnamed Windows NTSTATUS. Rendered as the hex status (which
        // is what a search for the cause will want) alongside the raw exit code, never as "killed by
        // signal N" - that was the defect this replaces.
        _ when code < 0 => $"Windows status 0x{(uint)code:X8} (exit code {code})",

        _ => $"exit code {code}",
    };
}