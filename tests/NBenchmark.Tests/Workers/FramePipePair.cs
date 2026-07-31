using System.IO.Pipes;
using NBenchmark.Workers;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     A connected pair of <see cref="FrameChannel" />s over real anonymous pipes, as the
///     coordinator and worker see them.
/// </summary>
/// <remarks>
///     Shared by <see cref="FrameChannelTests" /> and <see cref="WorkerFrameContractTests" /> rather
///     than duplicated, because the one subtlety in it - that
///     <c>DisposeLocalCopyOfClientHandle</c> must <i>not</i> be called when both ends live in this
///     process - is the kind of detail a second copy gets wrong once and then silently keeps.
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
