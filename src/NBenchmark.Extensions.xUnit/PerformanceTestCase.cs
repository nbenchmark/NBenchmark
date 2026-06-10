using System.Linq.Expressions;
using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Extensions.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace NBenchmark.Extensions.xUnit;

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

    protected override string GetSkipReason(IAttributeInfo factAttribute) =>
        _skipReason ?? base.GetSkipReason(factAttribute);

    Task<RunSummary> IXunitTestCase.RunAsync(
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        object[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
        => RunPerformanceTestAsync(messageBus, constructorArguments, aggregator, cancellationTokenSource);

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
                        Options = BuildMeasurementOptions(data),
                    };

                    var name = $"{TestMethod.TestClass.Class.Name}.{TestMethod.Method.Name}";

                    BenchmarkResult result;
                    if (TryBuildBody(methodInfo, instance, methodArgs, out var body, out var isAsync))
                    {
                        if (isAsync)
                        {
                            if (body is Func<Task> taskBody)
                            {
                                var outcome = await BenchmarkRunner.Instance.RunAsync(
                                    name, taskBody, runSpec, cancellationTokenSource.Token);
                                result = outcome.Result;
                            }
                            else
                            {
                                throw new InvalidOperationException("Async body must be Func<Task>.");
                            }
                        }
                        else
                        {
                            if (body is Action actionBody)
                            {
                                var outcome = BenchmarkRunner.Instance.Run(
                                    name, actionBody, runSpec, cancellationTokenSource.Token);
                                result = outcome.Result;
                            }
                            else
                            {
                                throw new InvalidOperationException("Sync body must be Action.");
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException($"Could not build body for method {methodInfo.Name}.");
                    }

                    var violations = ValidateResult(result, data);
                    var output = FormatBenchmarkOutput(result);

                    if (violations.Count > 0)
                    {
                        var message = string.Join(Environment.NewLine, violations);
                        summary.Failed++;
                        var exception = new PerformanceAssertException(message);
                        messageBus.QueueMessage(new TestFailed(test, timer.Total, output, exception));
                    }
                    else
                    {
                        messageBus.QueueMessage(new TestPassed(test, timer.Total, output));
                    }
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

    private static MeasurementOptions BuildMeasurementOptions(PerformanceTestData data)
    {
        var options = MeasurementOptions.Default;

        if (data.Iterations > 0)
            options = options with { Iterations = data.Iterations };
        if (data.WarmupIterations > 0)
            options = options with { WarmupIterations = data.WarmupIterations };
        if (data.MeasureAllocations || data.MaxAllocatedBytes >= 0)
            options = options with { MeasureAllocations = true };
        options = options with
        {
            OutlierMode = data.OutlierMode,
            ConfidenceLevel = data.ConfidenceLevel,
        };

        return options;
    }

    internal static IReadOnlyList<string> ValidateResult(BenchmarkResult result, PerformanceTestData data)
    {
        var violations = new List<string>();

        if (result.Errored)
            violations.Add($"Benchmark errored: {result.ErrorMessage}");

        var thresholds = new PerformanceThresholds
        {
            MaxMeanNs = data.MaxMeanNs >= 0 ? data.MaxMeanNs : null,
            MaxP95Ns = data.MaxP95Ns >= 0 ? data.MaxP95Ns : null,
            MaxAllocatedBytes = data.MaxAllocatedBytes >= 0 ? data.MaxAllocatedBytes : null,
        };

        violations.AddRange(BenchmarkAssert.Validate(result, thresholds));

        if (data.BaselinePath is not null)
            violations.AddRange(RegressionBaseline.Check(result, data.BaselinePath, data.MaxSlowdownRatio));

        return violations;
    }

    private static string FormatBenchmarkOutput(BenchmarkResult result)
    {
        var allocations = result.MeanAllocatedBytes.HasValue
            ? $"{result.MeanAllocatedBytes.Value} B"
            : "n/a";

        return
            $"NBenchmark metrics{Environment.NewLine}" +
            $"Mean: {result.Mean:F2} ns{Environment.NewLine}" +
            $"P95: {result.P95:F2} ns{Environment.NewLine}" +
            $"Allocations: {allocations}{Environment.NewLine}" +
            $"Iterations: {result.MeasuredIterations} (warmup: {result.WarmupIterations})";
    }

    private static Action BuildSyncBody(MethodInfo method, object? instance, object[] args)
    {
        var call = BuildCall(method, instance, args);
        return Expression.Lambda<Action>(call).Compile();
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

        body = null!;
        isAsync = false;
        return false;
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
        {
            return Expression.Convert(call, typeof(Task));
        }

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

    private static Task ConvertGenericValueTaskToTask<T>(ValueTask<T> valueTask)
    {
        return valueTask.AsTask();
    }

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
}
