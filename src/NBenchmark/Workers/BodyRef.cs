using System.Reflection;
using System.Runtime.CompilerServices;

namespace NBenchmark.Workers;

/// <summary>
///     How a worker should obtain the benchmarks in a group.
/// </summary>
internal enum WorkGroupKind
{
    /// <summary>
    ///     Harness mode. The worker runs the normal attribute discovery pass over the target
    ///     assembly and keeps the named benchmarks. Discovery - not the coordinator - owns
    ///     <c>[GlobalSetup]</c>, <c>[Params]</c>, <c>[InstanceLifetime]</c> and the rest, so
    ///     nothing about that machinery has to cross the process boundary.
    /// </summary>
    DiscoveredClass = 0,

    /// <summary>
    ///     Simple mode and inline suites. Each benchmark is a delegate addressed by
    ///     <see cref="BodyRef" /> and re-created in the worker from the already-compiled method.
    /// </summary>
    Lambdas = 1,

    /// <summary>
    ///     Suite mode. The worker invokes a <c>[BenchmarkPlan]</c> factory, so the suite's
    ///     lambdas, observers, detectors and instance factories are live objects constructed in
    ///     the worker rather than anything serialized.
    /// </summary>
    Plan = 2,

    /// <summary>
    ///     A test-framework integration. The worker resolves a single method that carries no
    ///     <c>[Benchmark]</c> attribute, constructs its declaring type for itself, and measures it.
    ///     <para>
    ///         The test instance the framework built is never sent - only the address of the method
    ///         and any simple argument values. A test class the worker cannot build from nothing is
    ///         not routed here at all; the coordinator keeps it in the test host and labels the
    ///         result, rather than measuring a differently-constructed object under the same name.
    ///     </para>
    /// </summary>
    TestMethod = 3,
}

/// <summary>
///     The lowered shape of a benchmark delegate, which determines how the worker recovers the
///     receiver it must bind the method to.
/// </summary>
internal enum BodyShape
{
    /// <summary>
    ///     A static method - either a method group like <c>Foo.Bar</c>, or a lambda the compiler
    ///     chose to emit as static. Bound with a null receiver.
    /// </summary>
    StaticMethod = 0,

    /// <summary>
    ///     An instance method on a compiler-generated closure class that holds no state, with a
    ///     cached singleton instance in a static field. This is what Roslyn emits for
    ///     <b>every</b> non-capturing lambda. Bound to the cached singleton.
    /// </summary>
    CachedSingleton = 1,

    /// <summary>
    ///     An instance method whose receiver holds state, sent field by field alongside the address.
    ///     The worker builds an equivalent receiver and binds the compiled method to it.
    /// </summary>
    /// <remarks>
    ///     Covers both shapes that hold values: a Roslyn display class for a lambda that captured
    ///     locals, and a user object a lambda captured <c>this</c> from or a method group was taken
    ///     over. They are the same problem - a receiver with fields - so they take the same route, and
    ///     the fields are admitted only by <see cref="StateTransfer.IsFaithful" />.
    /// </remarks>
    TransferredReceiver = 2,
}

/// <summary>
///     A durable, cross-process address for an already-compiled benchmark body.
///     <para>
///         The body is never serialized, lifted or regenerated - the worker resolves the exact
///         method the compiler already emitted, so there is no possibility of semantic
///         divergence between what was measured and what was written.
///     </para>
/// </summary>
internal sealed record BodyRef
{
    public required string DisplayName { get; init; }

    /// <summary>
    ///     Simple name of the assembly that <b>defines</b> the body. Deliberately not the entry
    ///     assembly: a benchmark lambda can live anywhere in the dependency graph, and under a
    ///     test host the entry assembly is the test runner rather than the code under test.
    /// </summary>
    public required string AssemblySimpleName { get; init; }

    /// <summary>
    ///     Path to the defining assembly, used to root the worker's dependency resolver so the
    ///     body's own transitive references (including NuGet packages) resolve.
    /// </summary>
    public required string AssemblyPath { get; init; }

