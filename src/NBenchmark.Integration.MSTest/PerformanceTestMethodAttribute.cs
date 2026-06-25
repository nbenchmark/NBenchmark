using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;

namespace NBenchmark.Integration.MSTest;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PerformanceTestMethodAttribute([CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = -1)
    : TestMethodAttribute(callerFilePath, callerLineNumber), IPerformanceThresholds
{
    public double MaxMeanNs { get; init; } = -1;
    public double MaxP95Ns { get; init; } = -1;
    public long MaxAllocatedBytes { get; init; } = -1;
    public string? ReferenceMethod { get; init; }
    public double MaxSlowdownRatio { get; init; } = 0;
    public int Iterations { get; init; }
    public int WarmupIterations { get; init; }
    public bool MeasureAllocations { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;
    public double ConfidenceLevel { get; init; } = 0.95;
    public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;

    public override Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        var methodInfo = testMethod.MethodInfo;
        var name = $"{testMethod.TestClassName}.{testMethod.TestMethodName}";
        var args = testMethod.Arguments ?? Array.Empty<object?>();

        var runSpec = new RunSpec
        {
            Options = MeasurementOptionsBuilder.Build(this),
        };

        var instance = methodInfo.IsStatic ? null : CreateInstance(methodInfo);

        BenchmarkResult? refResult = null;
        double[]? refSamples = null;
        BenchmarkResult result = null!;
        double[] rawSamples = null!;

        try
        {
            if (!string.IsNullOrWhiteSpace(ReferenceMethod))
            {
                MethodInfo refMethodInfo;
                object?[] refArgs;

                try
                {
                    (refMethodInfo, refArgs) = ResolveReferenceMethod(methodInfo, ReferenceMethod, args);
                }
                catch (InvalidOperationException ex)
                {
                    return Task.FromResult<TestResult[]>([CreateErrorResult(ex.Message)]);
                }

                if (!TryBuildBody(refMethodInfo, instance, refArgs, out var refBody, out var refIsAsync))
                    return Task.FromResult<TestResult[]>([CreateErrorResult(
                        $"Could not build body for reference method {ReferenceMethod}.")]);

                var refName = $"{testMethod.TestClassName}.{ReferenceMethod}";

                if (refIsAsync)
                {
                    if (refBody is Func<Task> refTaskBody)
                    {
                        var refOutcome = BenchmarkRunner.Instance.RunAsync(
                                refName, refTaskBody, runSpec, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        refResult = refOutcome.Result;
                        refSamples = refOutcome.RawSamples;
                    }
                    else
                        return Task.FromResult<TestResult[]>([CreateErrorResult(
                            $"Unsupported async body type for reference method {ReferenceMethod}.")]);
                }
                else
                {
                    if (refBody is Action refActionBody)
                    {
                        var refOutcome = BenchmarkRunner.Instance.Run(
                            refName, refActionBody, runSpec, CancellationToken.None);
                        refResult = refOutcome.Result;
                        refSamples = refOutcome.RawSamples;
                    }
                    else
                        return Task.FromResult<TestResult[]>([CreateErrorResult(
                            $"Unsupported sync body type for reference method {ReferenceMethod}.")]);
                }
            }

            if (TryBuildBody(methodInfo, instance, args, out var body, out var isAsync))
            {
                if (isAsync)
                {
                    if (body is Func<Task> taskBody)
                    {
                        var outcome = BenchmarkRunner.Instance.RunAsync(
                                name, taskBody, runSpec, CancellationToken.None)
                            .GetAwaiter().GetResult();

                        result = outcome.Result;
                        rawSamples = outcome.RawSamples;
                    }
                    else
                        return Task.FromResult<TestResult[]>([CreateErrorResult($"Unsupported async body type for method {methodInfo.Name}.")]);
                }
                else
                {
                    if (body is Action actionBody)
                    {
                        var outcome = BenchmarkRunner.Instance.Run(
                            name, actionBody, runSpec, CancellationToken.None);

                        result = outcome.Result;
                        rawSamples = outcome.RawSamples;
                    }
                    else
                        return Task.FromResult<TestResult[]>([CreateErrorResult($"Unsupported sync body type for method {methodInfo.Name}.")]);
                }
            }
            else
                return Task.FromResult<TestResult[]>([CreateErrorResult($"Could not build body for method {methodInfo.Name}.")]);
        }
        catch (Exception ex)
        {
            return Task.FromResult<TestResult[]>([CreateErrorResult(ex)]);
        }

        var violations = ValidateResult(result, rawSamples, refResult, refSamples, this);

        var testResult = new TestResult
        {
            DisplayName = testMethod.TestMethodName,
            Duration = result.TotalDuration,
            LogOutput = MetricsFormatter.Format(result),
        };

        if (violations.Count > 0)
        {
            testResult.Outcome = UnitTestOutcome.Failed;
            testResult.TestFailureException = new PerformanceAssertException(string.Join(Environment.NewLine, violations));
        }
        else if (result.Errored)
        {
            testResult.Outcome = UnitTestOutcome.Failed;
            testResult.TestFailureException = new PerformanceAssertException($"Benchmark errored: {result.ErrorMessage}");
        }
        else
            testResult.Outcome = UnitTestOutcome.Passed;

        return Task.FromResult<TestResult[]>([testResult]);
    }

    internal static IReadOnlyList<string> ValidateResult(
        BenchmarkResult result, double[] rawSamples,
        BenchmarkResult? refResult, double[]? refSamples,
        IPerformanceThresholds thresholds)
    {
        var violations = new List<string>();

        if (result.Errored)
            violations.Add($"Benchmark errored: {result.ErrorMessage}");

        var thresholdBag = new PerformanceThresholds
        {
            MaxMeanNs = thresholds.MaxMeanNs >= 0 ? thresholds.MaxMeanNs : null,
            MaxP95Ns = thresholds.MaxP95Ns >= 0 ? thresholds.MaxP95Ns : null,
            MaxAllocatedBytes = thresholds.MaxAllocatedBytes >= 0 ? thresholds.MaxAllocatedBytes : null,
            MaxAbsoluteThresholdTolerance = thresholds.MaxAbsoluteThresholdTolerance,
        };

        violations.AddRange(BenchmarkAssert.Validate(result, thresholdBag));

        if (thresholds.MaxSlowdownRatio > 0 && !result.Errored)
        {
            if (refResult is not null && refSamples is not null)
            {
                violations.AddRange(RelativeComparison.Check(
                    result, rawSamples, refResult, refSamples, thresholds.MaxSlowdownRatio));
            }
            else
            {
                var calibration = PerformanceCalibration.Run();
                violations.AddRange(RelativeComparison.Check(
                    result, rawSamples, PerformanceCalibration.CreateBenchmarkResult(), calibration.Samples, thresholds.MaxSlowdownRatio));
            }
        }

        return violations;
    }

    private static TestResult CreateErrorResult(string message)
    {
        return new TestResult
        {
            Outcome = UnitTestOutcome.Failed,
            TestFailureException = new PerformanceAssertException(message),
        };
    }

    private static TestResult CreateErrorResult(Exception ex)
    {
        return new TestResult
        {
            Outcome = UnitTestOutcome.Failed,
            TestFailureException = ex,
        };
    }

    private static object CreateInstance(MethodInfo method)
    {
        var declaringType = method.DeclaringType
                            ?? throw new InvalidOperationException(
                                $"Method {method.Name} has no declaring type and cannot be invoked on an instance.");

        return Activator.CreateInstance(declaringType)
               ?? throw new InvalidOperationException(
                   $"Failed to create instance of {declaringType.FullName} for benchmark method {method.Name}.");
    }

    private static Action BuildSyncBody(MethodInfo method, object? instance, object?[] args)
    {
        var call = BuildCall(method, instance, args);
        return Expression.Lambda<Action>(call).Compile();
    }

    private static Action BuildReturningSyncBody(MethodInfo method, object? instance, object?[] args)
    {
        var call = BuildCall(method, instance, args);
        var consumeField = typeof(ReturnSink).GetField(nameof(ReturnSink.Hole))!;
        var typedField = Expression.Field(null, consumeField);
        var assign = Expression.Assign(typedField, Expression.Convert(call, typeof(object)));
        return Expression.Lambda<Action>(assign).Compile();
    }

    private static class ReturnSink
    {
        public static object? Hole = new object();
    }

    internal static bool TryBuildBody(
        MethodInfo method,
        object? instance,
        object?[] args,
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

        body = BuildReturningSyncBody(method, instance, args);
        isAsync = false;
        return true;
    }

    internal static (MethodInfo Method, object?[] Args) ResolveReferenceMethod(
        MethodInfo benchmarkMethod,
        string referenceMethodName,
        object?[] benchmarkArgs)
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

    private static bool ParametersCompatible(ParameterInfo[] parameters, object?[] args)
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

    private static Func<Task> BuildAsyncBody(MethodInfo method, object? instance, object?[] args)
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
            var helper = typeof(PerformanceTestMethodAttribute)
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

        var typedInstance = Expression.Constant(instance, method.DeclaringType!);
        return Expression.Call(typedInstance, method, argExpressions);
    }

    private static Task ConvertGenericValueTaskToTask<T>(ValueTask<T> valueTask) => valueTask.AsTask();
}
