using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Mechanical guards on the frame contract, so the wire's safety does not depend on somebody
///     remembering to write a test.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="FrameChannel.SerializerOptions" /> justifies reflection-based JSON on the
///         grounds that <see cref="FrameChannelTests" /> round-trips every frame kind. That claim was
///         false when it was written - four of the eleven kinds had no test at all, including
///         <see cref="ObserverPhasePayload" />, whose <c>Succeeded</c> member defaults to <c>true</c>
///         and would therefore report a crashed suite as a clean one. Hand-written coverage of a
///         closed set is exactly the kind of invariant that holds until the next member is added.
///     </para>
///     <para>
///         So these tests derive their coverage from the types instead of restating it: a new
///         <see cref="WorkerFrameKind" />, a new payload slot, or a new property on an existing
///         payload fails here rather than shipping as a member that serializes and does not come back.
///     </para>
/// </remarks>
public sealed class WorkerFrameContractTests
{
    /// <summary>
    ///     <see cref="WorkerFrameKind.Shutdown" /> is the one kind with no payload - it is the whole
    ///     message - so it is excluded from the slot and factory pairings and covered on its own.
    /// </summary>
    private const WorkerFrameKind PayloadlessKind = WorkerFrameKind.Shutdown;

    private static IEnumerable<WorkerFrameKind> PayloadKinds
        => Enum.GetValues<WorkerFrameKind>().Where(k => k != PayloadlessKind);