    /// <summary>
    ///     The defining module's MVID, checked before the token is trusted.
    ///     <para>
    ///         This gate is mandatory rather than defensive. Deterministic builds are the SDK
    ///         default, so a rebuild with no source change keeps the same MVID and the token stays
    ///         correct - but inserting a single lambda changes the MVID while leaving the old
    ///         numeric token <i>still valid</i>, now pointing at a different method. Without the
    ///         gate, a stale address measures the wrong body and reports it under the right name.
    ///     </para>
    /// </summary>
    public required Guid ModuleVersionId { get; init; }

    /// <summary>Metadata token of the method the delegate points at.</summary>
    public required int MethodToken { get; init; }

    public required BodyShape Shape { get; init; }

    /// <summary>
    ///     Assembly-qualified type arguments of the declaring type, when the body was declared
    ///     inside a generic method (Roslyn puts its closure class on a generic type) or on a closed
    ///     generic class. Needed to close the type before the method can be bound.
    /// </summary>
    public IReadOnlyList<string>? TypeGenericArguments { get; init; }

    /// <summary>
    ///     Assembly-qualified type arguments of the <b>method</b>, when the body is a closed generic
    ///     method. <c>null</c> for the non-generic methods that are the common case.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="TypeGenericArguments" /> because the two close different things and
    ///     one does not imply the other: <c>Box&lt;int&gt;.Compare&lt;string&gt;</c> needs both, and a
    ///     token names neither. Without this a generic body resolved to its open definition and could
    ///     not be invoked at all - which is why generic contexts were refused rather than measured.
    /// </remarks>
    public IReadOnlyList<string>? MethodGenericArguments { get; init; }

    /// <summary>Diagnostics only: where the body came from, for error messages.</summary>
    public string? DeclaringTypeFullName { get; init; }

    /// <summary>
    ///     Where each of the body's own parameters gets its value, in declaration order. Empty for the
    ///     parameterless bodies that are the common case; otherwise exactly as long as the body's
    ///     parameter list.
    ///     <para>
    ///         This is what lets a parameterized suite be isolated. The suite's typed lambda -
    ///         <c>(int size) =&gt; …</c> - captures nothing and was always addressable; what could not
    ///         cross was the wrapper NBenchmark built around it to bind the parameter value. Sending
    ///         the value alongside the address means the worker builds that wrapper instead, in the
    ///         process that measures it.
    ///     </para>
    ///     <para>
    ///         A slot carries <b>either</b> an encoded value or a recipe to run - see
    ///         <see cref="ArgumentSource" />. Per slot rather than per body, which is what lets a body
    ///         take two prepared values, or one prepared value beside one swept constant, without a
    ///         wire field per combination.
    ///     </para>
    ///     <para>
    ///         Encoded values are the same closed set the test-integration path uses, via
    ///         <see cref="TestArgumentCodec" />. Widening it to "whatever happens to round-trip" is
    ///         the mechanism that is right most of the time and silently wrong the rest; a value
    ///         outside it is carried as a recipe instead, which is exact.
    ///     </para>
    /// </summary>
    public IReadOnlyList<ArgumentSource> Arguments { get; init; } = [];

    /// <summary>
    ///     Address of this benchmark's per-iteration setup, when it has one that can be addressed.
    ///     <para>
    ///         A hook is a delegate like any other, so it is addressed by the same rule as a body:
    ///         resolved in the worker from the already-compiled method, or refused. Carrying it means
    ///         <c>setup: () =&gt; Cache.Clear()</c> no longer costs a suite its isolation - which it did,
    ///         because the refusal keyed on a hook <i>existing</i> rather than on whether it could
    ///         cross.
    ///     </para>
    /// </summary>
    public BodyRef? IterationSetup { get; init; }

    /// <inheritdoc cref="IterationSetup" />
    public BodyRef? IterationTeardown { get; init; }

