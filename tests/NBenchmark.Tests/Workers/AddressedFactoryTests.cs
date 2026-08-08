using NBenchmark.Stats;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The one addressing rule behind every recipe on the wire - prepared state, service provider,
///     the two statistical strategies, and the <c>[BenchmarkPlan]</c> suite factory.
/// </summary>
/// <remarks>
///     These five were five implementations of one idea before, each with its own addressing helper
///     and its own wording for the same four failures. What is pinned here is the shared rule: a
///     factory is addressed by token or by name, never both, and the refusals a caller sees are
///     <see cref="BodyRef" />'s own - because a factory that captures is refused for exactly the
///     reason a capturing body is.
/// </remarks>
public class AddressedFactoryTests
{
    private const string Role = "the test factory";

    private static IOutlierDetector BuildDetector() => OutlierDetectors.IqrFence;

    [Fact]
    public void A_Static_Factory_Is_Addressed_By_Token()
    {
        Assert.True(AddressedFactory.TryCreate(BuildDetector, Role, out var addressed, out _));

        Assert.NotNull(addressed.Body);
        Assert.False(addressed.IsByName);
        Assert.Equal(Role, addressed.Role);
        Assert.True(addressed.IsWellFormed(out _));
    }

    /// <summary>
    ///     The refusal is <see cref="BodyRef" />'s verbatim, so a capturing factory and a capturing
    ///     body explain themselves the same way. Callers prefix it with their own role.
    /// </summary>
    [Fact]
    public void A_Capturing_Factory_Is_Refused_With_The_Body_Rule_Reason()
    {
        var configured = 0.9;

        Func<IOutlierDetector> factory = () => new TopPercentileOutlierDetector(configured);

        Assert.False(AddressedFactory.TryCreate(factory, Role, out _, out var refusal));
        Assert.NotNull(refusal);
        Assert.Contains("captures state", refusal);
    }

    /// <summary>
    ///     The display name is what a worker-side diagnostic names, and it is allowed to differ from
    ///     the role: a prepared-state factory reads as "its prepare delegate" but is addressed under
    ///     the benchmark's own name.
    /// </summary>
    [Fact]
    public void A_Display_Name_May_Differ_From_The_Role()
    {
        Assert.True(AddressedFactory.TryCreate(
            BuildDetector, Role, out var addressed, out _, displayName: "Sort (prepare)"));

        Assert.Equal(Role, addressed.Role);
        Assert.Equal("Sort (prepare)", addressed.Body!.DisplayName);
    }

    [Fact]
    public void A_Named_Address_Carries_The_Declaring_Type_And_Method_But_No_Token()
    {
        Assert.True(AddressedFactory.TryCreateByName(BuildDetector, Role, out var addressed, out _));

        Assert.True(addressed.IsByName);
        Assert.Null(addressed.Body);
        Assert.Equal(nameof(BuildDetector), addressed.MethodName);
        Assert.Equal(typeof(AddressedFactoryTests).FullName, addressed.DeclaringTypeFullName);
        Assert.True(addressed.IsWellFormed(out _));
    }

    /// <summary>
    ///     Name addressing is what a multi-runtime run uses, and a name is all the far build will have
    ///     to find the method with - so an instance method has nothing to be found by.
    /// </summary>
    [Fact]
    public void An_Instance_Method_Cannot_Be_Addressed_By_Name()
    {
        Assert.False(AddressedFactory.TryCreateByName(InstanceFactory, Role, out _, out var refusal));

        Assert.NotNull(refusal);
        Assert.Contains("static", refusal);
    }

    [Fact]
    public void OrNull_Returns_Null_For_No_Factory()
    {
        Assert.Null(AddressedFactory.OrNull(factory: null, Role));
    }

    [Fact]
    public void OrNull_Returns_Null_For_A_Factory_That_Cannot_Be_Addressed()
    {
        var captured = 0.9;

        Assert.Null(AddressedFactory.OrNull(() => new TopPercentileOutlierDetector(captured), Role));
    }

    /// <summary>
    ///     Both modes at once names two methods, and resolving whichever branch is tested first would
    ///     run one of them while the request claimed the other. Checked on receipt rather than trusted
    ///     to hold because both factory methods happen to set one mode.
    /// </summary>
    [Fact]
    public void Carrying_Both_A_Token_And_A_Name_Is_Malformed()
    {
        Assert.True(AddressedFactory.TryCreate(BuildDetector, Role, out var byToken, out _));

        var both = byToken with { DeclaringTypeFullName = "T", MethodName = "M" };

        Assert.False(both.IsWellFormed(out var problem));
        Assert.NotNull(problem);
        Assert.Contains("both a metadata token and a method name", problem);
    }

    [Fact]
    public void Carrying_Neither_A_Token_Nor_A_Name_Is_Malformed()
    {
        var empty = new AddressedFactory { Role = Role };

        Assert.False(empty.IsWellFormed(out var problem));
        Assert.NotNull(problem);
        Assert.Contains("neither", problem);
    }

    [Fact]
    public void A_Method_Name_Without_A_Declaring_Type_Is_Malformed()
    {
        var partial = new AddressedFactory { Role = Role, MethodName = "M" };

        Assert.False(partial.IsWellFormed(out var problem));
        Assert.NotNull(problem);
        Assert.Contains("not the type declaring it", problem);
    }

    [Fact]
    public void A_Declaring_Type_Without_A_Method_Name_Is_Malformed()
    {
        Assert.True(AddressedFactory.TryCreate(BuildDetector, Role, out var byToken, out _));

        var partial = byToken with { DeclaringTypeFullName = "T" };

        Assert.False(partial.IsWellFormed(out var problem));
        Assert.NotNull(problem);
        Assert.Contains("no method name", problem);
    }

    private IOutlierDetector InstanceFactory() => OutlierDetectors.IqrFence;
}
