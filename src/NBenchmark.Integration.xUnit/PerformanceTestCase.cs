using System.Linq.Expressions;
using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

public sealed class PerformanceTestCase : XunitTestCase, IXunitTestCase
{
    private PerformanceTestData? _data;
    private string? _skipReason;

    [Obsolete("Called by the deserializer; should only be called by deriving classes for de-serialization purposes")]
    public PerformanceTestCase()
    {
    }

    internal PerformanceTestCase(
        IMessageSink diagnosticMessageSink,
        TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions,
        ITestMethod testMethod,
        PerformanceTestData data,
        object[]? testMethodArguments = null)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, testMethodArguments)
    {
        _data = data;
        _skipReason = data.SkipReason;
    }

    public override void Serialize(IXunitSerializationInfo info)
    {
        base.Serialize(info);
        info.AddValue(nameof(_data), _data);
    }

    public override void Deserialize(IXunitSerializationInfo info)
    {
        base.Deserialize(info);
        _data = info.GetValue<PerformanceTestData>(nameof(_data));
        _skipReason = _data?.SkipReason;
    }

    Task<RunSummary> IXunitTestCase.RunAsync(
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        object[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
        => RunPerformanceTestAsync(messageBus, constructorArguments, aggregator, cancellationTokenSource);

    protected override string GetSkipReason(IAttributeInfo factAttribute) =>
        _skipReason ?? base.GetSkipReason(factAttribute);

    private async Task<RunSummary> RunPerformanceTestAsync(
        IMessageBus messageBus,
        object[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        var data = _data!;
        var summary = new RunSummary { Total = 1 };
        var timer = new ExecutionTimer();
        var test = new XunitTest(this, DisplayName);

        if (!messageBus.QueueMessage(new TestStarting(test)))
            cancellationTokenSource.Cancel();

        try
        {
            await aggregator.RunAsync(async () =>
            {
                if (cancellationTokenSource.IsCancellationRequested)
                    return;

                if (!string.IsNullOrWhiteSpace(SkipReason))
                {
                    summary.Skipped++;
                    messageBus.QueueMessage(new TestSkipped(test, SkipReason));
                    return;
                }

                var testClass = TestMethod.TestClass.Class.ToRuntimeType();
                object? instance = null;

                try
                {
                    instance = CreateTestClassInstance(testClass, constructorArguments);
                    var methodInfo = TestMethod.Method.ToRuntimeMethod();
                    var methodArgs = TestMethodArguments ?? [];

                    var runSpec = new RunSpec
                    {
                        Options = MeasurementOptionsBuilder.Build(data),
                    };

                    var name = $"{TestMethod.TestClass.Class.Name}.{TestMethod.Method.Name}";

                    BenchmarkResult? refResult = null;
                    double[]? refSamples = null;

                    if (!string.IsNullOrWhiteSpace(data.ReferenceMethod))
                    {
                        var (refMethodInfo, refArgs) = ResolveReferenceMethod(methodInfo, data.ReferenceMethod, methodArgs);

                        if (TryBuildBody(refMethodInfo, instance, refArgs, out var refBody, out var refIsAsync))
                        {
                            var refName = $"{TestMethod.TestClass.Class.Name}.{data.ReferenceMethod}";

                            if (refIsAsync)
                            {
                                if (refBody is Func<Task> refTaskBody)
                                {
                                    var refOutcome = await BenchmarkRunner.Instance.RunAsync(
                                        refName, refTaskBody, runSpec, cancellationTokenSource.Token);

                                    refResult = refOutcome.Result;
                                    refSamples = refOutcome.RawSamples;
                                }
                                else
                                    throw new InvalidOperationException("Async reference body must be Func<Task>.");
                            }
                            else
                            {
                                if (refBody is Action refActionBody)
                                {
                                    var refOutcome = BenchmarkRunner.Instance.Run(
                                        refName, refActionBody, runSpec, cancellationTokenSource.Token);

                                    refResult = refOutcome.Result;
                                    refSamples = refOutcome.RawSamples;
                                }
                                else
                                    throw new InvalidOperationException("Sync reference body must be Action.");
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Could not build body for reference method {data.ReferenceMethod}.");
                        }
                    }

                    BenchmarkResult result;
                    double[] rawSamples;

                    if (TryBuildBody(methodInfo, instance, methodArgs, out var body, out var isAsync))
                    {
                        if (isAsync)
                        {
                            if (body is Func<Task> taskBody)
                            {
                                var outcome = await BenchmarkRunner.Instance.RunAsync(
                                    name, taskBody, runSpec, cancellationTokenSource.Token);

                                result = outcome.Result;
                                rawSamples = outcome.RawSamples;
                            }
                            else
                                throw new InvalidOperationException("Async body must be Func<Task>.");
                        }
                        else
                        {
                            if (body is Action actionBody)
                            {
                                var outcome = BenchmarkRunner.Instance.Run(
                                    name, actionBody, runSpec, cancellationTokenSource.Token);

                                result = outcome.Result;
                                rawSamples = outcome.RawSamples;
                            }
                            else
                                throw new InvalidOperationException("Sync body must be Action.");
                        }
                    }
                    else
                        throw new InvalidOperationException($"Could not build body for method {methodInfo.Name}.");

                    var violations = ValidateResult(result, rawSamples, refResult, refSamples, data);
                    var output = MetricsFormatter.Format(result);

                    if (violations.Count > 0)
                    {
                        var message = string.Join(Environment.NewLine, violations);
                        summary.Failed++;
                        var exception = new PerformanceAssertException(message);
                        messageBus.QueueMessage(new TestFailed(test, timer.Total, output, exception));
                    }
                    else
                        messageBus.QueueMessage(new TestPassed(test, timer.Total, output));
                }
                finally
                {
                    if (instance is not null)
                        await DisposeTestClassInstanceAsync(instance).ConfigureAwait(false);
                }
            });
        }
        catch (Exception ex)
        {
            summary.Failed++;
            var unwrapped = Unwrap(ex);
            messageBus.QueueMessage(new TestFailed(test, timer.Total, null, unwrapped));
        }
        finally
        {
            if (!messageBus.QueueMessage(new TestFinished(test, timer.Total, null)))
                cancellationTokenSource.Cancel();
        }

        return summary;
    }

    private static object CreateTestClassInstance(Type testClass, object[] constructorArguments)
    {
        if (constructorArguments.Length > 0)
            return Activator.CreateInstance(testClass, constructorArguments)!;

        return Activator.CreateInstance(testClass)!;
    }

    internal static IReadOnlyList<string> ValidateResult(
        BenchmarkResult result, double[] rawSamples,
        BenchmarkResult? refResult, double[]? refSamples,
        PerformanceTestData data)
    {
        var violations = new List<string>();

        if (result.Errored)
            violations.Add($"Benchmark errored: {result.ErrorMessage}");

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = data.MaxMeanNs >= 0 ? data.MaxMeanNs : null,
            MaxP95Ns = data.MaxP95Ns >= 0 ? data.MaxP95Ns : null,
            MaxAllocatedBytes = data.MaxAllocatedBytes >= 0 ? data.MaxAllocatedBytes : null,
            MaxAbsoluteThresholdTolerance = data.MaxAbsoluteThresholdTolerance,
        };

        violations.AddRange(BenchmarkAssert.Validate(result, thresholds));

        if (data.MaxSlowdownRatio > 0 && !result.Errored)
        {
            if (refResult is not null && refSamples is not null)
            {
                violations.AddRange(RelativeComparison.Check(
                    result, rawSamples, refResult, refSamples, data.MaxSlowdownRatio));
            }
            else
            {
                var calibration = PerformanceCalibration.Run();

                violations.AddRange(RelativeComparison.Check(
                    result, rawSamples, PerformanceCalibration.CreateBenchmarkResult(), calibration.Samples, data.MaxSlowdownRatio));
            }
        }

        return violations;
    }

    private static Action BuildSyncBody(MethodInfo method, object? instance, object[] args)
    {
        var call = BuildCall(method, instance, args);
        return Expression.Lambda<Action>(call).Compile();
    }

    private static Action BuildReturningSyncBody(MethodInfo method, object? instance, object[] args)
    {
        var call = BuildCall(method, instance, args);
        var consumeField = typeof(ReturnSink).GetField(nameof(ReturnSink.Hole))!;
        var typedField = Expression.Field(null, consumeField);

        // Store the return value in a static field so the JIT cannot elide the call.
        var assign = Expression.Assign(typedField, Expression.Convert(call, typeof(object)));
        return Expression.Lambda<Action>(assign).Compile();
    }

    internal static bool TryBuildBody(
        MethodInfo method,
        object? instance,
        object[] args,
        out Delegate body,
        out bool isAsync)
    {
        var returnType = method.ReturnType;

        if (returnType == typeof(void))
        {
            body = BuildSyncBody(method, instance, args);
            isAsync = false;
            return true;
        }

        var isSupportedAsyncReturn = returnType == typeof(Task)
                                     || returnType == typeof(ValueTask)
                                     || (returnType.IsGenericType
                                         && (returnType.GetGenericTypeDefinition() == typeof(Task<>)
                                             || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)));

        if (isSupportedAsyncReturn)
        {
            body = BuildAsyncBody(method, instance, args);
            isAsync = true;
            return true;
        }

        // Sync-returning method (Func<T>): wrap the call in an Action that
        // consumes the return value via BenchmarkRunner's JIT-elision sink,
        // matching the runner's own Run<T> overload semantics.
        body = BuildReturningSyncBody(method, instance, args);
        isAsync = false;
        return true;
    }

    internal static (MethodInfo Method, object[] Args) ResolveReferenceMethod(
        MethodInfo benchmarkMethod,
        string referenceMethodName,
        object[] benchmarkArgs)
    {
        var declaringType = benchmarkMethod.DeclaringType
                            ?? throw new InvalidOperationException(
                                $"Method {benchmarkMethod.Name} has no declaring type.");

        var candidates = declaringType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, referenceMethodName, StringComparison.Ordinal))
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"ReferenceMethod '{referenceMethodName}' not found on class '{declaringType.Name}'.");
        }

        var compatibleWithBenchmarkArgs = candidates
            .Where(m => ParametersCompatible(m.GetParameters(), benchmarkArgs))
            .ToArray();

        if (compatibleWithBenchmarkArgs.Length == 1)
            return (compatibleWithBenchmarkArgs[0], benchmarkArgs);

        if (compatibleWithBenchmarkArgs.Length > 1)
        {
            throw new InvalidOperationException(
                $"ReferenceMethod '{referenceMethodName}' is ambiguous on class '{declaringType.Name}' for the current test arguments.");
        }

        var parameterless = candidates
            .Where(m => m.GetParameters().Length == 0)
            .ToArray();

        if (parameterless.Length == 1)
            return (parameterless[0], []);

        if (parameterless.Length > 1)
        {
            throw new InvalidOperationException(
                $"ReferenceMethod '{referenceMethodName}' is ambiguous on class '{declaringType.Name}'.");
        }

        throw new InvalidOperationException(
            $"ReferenceMethod '{referenceMethodName}' on class '{declaringType.Name}' must either accept the same arguments as '{benchmarkMethod.Name}' or be parameterless.");
    }

    private static bool ParametersCompatible(ParameterInfo[] parameters, object[] args)
    {
        if (parameters.Length != args.Length)
            return false;

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;

            if (parameterType.IsByRef)
                parameterType = parameterType.GetElementType()!;

            var arg = args[i];

            if (arg is null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                    return false;

                continue;
            }

            if (!parameterType.IsInstanceOfType(arg))
                return false;
        }

        return true;
    }

    private static Func<Task> BuildAsyncBody(MethodInfo method, object? instance, object[] args)
    {
        var call = BuildCall(method, instance, args);
        var taskExpression = BuildAsyncTaskExpression(call, method.ReturnType);
        var invokeTask = Expression.Lambda<Func<Task>>(taskExpression).Compile();

        return async () =>
        {
            var task = invokeTask();

            if (task is not null)
                await task.ConfigureAwait(false);
        };
    }

    private static Expression BuildAsyncTaskExpression(Expression call, Type returnType)
    {
        if (returnType == typeof(Task)
            || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)))
            return Expression.Convert(call, typeof(Task));

        if (returnType == typeof(ValueTask))
            return Expression.Call(call, nameof(ValueTask.AsTask), Type.EmptyTypes);

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var helper = typeof(PerformanceTestCase)
                .GetMethod(nameof(ConvertGenericValueTaskToTask), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(returnType.GetGenericArguments()[0]);

            return Expression.Call(helper, call);
        }

        throw new InvalidOperationException($"Unsupported async return type: {returnType.FullName}");
    }

    private static MethodCallExpression BuildCall(MethodInfo method, object? instance, object[] args)
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

        var typedInstance = Expression.Constant(instance, method.DeclaringType!);
        return Expression.Call(typedInstance, method, argExpressions);
    }

    private static Task ConvertGenericValueTaskToTask<T>(ValueTask<T> valueTask) => valueTask.AsTask();

    private static async ValueTask DisposeTestClassInstanceAsync(object instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        (instance as IDisposable)?.Dispose();
    }

    private static Exception Unwrap(Exception ex)
    {
        return ex is AggregateException agg
            ? agg.InnerException ?? ex
            : ex;
    }

    private static class ReturnSink
    {
        public static object? Hole = new();
    }
}