    /// <summary>The ten payload slots on the envelope, i.e. everything except the discriminator.</summary>
    private static PropertyInfo[] PayloadSlots
        => typeof(WorkerFrame)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != nameof(WorkerFrame.Kind))
            .ToArray();

    /// <remarks>
    ///     Named rather than typed, because <see cref="WorkerFrameKind" /> is internal and a public test
    ///     signature cannot take it. The cases still come from <c>Enum.GetNames</c>, so a new kind gets
    ///     its own test case without this list being touched - which is the entire point.
    /// </remarks>
    public static TheoryData<string> AllKindNames
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var name in Enum.GetNames<WorkerFrameKind>())
                data.Add(name);

            return data;
        }
    }

    /// <summary>
    ///     Every kind can actually be built and carried. A kind added to the enum without a slot and a
    ///     factory is unreachable, and nothing else in the build says so.
    /// </summary>
    [Fact]
    public void EveryFrameKind_HasAPayloadSlotAndAFactory()
    {
        foreach (var kind in PayloadKinds)
        {
            var slot = typeof(WorkerFrame).GetProperty(kind.ToString());

            Assert.True(
                slot is not null,
                $"{nameof(WorkerFrameKind)}.{kind} has no matching payload property on "
                + $"{nameof(WorkerFrame)}. Add one, or exclude the kind deliberately.");

            Assert.True(
                FactoryFor(slot!.PropertyType) is not null,
                $"{nameof(WorkerFrame)} has no Of({slot.PropertyType.Name}) factory, so a "
                + $"{kind} frame can only be built by hand - which is how a payload ends up in the "
                + "wrong slot with the right Kind.");
        }
    }

    /// <summary>
    ///     The reverse direction, so a slot added without a kind fails too. Checked as a set rather
    ///     than per-slot because a duplicated or misnamed slot shows up as a count mismatch.
    /// </summary>
    [Fact]
    public void EveryPayloadSlot_HasAMatchingFrameKindAndFactory()
    {
        var slotNames = PayloadSlots.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
        var kindNames = PayloadKinds.Select(k => k.ToString()).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(kindNames, slotNames);

        var factoryTypes = typeof(WorkerFrame)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Of")
            .Select(m => m.GetParameters().Single().ParameterType)
            .ToArray();

        Assert.Equal(PayloadSlots.Length, factoryTypes.Length);
        Assert.Equal(PayloadSlots.Length, factoryTypes.Distinct().Count());
    }

    /// <summary>
    ///     Each slot is suppressed when null, so a frame carries only its own payload. This is done
    ///     per-property rather than with a global omit-nulls policy on purpose - the global form
    ///     produces JSON that will not deserialize, because <see cref="BenchmarkResult" /> declares its
    ///     allocation columns <c>required</c> <i>and</i> nullable. A new slot missing the attribute
    ///     quietly reverses that reasoning.
    /// </summary>
    [Fact]
    public void EveryPayloadSlot_IsSuppressedWhenNull()
    {
        foreach (var slot in PayloadSlots)
        {
            var ignore = slot.GetCustomAttribute<JsonIgnoreAttribute>();

            Assert.True(
                ignore is { Condition: JsonIgnoreCondition.WhenWritingNull },
                $"{nameof(WorkerFrame)}.{slot.Name} needs "
                + "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] so an unused slot is "
                + "not written. Do not reach for a global omit-nulls policy instead - it breaks "
                + "required nullable members inside BenchmarkResult.");
        }
    }

    /// <summary>
    ///     A frame built through its factory puts the payload in exactly one slot. Ten near-identical
    ///     two-line factories are precisely where a copy-paste lands a payload in the wrong slot while
    ///     still setting the right <see cref="WorkerFrame.Kind" />, which no other check would catch.
    /// </summary>
    [Fact]
    public void EveryFrameKind_PutsItsPayloadInExactlyOneSlot()
    {
        foreach (var kind in PayloadKinds)
        {
            var frame = BuildFrame(kind);

            Assert.Equal(kind, frame.Kind);

            var occupied = PayloadSlots.Where(p => p.GetValue(frame) is not null).Select(p => p.Name);

            Assert.Equal([kind.ToString()], occupied);
        }
    }

    [Fact]
    public void Shutdown_CarriesNoPayload()
    {
        var frame = WorkerFrame.Shutdown();

        Assert.Equal(WorkerFrameKind.Shutdown, frame.Kind);
        Assert.All(PayloadSlots, slot => Assert.Null(slot.GetValue(frame)));
    }

    /// <summary>
    ///     The guard that does the real work: build each frame with <b>every</b> property set to a
    ///     non-default value, send it over a real pipe, and require the two sides to serialize
    ///     identically.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Comparing the JSON projections rather than the objects is deliberate. These payloads
    ///         carry <c>double[]</c> and <c>IReadOnlyList&lt;T&gt;</c>, which a record's synthesised
    ///         <c>Equals</c> compares <i>by reference</i> - so <c>Assert.Equal(sent, received)</c> fails
    ///         on correct code. The projection catches what matters instead: a member that comes back as
    ///         its default re-serializes differently, which is the one failure mode source generation
    ///         would not have caught either. It correctly ignores the strategy instances that are
    ///         <c>[JsonIgnore]</c>d on purpose, since those are absent on both sides.
    ///     </para>
    ///     <para>
    ///         Non-default values are the whole point. A populator that left members at their defaults
    ///         would round-trip a dropped member successfully.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllKindNames))]
    public async Task EveryFrameKind_RoundTripsWithEveryPropertyPopulated(string kindName)
    {
        var kind = Enum.Parse<WorkerFrameKind>(kindName);

        var sent = kind == PayloadlessKind ? WorkerFrame.Shutdown() : BuildFrame(kind);

        var (left, right, cleanup) = FramePipePair.Create();
        using var _ = cleanup;

        await left.WriteAsync(sent, CancellationToken.None);

        var received = await right.ReadAsync(CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal(kind, received.Kind);

        Assert.Equal(
            JsonSerializer.Serialize(sent, FrameChannel.SerializerOptions),
            JsonSerializer.Serialize(received, FrameChannel.SerializerOptions));
    }

    private static WorkerFrame BuildFrame(WorkerFrameKind kind)
    {
        var slot = typeof(WorkerFrame).GetProperty(kind.ToString())
                   ?? throw new InvalidOperationException($"No payload slot for {kind}.");

        var payload = Populate(slot.PropertyType, salt: (int)kind + 1, depth: 0);

        var factory = FactoryFor(slot.PropertyType)
                      ?? throw new InvalidOperationException($"No Of() factory for {slot.PropertyType}.");

        return (WorkerFrame)factory.Invoke(null, [payload])!;
    }

    private static MethodInfo? FactoryFor(Type payloadType)
        => typeof(WorkerFrame)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == "Of"
                && m.GetParameters() is [var p]
                && p.ParameterType == payloadType);

    /// <summary>
    ///     Hand-built instances for the two types whose graphs reach well past the wire. Walking
    ///     <see cref="MeasurementOptions" /> or <see cref="BenchmarkResult" /> reflectively would drag
    ///     in runtime profiles, environment maps, percentile lists and two deliberately-ignored
    ///     interface members - a maintenance tax that fails for reasons unrelated to the protocol. Both
    ///     already have dedicated fidelity coverage in <see cref="FrameChannelTests" />.
    /// </summary>
    private static object? Seeded(Type type)
    {
        if (type == typeof(MeasurementOptions))
        {
            return MeasurementOptions.Default with
            {
                Iterations = 123,
                WarmupIterations = 7,
                OpsPerSample = 64,
                MaxRawSamples = 512,
                RuntimeProfile = RuntimeProfile.ServerGc,
            };
        }

        if (type == typeof(BenchmarkResult))
        {
            return new BenchmarkResult
            {
                Name = "Bench.Body",
                Mean = 12.5,
                Median = 12.0,
                Percentiles = [new PercentileEntry(0.5, 12.0)],
                Min = 10,
                Max = 30,
                StandardDeviation = 1.25,
                Q1 = 11,
                Q3 = 13,
                InterquartileRange = 2,
                OutliersRemoved = 3,
                N = 4096,
                Skewness = 0.1,
                Kurtosis = 0.2,
                Mad = 0.5,
                AllocMedian = 24,
                AllocP95 = 24,
                AllocMax = 24,
                RuntimeProfileName = "steady-state",
                RuntimeKnobs = "tiered=off pgo=off r2r=off",
            };
        }

        return null;
    }

    /// <summary>
    ///     Builds an instance of a wire type with every property set to a value distinct from its
    ///     type's default, so a member that fails to cross shows up as a difference rather than as a
    ///     plausible default.
    /// </summary>
    /// <remarks>
    ///     A type this does not know how to fill fails the test by design: a property nothing can
    ///     populate is a property nothing proves crosses the boundary. Add it to
    ///     <see cref="Seeded" /> or teach the populator, rather than letting it pass unexamined.
    /// </remarks>
    private static object Populate(Type type, int salt, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type);

        if (underlying is not null)
            return Populate(underlying, salt, depth);

        if (Seeded(type) is { } seeded)
            return seeded;

        if (type.IsEnum)
            return FirstNonDefaultEnumValue(type);

        if (type == typeof(bool))
            return true;

        if (type == typeof(string))
            return $"s{salt}";

        if (type == typeof(int))
            return salt;

        if (type == typeof(byte))
            return (byte)(salt % 251);

        if (type == typeof(long))
            return (long)salt;

        if (type == typeof(double))
            return salt + 0.5;

        if (type == typeof(Guid))
            return new Guid(salt, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]);

        if (type == typeof(TimeSpan))
            return TimeSpan.FromMilliseconds(salt);

        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            var array = Array.CreateInstance(element, 2);

            array.SetValue(Populate(element, salt, depth + 1), 0);
            array.SetValue(Populate(element, salt + 1, depth + 1), 1);

            return array;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = type.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;

            list.Add(Populate(element, salt, depth + 1));
            list.Add(Populate(element, salt + 1, depth + 1));

            return list;
        }

        if (IsWireRecord(type))
            return PopulateRecord(type, salt, depth);

        Assert.Fail(
            $"The frame graph reached '{type.FullName}', which the contract populator does not know "
            + "how to build. Seed it in Seeded(), or extend Populate() - a wire member nothing can "
            + "populate is a member nothing proves survives the round trip.");

        return null!;
    }

    /// <summary>
    ///     Whether this is one of the protocol's own record types, which are safe to walk. Scoped by
    ///     namespace so the populator cannot wander out of the wire and into the engine.
    /// </summary>
    private static bool IsWireRecord(Type type)
        => type.Namespace == typeof(WorkerFrame).Namespace && !type.IsEnum;

    /// <summary>
    ///     Whether this member is a wire record or a collection of them - the two shapes that can
    ///     recurse.
    /// </summary>
    private static bool ReachesWireRecord(Type type)
        => IsWireRecord(type)
           || (type.IsGenericType
               && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
               && IsWireRecord(type.GetGenericArguments()[0]));

    private static object PopulateRecord(Type type, int salt, int depth)
    {
        // Self-referential shapes exist on the wire - BodyRef carries its own setup, teardown and
        // state factory - so recursion is bounded and the leaf leaves the optional links null.
        const int maxDepth = 3;

        var constructor = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = constructor.GetParameters();

        var instance = constructor.Invoke(
            parameters.Select((p, i) => Populate(p.ParameterType, salt + i, depth + 1)).ToArray());

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetSetMethod(nonPublic: true) is null)
                continue;

            // Already supplied positionally; setting it again would be harmless but pointless.
            if (parameters.Any(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var isOptionalLink = Nullable.GetUnderlyingType(property.PropertyType) is null
                                 && !property.PropertyType.IsValueType;

            // A *collection* of wire records is as self-referential as a direct link, and the guard
            // used to miss it: CapturedField.Nested is IReadOnlyList<CapturedField>, whose own type
            // lives in System.Collections.Generic, so IsWireRecord said no and each level populated two
            // more children forever. That overflowed the stack, which aborts the whole run rather than
            // failing one test - so the recursion bound has to see through the collection.
            if (depth >= maxDepth && isOptionalLink && ReachesWireRecord(property.PropertyType))
                continue;

            property.SetValue(instance, Populate(property.PropertyType, salt + property.Name.Length, depth + 1));
        }

        return instance;
    }

    /// <summary>
    ///     The first member with a non-zero value, so a dropped enum member arrives as <c>0</c> and
    ///     reads as a difference. An enum whose only member is the default cannot demonstrate that.
    /// </summary>
    private static object FirstNonDefaultEnumValue(Type type)
    {
        foreach (var value in Enum.GetValues(type))
        {
            if (Convert.ToInt64(value) != 0)
                return value;
        }

        Assert.Fail($"Enum '{type.Name}' has no non-zero member, so a dropped value would be invisible.");

        return null!;
    }
}
