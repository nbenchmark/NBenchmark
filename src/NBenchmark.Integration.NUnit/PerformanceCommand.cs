using System.Linq.Expressions;
using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Commands;

namespace NBenchmark.Integration.NUnit;

public sealed class PerformanceCommand : DelegatingTestCommand
{
    private readonly PerformanceAttribute _attribute;

    public PerformanceCommand(TestCommand innerCommand, PerformanceAttribute attribute)
        : base(innerCommand)
    {
        _attribute = attribute;
    }

    public override TestResult Execute(TestExecutionContext context)
    {
        var testMethod = (TestMethod)Test;
        var methodInfo = testMethod.Method.MethodInfo;
        var instance = context.TestObject;
        var args = testMethod.Arguments ?? Array.Empty<object?>();

        var runSpec = new RunSpec
        {
            Options = MeasurementOptionsBuilder.Build(_attribute),
        };

        var name = $"{testMethod.Method.TypeInfo.FullName}.{testMethod.Method.Name}";

        try
        {
            if (!TryBuildBody(methodInfo, instance, args, out var body, out var isAsync))
                throw new InvalidOperationException($"Could not build body for method {methodInfo.Name}.");

            BenchmarkResult? refResult = null;
            double[]? refSamples = null;

            if (!string.IsNullOrWhiteSpace(_attribute.ReferenceMethod))
            {
                var (refMethodInfo, refArgs) = ResolveReferenceMethod(methodInfo, _attribute.ReferenceMethod, args);

                if (!TryBuildBody(refMethodInfo, instance, refArgs, out var refBody, out var refIsAsync))
                {
                    throw new InvalidOperationException(
                        $"Could not build body for reference method {_attribute.ReferenceMethod}.");
                }

                var refName = $"{testMethod.Method.TypeInfo.FullName}.{_attribute.ReferenceMethod}";

                if (refIsAsync)
                {
                    var refTaskBody = (Func<Task>)refBody;

                    var refOutcome = BenchmarkRunner.Instance.RunAsync(
                            refName, refTaskBody, runSpec, context.CancellationToken)
                        .GetAwaiter().GetResult();

                    refResult = refOutcome.Result;
                    refSamples = refOutcome.RawSamples;
                }
                else
                {
                    var refActionBody = (Action)refBody;

                    var refOutcome = BenchmarkRunner.Instance.Run(
                        refName, refActionBody, runSpec, context.CancellationToken);

                    refResult = refOutcome.Result;
                    refSamples = refOutcome.RawSamples;
                }
            }

            BenchmarkResult result;
            double[] rawSamples;

            if (isAsync)
            {
                // NUnit's DelegatingTestCommand.Execute is synchronous, so we block on the async runner.
                var taskBody = (Func<Task>)body;

                var outcome = BenchmarkRunner.Instance.RunAsync(
                        name, taskBody, runSpec, context.CancellationToken)
                    .GetAwaiter().GetResult();

                result = outcome.Result;
                rawSamples = outcome.RawSamples;
            }
            else
            {
                var actionBody = (Action)body;

                var outcome = BenchmarkRunner.Instance.Run(
                    name, actionBody, runSpec, context.CancellationToken);

                result = outcome.Result;
                rawSamples = outcome.RawSamples;
            }

            WriteMetrics(context, result);

            var violations = ValidateResult(result, rawSamples, refResult, refSamples, _attribute);

            if (violations.Count > 0)
            {
                var message = string.Join(Environment.NewLine, violations);
                context.CurrentResult.SetResult(ResultState.Failure, message);
            }
            else
                context.CurrentResult.SetResult(ResultState.Success);
        }
        catch (Exception ex)
        {
            context.CurrentResult.RecordException(ex);
        }

        return context.CurrentResult;
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

    private static void WriteMetrics(TestExecutionContext context, BenchmarkResult result) => context.OutWriter.WriteLine(MetricsFormatter.Format(result));

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
            var helper = typeof(PerformanceCommand)
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

    private static class ReturnSink
    {
        public static object? Hole = new();
    }
}
