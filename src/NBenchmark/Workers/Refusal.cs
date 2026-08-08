namespace NBenchmark.Workers;

/// <summary>
///     Why a delegate could not be addressed for another process.
/// </summary>
/// <remarks>
///     <para>
///         The reason is carried as a value because three call sites used to recover it by searching
///         the message text - <c>refusal.Contains("captures")</c> - to choose between
///         <see cref="IsolationStatus.InProcessCapturedState" /> and a structural status. That made
///         every refusal message load-bearing prose: rewording one, or translating it, silently
///         changed which remedy a user was shown, and nothing in the build would have said so.
///     </para>
///     <para>
///         The set is deliberately shorter than the list of things that can go wrong. What a consumer
///         needs is the <i>remedy class</i> - "a value in this process cannot be reproduced" versus
///         "the shape cannot be addressed at all" - not a distinct member per check. Messages stay
///         specific; only the classification is coarse.
///     </para>
/// </remarks>
internal enum RefusalReason
{
    /// <summary>No refusal.</summary>
    None = 0,

    /// <summary>
    ///     The delegate closes over a value this process holds that could not be transferred or
    ///     rebuilt. The remedy is to name the preparation so the worker can build it.
    /// </summary>
    CapturedState,

    /// <summary>
    ///     The delegate is bound to a live user object whose state could not be transferred. Distinct
    ///     from <see cref="CapturedState" /> only in the message: a reader who wrote
    ///     <c>widget.Compute</c> is not looking for the word "capture".
    /// </summary>
    LiveReceiver,

    /// <summary>
    ///     The delegate's lowered shape cannot be addressed at all - an open-instance delegate, an
    ///     unbound one, or a closure with no singleton to bind to. Nothing about the user's data is
    ///     the problem, so no state-shaped remedy applies.
    /// </summary>
    UnaddressableShape,

    /// <summary>The defining assembly has no file on disk for a worker to load.</summary>
    NoAssemblyOnDisk,

    /// <summary>Declared in a generic context whose type arguments cannot be named across the boundary.</summary>
    OpenGenericContext,

    /// <summary>
    ///     The body's own parameters cannot be supplied - wrong arity, too many, an unsupported
    ///     parameter type, or two conflicting sources for one slot.
    /// </summary>
    UnaddressableArguments,

    /// <summary>The prepare delegate itself could not be addressed, or does not fit the body.</summary>
    PrepareDelegate,
}

/// <summary>
///     A refusal: why, in a form a consumer can branch on, and what to tell the user.
/// </summary>
internal readonly record struct Refusal(RefusalReason Reason, string Message)
{
    public static Refusal None => default;

    public bool IsRefused => Reason != RefusalReason.None;

    /// <summary>
    ///     The status to stamp on results this refusal sends to the host process.
    /// </summary>
    /// <param name="structural">
    ///     What to call a refusal that is <i>not</i> about a value in this process. Supplied by the
    ///     caller because the honest name differs by mode: an inline suite's structural refusal is
    ///     answered by a <c>[BenchmarkPlan]</c> factory, and Simple mode has no plan to point at.
    /// </param>
    public IsolationStatus ToStatus(IsolationStatus structural) => Reason switch
    {
        // Both mean the same thing to a user - a value this process holds could not be reproduced in
        // another - and so share a remedy. They differ only in how the message reads.
        RefusalReason.CapturedState or RefusalReason.LiveReceiver => IsolationStatus.InProcessCapturedState,
        _ => structural,
    };

    public override string ToString() => Message;
}
