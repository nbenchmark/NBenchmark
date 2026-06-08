using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using NBenchmark.Attributes;
using NBenchmark.Engine;

namespace NBenchmark.Discovery;

public sealed class BenchmarkDiscoverer
{
    public IReadOnlyList<BenchmarkSuiteDefinition> Discover(Assembly assembly)
    {
        var suites = new List<BenchmarkSuiteDefinition>();

        var types = assembly.GetTypes()
            .Where(t => !t.IsAbstract
                        && t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Any(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null));

        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Concat(type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                .ToArray();

            var setupMethod = methods.FirstOrDefault(m2 => m2.GetCustomAttribute<BenchmarkSetupAttribute>() is not null);
            var teardownMethod = methods.FirstOrDefault(m2 => m2.GetCustomAttribute<BenchmarkTeardownAttribute>() is not null);
            var iterSetupMethod = methods.FirstOrDefault(m2 => m2.GetCustomAttribute<BenchmarkIterationSetupAttribute>() is not null);
            var iterTeardownMethod = methods.FirstOrDefault(m2 => m2.GetCustomAttribute<BenchmarkIterationTeardownAttribute>() is not null);

            var setupDel = BuildVoidDelegate(setupMethod);
            var teardownDel = BuildVoidDelegate(teardownMethod);
            var iterSetupDel = BuildVoidDelegate(iterSetupMethod);
            var iterTeardownDel = BuildVoidDelegate(iterTeardownMethod);

            var benchmarks = methods
                .Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null)
                .SelectMany(m => BuildBenchmarkDefinitions(m, iterSetupDel, iterTeardownDel))
                .ToList();

            if (benchmarks.Count == 0)
                continue;

            suites.Add(new BenchmarkSuiteDefinition(
                type,
                benchmarks,
                setupDel,
                teardownDel
            ));
        }

