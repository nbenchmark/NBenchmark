namespace NBenchmark;

/// <summary>
///     Marks a <b>static, parameterless</b> method returning a <see cref="BenchmarkSuite" /> as a
///     benchmark plan: a factory that a measurement worker can invoke to build the suite in the
///     process that will measure it.
/// </summary>
/// <remarks>
///     <para>
///         A suite is full of live delegates - benchmark bodies, setup and teardown, custom outlier
///         detectors and significance tests, instance factories - and none of that can be serialized
///         across a process boundary honestly. Sending the <i>factory's address</i> instead means the
///         worker builds all of it as real objects in its own process, so nothing has to be
///         serializable and nothing can be lost in translation.
///     </para>
///     <para>
///         The method must be <c>static</c> and must not capture anything from an enclosing scope,
///         because a captured value exists only in the process that created it. A factory that
///         captures is refused rather than approximated: reconstructing captured state was measured
///         to return plausible, silently wrong numbers rather than failing.
///     </para>
///     <para>
///         Passing the method group directly to
///         <see cref="BenchmarkSuite.RunPlanAsync(Func{BenchmarkSuite}, CancellationToken)" /> works
///         without this attribute - the method group is itself the address. The attribute marks the
///         method as a plan so <see cref="BenchmarkSuite.RunPlansAsync(Type, CancellationToken)" />
///         can find it, and so the intent is visible at the declaration.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     await BenchmarkSuite.RunPlansAsync&lt;Plans&gt;();
///
///     static class Plans
///     {
///         [BenchmarkPlan]
///         public static BenchmarkSuite Serialization() =>
///             new BenchmarkSuite("serialization")
///                 .Add("json", () =&gt; SerializeJson())
///                 .Add("msgpack", () =&gt; SerializeMsgPack())
///                 .WithBaseline("json");
///     }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BenchmarkPlanAttribute : Attribute
{
}
