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
    ///         method the developer actually wrote. <c>null</c> means "cannot be isolated", never
    ///         "guess".
    ///     </para>
    ///     <para>
    ///         For a parameterized benchmark this is the user's <i>typed</i> lambda - the one that still
    ///         takes its parameters - with the values it should be called with in
    ///         <see cref="Arguments" />. That pairing is what makes a parameter sweep isolatable: the
    ///         typed lambda captures nothing and always could be addressed, and what could not cross was
    ///         only the wrapper NBenchmark built to bind the value.
    ///     </para>
    /// </summary>
    public Delegate? Body { get; init; }

    /// <summary>
    ///     Values to call <see cref="Body" /> with, in declaration order. Empty for the parameterless
    ///     bodies that are the common case.
    /// </summary>
    public IReadOnlyList<object?> Arguments { get; init; } = [];

    /// <summary>
    ///     Factories producing <see cref="Body" />'s arguments, aligned with its parameters - a
    ///     <c>null</c> entry means that parameter's value comes from <see cref="Arguments" /> instead.
    ///     <c>null</c> for a benchmark with no prepared state at all.
    ///     <para>
    ///         Each is invoked once per benchmark, immediately before that benchmark's warmup, in
    ///         whichever process measures it. Per benchmark rather than per suite deliberately: a suite
    ///         comparing two sorts over one shared array would have the second measure what the first
    ///         already sorted, which is the order-dependence trap the run-order randomizer exists to
    ///         expose.
    ///     </para>
    ///     <para>
    ///         A list rather than one factory because a body may take more than one prepared value, and
    ///         because a parameter sweep whose values are too complex to encode is the same thing - a
    ///         slot filled by a recipe. Users hand-tupled two values into one to work around the single
    ///         slot.
    ///     </para>
    /// </summary>
    public IReadOnlyList<StateRecipe?>? StateRecipes { get; init; }

    /// <summary>
    ///     Per-iteration setup, if the caller supplied one. Kept as the delegate rather than as a flag
    ///     so it can be <i>addressed</i> like a body: a hook that captures nothing is no less
    ///     reproducible in a worker than the benchmark it wraps, and refusing on the mere presence of
    ///     one made the common shape - <c>setup: () =&gt; Cache.Clear()</c> - cost the whole suite its
    ///     isolation for nothing.
    /// </summary>
    public Delegate? IterationSetup { get; init; }

    /// <inheritdoc cref="IterationSetup" />
    public Delegate? IterationTeardown { get; init; }

    /// <summary>
    ///     Set by <c>BenchmarkSuite.AddInProcess</c>: this benchmark is measured in the coordinator on
    ///     purpose, and the rest of the suite is isolated around it.
    /// </summary>
    /// <remarks>
    ///     A request, not a refusal - so it stamps <see cref="IsolationStatus.InProcessRequested" /> and
    ///     never trips <see cref="MeasurementOptions.Isolation" />. It exists because
    ///     <c>WithIsolation(Isolation.Off)</c> was the only lever and it is all-or-nothing: one body holding a
    ///     live object took every other benchmark in the suite into the host process with it, so the
    ///     price of measuring one un-isolatable thing was every comparison it was part of.
    /// </remarks>
    public bool RunsInProcess { get; init; }

    /// <summary>
    ///     Whether this benchmark carries per-iteration hooks. Derived rather than stored: a stored
    ///     flag can disagree with the delegates it describes, and it did - the parameterized
    ///     registrations never set it, which was invisible only because they carried no addressable
    ///     body either.
    /// </summary>
    public bool HasIterationHooks => IterationSetup is not null || IterationTeardown is not null;

    /// <summary>
    ///     The class identifier used for discovered-benchmark result rows.
    /// </summary>
    /// <remarks>
    ///     A simple type name collides for classes with the same name in different namespaces,
    ///     which then aliases significance/sample keys and class-level partitions. FullName keeps
    ///     those rows distinct while preserving a fallback for the rare runtime type whose
    ///     FullName is null.
    /// </remarks>
    internal static string QualifiedDiscoveredClassName(Type declaringType)
    {
        ArgumentNullException.ThrowIfNull(declaringType);
        return declaringType.FullName ?? declaringType.Name;
    }

    /// <summary>
    ///     The discovered benchmark identifier used for result and sample keys.
    /// </summary>
    internal static string QualifiedDiscoveredBenchmarkName(Type declaringType, string displayName)
    {
        ArgumentNullException.ThrowIfNull(declaringType);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return $"{QualifiedDiscoveredClassName(declaringType)}.{displayName}";
    }

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
