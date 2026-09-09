using System.IO.Pipes;
using NBenchmark.Workers;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     A connected pair of <see cref="FrameChannel" />s over a real pipe, for testing what
///     <see cref="FrameChannel" /> puts on the wire.
/// </summary>
/// <remarks>
///     <para>
///         Anonymous pipes, deliberately, and <i>not</i> the transport a real coordinator and worker
///         use - that is a named pair, built by <c>WorkerTransport</c> and exercised by
///         <see cref="WorkerTransportTests" />. What these tests are about is the frame codec, which
///         sees a <see cref="Stream" /> and nothing more, so the cheapest in-process pair that can be
///         built synchronously is the right one. Nothing here should be read as evidence about the
///         transport's own behaviour; in particular an anonymous pipe read is not cancellable on
///         Windows, which is the whole reason the product no longer uses one.
///     </para>
///     <para>
///         Shared by <see cref="FrameChannelTests" /> and <see cref="WorkerFrameContractTests" />
///         rather than duplicated, because the one subtlety in it - that
///         <c>DisposeLocalCopyOfClientHandle</c> must <i>not</i> be called when both ends live in
///         this process - is the kind of detail a second copy gets wrong once and then silently
///         keeps.
///     </para>
/// </remarks>
internal static class FramePipePair
{
    /// <summary>
    ///     How much a test may write before anything reads. Every caller writes a whole frame and
    ///     then reads it back on one thread, so a frame larger than the pipe holds blocks the write
    ///     against a reader that cannot run until the write returns, and the test deadlocks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Stated explicitly because the default is not the same number on every platform, and
    ///         that is what made this expensive. A Unix pipe holds 64 KB and ignores this argument
    ///         entirely; a Windows anonymous pipe defaults to about 4 KB. So
    ///         <c>ObserverSamples_RoundTripsAWholeBatch</c>, whose frame serializes to 12,702 bytes,
    ///         passed on Linux and macOS and hung forever on Windows - the whole CI job with it.
    ///     </para>
    ///     <para>
    ///         Matching the Unix capacity does not remove the ceiling, it just puts it in one place
    ///         on every platform. That is the property worth having: a future frame test that
    ///         outgrows this deadlocks on the machine of whoever writes it, rather than only in
    ///         Windows CI. If a test legitimately needs a bigger frame, overlap the read with the
    ///         write rather than raising this - the pipe is bounded at any size.
    ///     </para>
    /// </remarks>
    private const int PipeBufferBytes = 64 * 1024;

    public static (FrameChannel Left, FrameChannel Right, IDisposable Cleanup) Create()
    {
        var leftToRight = new AnonymousPipeServerStream(
            PipeDirection.Out, HandleInheritability.None, PipeBufferBytes);

        var rightToLeft = new AnonymousPipeServerStream(
            PipeDirection.In, HandleInheritability.None, PipeBufferBytes);

        var rightInbound = new AnonymousPipeClientStream(
            PipeDirection.In, leftToRight.GetClientHandleAsString());

        var rightOutbound = new AnonymousPipeClientStream(
            PipeDirection.Out, rightToLeft.GetClientHandleAsString());

        // Deliberately no DisposeLocalCopyOfClientHandle here. That call exists for the
        // cross-process case, where the child inherited a duplicate of the handle and the
        // parent's own copy must be closed so the child's exit is visible as end-of-stream.
        // Both ends live in this process, so the client stream wraps the very same handle and
        // closing it would break the pipe immediately.

        var left = new FrameChannel(rightToLeft, leftToRight);
        var right = new FrameChannel(rightInbound, rightOutbound);

        return (left, right, new Disposables(left, right));
    }

    private sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                try
                {
                    item.Dispose();
                }
                catch (IOException)
                {
                    // The peer may already have torn the pipe down; nothing actionable.
                }
            }
        }
    }
}
