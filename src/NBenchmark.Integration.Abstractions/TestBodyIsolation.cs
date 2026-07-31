using System.Reflection;

namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     Decides whether a test method can be measured in a separate worker process, or must be
///     measured in the test host and labelled as such.
/// </summary>
/// <remarks>
///     <para>
///         A worker builds the test class itself, from its own copy of the test assembly. That works
///         when the class can be constructed from nothing - the common case for a performance test -
///         and cannot work when the instance the test framework handed over carries live state:
///         an xUnit <c>IClassFixture</c>, an <c>ITestOutputHelper</c>, a mock, or a
///         <c>[MemberData]</c> object graph. Those exist only in the test host.
///     </para>
///     <para>
///         This matters more for a test integration than anywhere else, because its whole purpose is
///         to <b>gate</b>. A gate reading a number produced under the host's uncontrolled JIT state
///         is not conservative - on bodies of provably identical cost, in-process measurement
///         fabricated a 2.80x ratio with a tight confidence interval on each side. The reason a
///         benchmark could not be isolated therefore has to travel with its result rather than being
///         silently absorbed.
///     </para>
/// </remarks>
public static class TestBodyIsolation
{
    /// <summary>Whether a test method can be measured in a worker, and why not when it cannot.</summary>
    /// <param name="Status">
    ///     The <c>NBenchmark.IsolationStatus</c> value to stamp on the result, as a string so this
    ///     assembly does not have to take a dependency on the enum's declaring assembly purely to
    ///     name a case.
    /// </param>
    public readonly record struct Decision(bool CanIsolate, string Status, string? Reason)
    {
        internal static Decision Refuse(string status, string reason) => new(false, status, reason);

        internal static Decision Allow() => new(true, "Isolated", null);
    }

    internal const string LiveFixture = "InProcessLiveFixture";
    internal const string Unaddressable = "InProcessUnaddressablePlan";

    /// <summary>
    ///     Classifies <paramref name="method" /> against the instance and arguments the test
    ///     framework resolved for it.
    /// </summary>
    /// <param name="instance">
    ///     The live test-class instance, or <c>null</c> for a static method. Its <i>identity</i> is
    ///     never sent anywhere - only whether an equivalent one could be rebuilt elsewhere.
    /// </param>
    public static Decision Classify(MethodInfo method, object? instance, IReadOnlyList<object?> args)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);

        if (method.DeclaringType is not { } declaringType)
            return Decision.Refuse(Unaddressable, "the method has no declaring type to locate it by.");

        if (declaringType.IsGenericTypeDefinition || method.IsGenericMethodDefinition)
        {
            return Decision.Refuse(
                Unaddressable,
                "generic test methods and classes are not addressed across the process boundary yet.");
        }

        if (declaringType.Assembly.Location is not { Length: > 0 })
        {
            return Decision.Refuse(
                Unaddressable,
                $"'{declaringType.Assembly.GetName().Name}' has no file on disk, so a worker cannot load it. "
                + "This is usually a single-file or in-memory build.");
        }

        var parameters = method.GetParameters();

        for (var i = 0; i < args.Count; i++)
        {
            if (IsReconstructable(args[i]))
                continue;

            // Named by parameter rather than by type: a method taking two arguments of the same
            // type gives the author nothing to act on if the message only reports the type.
            var described = i < parameters.Length && parameters[i].Name is { Length: > 0 } parameterName
                ? $"parameter '{parameterName}' (of type '{args[i]!.GetType().Name}')"
                : $"an argument of type '{args[i]!.GetType().Name}'";

            return Decision.Refuse(
                LiveFixture,
                $"{described} is a live object that exists only in this test process. Simple values "
                + "([InlineData] and the like) travel; object graphs and mocks do not.");
        }

        if (!method.IsStatic)
        {
            if (instance is null)
                return Decision.Refuse(Unaddressable, "the instance method has no target instance.");

            // The worker constructs its own instance, so anything the framework injected through the
            // constructor - a class fixture, an output helper - would be absent there. Measuring
            // against a differently-constructed object while reporting it as the same test is worse
            // than measuring in this process and saying so.
            if (declaringType.GetConstructor(Type.EmptyTypes) is null)
            {
                var injected = declaringType
                    .GetConstructors()
                    .SelectMany(c => c.GetParameters())
                    .Select(pa => pa.ParameterType.Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return Decision.Refuse(
                    LiveFixture,
                    $"'{declaringType.Name}' has no parameterless constructor, so a worker cannot build "
                    + $"it. Its constructor takes {string.Join(", ", injected)}, which the test "
                    + "framework supplies and a worker has no way to reproduce.");
            }
        }

        return Decision.Allow();
    }

    /// <summary>
    ///     Whether an argument is a value a worker could be handed rather than a live object.
    /// </summary>
    /// <remarks>
    ///     Deliberately conservative: only primitives, strings, enums and the small value types with
    ///     an unambiguous representation. Anything else is refused, including types that <i>happen</i>
    ///     to be serializable - a mechanism that works most of the time and silently substitutes a
    ///     different object the rest is worse than one that declines.
    /// </remarks>
    private static bool IsReconstructable(object? argument)
    {
        if (argument is null)
            return true;

        var type = argument.GetType();

        if (type.IsEnum || type.IsPrimitive)
            return true;

        return argument is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid;
    }
}
