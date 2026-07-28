using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBenchmark.Workers;

/// <summary>
///     Length-prefixed frame transport over a pair of one-way streams.
///     <para>
///         Framing is a 4-byte little-endian byte count followed by that many bytes of UTF-8
///         JSON. A length prefix rather than a delimiter means a benchmark that writes to its own
///         stdout, or a payload containing newlines, cannot desynchronize the stream - the
///         previous file-based protocol avoided stdout for the same reason, and this keeps that
///         property while gaining a live channel.
///     </para>
/// </summary>
internal sealed class FrameChannel : IDisposable
{
    /// <summary>
    ///     Reflection-based rather than source-generated, matching how the rest of the repo
    ///     serializes <see cref="BenchmarkResult" />. The frames that carry real volume are sent
    ///     once per benchmark, and a frame costs tens of microseconds against a per-benchmark
    ///     floor of roughly 600 ms, so source generation would buy nothing measurable here.
    /// </summary>
    /// <remarks>
    ///     Nulls are written rather than omitted. <see cref="BenchmarkResult" /> declares its
    ///     allocation columns as <c>required</c> <i>and</i> nullable - "the measurement must state
    ///     whether it tracked allocations, and null means it did not" - so a global omit-nulls
    ///     policy produces JSON that will not deserialize. The envelope's unused payload slots are
    ///     suppressed individually instead, which keeps frames compact without that trap.
    /// </remarks>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly Stream _inbound;
    private readonly Stream _outbound;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _lengthBuffer = new byte[4];

    public FrameChannel(Stream inbound, Stream outbound)
    {
        _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
    }

    /// <summary>
    ///     Writes one frame. Serialized under a lock because the worker's measurement thread and
    ///     its progress callbacks both write, and two interleaved payloads would corrupt the
    ///     stream in a way the length prefix cannot recover from.
    /// </summary>
    public async Task WriteAsync(WorkerFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, SerializerOptions);

        if (payload.Length > WorkerProtocol.MaxFrameBytes)
        {
            throw new InvalidOperationException(
                $"A {frame.Kind} frame serialized to {payload.Length} bytes, above the "
                + $"{WorkerProtocol.MaxFrameBytes}-byte frame ceiling.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

            await _outbound.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await _outbound.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _outbound.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Reads one frame, or returns <c>null</c> at end of stream.
    ///     <para>
    ///         End of stream is the load-bearing signal on the worker side: it blocks here, so a
    ///         coordinator that dies (crash, kill -9, IDE stop button) closes the write end, this
    ///         returns <c>null</c>, and the worker exits on its own. Orphan avoidance is therefore
    ///         structural - measured at 7 ms - rather than dependent on a supervisor that could
    ///         itself be the thing that died.
    ///     </para>
    /// </summary>
    public async Task<WorkerFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await ReadExactlyAsync(_lengthBuffer, cancellationToken).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(_lengthBuffer);

        if (length <= 0 || length > WorkerProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException(
                $"Frame length prefix was {length}, outside 1..{WorkerProtocol.MaxFrameBytes}. "
                + "The stream is out of sync or was written by an incompatible build.");
        }

        var payload = new byte[length];

        if (!await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException(
                $"Stream ended mid-frame: expected {length} payload bytes.");
        }

        return JsonSerializer.Deserialize<WorkerFrame>(payload, SerializerOptions)
               ?? throw new InvalidDataException("A frame deserialized to null.");
    }

    /// <summary>
    ///     Fills <paramref name="buffer" /> completely, returning <c>false</c> only when the
    ///     stream ended cleanly before any byte arrived. A pipe hands over whatever is available,
    ///     so a partial read is normal and must be looped rather than treated as a short frame.
    /// </summary>
    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await _inbound
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
                return false;

            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _inbound.Dispose();
        _outbound.Dispose();
    }
}
