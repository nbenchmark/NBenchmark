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

        switch (body)
        {
            case Action action:
                return BenchmarkRunner.Instance.Run(name, action, spec, cancellationToken);

            case Func<Task> asyncVoid:
                return await BenchmarkRunner.Instance
                    .RunAsync(name, asyncVoid, spec, cancellationToken)
                    .ConfigureAwait(false);
        }

        var returnType = body.Method.ReturnType;
        var isAsync = returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>);

        var closed = (isAsync ? RunAsyncGeneric : RunGeneric)
            .MakeGenericMethod(isAsync ? returnType.GetGenericArguments()[0] : returnType);

        var invoked = closed.Invoke(BenchmarkRunner.Instance, [name, body, spec, cancellationToken]);

        return isAsync
            ? await ((Task<MeasurementOutcome>)invoked!).ConfigureAwait(false)
            : (MeasurementOutcome)invoked!;
    }
}
