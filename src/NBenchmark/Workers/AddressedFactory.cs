namespace NBenchmark.Workers;

/// <summary>
///     The address of a static, non-capturing method the worker runs to obtain an object the
///     coordinator could not send it.
/// </summary>
/// <remarks>
///     <para>
///         This is the one mechanism behind every "recipe" in the protocol: the prepared-state
///         factory, the service-provider factory, the two statistical-strategy factories, and the
///         <c>[BenchmarkPlan]</c> suite factory. All five are the same idea - <i>the value cannot
///         cross, so send the instructions for building it and let the measuring process follow
///         them</i> - and all five were previously implemented separately, each with its own
///         addressing helper, its own resolve-and-invoke block in the worker, and its own wording for
///         the four ways it can fail. Five copies of one rule is five chances for them to disagree,
///         and this area has already paid for that shape once.
///     </para>
///     <para>
///         Two addressing modes, because there are two genuinely different questions:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>By token</b> (<see cref="Body" />) - the default, and the stronger guarantee.
///                 The metadata token plus the module version id names <i>precisely</i> the method the
///                 caller passed, in the build they passed it from.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>By name</b> (<see cref="DeclaringTypeFullName" /> + <see cref="MethodName" />) -
///                 for a target that is a <i>different build</i> of the same source, which is what a
///                 multi-runtime run measures. A token is only meaningful within the build that
///                 produced it, and the module version id that guards a stale token differs between
///                 two target frameworks' builds by construction, so token addressing cannot be made
///                 safe across them. A fully-qualified name is stable.
///             </description>
///         </item>
///     </list>
///     <para>
///         What this type deliberately does <b>not</b> carry is the type the factory must produce.
///         That is checked on the worker's side against the caller's own expectation, because the
///         resolved method's signature is a fact about the assembly on disk while a carried type name
///         would only be a claim about the far side - the same rule argument decoding already follows,
///         where parameter types are read from the resolved method rather than from the payload.
///     </para>
/// </remarks>
internal sealed record AddressedFactory
{
    /// <summary>
    ///     What this factory produces, in the caller's words - "the service provider factory", "the
    ///     benchmark plan 'BuildSuite'".
    /// </summary>
    /// <remarks>
    ///     Carried rather than derived so that both sides of the boundary name the same thing the same
    ///     way. Every diagnostic this type's resolver produces is phrased around it, which is what
    ///     stops a worker-side failure from describing the user's code in vocabulary the coordinator
    ///     never used.
    /// </remarks>
    public required string Role { get; init; }

    /// <summary>
    ///     Token-based address. Non-null exactly when <see cref="MethodName" /> is null.
    /// </summary>
    public BodyRef? Body { get; init; }

    /// <summary>Name-based address: the declaring type. Set with <see cref="MethodName" />.</summary>
    public string? DeclaringTypeFullName { get; init; }

    /// <summary>Name-based address: the method's name. Set with <see cref="DeclaringTypeFullName" />.</summary>
    public string? MethodName { get; init; }

    /// <summary>Whether this address is resolved by name rather than by metadata token.</summary>
    public bool IsByName => MethodName is { Length: > 0 };

