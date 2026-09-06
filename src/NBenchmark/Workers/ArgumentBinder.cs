using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace NBenchmark.Workers;

/// <summary>
///     Binds argument values to a parameterized benchmark body, producing the parameterless delegate
///     the engine measures.
/// </summary>
/// <remarks>
///     <para>
///         This reproduces, in the measuring process, exactly what an in-process parameterized suite
///         has always built in the coordinator: <c>BenchmarkSuite.Add&lt;T&gt;</c> wraps the user's
///         typed lambda as <c>() =&gt; action(value)</c> before handing it to the engine. Building the
///         same wrapper here means an isolated parameterized benchmark is dispatched through an
///         identical call shape to an in-process one - which is the property
///         <see cref="DelegateDispatch" /> exists to preserve. It is not an extra layer: it is the same
///         layer, moved to the side of the boundary where the parameter value actually exists.
///     </para>
///     <para>
///         The wrapper is built through a typed generic helper reached by reflection <b>once per
///         benchmark</b>, outside the measured region, so what runs inside the loop is a real
///         <c>Func&lt;TResult&gt;</c> over a monomorphic call rather than a <c>Func&lt;object&gt;</c>
///         adapter that would box a value-typed return and charge the user for an allocation they
///         never wrote.
///     </para>
/// </remarks>
[RequiresUnreferencedCode("Binds [BenchmarkCase] arguments by constructing generic types and methods from the parameter types found at run time.")]
[RequiresDynamicCode("Binds [BenchmarkCase] arguments by constructing generic types and methods from the parameter types found at run time.")]
internal static class ArgumentBinder
{
    /// <summary>
    ///     The open generic delegate types for each supported arity, indexed by parameter count minus
    ///     one. Bounded at three because that is the widest parameter sweep the suite API accepts
    ///     (<c>WithParameter&lt;T1, T2, T3&gt;</c>); a wider body is refused with a message rather than
    ///     silently mis-bound.
    /// </summary>
    private static readonly Type[] OpenActionTypes = [typeof(Action<>), typeof(Action<,>), typeof(Action<,,>)];

    private static readonly Type[] OpenFuncTypes = [typeof(Func<,>), typeof(Func<,,>), typeof(Func<,,,>)];

    /// <summary>The maximum number of parameters a benchmark body may take.</summary>
    public const int MaxArity = 3;

    /// <summary>
    ///     The delegate type a method with parameters should be bound as, so its arguments can be
    ///     supplied before measurement.
    /// </summary>
    public static bool TryDelegateTypeFor(MethodInfo method, out Type delegateType, out string? error)
    {
        ArgumentNullException.ThrowIfNull(method);

        delegateType = typeof(Action);
        error = null;

        var parameters = method.GetParameters();

        if (parameters.Length == 0)
        {
            error = $"'{method.Name}' takes no parameters, so it needs no argument binding.";

            return false;
        }

        if (parameters.Length > MaxArity)
        {
            error = $"'{method.Name}' takes {parameters.Length} parameters; a benchmark body may take "
                    + $"at most {MaxArity}.";

            return false;
        }

        var parameterTypes = new Type[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;

            // A by-ref or by-ref-like parameter cannot be carried by an Action<>/Func<> at all, so
            // this has to be refused here rather than surfacing as a cast failure later.
            if (parameterType.IsByRef || parameterType.IsByRefLike || parameterType.IsPointer)
            {
                error = $"'{method.Name}' has parameter '{parameters[i].Name}' of type "
                        + $"'{parameterType.Name}', which cannot be carried by a delegate.";

                return false;
            }

            parameterTypes[i] = parameterType;
        }

        var returnType = method.ReturnType;

        if (returnType == typeof(void))
        {
            delegateType = OpenActionTypes[parameters.Length - 1].MakeGenericType(parameterTypes);

            return true;
        }

        if (returnType.IsByRefLike)
        {
            error = $"'{method.Name}' returns the by-ref-like type {returnType.Name}, which cannot be "
                    + "carried by a delegate.";

            return false;
        }

        // The parameterless path refuses these in BodyResolver.TryDelegateType and this one did not,
        // so a Func<TState, ValueTask> bound here as Func<ValueTask> and was dispatched down the
        // *synchronous* branch with T = ValueTask - the task was never awaited and the benchmark
        // measured only its synchronous prefix. Refused rather than converted, because the caller
        // asked for a synchronous measurement of something that is not one.
        if (returnType == typeof(ValueTask)
            || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            error = $"'{method.Name}' returns {returnType.Name}. The engine measures Task-returning "
                    + "bodies; wrap it as `() => Method().AsTask()`.";

            return false;
        }

        delegateType = OpenFuncTypes[parameters.Length - 1].MakeGenericType([.. parameterTypes, returnType]);

        return true;
    }

