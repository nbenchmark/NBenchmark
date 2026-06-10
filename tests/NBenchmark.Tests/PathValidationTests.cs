using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class PathValidationTests
{
    [Fact]
    public void ValidateOutputPath_Accepts_Current_Directory()
    {
        var result = PathValidation.ValidateOutputPath(".");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateOutputPath_Accepts_Subdirectory()
    {
        var sub = Path.Combine(Directory.GetCurrentDirectory(), "sub-dir");
        var result = PathValidation.ValidateOutputPath(sub);
        Assert.StartsWith(Directory.GetCurrentDirectory(), result);
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("../../etc")]
    [InlineData("/tmp")]
    public void ValidateOutputPath_Rejects_Path_Traversal(string path) => Assert.Throws<ArgumentException>(() => PathValidation.ValidateOutputPath(path));

    [Fact]
    public void ValidateOutputPath_Accepts_Nested_Subdirectory()
    {
        var nested = Path.Combine(Directory.GetCurrentDirectory(), "a", "b", "c");
        var result = PathValidation.ValidateOutputPath(nested);
        Assert.StartsWith(Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar, result);
    }
}
