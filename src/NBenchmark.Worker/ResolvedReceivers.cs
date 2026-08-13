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
///         Each entry names its own type, so the entry is built from what the coordinator walked
///         rather than from whichever delegate happens to reach it first. Those used to be assumed
///         equal - and are, for the receiver itself, but what a delegate offers is its method's
///         <i>declaring</i> type, which for an inherited method is a base class. The order the bodies
///         are shuffled into therefore decided which type an entry became, so a suite mixing
///         <c>obj.BaseMethod</c> and <c>obj.DerivedMethod</c> passed or failed on the seed.
///     </para>
///     <para>
///         What a delegate still supplies is the type it needs to <i>bind</i> to, which is checked
///         against what was built rather than used to build it.
///     </para>
/// </remarks>
internal sealed class ResolvedReceivers(
    IReadOnlyList<TransferredReceiver> payloads,
    BenchmarkLoadContext? context)
{
    /// <summary>For groups that carry no addressed bodies, and so can never ask for a receiver.</summary>
    public static ResolvedReceivers None { get; } = new([], null);

    private readonly object?[] _built = new object?[payloads.Count];

    /// <param name="boundTo">
    ///     The declaring type of the method about to be bound to this receiver. An assertion, not an
    ///     instruction: the entry is built from its own carried type, and this is what proves the two
    ///     agree before <c>CreateDelegate</c> fails with a signature error that names neither.
    /// </param>
    public bool TryGet(int index, Type boundTo, out object? receiver, out string? error)
    {
        ArgumentNullException.ThrowIfNull(boundTo);

        receiver = null;
        error = null;

        if (index < 0 || index >= payloads.Count)
        {
            error = $"it names receiver {index}, but the group carries {payloads.Count}.";

            return false;
        }

        if (_built[index] is not { } already)
        {
            var payload = payloads[index];

            if (context is null)
            {
                error = "its receiver holds state, but this group was resolved without a receiver table.";

                return false;
            }

            if (TypeNames.Resolve(payload.TypeName, context) is not { } receiverType)
            {
                error = $"the receiver type '{payload.TypeName}' could not be resolved in this worker.";

                return false;
            }

            if (!BodyResolver.TryBuild(receiverType, payload.Captures, context, out already, out error))
                return false;

            _built[index] = already;
        }

        if (!boundTo.IsInstanceOfType(already))
        {
            error = $"its receiver was rebuilt as '{already!.GetType().Name}', which is not a "
                    + $"'{boundTo.Name}' - the address and the receiver table disagree about what this "
                    + "body binds to.";

            return false;
        }

        receiver = already;

        return true;
    }
}