    /// <summary>
    ///     Whether this address names exactly one method by exactly one mode, or says what is wrong
    ///     with it.
    /// </summary>
    /// <remarks>
    ///     Checked on receipt rather than left to hold by construction. Both factory methods here set
    ///     one mode and only one, so the invariant is true of everything this type builds - but that is
    ///     an argument about the coordinator, and the worker is reading a frame off a pipe. An address
    ///     carrying two claims about which method to run must be refused rather than resolved by
    ///     whichever branch happens to be tested first, which is the failure shape this whole area is
    ///     built to avoid.
    /// </remarks>
    public bool IsWellFormed(out string? problem)
    {
        problem = null;

        if (IsByName)
        {
            if (Body is not null)
            {
                problem = $"{Role} carries both a metadata token and a method name, which are two "
                          + "different claims about which method to run.";

                return false;
            }

            if (DeclaringTypeFullName is not { Length: > 0 })
            {
                problem = $"{Role} names the method '{MethodName}' but not the type declaring it.";

                return false;
            }

            return true;
        }

        if (Body is null)
        {
            problem = $"{Role} carries neither a metadata token nor a method name.";

            return false;
        }

        if (DeclaringTypeFullName is { Length: > 0 })
        {
            problem = $"{Role} carries a metadata token and a declaring type name but no method name, "
                      + "so it names two methods incompletely rather than one completely.";

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Addresses <paramref name="factory" /> by metadata token, or explains why it cannot be
    ///     addressed.
    /// </summary>
    /// <remarks>
    ///     The refusal is <see cref="BodyRef.TryCreate" />'s verbatim, prefixed with the role, because
    ///     the reasons are identical: a factory that captures is refused for exactly the reason a
    ///     capturing body is - it would have to run in the coordinator, and what it builds there is
    ///     the live object that cannot cross.
    /// </remarks>
    /// <param name="displayName">
    ///     The name carried on the underlying <see cref="BodyRef" />, when it should differ from the
    ///     role - a prepared-state factory is "its prepare delegate" to the reader but is addressed
    ///     under the benchmark's own name.
    /// </param>
    /// <param name="arguments">
    ///     Values for the factory's own parameters, in declaration order. They ride on the underlying
    ///     <see cref="BodyRef" />, which already knows how to encode a body's arguments - a recipe is
    ///     just a body whose result is a parameter rather than a measurement.
    /// </param>
    public static bool TryCreate(
        Delegate factory,
        string role,
        out AddressedFactory addressed,
        out Refusal refusal,
        string? displayName = null,
        IReadOnlyList<object?>? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        addressed = null!;

        // Captures are refused here rather than transferred, which is the one place this rule differs
        // from a benchmark body's. A factory is the *recipe*: it exists to run in the measuring
        // process and build something there. One that needs a value from this process has a different
        // remedy - make it static, so the value is part of the recipe - and sending its captures
        // would make the recipe depend on the process it was supposed to be independent of.
        // No receiver table, which is how "this may not transfer captures" is said: without one there
        // is nowhere to put them, so a capturing factory is refused.
        if (!BodyRef.TryCreate(
                factory, displayName ?? role, out var body, out refusal, arguments, receivers: null))
        {
            return false;
        }

        addressed = new AddressedFactory { Role = role, Body = body };

        return true;
    }

    /// <summary>
    ///     Addresses <paramref name="factory" /> by fully-qualified name, for a target that is a
    ///     different build of the same source.
    /// </summary>
    /// <remarks>
    ///     A name-addressed factory must be static and declared on a named type, because that is all
    ///     the worker will have to find it with. The parameterless and return-type requirements are
    ///     checked in the worker instead, against the build that will actually run it - under another
    ///     target framework the method genuinely might have a different shape.
    /// </remarks>
    public static bool TryCreateByName(
        Delegate factory,
        string role,
        out AddressedFactory addressed,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        addressed = null!;
        refusal = null;

        var method = factory.Method;

        if (!method.IsStatic || method.DeclaringType?.FullName is not { } declaringType)
        {
            refusal = $"'{method.Name}' must be a static method on a named type to be located in "
                      + "another build, because that build has no metadata token in common with this one.";

            return false;
        }

        addressed = new AddressedFactory
        {
            Role = role,
            DeclaringTypeFullName = declaringType,
            MethodName = method.Name,
        };

        return true;
    }

    /// <summary>
    ///     Addresses <paramref name="factory" /> by token, or returns <c>null</c> when there is none
    ///     to address.
    /// </summary>
    /// <remarks>
    ///     For request building, which happens <i>after</i> the decision to isolate has already been
    ///     taken - so an un-addressable factory cannot be present by then and there is no refusal left
    ///     to report. Every caller pairs this with an earlier <see cref="TryCreate" /> through
    ///     <see cref="WorkerRunPlan" />.
    /// </remarks>
    public static AddressedFactory? OrNull(Delegate? factory, string role)
        => factory is not null && TryCreate(factory, role, out var addressed, out _) ? addressed : null;
}
