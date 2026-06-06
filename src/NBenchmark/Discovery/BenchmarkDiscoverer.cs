using System.Reflection;
using NBenchmark.Attributes;

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
                           .Cast<MethodInfo>()
                           .Concat(type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                           .ToArray();

            var setupMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkSetupAttribute>() is not null);
            var teardownMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkTeardownAttribute>() is not null);
            var iterSetupMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkIterationSetupAttribute>() is not null);
            var iterTeardownMethod = methods.FirstOrDefault(
                m2 => m2.GetCustomAttribute<BenchmarkIterationTeardownAttribute>() is not null);

            var setupDel = BuildVoidDelegate(setupMethod);
            var teardownDel = BuildVoidDelegate(teardownMethod);
            var iterSetupDel = BuildVoidDelegate(iterSetupMethod);
            var iterTeardownDel = BuildVoidDelegate(iterTeardownMethod);

            var benchmarks = methods
                .Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null)
                .Select(m =>
                {
                    var isAsync = typeof(Task).IsAssignableFrom(m.ReturnType);

                    Func<object, object?>? syncDelegate = null;
                    Func<object, Task>? asyncDelegate = null;
                    Func<Task, object?>? resultExtractor = null;

                    if (isAsync)
                    {
                        asyncDelegate = BuildAsyncDelegate(m);
                        if (m.ReturnType.IsGenericType)
                            resultExtractor = BuildResultExtractor(m.ReturnType);
                    }
                    else if (m.ReturnType == typeof(void))
                    {
                        var act = BuildVoidDelegate(m)!;
                        syncDelegate = instance => { act(instance); return null; };
                    }
                    else
                    {
                        syncDelegate = BuildSyncDelegate(m);
                    }

                    return new BenchmarkMethodDefinition(
                        Method: m,
                        Attribute: m.GetCustomAttribute<BenchmarkAttribute>()!
                    )
                    {
                        SyncDelegate = syncDelegate,
                        AsyncDelegate = asyncDelegate,
                        ResultExtractor = resultExtractor,
                        IterationSetupDelegate = iterSetupDel,
                        IterationTeardownDelegate = iterTeardownDel,
                    };
                })
                .ToList();

            if (benchmarks.Count == 0) continue;

            suites.Add(new BenchmarkSuiteDefinition(
                Type: type,
                Benchmarks: benchmarks,
                SetupDelegate: setupDel,
                TeardownDelegate: teardownDel
            ));
        }

        return suites;
    }

    // Open instance delegates require the delegate's first parameter to match the
    // method's declaring type — Action<object>/Func<object, _> cannot bind directly
    // (the implicit `this` cannot be contravariantly widened to object). We build a
    // strongly-typed delegate against the declaring type via a generic helper, then
    // wrap it to accept object and cast once per call. This eliminates per-iteration
    // MethodInfo.Invoke / DynamicInvoke overhead in the measurement hot loop.

    private static Action<object>? BuildVoidDelegate(MethodInfo? method)
    {
        if (method is null) return null;

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

    private static Func<Task, object?> BuildResultExtractor(Type taskType)
    {
        var resultType = taskType.GetGenericArguments()[0];
        var helper = typeof(BenchmarkDiscoverer)
            .GetMethod(nameof(BuildResultExtractorGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(resultType);
        return (Func<Task, object?>)helper.Invoke(null, [])!;
    }

    private static Func<Task, object?> BuildResultExtractorGeneric<T>()
        => task => ((Task<T>)task).Result;
}
