using System.IO.Pipes;
using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     <c>nbworker</c> - the process that actually measures.
///     <para>
///         It exists for one reason: JIT tiering, dynamic PGO, ReadyToRun and GC flavour are read by
///         the runtime once, at startup, and can never be changed afterwards. A coordinator can only
///         deliver them to a process that has not started yet. Everything else about the design
///         follows from that.
///     </para>
///     <para>
///         The worker never re-runs the user's entry point. It loads the assembly under test, binds
///         the requested bodies, measures, and streams results back over a pipe. That is what keeps
///         a run linear in the number of benchmarks rather than quadratic, and what stops a
///         benchmark project's <c>Main</c> - and any side effect in it - from re-executing once per
///         measurement.
///     </para>
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var inboundHandle, out var outboundHandle, out var parentPid, out var error))
        {
            Console.Error.WriteLine($"nbworker: {error}");

            Console.Error.WriteLine(
                $"usage: nbworker {WorkerProtocol.InboundHandleArgument} <handle> "
                + $"{WorkerProtocol.OutboundHandleArgument} <handle> "
                + $"[{WorkerProtocol.ParentProcessIdArgument} <pid>]");

            Console.Error.WriteLine(
                "nbworker is launched by NBenchmark and is not intended to be run by hand.");

            return WorkerExitCode.BadArguments;
        }

        // Ctrl-C is handled by the coordinator, which then closes the pipe. Cancelling here as well
        // would race the two shutdown paths and could truncate a result the worker had already
        // finished measuring.
        using var cts = new CancellationTokenSource();

        try
        {
            using var inbound = new AnonymousPipeClientStream(PipeDirection.In, inboundHandle);
            using var outbound = new AnonymousPipeClientStream(PipeDirection.Out, outboundHandle);
            using var channel = new FrameChannel(inbound, outbound);

            var session = new WorkerSession(channel);

            return await session.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The coordinator went away. This is the normal end for an orphaned worker and is not
            // an error worth a stack trace - the read returning end-of-stream is exactly the
            // mechanism that stops workers outliving the run that started them.
            return WorkerExitCode.Success;
        }
        catch (Exception ex)
        {
            // Last resort. The coordinator can no longer be told (the channel is the thing that
            // failed), so stderr is the only channel left - and the coordinator captures it.
            Console.Error.WriteLine($"nbworker: unhandled failure (parent pid {parentPid}): {ex}");

            return WorkerExitCode.Crashed;
        }
    }

    private static bool TryParse(
        string[] args,
        out string inboundHandle,
        out string outboundHandle,
        out int parentPid,
        out string? error)
    {
        inboundHandle = "";
        outboundHandle = "";
        parentPid = 0;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];

            if (i + 1 >= args.Length)
            {
                error = $"'{name}' expects a value.";
                return false;
            }

            var value = args[++i];

            switch (name)
            {
                case WorkerProtocol.InboundHandleArgument:
                    inboundHandle = value;
                    break;

                case WorkerProtocol.OutboundHandleArgument:
                    outboundHandle = value;
                    break;

                case WorkerProtocol.ParentProcessIdArgument:
                    _ = int.TryParse(value, out parentPid);
                    break;

                default:
                    error = $"unrecognized argument '{name}'.";
                    return false;
            }
        }

        if (inboundHandle.Length == 0 || outboundHandle.Length == 0)
        {
            error = "both pipe handles are required.";
            return false;
        }

        return true;
    }
}
