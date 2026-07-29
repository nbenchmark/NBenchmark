using System.Linq.Expressions;
using System.Reflection;

namespace NBenchmark.Discovery;

/// <summary>
///     Builds the delegate a discovered benchmark method is measured through: one delegate whose
///     type is the method's <i>own</i> signature, bound to the instance it runs against.
/// </summary>
/// <remarks>
///     <para>
///         Discovery used to hand the engine a <c>Func&lt;object, object?&gt;</c>. That single
///         choice cost every value-returning benchmark method a box on every operation - 24 bytes,
///         reported as the user's allocation on a body that allocates nothing - plus two extra
///         delegate hops between the loop and the method. On a nanosecond-scale body, which is
///         exactly what the ops-per-sample calibration exists to make measurable, that is most of
///         the reading.
///     </para>
///     <para>
///         So the shape is reconstructed instead: <c>int Foo()</c> becomes a <c>Func&lt;int&gt;</c>,
///         <c>Task&lt;string&gt; Bar()</c> a <c>Func&lt;Task&lt;string&gt;&gt;</c>, and
///         <see cref="Workers.DelegateDispatch" /> closes the matching generic engine entry point
///         over it once per benchmark, outside the measured region. The value then reaches the
///         JIT-elision sink through a closed generic static field: no box, no cast, and no
///         allocation charged to code that never allocated.
///     </para>
///     <para>
///         Two build paths, and the distinction is not cosmetic. With no arguments to bind, the
///         delegate <i>is</i> the method - <see cref="MethodInfo.CreateDelegate(Type, object)" />
///         closed over the receiver - so nothing sits between the loop and the body. Binding
///         arguments, or converting a <c>ValueTask</c>, needs generated code, and there the
///         compiled binder returns an inner lambda that has already captured the typed receiver, so
///         the per-operation cost is still a single delegate invocation.
///     </para>
/// </remarks>
internal static class BenchmarkBodyFactory
{
    /// <summary>
    ///     Creates the binder for <paramref name="method" />: given a benchmark instance, it returns
    ///     the delegate to measure.
    /// </summary>
    /// <param name="arguments">
    ///     Values to bind into the call, or <c>null</c> / empty when the method takes none.
    /// </param>
    public static Func<object, Delegate> Create(MethodInfo method, object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);

        var bodyType = BodyDelegateType(method.ReturnType);
        var hasArguments = arguments is { Length: > 0 };

        // A ValueTask has to become a Task before the engine can await it, and that conversion
        // cannot be expressed by binding a delegate directly to the method.
        var needsConversion = NeedsTaskConversion(method.ReturnType);

        if (!hasArguments && !needsConversion)
        {
            return method.IsStatic
                ? _ => method.CreateDelegate(bodyType)
                : instance => method.CreateDelegate(bodyType, instance);
        }

        return CompileBinder(method, bodyType, arguments ?? []);
    }

    /// <summary>
    ///     The delegate type that carries a method's declared return type without widening it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Widening is the whole problem: a <c>Func&lt;object&gt;</c> would accept every shape
    ///         and box every value type. An awaitable maps to <c>Func&lt;Task&gt;</c> or
    ///         <c>Func&lt;Task&lt;T&gt;&gt;</c> so the engine's async entry points can await it and
    ///         consume its result.
    ///     </para>
    ///     <para>
    ///         <c>ValueTask</c> is mapped here, and once was not. Because it is a struct and
    ///         therefore not assignable to <c>Task</c>, an earlier check classified an
    ///         <c>async ValueTask</c> benchmark as <i>synchronous</i> and boxed the returned struct
    ///         instead of awaiting it - so measurement stopped at the first <c>await</c> inside the
    ///         body. On a body that delays 50 ms that reported <b>1 ms</b>: a plausible,
    ///         confidently-wrong number rather than an error.
    ///     </para>
    /// </remarks>
    public static Type BodyDelegateType(Type returnType)
    {
        ArgumentNullException.ThrowIfNull(returnType);

        if (returnType == typeof(void))
            return typeof(Action);

        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
            return typeof(Func<Task>);

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();

            if (definition == typeof(Task<>))
                return typeof(Func<>).MakeGenericType(returnType);

            if (definition == typeof(ValueTask<>))
            {
                var resultType = typeof(Task<>).MakeGenericType(returnType.GetGenericArguments()[0]);
                return typeof(Func<>).MakeGenericType(resultType);
            }
        }

        return typeof(Func<>).MakeGenericType(returnType);
    }

    private static bool NeedsTaskConversion(Type returnType)
        => returnType == typeof(ValueTask)
           || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>));

    /// <summary>
    ///     Compiles a binder that converts the instance once and returns a body delegate which has
    ///     already captured it.
    /// </summary>
    /// <remarks>
    ///     The nested lambda is what keeps the measured path to one hop. Compiling a
    ///     <c>Func&lt;object, T&gt;</c> and wrapping it in a closure would work too, and would put a
    ///     second delegate invocation and a cast inside the loop for every operation.
    /// </remarks>
    private static Func<object, Delegate> CompileBinder(MethodInfo method, Type bodyType, object?[] arguments)
    {
        var instanceParam = Expression.Parameter(typeof(object), "instance");

        if (method.IsStatic)
        {
            var staticBody = Expression.Lambda(bodyType, AsAwaitable(BuildCall(method, null, arguments), method.ReturnType));

            return Expression.Lambda<Func<object, Delegate>>(
                Expression.Convert(staticBody, typeof(Delegate)), instanceParam).Compile();
        }

        var target = Expression.Variable(method.DeclaringType!, "target");
        var inner = Expression.Lambda(bodyType, AsAwaitable(BuildCall(method, target, arguments), method.ReturnType));

        var block = Expression.Block(
            [target],
            Expression.Assign(target, Expression.Convert(instanceParam, method.DeclaringType!)),
            inner);

        return Expression.Lambda<Func<object, Delegate>>(
            Expression.Convert(block, typeof(Delegate)), instanceParam).Compile();
    }

    /// <summary>Converts a <c>ValueTask</c>-returning call to a <c>Task</c>; leaves everything else alone.</summary>
    private static Expression AsAwaitable(Expression call, Type returnType)
        => NeedsTaskConversion(returnType)
            ? Expression.Call(call, nameof(ValueTask.AsTask), Type.EmptyTypes)
            : call;

    private static MethodCallExpression BuildCall(MethodInfo method, Expression? target, object?[] arguments)
    {
        var parameters = method.GetParameters();
        var argExpressions = new Expression[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            // Typed to the parameter rather than to the value: a null argument, or an int bound to
            // a long parameter, has to carry the declared type or the call will not bind.
            argExpressions[i] = Expression.Constant(
                i < arguments.Length ? arguments[i] : null, parameters[i].ParameterType);
        }

        // A static method has no receiver, and passing one to Expression.Call throws. Attribute
        // discovery never reaches this with a static method, but DefineExplicit does - a test
        // framework will happily hand over a static test method.
        return method.IsStatic
            ? Expression.Call(method, argExpressions)
            : Expression.Call(target!, method, argExpressions);
    }
}
