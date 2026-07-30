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
    ///     inside a generic method (Roslyn puts its closure class on a generic type). Needed to
    ///     close the type before the method can be bound.
    /// </summary>
    public IReadOnlyList<string>? TypeGenericArguments { get; init; }

    /// <summary>Diagnostics only: where the body came from, for error messages.</summary>
    public string? DeclaringTypeFullName { get; init; }

    /// <summary>
    ///     Values to supply to the body's own parameters, in declaration order. Empty for the
    ///     parameterless bodies that are the common case.
    ///     <para>
    ///         This is what lets a parameterized suite be isolated. The suite's typed lambda -
    ///         <c>(int size) =&gt; …</c> - captures nothing and was always addressable; what could not
    ///         cross was the wrapper NBenchmark built around it to bind the parameter value. Sending
    ///         the value alongside the address means the worker builds that wrapper instead, in the
    ///         process that measures it.
    ///     </para>
    ///     <para>
    ///         Deliberately the same closed value set the test-integration path uses, via
    ///         <see cref="TestArgumentCodec" />. Widening it to "whatever happens to round-trip" is
    ///         the mechanism that is right most of the time and silently wrong the rest.
    ///     </para>
    /// </summary>
    public IReadOnlyList<TestArgumentPayload> Arguments { get; init; } = [];

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
    ///     Address of a factory producing the value to pass as the body's single parameter, invoked once
    ///     in the worker before warmup.
    ///     <para>
    ///         This is what makes a benchmark over prepared data isolatable. The shape people actually
    ///         write - <c>var data = Build(); Run(() =&gt; Sort(data));</c> - captures, and a capture can
    ///         only ever be refused. Splitting it into two non-capturing delegates,
    ///         <c>Run(() =&gt; Build(), d =&gt; Sort(d))</c>, makes both addressable: the data is no longer
    ///         a value trapped in this process but a <i>recipe</i> the worker can follow itself.
    ///     </para>
    ///     <para>
    ///         Mutually exclusive with <see cref="Arguments" />. A parameter sweep supplies its value as
    ///         a serialized constant and a prepared state supplies it by construction, and a body takes
    ///         one parameter either way - so carrying both would leave two claims about the same slot.
    ///     </para>
    /// </summary>
    public BodyRef? StateFactory { get; init; }

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
    /// <param name="stateFactory">
    ///     A parameterless factory producing the body's single argument, to be invoked in the worker.
    ///     Addressed by the same rule as the body, so a factory that captures is refused too - the
    ///     capture is exactly what splitting the shape was supposed to remove.
    /// </param>
    public static bool TryCreate(
        Delegate body,
        string displayName,
        out BodyRef bodyRef,
        out string? refusal,
        IReadOnlyList<object?>? arguments = null,
        Delegate? stateFactory = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        bodyRef = null!;
        refusal = null;

        var method = body.Method;
        var module = method.Module;
        var assembly = module.Assembly;
        var location = assembly.Location;

        if (string.IsNullOrEmpty(location))
        {
            refusal = $"its defining assembly '{assembly.GetName().Name}' has no file on disk "
                      + "(single-file, in-memory or dynamically emitted).";
            return false;
        }

        if (!TryResolveShape(body, out var shape, out refusal))
            return false;

        BodyRef? stateRef = null;
        IReadOnlyList<TestArgumentPayload> encodedArguments = [];

        if (stateFactory is not null)
        {
            if (!TryAddressStateFactory(method, stateFactory, displayName, out stateRef, out refusal))
                return false;
        }
        else if (!TryEncodeArguments(method, arguments, out encodedArguments, out refusal))
        {
            return false;
        }

        var declaringType = method.DeclaringType;

        IReadOnlyList<string>? genericArguments = null;

        if (declaringType is { IsGenericType: true })
        {
            var typeArguments = declaringType.GetGenericArguments();
            var names = new List<string>(typeArguments.Length);

            foreach (var argument in typeArguments)
            {
                // A generic argument that is itself a type parameter means the delegate came from
                // an open generic context we cannot close in the worker.
                if (argument.IsGenericParameter || argument.AssemblyQualifiedName is null)
                {
                    refusal = $"it was declared in a generic context whose type argument "
                              + $"'{argument.Name}' cannot be named across a process boundary.";
                    return false;
                }

                names.Add(argument.AssemblyQualifiedName);
            }

            genericArguments = names;
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
            DeclaringTypeFullName = declaringType?.FullName,
            Arguments = encodedArguments,
            StateFactory = stateRef,
        };

        return true;
    }

    /// <summary>
    ///     Addresses the state factory and checks that what it produces is what the body accepts.
    /// </summary>
    /// <remarks>
    ///     The generic <c>Run&lt;TState&gt;(Func&lt;TState&gt;, Action&lt;TState&gt;)</c> signature already
    ///     makes the two agree at compile time, so the type check here is not for the caller's benefit -
    ///     it is for the worker's. Both delegates are re-resolved there from metadata tokens, and a check
    ///     that costs nothing at plan time is worth more than a cast failure inside a measurement.
    /// </remarks>
    private static bool TryAddressStateFactory(
        MethodInfo bodyMethod,
        Delegate stateFactory,
        string displayName,
        out BodyRef? stateRef,
        out string? refusal)
    {
        stateRef = null;
        refusal = null;

        var parameters = bodyMethod.GetParameters();

        if (parameters.Length != 1)
        {
            refusal = $"it takes {parameters.Length} parameter(s); a body measured over prepared state "
                      + "must take exactly one, being the prepared value.";

            return false;
        }

        if (stateFactory.Method.GetParameters().Length != 0)
        {
            refusal = "its prepare delegate takes parameters; it must be parameterless, because nothing "
                      + "exists yet to pass it.";

            return false;
        }

        var produced = stateFactory.Method.ReturnType;
        var accepted = parameters[0].ParameterType;

        if (!accepted.IsAssignableFrom(produced))
        {
            refusal = $"its prepare delegate returns '{produced.Name}' but the body accepts "
                      + $"'{accepted.Name}'.";

            return false;
        }

        if (!TryCreate(stateFactory, $"{displayName} (prepare)", out var created, out var factoryRefusal))
        {
            refusal = $"its prepare delegate {factoryRefusal}";

            return false;
        }

        stateRef = created;

        return true;
    }

    /// <summary>
    ///     Encodes the body's argument values against its <b>declared</b> parameter types, or explains
    ///     why they cannot cross.
    /// </summary>
    /// <remarks>
    ///     Encoding against the declared type rather than the runtime type of the value is load-bearing,
    ///     for the reason <see cref="TestArgumentCodec.Encode" /> documents: a <c>long</c> parameter
    ///     given the literal <c>1</c> arrives as a boxed <c>int</c>, and sending <c>Int32</c> would bind
    ///     the wrong shape on the far side.
    /// </remarks>
    private static bool TryEncodeArguments(
        MethodInfo method,
        IReadOnlyList<object?>? arguments,
        out IReadOnlyList<TestArgumentPayload> encoded,
        out string? refusal)
    {
        encoded = [];
        refusal = null;

        var parameters = method.GetParameters();
        var supplied = arguments ?? [];

        if (parameters.Length != supplied.Count)
        {
            // Never truncate or pad. Binding a different number of arguments than the caller named
            // measures a different call and reports it under the right name, which is the exact
            // failure class this whole area exists to prevent.
            refusal = $"it takes {parameters.Length} parameter(s) but {supplied.Count} argument "
                      + "value(s) were supplied for it.";

            return false;
        }

        if (parameters.Length == 0)
            return true;

        var payloads = new TestArgumentPayload[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;

            if (!TestArgumentCodec.IsSupported(parameterType))
            {
                refusal = $"its parameter '{parameters[i].Name}' has type '{parameterType.Name}', which "
                          + "cannot cross a process boundary as a value. Parameter values must be "
                          + "primitives, strings, enums, decimal, DateTime, DateTimeOffset, TimeSpan or "
                          + "Guid; anything else has to be built in the measuring process, which is what "
                          + "a static [BenchmarkPlan] factory is for.";

                return false;
            }

            payloads[i] = TestArgumentCodec.Encode(parameterType, supplied[i]);
        }

        encoded = payloads;

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
    private static bool TryResolveShape(Delegate body, out BodyShape shape, out string? refusal)
    {
        shape = BodyShape.StaticMethod;
        refusal = null;

        if (body.Method.IsStatic)
        {
            if (body.Target is null)
                return true;

            // A static method with a receiver is an open-instance delegate; there is no way to
            // recover the receiver, so this is a capture in everything but name.
            refusal = "it is an open-instance delegate bound to a specific receiver.";
            return false;
        }

        var target = body.Target;

        if (target is null)
        {
            refusal = "it is an unbound instance-method delegate.";
            return false;
        }

        var targetType = target.GetType();

        if (!targetType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            // A method group over a live object, e.g. `Func<int> f = widget.Compute`. The
            // receiver is user state that the worker has no way to reproduce.
            refusal = $"it is bound to an instance of '{targetType.Name}', which is live state in "
                      + "this process rather than a compiler-generated closure.";
            return false;
        }

        var instanceFields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (instanceFields.Length > 0)
        {
            // A display class with fields. Roslyn merges the captures of every *capturing* lambda
            // in a lexical scope into one display class, so the named fields may include captures
            // belonging to a sibling rather than to this body - the message can therefore be
            // broader than the lambda it describes. The decision is unaffected: a non-capturing
            // sibling is hoisted to the field-less `<>c` singleton instead of joining this class,
            // so it is never refused for a neighbour's capture. Both pinned in BodyRefCaptureTests.
            var captured = string.Join(", ", instanceFields.Take(4).Select(f => f.Name));

            refusal = $"it captures state from its enclosing scope ({captured}"
                      + (instanceFields.Length > 4 ? ", ..." : "")
                      + "). Captured values cannot be reproduced in another process, and "
                      + "reconstructing them yields silently wrong measurements rather than errors.";

            return false;
        }

        if (FindSingletonField(targetType) is null)
        {
            refusal = $"its compiler-generated receiver '{targetType.Name}' has no cached "
                      + "singleton to bind to.";
            return false;
        }

        shape = BodyShape.CachedSingleton;
        return true;
    }

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
