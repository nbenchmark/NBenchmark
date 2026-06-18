using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using NBenchmark.Attributes;
using NBenchmark.Engine;

namespace NBenchmark.Discovery;

public sealed class BenchmarkDiscoverer
{
    private readonly InstanceLifetime _defaultInstanceLifetime;

    public BenchmarkDiscoverer()
        : this(InstanceLifetime.PerMethod)
    {
    }

    public BenchmarkDiscoverer(InstanceLifetime defaultInstanceLifetime)
    {
        _defaultInstanceLifetime = defaultInstanceLifetime;
    }

    public IReadOnlyList<BenchmarkSuiteDefinition> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var suites = new List<BenchmarkSuiteDefinition>();

        var types = assembly.GetTypes()
            .Where(t => !t.IsAbstract
                        && t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Any(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null));

        foreach (var type in types)
        {
            var suite = DiscoverType(type);

            if (suite is not null)
                suites.Add(suite);
        }

        return suites;
    }

    public IReadOnlyList<BenchmarkSuiteDefinition> Discover(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var suite = DiscoverType(type);

        return suite is null
            ? Array.Empty<BenchmarkSuiteDefinition>()
            : [suite];
    }

    private BenchmarkSuiteDefinition? DiscoverType(Type type)
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

        var classCategories = ResolveCategories(type);

        var benchmarks = methods
            .Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null)
            .SelectMany(m => BuildBenchmarkDefinitions(m, iterSetupDel, iterTeardownDel, classCategories))
            .ToList();

        if (benchmarks.Count == 0)
            return null;

        var instanceLifetime = type.GetCustomAttribute<InstanceLifetimeAttribute>()?.Lifetime
                               ?? _defaultInstanceLifetime;

        return new BenchmarkSuiteDefinition(
            type,
            benchmarks,
            setupDel,
            teardownDel
        )
        {
            Lifetime = instanceLifetime,
        };
    }

    private static IEnumerable<BenchmarkMethodDefinition> BuildBenchmarkDefinitions(
        MethodInfo method,
        Action<object>? iterSetupDel,
        Action<object>? iterTeardownDel,
        IReadOnlyList<string> classCategories)
    {
        var attribute = method.GetCustomAttribute<BenchmarkAttribute>()!;
        var caseAttributes = method.GetCustomAttributes<BenchmarkCaseAttribute>().ToArray();
        var casesAttribute = method.GetCustomAttribute<BenchmarkCasesAttribute>();
        var parameters = method.GetParameters();

        if (caseAttributes.Length > 0 && casesAttribute is not null)
        {
            throw new InvalidOperationException(
                $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' has both "
                + "[BenchmarkCase] and [BenchmarkCases]. Use one or the other.");
        }

        if (parameters.Length == 0)
        {
            if (caseAttributes.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' has [BenchmarkCase] "
                    + "but takes no parameters.");
            }

            if (casesAttribute is not null)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' has [BenchmarkCases] "
                    + "but takes no parameters.");
            }

            yield return CreateDefinition(method, attribute, method.Name, null,
                iterSetupDel, iterTeardownDel, classCategories);

            yield break;
        }

        if (casesAttribute is not null)
        {
            foreach (var definition in ExpandFromBenchmarkCases(method, casesAttribute, attribute,
                         iterSetupDel, iterTeardownDel, classCategories, parameters))
            {
                yield return definition;
            }

            yield break;
        }

        if (caseAttributes.Length == 0)
        {
            if (parameters.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' declares "
                    + $"{parameters.Length} parameter(s) but has no [BenchmarkCase] or [BenchmarkCases]. "
                    + "Add one [BenchmarkCase(...)] per argument set, or remove the parameters.");
            }

            yield return CreateDefinition(method, attribute, method.Name, null,
                iterSetupDel, iterTeardownDel, classCategories);

            yield break;
        }

        foreach (var caseAttr in caseAttributes)
        {
            var rawArgs = caseAttr.Arguments;

            if (rawArgs.Length != parameters.Length)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' expects "
                    + $"{parameters.Length} argument(s) but a [BenchmarkCase] attribute supplied "
                    + $"{rawArgs.Length}.");
            }

            // Display names use converted values so coerced types (e.g. DayOfWeek from int)
            // render as their enum names rather than raw numeric literals.
            var converted = ConvertArguments(rawArgs, parameters);
            var displayName = BuildDisplayName(method.Name, null, converted);

            yield return CreateDefinition(method, attribute, displayName, converted,
                iterSetupDel, iterTeardownDel, classCategories);
        }
    }

    private static IEnumerable<BenchmarkMethodDefinition> ExpandFromBenchmarkCases(
        MethodInfo method,
        BenchmarkCasesAttribute casesAttribute,
        BenchmarkAttribute benchmarkAttr,
        Action<object>? iterSetupDel,
        Action<object>? iterTeardownDel,
        IReadOnlyList<string> classCategories,
        ParameterInfo[] parameters)
    {
        var source = ResolveCaseSource(method, casesAttribute);
        var tuples = MaterialiseCaseTuples(method, source, parameters);

        foreach (var (rawValues, paramNames) in tuples)
        {
            var converted = ConvertArguments(rawValues, parameters);
            var displayName = BuildDisplayName(method.Name, paramNames, converted);

            yield return CreateDefinition(method, benchmarkAttr, displayName, converted,
                iterSetupDel, iterTeardownDel, classCategories);
        }
    }

    private static MethodInfo ResolveCaseSource(MethodInfo method, BenchmarkCasesAttribute attr)
    {
        var declaringType = method.DeclaringType!;

        var source = declaringType.GetMethod(attr.SourceName,
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (source is null)
        {
            throw new InvalidOperationException(
                $"Benchmark '{declaringType.Name}.{method.Name}' has [BenchmarkCases(\"{attr.SourceName}\")] "
                + $"but no member named '{attr.SourceName}' was found on type '{declaringType.Name}'.");
        }

        if (source.IsGenericMethod)
        {
            throw new InvalidOperationException(
                $"Benchmark '{declaringType.Name}.{method.Name}' references source method "
                + $"'{source.Name}' via [BenchmarkCases], but the source method must not be generic.");
        }

        if (source.GetParameters().Length > 0)
        {
            throw new InvalidOperationException(
                $"Benchmark '{declaringType.Name}.{method.Name}' references source method "
                + $"'{source.Name}' via [BenchmarkCases], but the source method must have no parameters.");
        }

        var returnType = source.ReturnType;

        if (!returnType.IsGenericType)
        {
            throw new InvalidOperationException(
                $"Benchmark '{declaringType.Name}.{method.Name}' references source method "
                + $"'{source.Name}' which returns '{returnType.Name}', but the source must return "
                + $"IEnumerable<(ValueTuple<...>)>.");
        }

        Type enumerableInterface;

        if (returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            enumerableInterface = returnType;
        else
        {
            enumerableInterface = returnType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))!;

            if (enumerableInterface is null)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{declaringType.Name}.{method.Name}' references source method "
                    + $"'{source.Name}' which returns '{returnType.Name}', but the source must return "
                    + $"IEnumerable<(ValueTuple<...>)>.");
            }
        }

        var elementType = enumerableInterface.GetGenericArguments()[0];

        if (!elementType.IsValueType || !IsValueTupleType(elementType))
        {
            throw new InvalidOperationException(
                $"Benchmark '{declaringType.Name}.{method.Name}' references source method "
                + $"'{source.Name}' which returns IEnumerable<{elementType.Name}>, "
                + "but the element type must be a ValueTuple.");
        }

        return source;
    }

    private static bool IsValueTupleType(Type type)
    {
        if (!type.IsGenericType)
            return false;

        var def = type.GetGenericTypeDefinition();

        return def == typeof(ValueTuple<>)
               || def == typeof(ValueTuple<,>)
               || def == typeof(ValueTuple<,,>)
               || def == typeof(ValueTuple<,,,>)
               || def == typeof(ValueTuple<,,,,>)
               || def == typeof(ValueTuple<,,,,,>)
               || def == typeof(ValueTuple<,,,,,,>)
               || def == typeof(ValueTuple<,,,,,,,>);
    }

    private static int GetValueTupleArity(Type tupleType)
    {
        var def = tupleType.GetGenericTypeDefinition();
        var typeArgs = tupleType.GetGenericArguments();

        if (def == typeof(ValueTuple<,,,,,,,>))
        {
            var rest = typeArgs[7];

            return IsValueTupleType(rest)
                ? 7 + GetValueTupleArity(rest)
                : 8;
        }

        return typeArgs.Length;
    }

    private static List<(object?[] RawValues, string[]? ParamNames)> MaterialiseCaseTuples(
        MethodInfo method, MethodInfo source, ParameterInfo[] benchmarkParams)
    {
        var declaringType = source.DeclaringType!;
        object? instance = null;

        if (!source.IsStatic)
        {
            try
            {
                instance = Activator.CreateInstance(declaringType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot create an instance of '{declaringType.Name}' to invoke the "
                    + $"[BenchmarkCases] source method '{source.Name}': {ex.Message}", ex);
            }
        }

        IEnumerable enumerable;

        try
        {
            enumerable = (IEnumerable)source.Invoke(instance, [])!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to invoke [BenchmarkCases] source method '{declaringType.Name}.{source.Name}': "
                + $"{ex.InnerException?.Message ?? ex.Message}", ex);
        }

        var results = new List<(object?[] RawValues, string[]? ParamNames)>();
        var enumerableType = enumerable.GetType();

        var enumerableInterface = enumerableType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableInterface is null)
        {
            throw new InvalidOperationException(
                $"[BenchmarkCases] source method '{declaringType.Name}.{source.Name}' "
                + $"returned '{enumerableType.Name}', which is not IEnumerable<T>. The source must "
                + "yield IEnumerable<ValueTuple<...>>.");
        }

        var elementType = enumerableInterface.GetGenericArguments()[0];

        var tupleNames = GetTupleNames(source);
        var hasNames = tupleNames is { Length: > 0 };

        foreach (var element in enumerable)
        {
            if (element is null)
            {
                throw new InvalidOperationException(
                    $"[BenchmarkCases] source method '{declaringType.Name}.{source.Name}' yielded "
                    + "a null element. ValueTuple elements must not be null.");
            }

            var elementTupleType = element.GetType();

            if (!IsValueTupleType(elementTupleType))
            {
                throw new InvalidOperationException(
                    $"[BenchmarkCases] source method '{declaringType.Name}.{source.Name}' yielded "
                    + $"a non-ValueTuple element of type '{elementTupleType.Name}'. "
                    + "All elements must be ValueTuples.");
            }

            var arity = GetValueTupleArity(elementTupleType);

            if (arity != benchmarkParams.Length)
            {
                throw new InvalidOperationException(
                    $"[BenchmarkCases] source method '{declaringType.Name}.{source.Name}' yields "
                    + $"ValueTuple with {arity} element(s), but benchmark method "
                    + $"'{method.DeclaringType!.Name}.{method.Name}' expects {benchmarkParams.Length} parameter(s).");
            }

            if (arity > 7)
            {
                throw new InvalidOperationException(
                    $"[BenchmarkCases] source method '{declaringType.Name}.{source.Name}' yields "
                    + $"a ValueTuple with {arity} element(s). NBenchmark supports at most 7 "
                    + "parameters for [BenchmarkCases] sources.");
            }

            var tuple = (ITuple)element;
            var values = new object?[tuple.Length];

            for (var i = 0; i < tuple.Length; i++)
            {
                values[i] = tuple[i];
            }

            var effectiveNames = hasNames && tupleNames!.Length >= arity ? tupleNames : null;
            results.Add((values, effectiveNames));
        }

        return results;
    }

    private static string[]? GetTupleNames(MethodInfo sourceMethod)
    {
        var attr = sourceMethod.ReturnParameter
            .GetCustomAttributes(typeof(TupleElementNamesAttribute), false)
            .Cast<TupleElementNamesAttribute>()
            .FirstOrDefault();

        var names = attr?.TransformNames;
        return names is { Count: > 0 } ? names.Where(n => n is not null).Select(n => n!).ToArray() : null;
    }

    private static string BuildDisplayName(string methodName, string[]? paramNames, object?[] values)
    {
        var parts = new string[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            var formatted = FormatArgument(values[i]);
            parts[i] = paramNames is not null && i < paramNames.Length ? $"{paramNames[i]}={formatted}" : formatted;
        }

        return $"{methodName}({string.Join(", ", parts)})";
    }

    private static BenchmarkMethodDefinition CreateDefinition(
        MethodInfo method,
        BenchmarkAttribute attribute,
        string displayName,
        object?[]? arguments,
        Action<object>? iterSetupDel,
        Action<object>? iterTeardownDel,
        IReadOnlyList<string> classCategories)
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
            Isolation = ResolveIsolationMode(method),
            Categories = MergeCategories(classCategories, ResolveCategories(method)),
        };
    }

    private static IsolationMode ResolveIsolationMode(MethodInfo method)
    {
        if (method.GetCustomAttribute<InProcessAttribute>(true) is not null)
            return IsolationMode.InProcess;

        if (method.GetCustomAttribute<IsolatedProcessAttribute>(true) is not null)
            return IsolationMode.PerBenchmark;

        var declaringType = method.DeclaringType;

        if (declaringType?.GetCustomAttribute<InProcessAttribute>(true) is not null)
            return IsolationMode.InProcess;

        if (declaringType?.GetCustomAttribute<IsolatedProcessAttribute>(true) is not null)
            return IsolationMode.PerBenchmark;

        return IsolationMode.Default;
    }

    private static IReadOnlyList<string> ResolveCategories(MemberInfo member)
    {
        var resolved = new List<string>();

        foreach (var attribute in member.GetCustomAttributes<BenchmarkCategoryAttribute>(true))
        {
            if (!resolved.Contains(attribute.Name, StringComparer.OrdinalIgnoreCase))
                resolved.Add(attribute.Name);
        }

        return resolved;
    }

    private static IReadOnlyList<string> MergeCategories(
        IReadOnlyList<string> classCategories,
        IReadOnlyList<string> methodCategories)
    {
        if (classCategories.Count == 0)
            return methodCategories;

        if (methodCategories.Count == 0)
            return classCategories;

        var merged = new List<string>(classCategories);

        foreach (var category in methodCategories)
        {
            if (!merged.Contains(category, StringComparer.OrdinalIgnoreCase))
                merged.Add(category);
        }

        return merged;
    }

    private static object?[] ConvertArguments(object?[] arguments, ParameterInfo[] parameters)
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