    /// <summary>
    ///     Wraps <paramref name="body" /> so its arguments are already supplied, leaving a delegate the
    ///     engine's typed entry points accept.
    /// </summary>
    public static bool TryBind(
        Delegate body,
        IReadOnlyList<object?> arguments,
        out Delegate bound,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(arguments);

        bound = body;
        error = null;

        var parameters = body.Method.GetParameters();

        if (parameters.Length == 0)
            return true;

        if (parameters.Length != arguments.Count)
        {
            error = $"'{body.Method.Name}' takes {parameters.Length} parameter(s) but "
                    + $"{arguments.Count} argument value(s) were supplied.";

            return false;
        }

        var isVoid = body.Method.ReturnType == typeof(void);

        var helperName = (isVoid ? "BindAction" : "BindFunc")
                         + parameters.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var helper = typeof(ArgumentBinder).GetMethod(helperName, BindingFlags.NonPublic | BindingFlags.Static);

        if (helper is null)
        {
            error = $"no argument binder exists for a {parameters.Length}-parameter body.";

            return false;
        }

        // An Action<T1..Tn> carries exactly [T1..Tn] and a Func<T1..Tn, TResult> exactly
        // [T1..Tn, TResult], which are precisely the type arguments the matching helper declares. The
        // delegate's own generic arguments are therefore the binding, with nothing to derive.
        var typeArguments = body.GetType().GetGenericArguments();

        try
        {
            var invocationArguments = new object?[arguments.Count + 1];
            invocationArguments[0] = body;

            for (var i = 0; i < arguments.Count; i++)
            {
                invocationArguments[i + 1] = arguments[i];
            }

            bound = (Delegate)helper.MakeGenericMethod(typeArguments).Invoke(null, invocationArguments)!;

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or TargetInvocationException or InvalidCastException)
        {
            error = $"'{body.Method.Name}' could not be bound to its {arguments.Count} argument "
                    + $"value(s): {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}";

            return false;
        }
    }

    // The bound closure is created once, before warmup. Inside the measured loop it is one delegate
    // invocation over fields the JIT can hoist - the same shape an in-process parameterized suite has
    // always measured.

    private static Action BindAction1<T1>(Action<T1> body, T1 a1) => () => body(a1);

    private static Action BindAction2<T1, T2>(Action<T1, T2> body, T1 a1, T2 a2) => () => body(a1, a2);

    private static Action BindAction3<T1, T2, T3>(Action<T1, T2, T3> body, T1 a1, T2 a2, T3 a3)
        => () => body(a1, a2, a3);

    private static Func<TResult> BindFunc1<T1, TResult>(Func<T1, TResult> body, T1 a1) => () => body(a1);

    private static Func<TResult> BindFunc2<T1, T2, TResult>(Func<T1, T2, TResult> body, T1 a1, T2 a2)
        => () => body(a1, a2);

    private static Func<TResult> BindFunc3<T1, T2, T3, TResult>(
        Func<T1, T2, T3, TResult> body, T1 a1, T2 a2, T3 a3)
        => () => body(a1, a2, a3);
}
