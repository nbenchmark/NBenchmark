using NBenchmark.Discovery;
using NBenchmark.Workers;

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
        var hasIterationsOverride = method.Attribute.HasIterationsOverride;
        var hasWarmupIterationsOverride = method.Attribute.HasWarmupIterationsOverride;
        var iterationSetupDel = method.IterationSetupDelegate;
        var iterationTeardownDel = method.IterationTeardownDelegate;
        var bodyFactory = method.BodyFactory
                          ?? throw new InvalidOperationException(
                              $"Benchmark '{method.DisplayName}' carries no body factory, so there is "
                              + "nothing to measure. Definitions must come from BenchmarkDiscoverer.");

        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync = (spec, ct) =>
        {
            var instance = instanceFactory();
            var specWithOverride = spec;

            // Only the attribute overrides a measurement pass can act on. [Benchmark(LaunchCount)] is
            // not one of them: a launch is a process, so it is read by whichever coordinator spawns
            // them and never reaches here. It used to be applied to the options anyway, guarded on
            // their launch count already being 1 - which every request path had pinned it to, making
            // a transport detail decide whether a user's attribute took effect.
            if (spec.Options.Iterations is not 0)
            {
                var overriddenOptions = spec.Options;

                if (hasIterationsOverride)
                    overriddenOptions = overriddenOptions with { Iterations = attributeIterations };

                if (hasWarmupIterationsOverride)
                    overriddenOptions = overriddenOptions with { WarmupIterations = attributeWarmupIterations };

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

            // Bound once per run, outside the measured region: the delegate handed to the engine
            // has the benchmark method's own signature and its receiver already captured.
            return DelegateDispatch.MeasureAsync(name, bodyFactory(instance), specWithClass, ct);
        };

        var parameterSet = method.ParameterSet;

        return new BenchmarkEnvelope(name, className, description, isBaseline, categories, runAsync)
        {
            ParameterSet = parameterSet,
        };
    }
}

internal readonly record struct InstanceHandle(object Instance, Action Teardown)
{
    public static InstanceHandle NoTeardown(object instance) => new(instance, () => { });
}
