using System.Collections;
using System.Globalization;
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
        var classRuntimes = ResolveRuntimes(type);

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
            Runtimes = classRuntimes,
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

            yield return CreateDefinition(method, attribute, method.Name, null, null,
                iterSetupDel, iterTeardownDel, classCategories, attribute.Baseline);

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

        var methodIsBaseline = attribute.Baseline;

        if (caseAttributes.Length == 0)
        {
            if (parameters.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Benchmark '{method.DeclaringType!.Name}.{method.Name}' declares "
                    + $"{parameters.Length} parameter(s) but has no [BenchmarkCase] or [BenchmarkCases]. "
                    + "Add one [BenchmarkCase(...)] per argument set, or remove the parameters.");
            }

            yield return CreateDefinition(method, attribute, method.Name, null, null,
                iterSetupDel, iterTeardownDel, classCategories, attribute.Baseline);

            yield break;
        }

        var paramNames = parameters.Select(p => p.Name!).ToArray();

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
            var displayName = BuildDisplayName(method.Name, paramNames, converted);

            yield return CreateDefinition(method, attribute, displayName, converted, paramNames,
                iterSetupDel, iterTeardownDel, classCategories, methodIsBaseline);
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
        var methodIsBaseline = benchmarkAttr.Baseline;

        foreach (var (rawValues, paramNames) in tuples)
        {
            var converted = ConvertArguments(rawValues, parameters);
            var displayName = BuildDisplayName(method.Name, paramNames, converted);

            yield return CreateDefinition(method, benchmarkAttr, displayName, converted, paramNames,
                iterSetupDel, iterTeardownDel, classCategories, methodIsBaseline);
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

            // Prefer tuple element names; otherwise fall back to the benchmark method's own
            // parameter names so the report still shows meaningful, named parameter columns.
            var effectiveNames = hasNames && tupleNames!.Length >= arity
                ? tupleNames
                : benchmarkParams.Select(p => p.Name!).ToArray();

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
        var paramSet = BuildParameterSet(paramNames, values);
        return BenchmarkParameter.FormatDisplayName(methodName, paramSet);
    }

    /// <summary>
    ///     Builds a one-benchmark suite around a method chosen by the caller rather than found by
    ///     attribute discovery.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is how a test-framework integration measures a test method in a worker. The
    ///         method carries no <c>[Benchmark]</c> attribute, so discovery would never find it -
    ///         but everything downstream of discovery (instance lifetime, iteration structure,
    ///         sample transport, progress streaming) applies unchanged, so a synthesized definition
    ///         reuses all of it rather than growing a parallel measurement path.
    ///     </para>
    ///     <para>
    ///         <paramref name="arguments" /> are bound into the compiled delegate, so a
    ///         parameterized test case measures the same call the test framework would have made.
    ///     </para>
    /// </remarks>
    internal static BenchmarkSuiteDefinition DefineExplicit(
        MethodInfo method,
        object?[] arguments,
        string displayName)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);

        return DefineExplicit([(method, arguments, displayName)]);
    }

    /// <summary>
    ///     Builds one suite around several caller-chosen methods, so they are measured co-resident.
    /// </summary>
    /// <remarks>
    ///     The multi-method form exists for a <c>[Performance]</c> test that names a reference method:
    ///     both sides of the ratio belong in one suite, in one worker, per replicate. Two suites in two
    ///     workers would measure the same bodies and produce a ratio with every worker-to-worker
    ///     difference left in it.
    ///     <para>
    ///         Every method must be declared by the same type - the suite has one
    ///         <see cref="BenchmarkSuiteDefinition.Type" />, and it is the type the worker instantiates.
    ///     </para>
    /// </remarks>
    internal static BenchmarkSuiteDefinition DefineExplicit(
        IReadOnlyList<(MethodInfo Method, object?[] Arguments, string DisplayName)> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        if (methods.Count == 0)
            throw new ArgumentException("At least one method is required.", nameof(methods));

        var declaringType = methods[0].Method.DeclaringType
                            ?? throw new InvalidOperationException(
                                $"Method '{methods[0].Method.Name}' has no declaring type.");

        var definitions = new List<BenchmarkMethodDefinition>(methods.Count);

        foreach (var (method, arguments, displayName) in methods)
        {
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(arguments);

            if (method.DeclaringType != declaringType)
            {
                throw new InvalidOperationException(
                    $"'{method.Name}' is declared by '{method.DeclaringType?.FullName}' but the suite is "
                    + $"built around '{declaringType.FullName}'. A co-resident group shares one instance "
                    + "type.");
            }

            definitions.Add(CreateDefinition(
                method,
                new BenchmarkAttribute(),
                displayName,
                arguments,
                paramNames: method.GetParameters().Select(pa => pa.Name ?? string.Empty).ToArray(),
                iterSetupDel: null,
                iterTeardownDel: null,
                classCategories: []));
        }

        return new BenchmarkSuiteDefinition(declaringType, definitions);
    }

    private static BenchmarkMethodDefinition CreateDefinition(
        MethodInfo method,
        BenchmarkAttribute attribute,
        string displayName,
        object?[]? arguments,
        string[]? paramNames,
        Action<object>? iterSetupDel,
        Action<object>? iterTeardownDel,
        IReadOnlyList<string> classCategories,
        bool isBaseline = false)
    {
        var parameterSet = BuildParameterSet(paramNames, arguments);

        return new BenchmarkMethodDefinition(method, attribute)
        {
            DisplayName = displayName,

            // One binder for every shape. The delegate it produces carries the method's own
            // signature, so the engine reaches the body without boxing its result - see
            // BenchmarkBodyFactory.
            BodyFactory = BenchmarkBodyFactory.Create(method, arguments),
            IterationSetupDelegate = iterSetupDel,
            IterationTeardownDelegate = iterTeardownDel,
            Isolation = ResolveIsolationMode(method),
            Categories = MergeCategories(classCategories, ResolveCategories(method)),
            IsBaseline = isBaseline,
            ParameterSet = parameterSet,
        };
    }

    private static IReadOnlyList<BenchmarkParameter> BuildParameterSet(string[]? paramNames, object?[]? arguments)
    {
        if (arguments is null || arguments.Length == 0)
            return [];

        var result = new BenchmarkParameter[arguments.Length];

        for (var i = 0; i < arguments.Length; i++)
        {
            var name = paramNames is not null && i < paramNames.Length ? paramNames[i] : $"arg{i}";
            result[i] = new BenchmarkParameter(name, arguments[i]);
        }

        return result;
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

    private static IReadOnlyList<RuntimeMoniker> ResolveRuntimes(Type type)
    {
        var attr = type.GetCustomAttribute<RuntimesAttribute>(true);
        return attr?.Runtimes ?? [];
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

    /// <summary>Builds the delegate for a lifecycle hook - setup, teardown, or their per-iteration pair.</summary>
    /// <remarks>
    ///     A hook runs outside the timed window, so it keeps the uniform
    ///     <c>Action&lt;object&gt;</c> shape rather than reconstructing its exact signature the way
    ///     <see cref="BenchmarkBodyFactory" /> does for a measured body.
    /// </remarks>
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
}
