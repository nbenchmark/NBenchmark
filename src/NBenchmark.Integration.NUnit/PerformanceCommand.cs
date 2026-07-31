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
            TestMeasurement.Target? referenceTarget = null;

            if (!string.IsNullOrWhiteSpace(_attribute.ReferenceMethod))
            {
                var (refMethodInfo, refArgs) = ResolveReferenceMethod(methodInfo, _attribute.ReferenceMethod, args);
                var refName = $"{testMethod.Method.TypeInfo.FullName}.{_attribute.ReferenceMethod}";

                referenceTarget = new TestMeasurement.Target(refMethodInfo, refArgs, refName);
            }

            // Both sides in one call, so each replicate measures them co-resident and their ratio is
            // paired. NUnit's DelegatingTestCommand.Execute is synchronous, so the async path is
            // blocked on here rather than propagated.
            var pair = TestMeasurement
                .MeasurePairAsync(
                    new TestMeasurement.Target(methodInfo, args, name),
                    referenceTarget,
                    instance,
                    runSpec,
                    MeasurementOptionsBuilder.LaunchCount(_attribute),
                    context.CancellationToken,
                    PerformanceGate.NeedsCalibration(_attribute))
                .GetAwaiter().GetResult();

            var measured = pair.Candidate;
            var result = measured.Result;
            var rawSamples = measured.RawSamples;

            if (measured.Refusal is not null)
                context.OutWriter.WriteLine($"NBenchmark: '{name}' measured in the test host - {measured.Refusal}");

            WriteMetrics(context, result);

            var gate = PerformanceGate.Evaluate(
                result, rawSamples, pair.Reference?.Result, pair.Reference?.RawSamples, _attribute,
                PerformanceGate.AllowsInProcessGate(methodInfo), measured.Calibration, pair.PairedRatio);

            var violations = gate.Violations;

            foreach (var note in gate.Notes)
            {
                context.OutWriter.WriteLine(note);
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

    /// <summary>
    ///     Thin wrapper over <see cref="PerformanceGate.Evaluate" />, kept so the gate can be
    ///     exercised without standing up an NUnit test command.
    /// </summary>
    internal static IReadOnlyList<string> ValidateResult(
        BenchmarkResult result, double[] rawSamples,
        BenchmarkResult? refResult, double[]? refSamples,
        IPerformanceThresholds thresholds,
        bool allowInProcessGate = false)
        => PerformanceGate
            .Evaluate(result, rawSamples, refResult, refSamples, thresholds, allowInProcessGate)
            .Violations;

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
