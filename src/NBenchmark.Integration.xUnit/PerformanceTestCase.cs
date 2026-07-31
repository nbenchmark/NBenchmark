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

                    TestMeasurement.Target? referenceTarget = null;

                    if (!string.IsNullOrWhiteSpace(data.ReferenceMethod))
                    {
                        var (refMethodInfo, refArgs) =
                            ResolveReferenceMethod(methodInfo, data.ReferenceMethod, methodArgs);

                        var refName = $"{TestMethod.TestClass.Class.Name}.{data.ReferenceMethod}";

                        referenceTarget = new TestMeasurement.Target(refMethodInfo, refArgs, refName);
                    }

                    // Both sides in one call, so each replicate measures them co-resident and their
                    // ratio is paired. Measured separately they would be two workers per replicate,
                    // and the ratio would carry both workers' differences instead of neither's.
                    var pair = await TestMeasurement.MeasurePairAsync(
                        new TestMeasurement.Target(methodInfo, methodArgs, name),
                        referenceTarget,
                        instance,
                        runSpec,
                        MeasurementOptionsBuilder.LaunchCount(data),
                        cancellationTokenSource.Token,
                        PerformanceGate.NeedsCalibration(data));

                    var measured = pair.Candidate;
                    var result = measured.Result;
                    var rawSamples = measured.RawSamples;

                    var gate = PerformanceGate.Evaluate(
                        result,
                        rawSamples,
                        pair.Reference?.Result,
                        pair.Reference?.RawSamples,
                        data,
                        PerformanceGate.AllowsInProcessGate(methodInfo),
                        measured.Calibration,
                        pair.PairedRatio);

                    var violations = gate.Violations;
                    var notes = new List<string>();

                    if (measured.Refusal is not null)
                        notes.Add($"NBenchmark: '{name}' measured in the test host - {measured.Refusal}");

                    notes.AddRange(gate.Notes);

                    var output = notes.Count == 0
                        ? MetricsFormatter.Format(result)
                        : MetricsFormatter.Format(result) + Environment.NewLine + string.Join(Environment.NewLine, notes);

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

    /// <summary>
    ///     Thin wrapper over <see cref="PerformanceGate.Evaluate" />, kept so the gate can be
    ///     exercised without standing up an xUnit test case.
    /// </summary>
    internal static IReadOnlyList<string> ValidateResult(
        BenchmarkResult result, double[] rawSamples,
        BenchmarkResult? refResult, double[]? refSamples,
        PerformanceTestData data,
        bool allowInProcessGate = false)
        => PerformanceGate
            .Evaluate(result, rawSamples, refResult, refSamples, data, allowInProcessGate)
            .Violations;

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

}
