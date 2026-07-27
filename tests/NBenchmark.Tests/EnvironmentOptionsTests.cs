using System.Diagnostics;
using Xunit;

namespace NBenchmark.Tests;

public class EnvironmentOptionsTests
{
    [Fact]
    public void Default_Has_All_Null_Fields()
    {
        var opts = EnvironmentOptions.Default;

        Assert.Null(opts.CpuAffinity);
        Assert.Null(opts.ProcessPriority);
        Assert.False(opts.DedicatedHostGuidance);
        Assert.False(opts.SuppressBuildConfigurationWarning);
    }

    [Fact]
    public void ParseCpuAffinity_Null_Returns_Null() => Assert.Null(EnvironmentOptions.ParseCpuAffinity(null));

    [Fact]
    public void ParseCpuAffinity_Blank_Returns_Null() => Assert.Null(EnvironmentOptions.ParseCpuAffinity("   "));

    [Fact]
    public void ParseCpuAffinity_Single_Parses() => Assert.Equal([0], EnvironmentOptions.ParseCpuAffinity("0"));

    [Fact]
    public void ParseCpuAffinity_List_Parses()
    {
        // Pick distinct indices that are valid on any host. CI runners can have as few
        // as 1 logical core, so cap the list at the host's core count.
        var coreCount = Environment.ProcessorCount;
        var indices = coreCount >= 3 ? "0,1,2" : coreCount == 2 ? "0,1" : "0";

        var expected = indices.Split(',').Select(int.Parse).ToArray();

        Assert.Equal(expected, EnvironmentOptions.ParseCpuAffinity(indices));
    }

    [Fact]
    public void ParseCpuAffinity_Trims_Whitespace() => Assert.Equal([0, 1], EnvironmentOptions.ParseCpuAffinity(" 0 , 1 "));

    [Fact]
    public void ParseCpuAffinity_Negative_Throws() => Assert.Throws<FormatException>(() => EnvironmentOptions.ParseCpuAffinity("-1"));

    [Fact]
    public void ParseCpuAffinity_NonNumeric_Throws() => Assert.Throws<FormatException>(() => EnvironmentOptions.ParseCpuAffinity("foo"));

    [Fact]
    public void ParseCpuAffinity_OutOfRange_Throws()
    {
        var tooHigh = Environment.ProcessorCount;

        var ex = Assert.Throws<FormatException>(() => EnvironmentOptions.ParseCpuAffinity(tooHigh.ToString()));
        Assert.Contains($"CPU index {tooHigh}", ex.Message);
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void ParseCpuAffinity_Duplicates_Are_Deduplicated()
    {
        // The parser does not reject duplicates; BuildAffinityMask ORs them to the same
        // bit. Assert the parser preserves the input order (mask construction handles
        // deduplication).
        Assert.Equal([2, 2], EnvironmentOptions.ParseCpuAffinity("2,2"));
    }

    [Fact]
    public void MeasurementOptions_Default_Environment_IsNull() => Assert.Null(MeasurementOptions.Default.Environment);

    [Fact]
    public void MeasurementOptions_Environment_Can_Be_Set()
    {
        var opts = MeasurementOptions.Default with
        {
            Environment = new EnvironmentOptions { CpuAffinity = [0], ProcessPriority = ProcessPriorityClass.High },
        };

        Assert.NotNull(opts.Environment);
        Assert.Equal([0], opts.Environment.CpuAffinity);
        Assert.Equal(ProcessPriorityClass.High, opts.Environment.ProcessPriority);
    }
}

public class BenchmarkSuiteEnvironmentFluentTests
{
    [Fact]
    public void WithHardwareAffinity_Sets_CpuAffinity()
    {
        var suite = new BenchmarkSuite("test");

        suite.WithHardwareAffinity(2, 3);

        Assert.NotNull(suite.Environment);
        Assert.Equal([2, 3], suite.Environment!.CpuAffinity);
    }

    [Fact]
    public void WithProcessPriority_Sets_Priority()
    {
        var suite = new BenchmarkSuite("test");

        suite.WithProcessPriority(ProcessPriorityClass.High);

        Assert.NotNull(suite.Environment);
        Assert.Equal(ProcessPriorityClass.High, suite.Environment!.ProcessPriority);
    }

    [Fact]
    public void WithDedicatedHostGuidance_Sets_Flag()
    {
        var suite = new BenchmarkSuite("test");

        suite.WithDedicatedHostGuidance();

        Assert.NotNull(suite.Environment);
        Assert.True(suite.Environment!.DedicatedHostGuidance);
    }

    [Fact]
    public void WithSuppressBuildConfigurationWarning_Sets_Flag()
    {
        var suite = new BenchmarkSuite("test");

        suite.WithSuppressBuildConfigurationWarning();

        Assert.NotNull(suite.Environment);
        Assert.True(suite.Environment!.SuppressBuildConfigurationWarning);
    }

    [Fact]
    public void WithHardwareAffinity_Preserves_Other_Environment_Fields()
    {
        var suite = new BenchmarkSuite("test");

        suite.WithProcessPriority(ProcessPriorityClass.High).WithHardwareAffinity(0);

        Assert.NotNull(suite.Environment);
        Assert.Equal([0], suite.Environment!.CpuAffinity);
        Assert.Equal(ProcessPriorityClass.High, suite.Environment!.ProcessPriority);
    }

    [Fact]
    public void WithHardwareAffinity_Null_Throws()
    {
        var suite = new BenchmarkSuite("test");

        Assert.Throws<ArgumentNullException>(() => suite.WithHardwareAffinity(null!));
    }
}
