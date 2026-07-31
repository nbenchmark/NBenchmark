namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     Accepts a performance gate computed from measurements taken in the test host.
/// </summary>
/// <remarks>
///     <para>
///         This is the single opt-out for host measurement, and it does two things. It permits a
///         <b>ratio</b> gate that would otherwise decline to run, and it waives the
///         <see cref="IPerformanceThresholds.RequireIsolation" /> requirement that otherwise fails a
///         host-measured gate outright. Both are the same judgement - "this test cannot be isolated and
///         I accept a number measured in the host" - so they are the same switch rather than two that
///         can disagree.
///     </para>
///     <para>
///         A ratio gate is only enforced automatically when both sides were measured in worker
///         processes. Measured in the test host, a candidate and its reference share whatever JIT
///         tiering state the preceding tests left behind - and on four benchmark bodies of provably
///         identical cost, that produced a <b>2.80x</b> ratio with a tight confidence interval on
///         each side. A gate reading that number is not being conservative; it is reporting an
///         effect that does not exist, in either direction.
///     </para>
///     <para>
///         Some tests cannot be isolated - their arguments are live objects a worker cannot rebuild,
///         such as an <c>IClassFixture&lt;T&gt;</c>, an injected output helper, or a mock. Applying
///         this attribute says that a noisy ratio is more useful to you than none, and the gate runs
///         with a note on the result saying where it was measured. Prefer making the test
///         isolatable; this is the escape hatch for when you cannot.
///     </para>
///     <para>
///         It does <b>not</b> permit a ratio between one isolated measurement and one host
///         measurement. That comparison is dominated by the difference between the two processes'
///         runtime configuration rather than by the code, and no opt-in makes it mean something.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly)]
public sealed class AllowInProcessGateAttribute : Attribute;
