using NBenchmark.Engine;
using System.Diagnostics.CodeAnalysis;

namespace NBenchmark.Workers;

/// <summary>
///     How a measuring process obtains the instances a benchmark class's methods are invoked on.
/// </summary>
/// <remarks>
///     The kind travels with the recipe because the worker cannot infer it. Every one of these is a
///     <c>Func&lt;…&gt;</c> the worker resolves and runs, and two of them return the same type -
///     <see cref="ServiceProvider" /> and <see cref="ScopedServiceProvider" /> are both
///     <c>Func&lt;IServiceProvider&gt;</c> and differ only in what the worker does with the container
///     afterwards. Leaving that to be guessed is how a scoped registration ends up resolved from the
///     root, which throws under <c>ValidateScopes</c> and silently shares state without it.
/// </remarks>
internal enum InstanceSourceKind
{
    /// <summary>
    ///     Construct the declaring type directly. The default, and the only kind needing no recipe.
    /// </summary>
    Constructed = 0,

    /// <summary>
    ///     Resolve each instance from a container built by an addressed
    ///     <c>Func&lt;IServiceProvider&gt;</c>, off the root scope.
    /// </summary>
    ServiceProvider = 1,

    /// <summary>
    ///     As <see cref="ServiceProvider" />, but each instance is resolved from its own
    ///     <c>IServiceScope</c>, disposed when the instance is torn down.
    /// </summary>
    /// <remarks>
    ///     The kind that exists for <c>AddScoped</c> registrations - an EF Core <c>DbContext</c> being
    ///     the case the whole DI package is usually installed for. Without it those benchmarks could
    ///     not be isolated at all: the only scoped API took a live <c>IServiceProvider</c>, which is
    ///     the one thing that cannot cross a process boundary.
    /// </remarks>
    ScopedServiceProvider = 2,

    /// <summary>
    ///     Resolve each instance by invoking an addressed <c>Func&lt;Type, object&gt;</c> with the
    ///     benchmark class as its argument.
    /// </summary>
    InstanceFactory = 3,
}

/// <summary>
///     Where benchmark instances come from, from the coordinator's side: the recipe a worker could
///     follow, and the resolver this process uses when it measures them itself.
/// </summary>
/// <remarks>
///     <para>
///         One object rather than the two loosely-coupled fields this replaced - a
///         <c>Func&lt;Type, InstanceHandle&gt;</c> whose mere presence meant "cannot isolate", and a
///         separate <c>Func&lt;IServiceProvider&gt;</c> whose presence lifted that. Nothing tied them
///         together, so the harness could not tell a scoped provider from a plain one, or an
///         addressable factory from a live closure, and every DI API that was not exactly
///         <c>WithServices(Func&lt;IServiceProvider&gt;)</c> lost the run its isolation.
///     </para>
///     <para>
///         <see cref="Resolve" /> is deliberately a delegate rather than a built object. The host-side
///         container is only needed if this process ends up measuring, so building one at
///         configuration time opened a database and built an EF model in a process that then measured
///         nothing.
///     </para>
/// </remarks>
[RequiresUnreferencedCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
[RequiresDynamicCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
internal sealed record InstanceSource
{
    public required InstanceSourceKind Kind { get; init; }

    /// <summary>
    ///     The recipe a worker runs to reproduce this source, or <c>null</c> when instances come from
    ///     live code in the coordinator that has no addressable counterpart.
    /// </summary>
    public Delegate? Recipe { get; init; }

    /// <summary>
    ///     Resolves an instance in <i>this</i> process, for the in-process path. Called on demand.
    /// </summary>
    public required Func<Type, InstanceHandle> Resolve { get; init; }

    /// <summary>
    ///     Addresses this source for the wire, or <c>null</c> when it has no recipe to address.
    /// </summary>
    /// <param name="receivers">
    ///     The group's receiver table, which anything the factory closes over is transferred into. A
    ///     factory reading a connection string from a local is a recipe with a parameter it did not
    ///     get to declare; the live container it would otherwise have built here is still refused,
    ///     because that is what the faithfulness rule is for.
    /// </param>
    public InstanceSourcePayload? ToPayload(ReceiverTable? receivers = null)
        => Recipe is null || !TryAddress(receivers, out var addressed, out _)
            ? null
            : new InstanceSourcePayload { Kind = Kind, Factory = addressed };

    /// <summary>
    ///     Addresses the recipe, telling the addresser whether the measuring process supplies the
    ///     factory's arguments itself.
    /// </summary>
    /// <remarks>
    ///     An instance factory is a <c>Func&lt;Type, object&gt;</c> called once per instance with the
    ///     benchmark class; every other kind is simply run. The distinction has to be made here rather
    ///     than inferred from the delegate's shape, because "has a parameter nothing supplied a value
    ///     for" is the addresser's refusal for a benchmark body and is exactly the wrong answer here.
    /// </remarks>
    private bool TryAddress(ReceiverTable? receivers, out AddressedFactory addressed, out Refusal refusal)
        => AddressedFactory.TryCreate(
            Recipe!,
            RoleFor(Kind),
            out addressed,
            out refusal,
            argumentsSuppliedAtInvocation: Kind == InstanceSourceKind.InstanceFactory,
            receivers: receivers);

    /// <summary>
    ///     Why this source cannot be reproduced in a worker, or <c>null</c> when it can.
    /// </summary>
    public string? Refusal()
    {
        if (Recipe is null)
        {
            return Kind switch
            {
                InstanceSourceKind.InstanceFactory =>
                    "benchmark instances come from an instance factory this process holds as live code. "
                    + "Pass a static, non-capturing factory instead so a worker can run it itself.",
                _ =>
                    "benchmark instances come from a service provider, which is live code in this "
                    + "process and cannot be reproduced in a worker. Constructing the type directly "
                    + "instead would measure a differently-configured object and report it as though "
                    + "nothing had changed. Pass a factory instead - WithServices(BuildServices) "
                    + "with a static BuildServices - and the worker builds an equivalent container in "
                    + "the process that measures.",
            };
        }

        // A throwaway table: this asks only whether the factory is addressable, and the entries that
        // travel are built into the group's own table when the request is assembled.
        return TryAddress(new ReceiverTable(MeasurementOptions.DefaultMaxTransferredStateBytes), out _, out var refusal)
            ? null
            : $"{RoleFor(Kind)} {refusal.Message}";
    }

    internal static string RoleFor(InstanceSourceKind kind) => kind switch
    {
        InstanceSourceKind.ServiceProvider => "the service provider factory",
        InstanceSourceKind.ScopedServiceProvider => "the scoped service provider factory",
        InstanceSourceKind.InstanceFactory => "the instance factory",
        _ => "the instance source",
    };
}

/// <summary>
///     An <see cref="InstanceSource" /> on the wire: what to run, and what to do with what it returns.
/// </summary>
internal sealed record InstanceSourcePayload
{
    public required InstanceSourceKind Kind { get; init; }

    public required AddressedFactory Factory { get; init; }
}
