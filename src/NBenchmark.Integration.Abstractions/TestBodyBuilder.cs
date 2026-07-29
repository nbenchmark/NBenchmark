using System.Linq.Expressions;
using System.Reflection;

namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     Compiles a test method into a benchmark body.
/// </summary>
/// <remarks>
///     <para>
///         Shared by the xUnit, NUnit and MSTest integrations, which previously carried three
///         near-identical copies of this. They had already begun to drift - the copies differed only
///         in which type they reflected on for a helper, but nothing prevented a fix to one from
///         missing the others, and a divergence here changes what gets measured rather than failing
///         loudly.
///     </para>
///     <para>
///         Expression trees rather than <see cref="MethodBase.Invoke(object, object[])" />: reflection
///         invocation costs hundreds of nanoseconds and allocates an argument array per call, which
///         on a body of a few nanoseconds is the entire measurement. A compiled delegate calls the
///         method directly.
///     </para>
/// </remarks>
public static class TestBodyBuilder
{
    /// <summary>
    ///     Builds a delegate that invokes <paramref name="method" />, or returns <c>false</c> when
    ///     its shape is not measurable.
    /// </summary>
    /// <param name="isAsync">
    ///     Whether <paramref name="body" /> is a <see cref="Func{Task}" /> rather than an
    ///     <see cref="Action" />. The caller needs this to pick the right measurement overload -
    ///     awaiting a synchronous body would add the state machine's cost to the reading.
    /// </param>
    public static bool TryBuild(
        MethodInfo method,
        object? instance,
        object?[] args,
        out Delegate body,
        out bool isAsync)
    {
        ArgumentNullException.ThrowIfNull(method);

        body = null!;
        isAsync = false;

        var returnType = method.ReturnType;

        if (IsAwaitable(returnType))
        {
            body = BuildAsyncBody(method, instance, args);
            isAsync = true;

            return true;
        }

        body = returnType == typeof(void)
            ? BuildSyncBody(method, instance, args)

            // A returned value is stored rather than discarded, so the JIT cannot delete the call
            // it came from and leave the benchmark measuring an empty loop.
            : BuildReturningSyncBody(method, instance, args);

        return true;
    }

    /// <summary>Whether the return type is one the async path knows how to await.</summary>
    public static bool IsAwaitable(Type returnType)
    {
        ArgumentNullException.ThrowIfNull(returnType);

        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
            return true;

        if (!returnType.IsGenericType)
            return false;

        var definition = returnType.GetGenericTypeDefinition();

        return definition == typeof(Task<>) || definition == typeof(ValueTask<>);
    }

    private static Action BuildSyncBody(MethodInfo method, object? instance, object?[] args)
        => Expression.Lambda<Action>(BuildCall(method, instance, args)).Compile();

    private static Action BuildReturningSyncBody(MethodInfo method, object? instance, object?[] args)
    {
        var call = BuildCall(method, instance, args);
        var sink = Expression.Field(null, typeof(ReturnSink).GetField(nameof(ReturnSink.Hole))!);

        // Assigned to a static field so the call is observably used. Without this the JIT is free to
        // eliminate a pure call entirely, and the benchmark measures nothing while reporting a
        // plausible, very fast number.
        var assign = Expression.Assign(sink, Expression.Convert(call, typeof(object)));

        return Expression.Lambda<Action>(assign).Compile();
    }

    private static Func<Task> BuildAsyncBody(MethodInfo method, object? instance, object?[] args)
    {
        var call = BuildCall(method, instance, args);
        var invokeTask = Expression.Lambda<Func<Task>>(AsTaskExpression(call, method.ReturnType)).Compile();

        return async () =>
        {
            var task = invokeTask();

            if (task is not null)
                await task.ConfigureAwait(false);
        };
    }

    private static Expression AsTaskExpression(Expression call, Type returnType)
    {
        if (returnType == typeof(Task)
            || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
            return Expression.Convert(call, typeof(Task));

        if (returnType == typeof(ValueTask))
            return Expression.Call(call, nameof(ValueTask.AsTask), Type.EmptyTypes);

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var helper = typeof(TestBodyBuilder)
                .GetMethod(nameof(ConvertGenericValueTaskToTask), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(returnType.GetGenericArguments()[0]);

            return Expression.Call(helper, call);
        }

        throw new InvalidOperationException($"Unsupported async return type: {returnType.FullName}");
    }

    private static MethodCallExpression BuildCall(MethodInfo method, object? instance, object?[] args)
    {
        var parameters = method.GetParameters();

        if (parameters.Length != args.Length)
        {
            throw new InvalidOperationException(
                $"Method '{method.Name}' expects {parameters.Length} argument(s) but received {args.Length}.");
        }

        var argExpressions = new Expression[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            argExpressions[i] = Expression.Constant(args[i], parameters[i].ParameterType);
        }

        if (method.IsStatic)
            return Expression.Call(method, argExpressions);

        if (instance is null)
            throw new InvalidOperationException($"Method '{method.Name}' requires a target instance.");

        return Expression.Call(Expression.Constant(instance, method.DeclaringType!), method, argExpressions);
    }

    private static Task ConvertGenericValueTaskToTask<T>(ValueTask<T> valueTask) => valueTask.AsTask();
}

/// <summary>
///     Where a benchmarked method's return value goes.
/// </summary>
/// <remarks>
///     A static field, and deliberately not <c>readonly</c>: the point is that the write is
///     observable to the JIT, so the call producing the value cannot be optimized away. A benchmark
///     that measures a deleted call reports a number that looks excellent and means nothing.
/// </remarks>
public static class ReturnSink
{
    public static object? Hole;
}
