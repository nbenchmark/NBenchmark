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

    /// <summary>
    ///     Fails the test when the measurement was taken in the test host rather than in a worker
    ///     process. See <see cref="IPerformanceThresholds.RequireIsolation" />.
    /// </summary>
    public bool RequireIsolation { get; init; }

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
        string? refusal = null;
        CalibrationResult? calibration = null;

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

                var refName = $"{testMethod.TestClassName}.{ReferenceMethod}";

                // MSTest's Execute is synchronous, so the async path is blocked on here.
                var reference = TestMeasurement
                    .MeasureAsync(refMethodInfo, instance, refArgs, refName, runSpec, CancellationToken.None)
                    .GetAwaiter().GetResult();

                refResult = reference.Result;
                refSamples = reference.RawSamples;
            }

            var measured = TestMeasurement
                .MeasureAsync(methodInfo, instance, args, name, runSpec, CancellationToken.None,
                    PerformanceGate.NeedsCalibration(this))
                .GetAwaiter().GetResult();

            result = measured.Result;
            rawSamples = measured.RawSamples;
            refusal = measured.Refusal;
            calibration = measured.Calibration;
        }
        catch (Exception ex)
        {
            return Task.FromResult<TestResult[]>([CreateErrorResult(ex)]);
        }

        var gate = PerformanceGate.Evaluate(
            result, rawSamples, refResult, refSamples, this,
            PerformanceGate.AllowsInProcessGate(methodInfo), calibration);

        var violations = gate.Violations;
        var notes = new List<string>();

        if (refusal is not null)
            notes.Add($"NBenchmark: '{name}' measured in the test host - {refusal}");

        notes.AddRange(gate.Notes);

        var testResult = new TestResult
        {
            DisplayName = testMethod.TestMethodName,
            Duration = result.TotalDuration,
            LogOutput = notes.Count == 0
                ? MetricsFormatter.Format(result)
                : MetricsFormatter.Format(result) + Environment.NewLine + string.Join(Environment.NewLine, notes),
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

    /// <summary>
    ///     Thin wrapper over <see cref="PerformanceGate.Evaluate" />, kept so the gate can be
    ///     exercised without standing up an MSTest test method.
    /// </summary>
    internal static IReadOnlyList<string> ValidateResult(
        BenchmarkResult result, double[] rawSamples,
        BenchmarkResult? refResult, double[]? refSamples,
        IPerformanceThresholds thresholds,
        bool allowInProcessGate = false)
        => PerformanceGate
            .Evaluate(result, rawSamples, refResult, refSamples, thresholds, allowInProcessGate)
            .Violations;

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
