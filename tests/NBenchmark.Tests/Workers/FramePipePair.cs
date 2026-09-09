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
    public static (FrameChannel Left, FrameChannel Right, IDisposable Cleanup) Create()
    {
        var leftToRight = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var rightToLeft = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

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
