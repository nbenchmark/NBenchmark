using System.Diagnostics;
using System.IO.Pipes;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The properties the coordinator-to-worker pipe has to have, asserted against the real
///     transport rather than inferred from the code that builds it.
/// </summary>
/// <remarks>
///     <para>
///         These exist because of a Windows-only hang that cost a nightly CI job 114 minutes a
///         night. The transport was a pair of <see cref="AnonymousPipeServerStream" />s, which have
///         no <see cref="PipeOptions" /> overload and so are never opened for overlapped I/O. On
///         Windows <c>PipeStream.ReadAsync</c> checks <c>IsAsync</c>, finds it false, and falls back
///         to a <i>blocking</i> read on a thread-pool thread that observes its
///         <see cref="CancellationToken" /> only before it starts. Every deadline in the protocol
///         went with it: <c>WorkerGroupRunner</c>'s group ceiling and idle-frame timeout and
///         <c>WorkerHost</c>'s handshake timeout all fired their tokens into a read that could not
///         hear them.
///     </para>
///     <para>
///         Nothing caught it, because on Linux and macOS a pipe read is interruptible and every one
///         of those deadlines worked. <c>RealWorkerTests.Worker_ThatWedges_IsStoppedAndReported</c>
///         is a ceiling test that would have hung on Windows too - it simply sorts after
///         <c>HardCrashTests</c>, which hung first. So the point of the suite below is to pin the
///         transport property itself, cheaply and without spawning anything, rather than to rely on
///         a higher-level test noticing.
///     </para>
/// </remarks>
public sealed class WorkerTransportTests
{
    /// <summary>
    ///     Long enough to be unambiguously deliberate, short enough that the cancellation test does
    ///     not dominate the suite.
    /// </summary>
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     What "cancelled" has to beat. Deliberately loose: the assertion being made is "not
    ///     infinite", and the regression this guards produced a read that never returned at all. A
    ///     bound tight enough to measure scheduling latency would be flaky on a loaded CI runner and
    ///     would get the test deleted, which costs more than the precision is worth.
    /// </summary>
    private static readonly TimeSpan CancellationBound = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     A named-pipe pair connected exactly the way <c>WorkerHost</c> and <c>nbworker</c> connect
    ///     one, so these tests cannot pass against a transport the product does not use.
    /// </summary>
    private static async Task<TransportPair> ConnectAsync()
    {
        var inboundName = WorkerTransport.NewPipeName();
        var outboundName = WorkerTransport.NewPipeName();

        var toWorker = WorkerTransport.CreateServer(inboundName, PipeDirection.Out);
        var fromWorker = WorkerTransport.CreateServer(outboundName, PipeDirection.In);

        var workerInbound = WorkerTransport.CreateClient(inboundName, PipeDirection.In);
        var workerOutbound = WorkerTransport.CreateClient(outboundName, PipeDirection.Out);

        // Both sides block until the other arrives, so the four have to be in flight together.
        await Task.WhenAll(
            toWorker.WaitForConnectionAsync(),
            fromWorker.WaitForConnectionAsync(),
            workerInbound.ConnectAsync(WorkerProtocol.ConnectTimeout, CancellationToken.None),
            workerOutbound.ConnectAsync(WorkerProtocol.ConnectTimeout, CancellationToken.None));

        return new TransportPair(toWorker, fromWorker, workerInbound, workerOutbound);
    }

    /// <summary>
    ///     The defect itself, stated as a property. Overlapped I/O is what makes every deadline in
    ///     the worker protocol enforceable, so a transport that loses it silently disarms all of
    ///     them - which is precisely how this went unnoticed for as long as it did.
    /// </summary>
    /// <remarks>
    ///     The assertions have to come after the connect, and moving them earlier would quietly
    ///     empty this test out. On Unix a named pipe is a socket, and the stream does not adopt the
    ///     accepted handle - and so does not report <c>IsAsync</c> - until a peer is on the other
    ///     end: measured here, both ends read <c>false</c> before connecting and <c>true</c>
    ///     afterwards. An <see cref="AnonymousPipeServerStream" /> reads <c>false</c> at both points
    ///     on every platform, which is what makes this a real guard rather than a Windows-only one.
    /// </remarks>
    [Fact]
    public async Task Every_End_Of_The_Transport_Is_Opened_For_Overlapped_Io()
    {
        await using var pair = await ConnectAsync();

        Assert.True(pair.ToWorker.IsAsync, "the coordinator's write end is not async");
        Assert.True(pair.FromWorker.IsAsync, "the coordinator's read end is not async");
        Assert.True(pair.WorkerInbound.IsAsync, "the worker's read end is not async");
        Assert.True(pair.WorkerOutbound.IsAsync, "the worker's write end is not async");
    }

    /// <summary>
    ///     The regression, end to end at the transport level: a peer that is connected and alive but
    ///     says nothing must not be able to outlast the reader's deadline.
    /// </summary>
    /// <remarks>
    ///     This is the shape of every real hang it stands for - a benchmark body wedged on a lock, a
    ///     <c>[GlobalSetup]</c> that never returns, a worker whose write end outlived it - because in
    ///     all of them the coordinator is holding a live pipe that will never produce another byte.
    ///     On the anonymous transport this test does not fail on Windows; it never returns.
    /// </remarks>
    [Fact]
    public async Task A_Read_From_A_Living_But_Silent_Peer_Observes_Cancellation()
    {
        await using var pair = await ConnectAsync();

        var channel = new FrameChannel(pair.FromWorker, pair.ToWorker);

        using var cts = new CancellationTokenSource(ReadDeadline);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadAsync(cts.Token));

