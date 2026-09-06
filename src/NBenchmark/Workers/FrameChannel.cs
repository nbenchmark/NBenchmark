using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
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
[RequiresUnreferencedCode("Serializes the worker protocol with the reflection-based JSON serializer.")]
[RequiresDynamicCode("Serializes the worker protocol with the reflection-based JSON serializer.")]
internal sealed class FrameChannel : IDisposable
{
    /// <summary>
    ///     Reflection-based rather than source-generated, matching how the rest of the repo
    ///     serializes <see cref="BenchmarkResult" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Source generation was designed in and then measured out. Its three usual arguments all
    ///         come up empty here. <b>Speed:</b> the frames that carry real volume are sent once per
    ///         benchmark and cost tens of microseconds against a per-benchmark floor of roughly
    ///         600 ms. <b>Trimming and AOT:</b> unreachable by construction - the worker loads
    ///         arbitrary user assemblies into a custom load context and resolves benchmark bodies by
    ///         metadata token, which no static analysis can follow, and nothing in this repo declares
    ///         itself trimmable. <b>Correctness:</b> a generator context over the whole frame graph
    ///         was built and produced zero diagnostics, so there was no unsupported shape for it to
    ///         have caught.
    ///     </para>
    ///     <para>
    ///         What protects this wire instead is a pair of test suites. <c>FrameChannelTests</c>
    ///         round-trips each frame kind by hand, asserting field by field, including a
    ///         fully-populated <see cref="MeasurementOptions" />. <c>WorkerFrameContractTests</c> then
    ///         derives its coverage from the types rather than restating it: every
    ///         <see cref="WorkerFrameKind" /> is built with every property set to a non-default value
    ///         and required to survive the wire unchanged. Between them they catch the failure source
    ///         generation would not - a member that serializes but does not come back, which is silent
    ///         in both schemes - and the second one keeps catching it after a new member is added,
    ///         which hand-written coverage of a closed set does not.
    ///     </para>
    ///     <para>
    ///         Nulls are written rather than omitted. <see cref="BenchmarkResult" /> declares its
    ///         allocation columns as <c>required</c> <i>and</i> nullable - "the measurement must state
    ///         whether it tracked allocations, and null means it did not" - so a global omit-nulls
    ///         policy produces JSON that will not deserialize. The envelope's unused payload slots are
    ///         suppressed individually instead, which keeps frames compact without that trap.
    ///     </para>
    ///     <para>
    ///         <see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals" /> is not optional here.
    ///         Statistics legitimately produce non-finite values - a benchmark whose samples are all
    ///         identical has zero variance, so its skewness and kurtosis are 0/0 - and by default
    ///         <c>Utf8JsonWriter</c> throws rather than writing <c>NaN</c>. That threw <i>inside the
    ///         worker</i>, killing it after the measurement had already succeeded, and the coordinator
    ///         saw only a vanished process. Trivially fast bodies hit it intermittently, which is the
    ///         worst kind of bug to leave in a benchmarking tool.
    ///     </para>
    /// </remarks>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,

        // D7: a BenchmarkParameter's Value is object?, which the default reflection-based handling
        // cannot serialize (a [Arguments(typeof(X))] value) or deserialize back to anything but a
        // type-blind JsonElement. See BenchmarkParameterConverter.
        Converters = { new BenchmarkParameterConverter() },
    };

    /// <summary>
    ///     Why this process cannot use the frame transport at all, or <c>null</c> when it can.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The remark above - that trimming is "unreachable by construction" - is true of the
    ///         <i>worker</i> and not of the <i>coordinator</i>. The coordinator is the user's own
    ///         application, and a user is free to publish it trimmed or with reflection-based
    ///         serialization switched off. <see cref="SerializerOptions" /> carries no
    ///         <c>TypeInfoResolver</c>, so in that build the very first frame write throws - after the
    ///         worker has been spawned and handed a pipe - and the coordinator sees a process that
    ///         started and then went silent. The symptom is a dead worker; the cause is a publish
    ///         setting, and nothing connects the two.
    ///     </para>
    ///     <para>
    ///         Answered once, before anything is launched, and treated as "no worker is available":
    ///         that is exactly what it means, and the run then measures in-process with the reason
    ///         stamped on every row instead of losing a worker per group to a fault it cannot explain.
    ///         The message names the property because the property is the fix.
    ///     </para>
    ///     <para>
    ///         <see cref="JsonSerializer.IsReflectionEnabledByDefault" /> is the switch this can
    ///         actually observe. A build that leaves it on and merely trims the frame graph away is not
    ///         detectable from here, which is why the message names <c>PublishTrimmed</c> too.
    ///     </para>
    /// </remarks>
    internal static string? TransportRefusal { get; } = JsonSerializer.IsReflectionEnabledByDefault
        ? null
        : "reflection-based JSON serialization is disabled in this process, so the coordinator cannot "
          + "write a frame to a worker. This is a publish setting rather than anything about the "
          + "benchmark: set <JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault> "
          + "in the benchmark host project, or run it from a build without PublishTrimmed.";

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
            throw new BenchmarkExecutionException(
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
    ///         End of stream is the load-bearing signal on the worker side: a coordinator that dies
    ///         (crash, kill -9, IDE stop button) closes the write end, this returns <c>null</c>, and the
    ///         worker exits on its own. Orphan avoidance is therefore structural rather than dependent
    ///         on a supervisor that could itself be the thing that died.
    ///     </para>
    ///     <para>
    ///         That only holds while something is actually reading. It did not, once: the worker's
    ///         dispatch loop awaited each group before reading again, so during a group - which is most
    ///         of a run - nothing was blocked here and an orphan measured on for nobody.
    ///         <c>WorkerSession</c> now pumps this continuously on its own task for exactly that
    ///         reason.
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
    /// <remarks>
    ///     The distinction between "no byte yet" and "some bytes, then nothing" is what separates a
    ///     clean end from a torn frame. A read that returns zero <i>after</i> bytes have already
    ///     filled part of the buffer means the peer was mid-frame when it vanished; returning
    ///     <c>false</c> there would let a torn length prefix read as a clean <c>null</c>, and a
    ///     worker that crashed while writing would look exactly like one that had finished. So the
    ///     zero read throws once any byte has arrived, and <c>false</c> means only "the stream ended
    ///     cleanly before any byte of this read."
    /// </remarks>
    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await _inbound
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                // Clean end before any byte of this read: the caller treats false as end-of-stream.
                // Mid-frame: some bytes arrived and then the stream died - a torn frame, not a
                // clean end, and the caller must surface it rather than swallow it as null.
                if (offset > 0)
                {
                    throw new EndOfStreamException(
                        $"Stream ended mid-frame: expected {buffer.Length} bytes, "
                        + $"received {offset} before end of stream.");
                }

                return false;
            }

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