    /// <summary>
    ///     Which of the group's receivers this body binds to, when its receiver holds state - see
    ///     <see cref="BodyShape.TransferredReceiver" />. <c>null</c> for every other shape.
    /// </summary>
    /// <remarks>
    ///     An index rather than the values themselves, because receivers are shared. Several bodies and
    ///     their lifecycle hooks routinely close over one Roslyn display class, and carrying a copy per
    ///     address had the worker rebuild several objects where this process has one - so two
    ///     benchmarks over one array stopped seeing each other's writes the moment a worker was
    ///     available. See <see cref="ReceiverTable" />.
    /// </remarks>
    public int? ReceiverIndex { get; init; }

    /// <summary>
    ///     How a prepared-state factory is named in a diagnostic, on both sides of the boundary.
    /// </summary>
    internal const string PrepareRole = "its prepare delegate";

    /// <summary>
    ///     Attempts to build an address for <paramref name="body" />.
    ///     <para>
    ///         Returns <c>false</c> with a reason when the body captures state. Captured state is
    ///         <b>refused, never reconstructed</b>: a probe that fabricated a fresh closure
    ///         instance and invoked anyway did not throw - it returned plausible, silently
    ///         <i>wrong</i> values (a body over a captured <c>5</c> returned <c>1</c>). A
    ///         mechanism that is right most of the time and quietly wrong the rest is worse than
    ///         one that declines.
    ///     </para>
    /// </summary>
    /// <param name="arguments">
    ///     Values for the body's own parameters, in declaration order. Must match the delegate's
    ///     arity: a mismatch is a refusal rather than a truncation, because binding the wrong number
    ///     of arguments would measure a different call than the caller described.
    /// </param>
    /// <param name="recipes">
    ///     Factories producing the body's arguments, aligned with its parameters - a <c>null</c> entry
    ///     means that parameter's value comes from <paramref name="arguments" /> instead. Each is
    ///     addressed by the same rule as the body, so a factory that captures is refused too: the
    ///     capture is exactly what splitting the shape was supposed to remove, and a factory may carry
    ///     its own argument values for the thing it would otherwise have captured.
    /// </param>
    public static bool TryCreate(
        Delegate body,
        string displayName,
        out BodyRef bodyRef,
        out Refusal refusal,
        IReadOnlyList<object?>? arguments = null,
        IReadOnlyList<StateRecipe?>? recipes = null,
        ReceiverTable? receivers = null)
        => TryCreateCore(body, displayName, out bodyRef, out refusal, arguments, recipes, receivers, false);

    /// <summary>
    ///     Addresses a per-iteration hook, whose own parameters - when it has any - are filled in the
    ///     measuring process from the values the <b>body's</b> slots resolved to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A hook exists to act on the body's state, so it cannot carry argument values of its own:
    ///         doing so would build a second value and reset that one, leaving the body's untouched. The
    ///         worker binds it to what the body was bound to, which is what makes
    ///         <c>setup: d =&gt; Shuffle(d)</c> shuffle the array the body then sorts.
    ///     </para>
    ///     <para>
    ///         So the argument slots stay empty here, deliberately. Routing a hook through
    ///         <see cref="TryCreate" /> refused it for taking parameters nothing had supplied - a rule
    ///         written about bodies, where it is correct, since a body with unfilled parameters is
    ///         unmeasurable.
    ///     </para>
    /// </remarks>
    public static bool TryCreateHook(
        Delegate hook,
        string displayName,
        out BodyRef bodyRef,
        out Refusal refusal,
        ReceiverTable? receivers = null)
        => TryCreateCore(hook, displayName, out bodyRef, out refusal, null, null, receivers, true);

