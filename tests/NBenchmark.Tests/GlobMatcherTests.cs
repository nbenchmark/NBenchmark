using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class GlobMatcherTests
{
    [Fact]
    public void Match_Star_Alone_Matches_Any()
    {
        Assert.True(GlobMatcher.Match("*", ""));
        Assert.True(GlobMatcher.Match("*", "anything"));
        Assert.True(GlobMatcher.Match("*", "MyBench.Contains"));
    }

    [Fact]
    public void Match_Suffix_Star_Matches_Anything_Starting_With_Prefix()
    {
        Assert.True(GlobMatcher.Match("String*", "StringConcat"));
        Assert.True(GlobMatcher.Match("String*", "String"));
        Assert.True(GlobMatcher.Match("String*", "stringlower"));
        Assert.False(GlobMatcher.Match("String*", "MyStringConcat"));
    }

    [Fact]
    public void Match_Prefix_Star_Matches_Anything_Ending_With_Suffix()
    {
        Assert.True(GlobMatcher.Match("*Contains", "MyBench.Contains"));
        Assert.True(GlobMatcher.Match("*Contains", "Contains"));
        Assert.False(GlobMatcher.Match("*Contains", "MyBench.ContainsMore"));
    }

    [Fact]
    public void Match_Middle_Star_Matches_Prefix_And_Suffix_Anywhere()
    {
        Assert.True(GlobMatcher.Match("My*.Contains", "MyBench.Contains"));
        Assert.True(GlobMatcher.Match("My*Contains", "MyBenchStuffContains"));
        Assert.False(GlobMatcher.Match("My*.Contains", "OtherBench.Contains"));
        Assert.False(GlobMatcher.Match("My*.Contains", "MyBench.Other"));
    }

    [Fact]
    public void Match_Exact_Matches_Exact()
    {
        Assert.True(GlobMatcher.Match("Foo", "Foo"));
        Assert.False(GlobMatcher.Match("Foo", "Bar"));
        Assert.False(GlobMatcher.Match("Foo", "FooBar"));
        Assert.False(GlobMatcher.Match("Foo", "BarFoo"));
    }

    [Fact]
    public void Match_Is_Case_Insensitive()
    {
        Assert.True(GlobMatcher.Match("string*", "StringConcat"));
        Assert.True(GlobMatcher.Match("STRING*", "stringConcat"));
        Assert.True(GlobMatcher.Match("My*.contains", "MyBench.Contains"));
    }
}