        clock.Stop();

        Assert.True(
            clock.Elapsed < CancellationBound,
            $"the read took {clock.Elapsed.TotalSeconds:0.#}s to observe a {ReadDeadline.TotalSeconds:0.#}s "
            + "deadline, so cancellation is not reaching the pipe");
    }

    /// <summary>
    ///     Orphan avoidance is structural rather than supervised: the worker sits reading its inbound
    ///     pipe, so a coordinator that dies closes the write end and the worker's read ends cleanly.
    ///     Moving to named pipes had to preserve that, because nothing else stops a worker outliving
    ///     the run that started it.
    /// </summary>
    [Fact]
    public async Task A_Peer_That_Goes_Away_Reads_As_A_Clean_End_Of_Stream()
    {
        await using var pair = await ConnectAsync();

        var worker = new FrameChannel(pair.WorkerInbound, pair.WorkerOutbound);

        // The coordinator vanishing, as the worker experiences it.
        pair.ToWorker.Dispose();

        using var cts = new CancellationTokenSource(CancellationBound);

        Assert.Null(await worker.ReadAsync(cts.Token));
    }

    /// <summary>
    ///     That the transport still carries a frame. Cheap, and the thing most likely to break while
    ///     rearranging how the two ends meet.
    /// </summary>
    [Fact]
    public async Task A_Frame_Round_Trips_Over_The_Real_Transport()
    {
        await using var pair = await ConnectAsync();

        var coordinator = new FrameChannel(pair.FromWorker, pair.ToWorker);
        var worker = new FrameChannel(pair.WorkerInbound, pair.WorkerOutbound);

        using var cts = new CancellationTokenSource(CancellationBound);

        await coordinator.WriteAsync(
            WorkerFrame.Of(new HandshakePayload
            {
                ProtocolVersion = WorkerProtocol.Version,
                ParentProcessId = Environment.ProcessId,
            }),
            cts.Token);

        var frame = await worker.ReadAsync(cts.Token);

        Assert.NotNull(frame);
        Assert.Equal(WorkerFrameKind.Handshake, frame.Kind);
        Assert.Equal(WorkerProtocol.Version, frame.Handshake?.ProtocolVersion);
    }

    /// <summary>
    ///     A named endpoint is guessable in a way an inherited handle was not, so the name is the
    ///     whole of the isolation between two workers - and, with
    ///     <see cref="PipeOptions.CurrentUserOnly" /> handling other users, between this run and
    ///     anything else the same user is running.
    /// </summary>
    [Fact]
    public void Every_Worker_Gets_A_Name_Of_Its_Own()
    {
        var names = Enumerable.Range(0, 100).Select(_ => WorkerTransport.NewPipeName()).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    ///     The pipe name has to stay short enough that its Unix socket path fits in
    ///     <c>sockaddr_un.sun_path</c>, which is 104 bytes on macOS and 108 on Linux.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Worth a test because the failure is invisible until it is total, and it lands on a
    ///         developer's machine rather than in CI: a GitHub runner's <c>TMPDIR</c> is <c>/tmp</c>,
    ///         which has room to spare, while a stock macOS one is a 49-character
    ///         <c>/var/folders/…</c> path. The obvious <c>$"nbworker-{Guid:n}"</c> spelling of this
    ///         name cleared the macOS limit by three characters, so a slightly longer temp path -
    ///         or one more character of prefix here - would break every isolated benchmark for that
    ///         developer and for nobody reviewing the change.
    ///     </para>
    ///     <para>
    ///         Asserted against this machine's real <c>TMPDIR</c> rather than an assumed one, and
    ///         with a margin, so it fails while there is still room to fix it rather than at the
    ///         moment the last byte goes.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_Pipe_Name_Leaves_Room_For_A_Unix_Socket_Path()
    {
        if (OperatingSystem.IsWindows())
            return;

        // How .NET lays a named pipe out on Unix: a socket file under the temp directory, behind a
        // fixed prefix.
        const string RuntimePrefix = "CoreFxPipe_";
        const int ShortestSunPath = 104;
        const int Margin = 12;

        var path = Path.Combine(Path.GetTempPath(), RuntimePrefix + WorkerTransport.NewPipeName());

        Assert.True(
            path.Length + Margin <= ShortestSunPath,
            $"the worker pipe's socket path is {path.Length} bytes ('{path}'), within {Margin} of the "
            + $"{ShortestSunPath}-byte limit. Shorten the name in WorkerTransport.NewPipeName.");
    }

    private sealed record TransportPair(
        NamedPipeServerStream ToWorker,
        NamedPipeServerStream FromWorker,
        NamedPipeClientStream WorkerInbound,
        NamedPipeClientStream WorkerOutbound) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            foreach (var stream in new Stream[] { ToWorker, FromWorker, WorkerInbound, WorkerOutbound })
            {
                try
                {
                    stream.Dispose();
                }
                catch (IOException)
                {
                    // The peer tore its end down first; nothing actionable.
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
