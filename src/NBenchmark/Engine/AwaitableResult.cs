namespace NBenchmark.Engine;

/// <summary>
///     Whether a value-returning body's result type is an awaitable, and what to say when the
///     synchronous measurement path is handed one.
/// </summary>
/// <remarks>
///     <para>
///         <c>Run&lt;T&gt;(Func&lt;T&gt;)</c> measures the call and feeds its return value to the
///         JIT-elision sink. Given <c>T = ValueTask</c> or <c>T = Task</c> that is not a mistake the
///         engine can recover from - the call returns as soon as the body reaches its first
///         incomplete await, so the timing covers only the synchronous prefix and the rest of the
///         work is never observed at all. The number is plausible, tightly-intervalled, and about a
///         different amount of work than the benchmark describes.
///     </para>
///     <para>
///         Refused rather than adapted. Awaiting it here would silently change a synchronous
///         measurement into an asynchronous one, and the two are not interchangeable: the async path
///         has its own per-sample cost and its own dispatch shape, so the honest answer is to make
///         the caller name the path it wants.
///     </para>
///     <para>
///         Reachable, not defensive. <c>BenchmarkSuite&lt;TState&gt;.Add&lt;TResult&gt;</c> and the
///         parameterized <c>Add&lt;…, TResult&gt;</c> overloads infer <c>TResult</c> from the lambda,
///         so <c>Add("x", s =&gt; s.WorkAsync())</c> over a <c>ValueTask</c>-returning method binds
///         the synchronous overload with no diagnostic - and
///         <see cref="Workers.ArgumentBinder.TryDelegateTypeFor" /> reproduced the same binding in
///         the worker, so the isolated and in-process numbers agreed with each other on the wrong
///         answer.
///     </para>
/// </remarks>
internal static class AwaitableResult
{
    /// <summary>Whether <paramref name="resultType" /> is a <c>Task</c> or <c>ValueTask</c>.</summary>
    public static bool IsAwaitable(Type resultType)
    {
        ArgumentNullException.ThrowIfNull(resultType);

        if (resultType == typeof(Task) || resultType == typeof(ValueTask))
            return true;

        if (!resultType.IsGenericType)
            return false;

        var definition = resultType.GetGenericTypeDefinition();

        return definition == typeof(Task<>) || definition == typeof(ValueTask<>);
    }

    /// <summary>
    ///     The refusal message for a body whose result type the synchronous path cannot consume.
    /// </summary>
    /// <remarks>
    ///     Both remedies are named because which one applies depends on the shape the caller has: a
    ///     <c>Task</c> has an asynchronous entry point waiting for it, while a <c>ValueTask</c> has
    ///     to become a <c>Task</c> first - the engine measures <c>Task</c>-returning bodies, for the
    ///     reason <see cref="Discovery.BenchmarkBodyFactory" /> documents.
    /// </remarks>
    public static string Refusal(string name, Type resultType)
    {
        ArgumentNullException.ThrowIfNull(resultType);

        return $"'{name}' returns {resultType.Name}, which the synchronous measurement path cannot "
               + "measure: the call returns at the body's first incomplete await, so the timing would "
               + "cover only the part that ran before it. Use the asynchronous entry point "
               + "(RunAsync / Add), converting a ValueTask first with "
               + "`() => Method().AsTask()`.";
    }
}