    private static bool TryCreateCore(
        Delegate body,
        string displayName,
        out BodyRef bodyRef,
        out Refusal refusal,
        IReadOnlyList<object?>? arguments,
        IReadOnlyList<StateRecipe?>? recipes,
        ReceiverTable? receivers,
        bool parametersFromBody)
    {
        ArgumentNullException.ThrowIfNull(body);

        bodyRef = null!;
        refusal = Refusal.None;

        var method = body.Method;
        var module = method.Module;
        var assembly = module.Assembly;
        var location = assembly.Location;

        if (string.IsNullOrEmpty(location))
        {
            refusal = new Refusal(
                RefusalReason.NoAssemblyOnDisk,
                $"its defining assembly '{assembly.GetName().Name}' has no file on disk "
                + "(single-file, in-memory or dynamically emitted).");

            return false;
        }

        if (!TryResolveShape(body, receivers, out var shape, out var receiverIndex, out refusal))
            return false;

        IReadOnlyList<ArgumentSource> argumentSources = [];

        if (parametersFromBody)
        {
            // The ceiling still applies - the worker binds through the same ArgumentBinder - but nothing
            // is encoded, because the values come from the body at resolution time.
            if (method.GetParameters().Length > ArgumentBinder.MaxArity)
            {
                refusal = new Refusal(
                    RefusalReason.UnaddressableArguments,
                    $"it takes {method.GetParameters().Length} parameters; a benchmark body may take at "
                    + $"most {ArgumentBinder.MaxArity}, and a hook takes the body's own.");

                return false;
            }
        }
        else if (!TryBuildArgumentSources(method, displayName, arguments, recipes, out argumentSources, out refusal))
        {
            return false;
        }

        var declaringType = method.DeclaringType;

        // A generic argument that is still a type parameter means the delegate came from an open
        // generic context, which has no single answer to close it with in the worker. A *closed* one
        // is named and carried, so `Sort<int>` is measured rather than refused for being generic.
        if (!GenericArguments.TryNameTypeArguments(method, out var genericArguments, out var unnameable)
            || !GenericArguments.TryNameMethodArguments(method, out var methodGenericArguments, out unnameable))
        {
            refusal = new Refusal(
                RefusalReason.OpenGenericContext,
                "it was declared in a generic context whose type argument "
                + $"'{unnameable}' cannot be named across a process boundary.");

            return false;
        }

        bodyRef = new BodyRef
        {
            DisplayName = displayName,
            AssemblySimpleName = assembly.GetName().Name
                                 ?? throw new InvalidOperationException("Defining assembly has no simple name."),
            AssemblyPath = location,
            ModuleVersionId = module.ModuleVersionId,
            MethodToken = method.MetadataToken,
            Shape = shape,
            TypeGenericArguments = genericArguments,
            MethodGenericArguments = methodGenericArguments,
            DeclaringTypeFullName = declaringType?.FullName,
            Arguments = argumentSources,
            ReceiverIndex = receiverIndex,
        };

        return true;
    }

