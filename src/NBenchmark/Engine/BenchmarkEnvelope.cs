using NBenchmark.Discovery;

namespace NBenchmark.Engine;

internal sealed record BenchmarkEnvelope(
    string Name,
    string ClassName,
    string? Description,
    bool IsBaseline,
    IReadOnlyList<string> Categories,
    Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> RunAsync)
{
    public string OriginalName { get; init; } = Name;
    public IReadOnlyList<BenchmarkParameter> ParameterSet { get; init; } = [];

    /// <summary>
    ///     The user's own delegate, kept alongside the wrapper that invokes it, so the body can be
    ///     <i>addressed</i> for measurement in another process.
    ///     <para>
    ///         <see cref="RunAsync" /> is a closure this library built; its metadata token identifies
    ///         NBenchmark's own wrapper, not the user's code. Only the raw delegate points at the
    ///         method the developer actually wrote. <c>null</c> when the body is not a simple
    ///         delegate - a parameterized benchmark closes over its parameter values, which exist
    ///         only in this process - and a null here means "cannot be isolated", never "guess".
    ///     </para>
    /// </summary>
    public Delegate? Body { get; init; }

    /// <summary>
    ///     Per-iteration setup and teardown, if the caller supplied any. They are live delegates in
    ///     this process, so their presence is what stops a body from being isolatable on its own.
    /// </summary>
    public bool HasIterationHooks { get; init; }

    public static BenchmarkEnvelope FromDiscovered(
        BenchmarkMethodDefinition method,
        string className,
        Func<object> instanceFactory)
    {
        var name = $"{className}.{method.DisplayName}";
        var description = method.Attribute.Description;
        var isBaseline = method.IsBaseline;
        var categories = method.Categories;
        var attributeIterations = method.Attribute.Iterations;
        var attributeWarmupIterations = method.Attribute.WarmupIterations;
        var attributeLaunchCount = method.Attribute.LaunchCount;
        var hasIterationsOverride = method.Attribute.HasIterationsOverride;
        var hasWarmupIterationsOverride = method.Attribute.HasWarmupIterationsOverride;
        var hasLaunchCountOverride = method.Attribute.HasLaunchCountOverride;
        var iterationSetupDel = method.IterationSetupDelegate;
        var iterationTeardownDel = method.IterationTeardownDelegate;
        var asyncDel = method.AsyncDelegate;
        var syncDel = method.SyncDelegate;
        var resultConsumer = method.ResultConsumer;

        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync = (spec, ct) =>
        {
            var instance = instanceFactory();
            var specWithOverride = spec;

            if (spec.Options.Iterations is not 0)
            {
                var overriddenOptions = spec.Options;

                if (hasIterationsOverride)
                    overriddenOptions = overriddenOptions with { Iterations = attributeIterations };

                if (hasWarmupIterationsOverride)
                    overriddenOptions = overriddenOptions with { WarmupIterations = attributeWarmupIterations };

                if (hasLaunchCountOverride && spec.Options.LaunchCount == 1)
                    overriddenOptions = overriddenOptions with { LaunchCount = attributeLaunchCount };

                specWithOverride = spec with { Options = overriddenOptions };
            }

            var specWithIter = (iterationSetupDel, iterationTeardownDel) switch
            {
                (null, null) => specWithOverride,
                (not null, null) => specWithOverride with
                {
                    IterationSetup = () => iterationSetupDel(instance),
                },
                (null, not null) => specWithOverride with
                {
                    IterationTeardown = () => iterationTeardownDel(instance),
                },
                (not null, not null) => specWithOverride with
                {
                    IterationSetup = () => iterationSetupDel(instance),
                    IterationTeardown = () => iterationTeardownDel(instance),
                },
            };

            var specWithClass = specWithIter with { ClassName = className };
            return ExecuteAsync(name, asyncDel, syncDel, resultConsumer, instance, specWithClass, ct);
        };

        var parameterSet = method.ParameterSet;

        return new BenchmarkEnvelope(name, className, description, isBaseline, categories, runAsync)
        {
            ParameterSet = parameterSet,
        };
    }

    private static Task<MeasurementOutcome> ExecuteAsync(
        string name,
        Func<object, Task>? asyncDel,
        Func<object, object?>? syncDel,
        Action<Task>? resultConsumer,
        object instance,
        RunSpec spec,
        CancellationToken ct)
    {
        if (asyncDel is not null)
        {
            if (resultConsumer is not null)
            {
                var returningBody = async () =>
                {
                    var task = asyncDel(instance);
                    await task.ConfigureAwait(false);
                    resultConsumer(task);
                };

                return BenchmarkRunner.Instance.RunAsync(name, returningBody, spec, ct);
            }

            var voidBody = async () =>
            {
                var task = asyncDel(instance);
                await task.ConfigureAwait(false);
            };

            return BenchmarkRunner.Instance.RunAsync(name, voidBody, spec, ct);
        }

        var sd = syncDel!;
        var consumer = BenchmarkRunner.GetResultConsumer<object?>();
        var syncBody = () => consumer(sd(instance));
        return Task.FromResult(BenchmarkRunner.Instance.Run(name, syncBody, spec, ct));
    }
}

internal readonly record struct InstanceHandle(object Instance, Action Teardown)
{
    public static InstanceHandle NoTeardown(object instance) => new(instance, () => { });
}
