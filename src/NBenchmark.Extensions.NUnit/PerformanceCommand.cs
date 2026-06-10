using System.Linq.Expressions;
using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Extensions.Abstractions;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Commands;

namespace NBenchmark.Extensions.NUnit;

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

            BenchmarkResult result;

            if (isAsync)
            {
                // NUnit's DelegatingTestCommand.Execute is synchronous, so we block on the async runner.
                var taskBody = (Func<Task>)body;
                var outcome = BenchmarkRunner.Instance.RunAsync(
                    name, taskBody, runSpec, context.CancellationToken)
                    .GetAwaiter().GetResult();
                result = outcome.Result;
            }
            else
            {
                var actionBody = (Action)body;
                var outcome = BenchmarkRunner.Instance.Run(
                    name, actionBody, runSpec, context.CancellationToken);
                result = outcome.Result;
            }

            WriteMetrics(context, result);

            var violations = ValidateResult(result, _attribute);

            if (violations.Count > 0)
            {
                var message = string.Join(Environment.NewLine, violations);
                context.CurrentResult.SetResult(ResultState.Failure, message);
            }
            else
            {
                context.CurrentResult.SetResult(ResultState.Success);
            }
        }
        catch (Exception ex)
        {
            context.CurrentResult.RecordException(ex);
        }

        return context.CurrentResult;
    }

    internal static IReadOnlyList<string> ValidateResult(BenchmarkResult result, IPerformanceThresholds thresholds)
    {
        var violations = new List<string>();

        if (result.Errored)
            violations.Add($"Benchmark errored: {result.ErrorMessage}");

        var thresholdBag = new PerformanceThresholds
        {
            MaxMeanNs = thresholds.MaxMeanNs >= 0 ? thresholds.MaxMeanNs : null,
            MaxP95Ns = thresholds.MaxP95Ns >= 0 ? thresholds.MaxP95Ns : null,
            MaxAllocatedBytes = thresholds.MaxAllocatedBytes >= 0 ? thresholds.MaxAllocatedBytes : null,
        };

        violations.AddRange(BenchmarkAssert.Validate(result, thresholdBag));

        if (!string.IsNullOrWhiteSpace(thresholds.BaselinePath))
            violations.AddRange(RegressionBaseline.Check(result, thresholds.BaselinePath!, thresholds.MaxSlowdownRatio));

        return violations;
    }

    private static void WriteMetrics(TestExecutionContext context, BenchmarkResult result)
    {
        context.OutWriter.WriteLine(MetricsFormatter.Format(result));
    }

    private static Action BuildSyncBody(MethodInfo method, object? instance, object?[] args)
    {
        var call = BuildCall(method, instance, args);
        return Expression.Lambda<Action>(call).Compile();
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

        body = null!;
        isAsync = false;
        return false;
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
        {
            return Expression.Convert(call, typeof(Task));
        }

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

    private static Task ConvertGenericValueTaskToTask<T>(ValueTask<T> valueTask)
    {
        return valueTask.AsTask();
    }
}