    /// <summary>
    ///     Resolves where each of the body's parameters gets its value, or explains the first slot that
    ///     cannot be answered.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One walk over the parameter list, deciding per slot between an encoded value and a
    ///         recipe. This used to be two mutually-exclusive branches - all values, or one prepared
    ///         value - and the exclusivity was a limit of the wire rather than anything about
    ///         benchmarks: a body taking two prepared values, or a prepared value beside a swept
    ///         constant, was refused for a shape that is perfectly ordinary to write.
    ///     </para>
    ///     <para>
    ///         Values are encoded against the <b>declared</b> parameter type rather than the runtime
    ///         type of the value, for the reason <see cref="TestArgumentCodec.Encode" /> documents: a
    ///         <c>long</c> parameter given the literal <c>1</c> arrives as a boxed <c>int</c>, and
    ///         sending <c>Int32</c> would bind the wrong shape on the far side.
    ///     </para>
    /// </remarks>
    private static bool TryBuildArgumentSources(
        MethodInfo method,
        string displayName,
        IReadOnlyList<object?>? arguments,
        IReadOnlyList<StateRecipe?>? recipes,
        out IReadOnlyList<ArgumentSource> sources,
        out Refusal refusal)
    {
        sources = [];
        refusal = Refusal.None;

        var parameters = method.GetParameters();
        var suppliedValues = arguments ?? [];
        var suppliedRecipes = recipes ?? [];

        if (parameters.Length == 0)
        {
            if (suppliedValues.Count == 0 && suppliedRecipes.Count == 0)
                return true;

            // Never truncate or pad. Binding a different number of arguments than the caller named
            // measures a different call and reports it under the right name, which is the exact
            // failure class this whole area exists to prevent.
            refusal = new Refusal(
                RefusalReason.UnaddressableArguments,
                $"it takes no parameters but {suppliedValues.Count + suppliedRecipes.Count} argument "
                + "value(s) were supplied for it.");

            return false;
        }

        // Enforced here as well as in the worker, because the two sides disagreeing is the failure
        // this area is otherwise careful to avoid: a four-parameter body passed planning, was sent,
        // and faulted on arrival, so the run lost a benchmark to a shape the coordinator could have
        // declined - and would have said so about, in a message naming the fix.
        if (parameters.Length > ArgumentBinder.MaxArity)
        {
            refusal = new Refusal(
                RefusalReason.UnaddressableArguments,
                $"it takes {parameters.Length} parameters; a benchmark body may take at most "
                + $"{ArgumentBinder.MaxArity}.");

            return false;
        }

        if (suppliedRecipes.Count > 0 && suppliedRecipes.Count != parameters.Length)
        {
            refusal = new Refusal(
                RefusalReason.PrepareDelegate,
                $"it takes {parameters.Length} parameter(s) but {suppliedRecipes.Count} prepare "
                + "delegate(s) were supplied for it; there must be one slot per parameter.");

            return false;
        }

        if (suppliedValues.Count > 0 && suppliedValues.Count != parameters.Length)
        {
            refusal = new Refusal(
                RefusalReason.UnaddressableArguments,
                $"it takes {parameters.Length} parameter(s) but {suppliedValues.Count} argument "
                + "value(s) were supplied for it.");

            return false;
        }

        var built = new ArgumentSource[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var recipe = i < suppliedRecipes.Count ? suppliedRecipes[i] : null;

            if (recipe is not null)
            {
                // Both would be two claims about one parameter. The recipe branch skips the encoding
                // branch, so without this the value would be silently dropped rather than refused, and
                // the body would measure what the factory built under a request that named a constant.
                if (i < suppliedValues.Count && suppliedValues[i] is not null)
                {
                    refusal = new Refusal(
                        RefusalReason.UnaddressableArguments,
                        $"its parameter '{parameters[i].Name}' was given both an argument value and a "
                        + "prepare delegate, which are two different answers for the same parameter.");

                    return false;
                }

                if (!TryAddressRecipe(parameters[i], recipe, displayName, out var addressed, out refusal))
                    return false;

                built[i] = ArgumentSource.FromRecipe(addressed!);

                continue;
            }

            if (suppliedValues.Count == 0)
            {
                refusal = new Refusal(
                    RefusalReason.PrepareDelegate,
                    $"its parameter '{parameters[i].Name}' has neither an argument value nor a prepare "
                    + "delegate, so there is nothing to call the body with.");

                return false;
            }

            var parameterType = parameters[i].ParameterType;

            if (!TestArgumentCodec.IsSupported(parameterType))
            {
                refusal = new Refusal(
                    RefusalReason.UnaddressableArguments,
                    $"its parameter '{parameters[i].Name}' has type '{parameterType.Name}', which "
                    + "cannot cross a process boundary as a value. Parameter values must be "
                    + "primitives, strings, enums, decimal, DateTime, DateTimeOffset, TimeSpan or "
                    + "Guid; anything else has to be built in the measuring process, which is what "
                    + "a WithParameter recipe or a static [BenchmarkPlan] factory is for.");

                return false;
            }

            built[i] = ArgumentSource.FromValue(TestArgumentCodec.Encode(parameterType, suppliedValues[i]));
        }

        sources = built;

        return true;
    }

