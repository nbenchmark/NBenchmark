namespace NBenchmark.Workers;

/// <summary>
///     A factory the caller wants invoked in whichever process measures, together with the values it
///     takes - the coordinator-side input that becomes an <see cref="ArgumentSource.Recipe" />.
/// </summary>
/// <remarks>
///     <para>
///         The delegate and its arguments travel together because they are one claim.
///         <c>prepare: (int size) =&gt; Build(size)</c> paired with <c>1000</c> is a complete recipe
///         the worker can follow on its own, with the value named rather than captured.
///     </para>
///     <para>
///         Parameters are no longer the <i>only</i> way to get a value across.
///         <c>prepare: () =&gt; Build(size)</c> closes over <c>size</c> and isolates too - the capture
///         is transferred through the group's receiver table, the same route a body's captures take
///         (see <see cref="AddressedFactory" />). Requiring a non-capturing factory used to refuse
///         exactly the shape the library's own refusal messages tell people to write, which is the
///         refusal users reached <i>after</i> doing the rewrite the diagnostic asked for.
///     </para>
/// </remarks>
/// <param name="Factory">
///     The recipe. Addressed by the same rule as a body: it may capture, provided what it captures is
///     faithfully transferable.
/// </param>
/// <param name="Arguments">
///     Values for <paramref name="Factory" />'s own parameters, in declaration order. Empty for the
///     parameterless factories that are the common case.
/// </param>
internal sealed record StateRecipe(Delegate Factory, IReadOnlyList<object?> Arguments)
{
    public static StateRecipe For(Delegate factory) => new(factory, []);

    public static StateRecipe For(Delegate factory, params object?[] arguments) => new(factory, arguments);

    /// <summary>
    ///     The single-slot list a body taking one prepared value needs, or <c>null</c> when there is no
    ///     recipe at all.
    /// </summary>
    public static IReadOnlyList<StateRecipe?>? OneSlot(StateRecipe? recipe) => recipe is null ? null : [recipe];
}
