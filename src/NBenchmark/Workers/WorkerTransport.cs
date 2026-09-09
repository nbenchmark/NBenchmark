using System.IO.Pipes;

namespace NBenchmark.Workers;

/// <summary>
///     The pipe pair a coordinator and a worker talk over, created from one place so the two ends
///     cannot drift apart.
/// </summary>
/// <remarks>
///     <para>
///         These are named pipes rather than the anonymous pair this replaced, and the reason is
///         cancellation rather than naming. <see cref="AnonymousPipeServerStream" /> has no
///         <see cref="PipeOptions" /> overload, so on Windows its handle is never opened
///         <c>FILE_FLAG_OVERLAPPED</c>; <c>PipeStream.ReadAsync</c> checks <c>IsAsync</c>, finds it
///         false, and falls through to <see cref="Stream.ReadAsync(Memory{byte},CancellationToken)" />,
///         which runs a <i>blocking</i> read on a thread-pool thread and only observes the token
///         before it starts. Every deadline in the protocol - <c>WorkerGroupRunner</c>'s group
///         ceiling, its idle-frame timeout, the handshake timeout here - was therefore unenforceable
///         on Windows: the token fired and nothing happened. A worker that died in a way that left
///         the write end of the pipe open hung the coordinator forever, which is exactly what
///         <c>HardCrashTests</c> did to CI for 114 minutes a night.
///     </para>
///     <para>
///         Named pipes opened <see cref="PipeOptions.Asynchronous" /> use overlapped I/O, so a
///         cancelled read is really cancelled. They also take handle inheritance out of the picture
///         entirely - the worker connects by name rather than being handed a duplicated handle -
///         which removes the <c>DisposeLocalCopyOfClientHandle</c> ordering hazard and any question
///         of who else inherited the write end.
///     </para>
///     <para>
///         What a name costs is that the endpoint is reachable by anything that can guess it, unlike
///         an inherited handle. <see cref="PipeOptions.CurrentUserOnly" /> is the answer on both
///         platforms: on Windows it builds a security descriptor granting only the current user, and
///         on Unix - where a named pipe is a socket file - it restricts the file's permissions and
///         checks the peer's uid. The name itself is a fresh GUID per worker, and the server accepts
///         exactly one instance, so there is nothing to squat on afterwards either.
///     </para>
/// </remarks>
internal static class WorkerTransport
{
    /// <summary>
    ///     Per-direction pipe buffer. Large enough that a streamed sample payload does not stall the
    ///     writer on a reader that is briefly busy, small enough to be irrelevant per worker.
    /// </summary>
    private const int PipeBufferBytes = 64 * 1024;

    /// <summary>
    ///     Both ends must pass the same options or the connect fails in a way that looks like a
    ///     worker that hung on startup, so they live here rather than at the two call sites.
    /// </summary>
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    /// <summary>
    ///     A name no other worker on the machine will pick. Random rather than anything derived from
    ///     the run: a predictable name is the one thing <see cref="PipeOptions.CurrentUserOnly" />
    ///     cannot defend against a process running as the same user.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Short on purpose, and this is the constraint that sets the length. On Unix a named
    ///         pipe is a socket file at <c>$TMPDIR/CoreFxPipe_{name}</c>, and a Unix socket path is
    ///         capped by <c>sockaddr_un.sun_path</c> at 104 bytes on macOS and 108 on Linux. A stock
    ///         macOS <c>TMPDIR</c> is a 49-character <c>/var/folders/…</c> path, so the eleven
    ///         characters of <c>CoreFxPipe_</c> leave about 44 for the name - and the obvious
    ///         <c>$"nbworker-{Guid:n}"</c> spends 41 of them, clearing the limit by three characters
    ///         on a default machine and failing outright on any developer with a longer temp path.
    ///     </para>
    ///     <para>
    ///         So the GUID is carried as base64 rather than hex: 22 characters instead of 32, for
    ///         the same 128 bits. With the prefix that is 26, which leaves roughly 20 characters of
    ///         headroom on the tightest platform. <c>+</c> and <c>/</c> are swapped out because the
    ///         name is a filename on Unix, and the padding is dropped because it is always <c>==</c>
    ///         for a 16-byte input and carries nothing.
    ///     </para>
    /// </remarks>
    public static string NewPipeName()
        => "nbw-" + Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>The coordinator's end. <paramref name="direction" /> is the coordinator's view of it.</summary>
    /// <param name="pipeName">The name from <see cref="NewPipeName" />, also passed to the worker on its command line.</param>
    /// <param name="direction">Which way this end of the pipe flows, from the coordinator's side.</param>
    public static NamedPipeServerStream CreateServer(string pipeName, PipeDirection direction)
        => new(
            pipeName,
            direction,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            Options,
            PipeBufferBytes,
            PipeBufferBytes);

    /// <summary>The worker's end. <paramref name="direction" /> is the worker's view of it.</summary>
    /// <param name="pipeName">The name the coordinator passed on the command line.</param>
    /// <param name="direction">Which way this end of the pipe flows, from the worker's side.</param>
    public static NamedPipeClientStream CreateClient(string pipeName, PipeDirection direction)
        => new(".", pipeName, direction, Options);
}