    /// <summary>
    ///     Addresses one recipe and checks that what it produces is what the parameter accepts.
    /// </summary>
    /// <remarks>
    ///     The generic <c>Run&lt;TState&gt;(Func&lt;TState&gt;, Action&lt;TState&gt;)</c> signature already
    ///     makes the two agree at compile time, so the type check here is not for the caller's benefit -
    ///     it is for the worker's. Both delegates are re-resolved there from metadata tokens, and a check
    ///     that costs nothing at plan time is worth more than a cast failure inside a measurement.
    /// </remarks>
    private static bool TryAddressRecipe(
        ParameterInfo parameter,
        StateRecipe recipe,
        string displayName,
        out AddressedFactory? addressed,
        out Refusal refusal)
    {
        addressed = null;
        refusal = Refusal.None;

        var factoryParameters = recipe.Factory.Method.GetParameters().Length;

        if (factoryParameters != recipe.Arguments.Count)
        {
            // A prepare delegate may take parameters - that is how the value it would otherwise have
            // captured reaches it - but every one of them needs a value, because nothing else in the
            // worker knows what to pass.
            refusal = new Refusal(
                RefusalReason.PrepareDelegate,
                $"its prepare delegate takes {factoryParameters} parameter(s) but "
                + $"{recipe.Arguments.Count} value(s) were supplied for it.");

            return false;
        }

        var produced = recipe.Factory.Method.ReturnType;
        var accepted = parameter.ParameterType;

        if (!accepted.IsAssignableFrom(produced))
        {
            refusal = new Refusal(
                RefusalReason.PrepareDelegate,
                $"its prepare delegate returns '{produced.Name}' but the body accepts "
                + $"'{accepted.Name}'.");

            return false;
        }

        if (!AddressedFactory.TryCreate(
                recipe.Factory,
                PrepareRole,
                out var created,
                out var factoryRefusal,
                displayName: $"{displayName} (prepare)",
                arguments: recipe.Arguments))
        {
            // The inner reason is kept, not replaced with PrepareDelegate. A prepare delegate that
            // captures is a captured-state refusal and earns that remedy; only the shape mismatches
            // above are about the prepare delegate itself. Flattening the two lost the remedy for the
            // commonest case - a user who split the shape to remove a capture and captured in the
            // split - which is the reader most in need of it.
            refusal = factoryRefusal with { Message = $"{PrepareRole} {factoryRefusal.Message}" };

            return false;
        }

        addressed = created;

        return true;
    }

