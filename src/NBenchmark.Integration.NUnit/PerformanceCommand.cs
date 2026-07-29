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
            BenchmarkResult? refResult = null;
            double[]? refSamples = null;

            if (!string.IsNullOrWhiteSpace(_attribute.ReferenceMethod))
            {
                var (refMethodInfo, refArgs) = ResolveReferenceMethod(methodInfo, _attribute.ReferenceMethod, args);
                var refName = $"{testMethod.Method.TypeInfo.FullName}.{_attribute.ReferenceMethod}";

                // NUnit's DelegatingTestCommand.Execute is synchronous, so the async path is blocked
                // on here rather than propagated.
                var reference = TestMeasurement
                    .MeasureAsync(refMethodInfo, instance, refArgs, refName, runSpec, context.CancellationToken)
                    .GetAwaiter().GetResult();

                refResult = reference.Result;
                refSamples = reference.RawSamples;
            }

            var measured = TestMeasurement
                .MeasureAsync(methodInfo, instance, args, name, runSpec, context.CancellationToken)
                .GetAwaiter().GetResult();

            var result = measured.Result;
            var rawSamples = measured.RawSamples;

            // A ratio between one isolated and one host measurement is not a ratio between the two
            // bodies - it is mostly the difference between the two processes' runtime state. Better
            // to say so than to gate on it.
            var mixedIsolation = refResult is not null
                                 && refResult.IsolationStatus != result.IsolationStatus;

            if (measured.Refusal is not null)
                context.OutWriter.WriteLine($"NBenchmark: '{name}' measured in the test host - {measured.Refusal}");

            WriteMetrics(context, result);

            var violations = ValidateResult(
                result, rawSamples, mixedIsolation ? null : refResult, mixedIsolation ? null : refSamples, _attribute);

            if (mixedIsolation)
            {
                context.OutWriter.WriteLine(
                    $"NBenchmark: the ratio gate for '{name}' was skipped - the benchmark and its "
                    + "reference were measured in different processes, so their ratio would describe "
                    + "the processes rather than the code.");
            }

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

    /// <summary>
    ///     Compiles the test method into a benchmark body.
    /// </summary>
    /// <remarks>
    ///     Delegates to <see cref="TestBodyBuilder" />, which the three test-framework integrations
    ///     share. They each carried their own copy of this until the copies were found to differ,
    ///     and a divergence here changes what gets measured rather than failing loudly.
    /// </remarks>
    internal static bool TryBuildBody(
        MethodInfo method,
        object? instance,
        object?[] args,
        out Delegate body,
        out bool isAsync)
        => TestBodyBuilder.TryBuild(method, instance, args, out body, out isAsync);

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

    private static Task ConvertGenericValueTaskToTask<T>(ValueTask<T> valueTask) => valueTask.AsTask();

}
