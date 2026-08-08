using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     The group's transferred receivers, rehydrated on first use and shared thereafter.
/// </summary>
/// <remarks>
///     <para>
///         The sharing is the point. Roslyn merges the captures of every lambda in a lexical scope into
///         one display class, so a suite's bodies and its lifecycle hooks routinely close over one
///         object - and rebuilding one per address meant this side had several objects where the
///         coordinator has one. Two benchmarks over a single array stopped seeing each other's writes,
///         and a <c>setup</c> hook would have cleared a buffer its body never reads.
///     </para>
///     <para>
///         Built lazily rather than up front because the receiver's <i>type</i> is not on the wire:
///         it is whichever delegate reaches an entry first, whose method's declaring type is its
///         receiver by construction. Every delegate sharing an entry shares that type, so the first
///         one to arrive settles it and the rest bind to what it built.
///     </para>
/// </remarks>
internal sealed class ResolvedReceivers(IReadOnlyList<TransferredReceiver> payloads)
{
    /// <summary>For groups that carry no addressed bodies, and so can never ask for a receiver.</summary>
    public static ResolvedReceivers None { get; } = new([]);

    private readonly object?[] _built = new object?[payloads.Count];

    public bool TryGet(int index, Type type, out object? receiver, out string? error)
    {
        ArgumentNullException.ThrowIfNull(type);

        receiver = null;
        error = null;

        if (index < 0 || index >= payloads.Count)
        {
            error = $"it names receiver {index}, but the group carries {payloads.Count}.";

            return false;
        }

        if (_built[index] is { } already)
        {
            receiver = already;

            return true;
        }

        if (!BodyResolver.TryBuild(type, payloads[index].Captures, out var created, out error))
            return false;

        _built[index] = created;
        receiver = created;

        return true;
    }
}