    /// <summary>
    ///     Classifies a delegate as addressable-static, addressable-non-capturing, or capturing.
    ///     <para>
    ///         The obvious test - <c>body.Target is null</c> - is <b>wrong</b>, and measurably so.
    ///         Roslyn lowers every non-capturing lambda to an <i>instance</i> method on a
    ///         <c>[CompilerGenerated]</c> closure class with a cached singleton, so
    ///         <c>Target</c> is non-null for all of them. That includes the explicit
    ///         <c>static () =&gt; 43</c> form, whose whole purpose is to promise no captures.
    ///         Using <c>Target is null</c> would refuse to isolate almost every lambda a user
    ///         writes.
    ///     </para>
    ///     <para>
    ///         The reliable signal is the receiver's <i>shape</i>: a compiler-generated type with
    ///         no instance fields cannot be carrying captured state, whatever it is called.
    ///     </para>
    /// </summary>
    private static bool TryResolveShape(
        Delegate body,
        ReceiverTable? receivers,
        out BodyShape shape,
        out int? receiverIndex,
        out Refusal refusal)
    {
        shape = BodyShape.StaticMethod;
        receiverIndex = null;
        refusal = Refusal.None;

        if (body.Method.IsStatic)
        {
            if (body.Target is null)
                return true;

            // A static method with a receiver is an open-instance delegate. Unlike every other shape
            // here the receiver is not the delegate's state but its first *argument*, and there is no
            // way to know what to pass - so there is nothing to transfer.
            refusal = new Refusal(
                RefusalReason.UnaddressableShape,
                "it is an open-instance delegate bound to a specific receiver.");

            return false;
        }

        var target = body.Target;

        if (target is null)
        {
            refusal = new Refusal(
                RefusalReason.UnaddressableShape,
                "it is an unbound instance-method delegate.");

            return false;
        }

        var targetType = target.GetType();
        var isClosure = StateTransfer.IsCompilerGeneratedScope(targetType);
        var instanceFields = StateTransfer.InstanceFieldsOf(targetType);

        // A stateless closure keeps the cheapest route: nothing to send, and Roslyn's cached
        // singleton is already the exact receiver the method was compiled against.
        if (isClosure && instanceFields.Length == 0)
        {
            if (FindSingletonField(targetType) is null)
            {
                refusal = new Refusal(
                    RefusalReason.UnaddressableShape,
                    $"its compiler-generated receiver '{targetType.Name}' has no cached "
                    + "singleton to bind to.");

                return false;
            }

            shape = BodyShape.CachedSingleton;

            return true;
        }

        // Everything else is a receiver holding state, and there is only one rule for those: send it
        // when every field can be sent faithfully, refuse and name the field when one cannot. A
        // Roslyn display class and a user object the body captured `this` from are the same problem,
        // and used to be two different refusals.
        //
        // Roslyn merges the captures of every *capturing* lambda in a lexical scope into one display
        // class, so a body can be refused for a sibling's field. That is unchanged and still correct:
        // the sibling's value is equally un-sendable, and a non-capturing sibling is hoisted to the
        // field-less `<>c` singleton rather than joining this class, so it is never affected.
        if (receivers is null)
        {
            // Not every delegate addressed through here is a benchmark body, and the two that are not
            // refuse rather than transfer - for different reasons, and with different futures.
            //
            // A *lifecycle hook* must observe the same state as the body it belongs to, and hooks are
            // addressed as independent BodyRefs, so transferring each one's captures would give it a
            // private copy: `setup: () => Array.Clear(buffer)` would clear a buffer the body never
            // reads. That is a correctness bar, and it lifts only when one receiver can be shared
            // across a group.
            //
            // A *factory* is the recipe rather than the ingredient, and "make it static" is a rule that
            // teaches the model. Transferring its captures would work - a factory closing over a string
            // is still instructions, and anything genuinely live is refused by the faithfulness rule
            // regardless - so this is a deliberate choice about what to teach, not a limit of the
            // mechanism. Worth revisiting alongside W-07, which gives the prepared-state case a better
            // answer by carrying arguments explicitly.
            //
            // The remedy differs between them, so the message stops at the fact and each caller adds
            // its own advice.
            refusal = new Refusal(
                RefusalReason.CapturedState,
                $"it captures state from its enclosing scope ({DescribeFields(instanceFields)}), which "
                + "cannot be reproduced in the process that measures.");

            return false;
        }

        var subject = isClosure ? "it captures" : $"it is bound to a live '{targetType.Name}', which holds";

        if (!receivers.TryIndex(target, subject, out var index, out refusal))
        {
            if (!isClosure)
                refusal = refusal with { Reason = RefusalReason.LiveReceiver };

            return false;
        }

        receiverIndex = index;
        shape = BodyShape.TransferredReceiver;

        return true;
    }


    private static string DescribeFields(System.Reflection.FieldInfo[] fields)
        => string.Join(", ", fields.Take(4).Select(f => f.Name)) + (fields.Length > 4 ? ", ..." : "");

    /// <summary>
    ///     Finds Roslyn's cached closure singleton - the <c>&lt;&gt;9</c> static field whose type
    ///     is the closure class itself. Located by shape rather than by name so a change to the
    ///     compiler's naming convention does not silently break addressing.
    /// </summary>
    internal static FieldInfo? FindSingletonField(Type closureType)
    {
        foreach (var field in closureType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType == closureType)
                return field;
        }

        return null;
    }
}
