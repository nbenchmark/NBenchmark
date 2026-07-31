using System.Reflection;
using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Invokes the engine on a delegate whose exact shape is only known at run time.
///     <para>
///         Shared by the coordinator and the worker so a body measured in one is dispatched
///         identically in the other. If the two used different call shapes, an isolated number and an
///         in-process number would differ for a reason unrelated to the process boundary, which is
///         precisely the comparison the design exists to make meaningful.
///     </para>
///     <para>
///         The generic overloads are reached by reflection <b>once per benchmark</b>, outside the
///         measured region. What runs inside the loop is a real <c>Func&lt;T&gt;</c> - a monomorphic
///         call the JIT can inline - rather than a <c>Func&lt;object&gt;</c> adapter, which would box
///         the return value of a value-typed body and charge the user both the time and a
///         per-operation allocation they never wrote.
///     </para>
/// </summary>
internal static class DelegateDispatch
{
    private static readonly MethodInfo RunGeneric = typeof(BenchmarkRunner)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m is { Name: nameof(BenchmarkRunner.Run), IsGenericMethodDefinition: true });

    private static readonly MethodInfo RunAsyncGeneric = typeof(BenchmarkRunner)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(m => m is { Name: nameof(BenchmarkRunner.RunAsync), IsGenericMethodDefinition: true });

    public static async Task<MeasurementOutcome> MeasureAsync(
        string name,
        Delegate body,
        RunSpec spec,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body is Action action)
            return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);

        // The delegate's own runtime type, not body.Method.ReturnType. A method group conversion
        // may build a Func<Task> over a method returning Task<int> (return-type covariance), and
        // casting that delegate to Func<Task<int>> would fail. The runtime type is what any cast
        // below has to satisfy, so it is what the decision is made on.
        var resultType = ResultTypeOf(body);

        if (resultType is null)
        {
            // A delegate type the engine has no typed entry point for. Func<Task> also lands here
            // when it arrived as a custom delegate type rather than a Func<>.
            return body is Func<Task> untyped
                ? await BenchmarkRunner.Instance
                    .RunAsync(name, untyped, spec, cancellationToken)
                    .ConfigureAwait(false)
                : throw new InvalidOperationException(
                    $"Benchmark body '{name}' has delegate type {body.GetType().Name}, which the "
                    + "engine cannot measure. Wrap it as an Action, a Func<T>, or a Func<Task<T>>.");
        }

        if (resultType == typeof(Task))
        {
            return await BenchmarkRunner.Instance
                .RunAsync(name, (Func<Task>)body, spec, cancellationToken)
                .ConfigureAwait(false);
        }

        // Checked before the Func<Task> cast above would have matched it: Func<T> is covariant in
        // T, so a Func<Task<int>> *is* a Func<Task>. Taking that branch would await the body and
        // silently drop its result - the value would never reach the JIT-elision sink, which is the
        // one thing standing between an async benchmark and a measured-nothing reading.
        var isAsync = resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Task<>);

        var closed = (isAsync ? RunAsyncGeneric : RunGeneric)
            .MakeGenericMethod(isAsync ? resultType.GetGenericArguments()[0] : resultType);

        var invoked = closed.Invoke(BenchmarkRunner.Instance, [name, body, spec, cancellationToken]);

        return isAsync
            ? await ((Task<MeasurementOutcome>)invoked!).ConfigureAwait(false)
            : (MeasurementOutcome)invoked!;
    }

    /// <summary>
    ///     The <c>T</c> of a <c>Func&lt;T&gt;</c>, or <c>null</c> for any other delegate type.
    /// </summary>
    private static Type? ResultTypeOf(Delegate body)
    {
        var type = body.GetType();

        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Func<>)
            ? type.GetGenericArguments()[0]
            : null;
    }
}
