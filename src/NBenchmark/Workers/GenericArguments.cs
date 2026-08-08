using System.Reflection;

namespace NBenchmark.Workers;

/// <summary>
///     Names the generic arguments a body was closed over, so the measuring process can close the
///     same open definition the same way.
/// </summary>
/// <remarks>
///     <para>
///         A metadata token always names the <b>open</b> definition. <c>Sort&lt;int&gt;</c> and
///         <c>Sort&lt;string&gt;</c> share one token, and so does the method on a closed generic type
///         with the method on its definition - so a token alone is not an address for anything
///         generic, and resolving one gives back something that cannot be invoked. The type arguments
///         are the rest of the address.
///     </para>
///     <para>
///         Two lists, because there are two independent places a generic argument can come from: the
///         declaring type (a lambda inside a generic method gets its closure class put on a generic
///         type, and a benchmark can live on a closed generic class) and the method itself. Closing
///         one does not close the other, and a method generic over a type that is itself generic needs
///         both.
///     </para>
///     <para>
///         An argument that is still a type <i>parameter</i> is refused rather than named. An open
///         generic context has no single answer to close it with, so there is nothing to send and
///         nothing a worker could invoke.
///     </para>
/// </remarks>
internal static class GenericArguments
{
    /// <summary>
    ///     Assembly-qualified names for <paramref name="types" />, or <c>false</c> naming the first one
    ///     that cannot cross a process boundary. An empty input succeeds with <c>null</c>.
    /// </summary>
    public static bool TryName(Type[] types, out IReadOnlyList<string>? names, out string? unnameable)
    {
        ArgumentNullException.ThrowIfNull(types);

        names = null;
        unnameable = null;

        if (types.Length == 0)
            return true;

        var built = new string[types.Length];

        for (var i = 0; i < types.Length; i++)
        {
            if (types[i].IsGenericParameter || types[i].AssemblyQualifiedName is not { } qualified)
            {
                unnameable = types[i].Name;

                return false;
            }

            built[i] = qualified;
        }

        names = built;

        return true;
    }

    /// <inheritdoc cref="TryName(Type[], out IReadOnlyList{string}?, out string?)" />
    /// <remarks>
    ///     The method's own generic arguments, or <c>null</c> when it has none. A closed generic method
    ///     reports its arguments here; an open definition reports its parameters, which
    ///     <see cref="TryName(Type[], out IReadOnlyList{string}?, out string?)" /> then refuses.
    /// </remarks>
    public static bool TryNameMethodArguments(
        MethodInfo method,
        out IReadOnlyList<string>? names,
        out string? unnameable)
    {
        ArgumentNullException.ThrowIfNull(method);

        return method.IsGenericMethod
            ? TryName(method.GetGenericArguments(), out names, out unnameable)
            : Nothing(out names, out unnameable);
    }

    /// <inheritdoc cref="TryName(Type[], out IReadOnlyList{string}?, out string?)" />
    /// <remarks>The declaring type's generic arguments, or <c>null</c> when it is not generic.</remarks>
    public static bool TryNameTypeArguments(
        MethodInfo method,
        out IReadOnlyList<string>? names,
        out string? unnameable)
    {
        ArgumentNullException.ThrowIfNull(method);

        return method.DeclaringType is { IsGenericType: true } declaringType
            ? TryName(declaringType.GetGenericArguments(), out names, out unnameable)
            : Nothing(out names, out unnameable);
    }

    /// <summary>
    ///     Resolves carried names back to types in the measuring process, using
    ///     <paramref name="resolve" /> so the caller decides which load context answers.
    /// </summary>
    /// <remarks>
    ///     The resolver is the caller's because it matters which one answers: a user's own type
    ///     argument lives in the target's dependency graph, not in the worker's default context, and a
    ///     lookup that searches the wrong one fails for a type that is certainly present.
    /// </remarks>
    public static bool TryResolve(
        IReadOnlyList<string> names,
        Func<string, Type?> resolve,
        out Type[] types,
        out string? unresolved)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(resolve);

        types = [];
        unresolved = null;

        var built = new Type[names.Count];

        for (var i = 0; i < names.Count; i++)
        {
            if (resolve(names[i]) is not { } resolved)
            {
                unresolved = names[i];

                return false;
            }

            built[i] = resolved;
        }

        types = built;

        return true;
    }

    private static bool Nothing(out IReadOnlyList<string>? names, out string? unnameable)
    {
        names = null;
        unnameable = null;

        return true;
    }
}
