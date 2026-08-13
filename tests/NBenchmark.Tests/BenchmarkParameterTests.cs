using NBenchmark.Tests.Workers;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     D7: <see cref="BenchmarkParameter.FormatValue" /> and <see cref="BenchmarkParameter.GetKey" />
///     treat a value that arrived from an isolated worker exactly as they would the real one it
///     stands in for - see <c>BenchmarkParameterConverter</c> for how it gets built.
/// </summary>
public sealed class BenchmarkParameterTests
{
    [Fact]
    public void FormatValue_Reads_A_Remote_Values_Display_Text_Directly()
    {
        Assert.Equal("Slow", BenchmarkParameter.FormatValue(RemoteValue("Slow", "NBenchmark.Tests.Workers.ProcessingMode")));
    }

    [Fact]
    public void GetKey_Uses_A_Remote_Values_Own_Display_And_Type_Name()
    {
        var remote = new BenchmarkParameter("mode", RemoteValue("Slow", "NBenchmark.Tests.Workers.ProcessingMode"));
        var real = new BenchmarkParameter("mode", ProcessingMode.Slow);

        Assert.Equal(BenchmarkParameter.GetKey([real]), BenchmarkParameter.GetKey([remote]));
    }

    [Fact]
    public void GetKey_Still_Distinguishes_Different_Remote_Type_Names()
    {
        var asEnum = BenchmarkParameter.GetKey([new BenchmarkParameter("n", RemoteValue("1", "NBenchmark.Tests.Workers.ProcessingMode"))]);
        var asInt = BenchmarkParameter.GetKey([new BenchmarkParameter("n", RemoteValue("1", "System.Int32"))]);

        Assert.NotEqual(asEnum, asInt);
    }

    [Fact]
    public void A_Null_Parameter_Value_Is_Not_Confused_With_A_Remote_One()
    {
        var key = BenchmarkParameter.GetKey([new BenchmarkParameter("n", null)]);

        Assert.Contains("<null>", key);
    }

    /// <summary>
    ///     Built through the wire converter itself rather than a hand-rolled stand-in, so this test
    ///     breaks if the converter's own construction of a remote value ever changes shape.
    /// </summary>
    private static object RemoteValue(string display, string typeName)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new BenchmarkParameterConverter() } };
        var json = System.Text.Json.JsonSerializer.Serialize(
            new { Name = "x", Display = display, ValueTypeName = typeName });

        return System.Text.Json.JsonSerializer.Deserialize<BenchmarkParameter>(json, options)!.Value!;
    }
}