        return suites;
    }

    // Expands a [Benchmark] method into one definition per [BenchmarkArguments] set.
    // A parameterless method (no [BenchmarkArguments]) yields a single definition.
    private static IEnumerable<BenchmarkMethodDefinition> BuildBenchmarkDefinitions(
        MethodInfo method,
        Action<object>? iterSetupDel,
        Action<object>? iterTeardownDel)
    {
        var attribute = method.GetCustomAttribute<BenchmarkAttribute>()!;
        var argumentSets = method.GetCustomAttributes<BenchmarkArgumentsAttribute>().ToArray();
        var parameters = method.GetParameters();

        if (argumentSets.Length == 0)
        {
            if (parameters.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' declares "
                    + $"{parameters.Length} parameter(s) but has no [BenchmarkArguments]. "
                    + "Add one [BenchmarkArguments(...)] per argument set, or remove the parameters.");
            }

            yield return CreateDefinition(method, attribute, method.Name, null,
                iterSetupDel, iterTeardownDel);

            yield break;
        }

        if (parameters.Length == 0)
        {
            throw new InvalidOperationException(
                $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' has [BenchmarkArguments] "
                + "but takes no parameters.");
        }

        foreach (var argumentSet in argumentSets)
        {
            var rawArgs = argumentSet.Arguments;

            if (rawArgs.Length != parameters.Length)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' expects "
                    + $"{parameters.Length} argument(s) but a [BenchmarkArguments] attribute supplied "
                    + $"{rawArgs.Length}.");
            }

            var converted = ConvertArguments(rawArgs, parameters);
            var displayName = $"{method.Name}({string.Join(", ", rawArgs.Select(FormatArgument))})";

            yield return CreateDefinition(method, attribute, displayName, converted,
                iterSetupDel, iterTeardownDel);
        }
    }

    private static BenchmarkMethodDefinition CreateDefinition(
        MethodInfo method,
        BenchmarkAttribute attribute,
        string displayName,
        object?[]? arguments,
        Action<object>? iterSetupDel,
        Action<object>? iterTeardownDel)
    {
        var isAsync = typeof(Task).IsAssignableFrom(method.ReturnType);

        Func<object, object?>? syncDelegate = null;
        Func<object, Task>? asyncDelegate = null;
        Action<Task>? resultConsumer = null;

        if (isAsync)
        {
            asyncDelegate = arguments is null
                ? BuildAsyncDelegate(method)
                : BuildArgumentBoundAsyncDelegate(method, arguments);

            if (method.ReturnType.IsGenericType)
                resultConsumer = BuildResultConsumer(method.ReturnType);
        }
        else if (method.ReturnType == typeof(void))
        {
            if (arguments is null)
            {
                var act = BuildVoidDelegate(method)!;

                syncDelegate = instance =>
                {
                    act(instance);
                    return null;
                };
            }
            else
                syncDelegate = BuildArgumentBoundSyncDelegate(method, arguments);
        }
        else
        {
            syncDelegate = arguments is null
                ? BuildSyncDelegate(method)
                : BuildArgumentBoundSyncDelegate(method, arguments);
        }

        return new BenchmarkMethodDefinition(method, attribute)
        {
            DisplayName = displayName,
            SyncDelegate = syncDelegate,
            AsyncDelegate = asyncDelegate,
            ResultConsumer = resultConsumer,
            IterationSetupDelegate = iterSetupDel,
            IterationTeardownDelegate = iterTeardownDel,
        };
    }

    private static object?[] ConvertArguments(object[] arguments, ParameterInfo[] parameters)
    {
        var result = new object?[arguments.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            var target = parameters[i].ParameterType;
            var value = arguments[i];

            if (value is null || target.IsInstanceOfType(value))
                result[i] = value;
            else
            {
                result[i] = Convert.ChangeType(
                    value, Nullable.GetUnderlyingType(target) ?? target, CultureInfo.InvariantCulture);
            }
        }

        return result;
    }

    private static string FormatArgument(object? argument)
    {
        return argument switch
        {
            null => "null",
            string s => $"\"{s}\"",
            _ => Convert.ToString(argument, CultureInfo.InvariantCulture) ?? argument.ToString() ?? "",
        };
    }

    // Argument-bound delegates are compiled once per (method, argument set) via an
    // expression tree, so the measurement hot loop still calls a plain delegate with
    // no per-iteration reflection. Arbitrary parameter arities and types are supported.
    private static Func<object, object?> BuildArgumentBoundSyncDelegate(MethodInfo method, object?[] arguments)
    {
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var call = BuildCall(method, instanceParam, arguments);

        Expression body = method.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));

        return Expression.Lambda<Func<object, object?>>(body, instanceParam).Compile();
    }

    private static Func<object, Task> BuildArgumentBoundAsyncDelegate(MethodInfo method, object?[] arguments)
    {
        var instanceParam = Expression.Parameter(typeof(object), "instance");
        var call = BuildCall(method, instanceParam, arguments);
        var body = Expression.Convert(call, typeof(Task));
        return Expression.Lambda<Func<object, Task>>(body, instanceParam).Compile();
    }

    private static MethodCallExpression BuildCall(
        MethodInfo method, ParameterExpression instanceParam, object?[] arguments)
    {
        var typedInstance = Expression.Convert(instanceParam, method.DeclaringType!);
        var parameters = method.GetParameters();
        var argExpressions = new Expression[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            argExpressions[i] = Expression.Constant(arguments[i], parameters[i].ParameterType);
        }

        return Expression.Call(typedInstance, method, argExpressions);
    }

    // Open instance delegates require the delegate's first parameter to match the
    // method's declaring type - Action<object>/Func<object, _> cannot bind directly
    // (the implicit `this` cannot be contravariantly widened to object). We build a
    // strongly-typed delegate against the declaring type via a generic helper, then
    // wrap it to accept object and cast once per call. This eliminates per-iteration
    // MethodInfo.Invoke / DynamicInvoke overhead in the measurement hot loop.

    private static Action<object>? BuildVoidDelegate(MethodInfo? method)
    {
        if (method is null)
            return null;

        var helper = typeof(BenchmarkDiscoverer)
            .GetMethod(nameof(BuildVoidDelegateGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(method.DeclaringType!);

        return (Action<object>)helper.Invoke(null, [method])!;
    }

    private static Action<object> BuildVoidDelegateGeneric<TInstance>(MethodInfo method)
    {
        var typed = (Action<TInstance>)Delegate.CreateDelegate(typeof(Action<TInstance>), method);
        return instance => typed((TInstance)instance);
    }

    private static Func<object, object?> BuildSyncDelegate(MethodInfo method)
    {
        var helper = typeof(BenchmarkDiscoverer)
            .GetMethod(nameof(BuildSyncDelegateGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(method.DeclaringType!, method.ReturnType);

        return (Func<object, object?>)helper.Invoke(null, [method])!;
    }

    private static Func<object, object?> BuildSyncDelegateGeneric<TInstance, TReturn>(MethodInfo method)
    {
        var typed = (Func<TInstance, TReturn>)Delegate.CreateDelegate(typeof(Func<TInstance, TReturn>), method);
        return instance => typed((TInstance)instance);
    }

    private static Func<object, Task> BuildAsyncDelegate(MethodInfo method)
    {
        var helper = typeof(BenchmarkDiscoverer)
            .GetMethod(nameof(BuildAsyncDelegateGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(method.DeclaringType!);

        return (Func<object, Task>)helper.Invoke(null, [method])!;
    }

    // A method returning Task<T> binds to Func<TInstance, Task> via reference-type
    // return covariance, so this single helper covers both Task and Task<T>.
    private static Func<object, Task> BuildAsyncDelegateGeneric<TInstance>(MethodInfo method)
    {
        var typed = (Func<TInstance, Task>)Delegate.CreateDelegate(typeof(Func<TInstance, Task>), method);
        return instance => typed((TInstance)instance);
    }

    private static Action<Task> BuildResultConsumer(Type taskType)
    {
        var resultType = taskType.GetGenericArguments()[0];

        var helper = typeof(BenchmarkDiscoverer)
            .GetMethod(nameof(BuildResultConsumerGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType);

        return (Action<Task>)helper.Invoke(null, [])!;
    }

    private static Action<Task> BuildResultConsumerGeneric<T>()
    {
        var consume = BenchmarkRunner.GetResultConsumer<T>();
        return task => consume(((Task<T>)task).Result);
    }
}