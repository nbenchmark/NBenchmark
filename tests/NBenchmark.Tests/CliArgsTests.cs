using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class CliArgsTests
{
    [Fact]
    public void ParseCore_EmptyArgs_ReturnsDefaults()
    {
        var (result, errors) = CliArgs.ParseCore([]);

        Assert.Empty(errors);
        Assert.False(result.ShowHelp);
        Assert.False(result.ListOnly);
        Assert.False(result.DryRun);
        Assert.Null(result.ThresholdPct);
        Assert.Null(result.Filter);
        Assert.Null(result.OutputDir);
        Assert.Null(result.Seed);
        Assert.Null(result.RunOrder);
        Assert.Null(result.Iterations);
        Assert.Null(result.WarmupIterations);
        Assert.Null(result.ConfidenceLevel);
        Assert.Empty(result.ReporterNames);
    }

    [Fact]
    public void ParseCore_Help_SetsShowHelp()
    {
        var (result, errors) = CliArgs.ParseCore(["--help"]);
        Assert.Empty(errors);
        Assert.True(result.ShowHelp);
    }

    [Fact]
    public void ParseCore_Help_ShortForm_SetsShowHelp()
    {
        var (result, errors) = CliArgs.ParseCore(["-h"]);
        Assert.Empty(errors);
        Assert.True(result.ShowHelp);
    }

    [Fact]
    public void ParseCore_List_SetsListOnly()
    {
        var (result, errors) = CliArgs.ParseCore(["--list"]);
        Assert.Empty(errors);
        Assert.True(result.ListOnly);
    }

    [Fact]
    public void ParseCore_DryRun_SetsDryRun()
    {
        var (result, errors) = CliArgs.ParseCore(["--dry-run"]);
        Assert.Empty(errors);
        Assert.True(result.DryRun);
    }

    [Fact]
    public void ParseCore_Filter_SetsFilter()
    {
        var (result, errors) = CliArgs.ParseCore(["--filter", "Foo*"]);
        Assert.Empty(errors);
        Assert.Equal("Foo*", result.Filter);
    }

    [Fact]
    public void ParseCore_Seed_Valid_SetsSeed()
    {
        var (result, errors) = CliArgs.ParseCore(["--seed", "42"]);
        Assert.Empty(errors);
        Assert.Equal(42, result.Seed);
    }

    [Fact]
    public void ParseCore_Seed_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--seed", "abc"]);

        Assert.Null(result.Seed);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --seed", error);
    }

    [Fact]
    public void ParseCore_Order_Declaration_SetsRunOrder()
    {
        var (result, errors) = CliArgs.ParseCore(["--order", "declaration"]);
        Assert.Empty(errors);
        Assert.Equal(RunOrder.Declaration, result.RunOrder);
    }

    [Fact]
    public void ParseCore_Order_Random_SetsRunOrder()
    {
        var (result, errors) = CliArgs.ParseCore(["--order", "random"]);
        Assert.Empty(errors);
        Assert.Equal(RunOrder.Random, result.RunOrder);
    }

    [Fact]
    public void ParseCore_Order_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--order", "bogus"]);

        Assert.Null(result.RunOrder);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --order", error);
    }

    [Fact]
    public void ParseCore_Iterations_Valid_SetsIterations()
    {
        var (result, errors) = CliArgs.ParseCore(["--iterations", "100"]);
        Assert.Empty(errors);
        Assert.Equal(100, result.Iterations);
    }

    [Fact]
    public void ParseCore_Iterations_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--iterations", "-1"]);

        Assert.Null(result.Iterations);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --iterations", error);
    }

    [Fact]
    public void ParseCore_Warmup_Valid_SetsWarmup()
    {
        var (result, errors) = CliArgs.ParseCore(["--warmup", "10"]);
        Assert.Empty(errors);
        Assert.Equal(10, result.WarmupIterations);
    }

    [Fact]
    public void ParseCore_Warmup_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--warmup", "-1"]);

        Assert.Null(result.WarmupIterations);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --warmup", error);
    }

    [Fact]
    public void ParseCore_Confidence_Valid_SetsConfidence()
    {
        var (result, errors) = CliArgs.ParseCore(["--confidence", "0.99"]);
        Assert.Empty(errors);
        Assert.Equal(0.99, result.ConfidenceLevel);
    }

    [Fact]
    public void ParseCore_Confidence_OutOfRange_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--confidence", "1.5"]);

        Assert.Null(result.ConfidenceLevel);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --confidence", error);
    }

    [Theory]
    [InlineData("none", OutlierMode.None)]
    [InlineData("top5", OutlierMode.RemoveTop5Percent)]
    [InlineData("both5", OutlierMode.RemoveTopAndBottom5Percent)]
    [InlineData("iqr", OutlierMode.IqrFence)]
    [InlineData("mad", OutlierMode.MedianAbsoluteDeviation)]
    public void ParseCore_Outlier_Valid_SetsOutlierMode(string value, OutlierMode expected)
    {
        var (result, errors) = CliArgs.ParseCore(["--outlier", value]);
        Assert.Empty(errors);
        Assert.Equal(expected, result.OutlierMode);
    }

    [Fact]
    public void ParseCore_Outlier_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.OutlierMode);
    }

    [Fact]
    public void ParseCore_Outlier_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--outlier", "bogus"]);

        Assert.Null(result.OutlierMode);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --outlier", error);
    }

    [Fact]
    public void ParseCore_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--filter"]);

        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    [Fact]
    public void ParseCore_ThresholdPct_Valid_SetsThresholdPct()
    {
        var (result, errors) = CliArgs.ParseCore(["--threshold-pct", "5"]);

        Assert.Empty(errors);
        Assert.Equal(5, result.ThresholdPct);
    }

    [Fact]
    public void ParseCore_ThresholdPct_NonNumeric_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--threshold-pct", "abc"]);

        Assert.Null(result.ThresholdPct);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --threshold-pct", error);
    }

    [Fact]
    public void ParseCore_ThresholdPct_Negative_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--threshold-pct", "-5"]);

        Assert.Null(result.ThresholdPct);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --threshold-pct", error);
    }

    [Fact]
    public void ParseCore_ThresholdPct_Zero_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--threshold-pct", "0"]);

        Assert.Null(result.ThresholdPct);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --threshold-pct", error);
    }

    [Fact]
    public void ParseCore_Output_SetsOutputDir()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "output-test");
        Directory.CreateDirectory(dir);

        try
        {
            var (result, errors) = CliArgs.ParseCore(["--output", dir]);
            Assert.Empty(errors);
            Assert.Equal(dir, result.OutputDir);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void ParseCore_Reporter_Known_AddsName()
    {
        var (result, errors) = CliArgs.ParseCore(["--reporter", "json"]);
        Assert.Empty(errors);
        var name = Assert.Single(result.ReporterNames);
        Assert.Equal("json", name);
    }

    [Fact]
    public void ParseCore_Reporter_Unknown_StoresName()
    {
        var (result, errors) = CliArgs.ParseCore(["--reporter", "unknown-reporter"]);

        Assert.Empty(errors);
        Assert.Equal(["unknown-reporter"], result.ReporterNames);
    }

    [Fact]
    public void ParseCore_MultipleReporters_AddsAll()
    {
        var (result, errors) = CliArgs.ParseCore(["--reporter", "json", "--reporter", "csv"]);
        Assert.Empty(errors);
        Assert.Equal(2, result.ReporterNames.Count);
        Assert.Contains("json", result.ReporterNames);
        Assert.Contains("csv", result.ReporterNames);
    }

    [Fact]
    public void ParseCore_MultipleFlags_AllApplied()
    {
        var (result, errors) = CliArgs.ParseCore(["--filter", "Foo*", "--iterations", "50", "--reporter", "json"]);
        Assert.Empty(errors);
        Assert.Equal("Foo*", result.Filter);
        Assert.Equal(50, result.Iterations);
        var name = Assert.Single(result.ReporterNames);
        Assert.Equal("json", name);
    }

    [Fact]
    public void ParseCore_Category_AddsToInclude()
    {
        var (result, errors) = CliArgs.ParseCore(["--category", "String"]);
        Assert.Empty(errors);
        Assert.Equal(["String"], result.CategoryFilterInclude);
        Assert.Empty(result.CategoryFilterExclude);
    }

    [Fact]
    public void ParseCore_MultipleCategories_Are_OR()
    {
        var (result, errors) = CliArgs.ParseCore(["--category", "String", "--category", "Memory"]);
        Assert.Empty(errors);
        Assert.Equal(["String", "Memory"], result.CategoryFilterInclude);
    }

    [Fact]
    public void ParseCore_Category_Trims_And_Deduplicates_CaseInsensitive()
    {
        var (result, errors) = CliArgs.ParseCore(["--category", " String ", "--category", "string"]);
        Assert.Empty(errors);
        Assert.Equal(["String"], result.CategoryFilterInclude);
    }

    [Fact]
    public void ParseCore_ExcludeCategory_AddsToExclude()
    {
        var (result, errors) = CliArgs.ParseCore(["--exclude-category", "Slow"]);
        Assert.Empty(errors);
        Assert.Equal(["Slow"], result.CategoryFilterExclude);
    }

    [Fact]
    public void ParseCore_Category_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--category"]);
        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    [Fact]
    public void ParseCore_Category_BlankValue_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--category", "   "]);

        Assert.Empty(result.CategoryFilterInclude);
        var error = Assert.Single(errors);
        Assert.Contains("cannot be blank", error);
    }

    [Fact]
    public void ParseCore_ExcludeCategory_BlankValue_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--exclude-category", ""]);

        Assert.Empty(result.CategoryFilterExclude);
        var error = Assert.Single(errors);
        Assert.Contains("cannot be blank", error);
    }

    [Fact]
    public void ParseCore_UnknownFlag_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--bogus-flag"]);

        var error = Assert.Single(errors);
        Assert.Contains("Unknown flag", error);
    }

    [Theory]
    [InlineData("realistic", MeasurementProfile.Realistic)]
    [InlineData("independent", MeasurementProfile.Independent)]
    public void ParseCore_Profile_Valid_SetsProfile(string value, MeasurementProfile expected)
    {
        var (result, errors) = CliArgs.ParseCore(["--profile", value]);
        Assert.Empty(errors);
        Assert.Equal(expected, result.Profile);
    }

    [Fact]
    public void ParseCore_Profile_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void ParseCore_Profile_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--profile", "bogus"]);

        Assert.Null(result.Profile);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --profile", error);
    }

    [Fact]
    public void ParseCore_ForceGc_SetsForceGc()
    {
        var (result, _) = CliArgs.ParseCore(["--force-gc"]);
        Assert.True(result.ForceGc);
    }

    [Fact]
    public void ParseCore_ForceGc_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.ForceGc);
    }

    [Fact]
    public void ParseCore_NoAllocations_SetsNoAllocations()
    {
        var (result, _) = CliArgs.ParseCore(["--no-allocations"]);
        Assert.True(result.NoAllocations);
    }

    [Fact]
    public void ParseCore_NoAllocations_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.NoAllocations);
    }

    [Fact]
    public void Parse_WritesErrorsToStderr()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = CaptureConsoleError(() => CliArgs.Parse(["--seed", "abc"]));

            Assert.Contains("Invalid --seed", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_ReporterUnknown_WritesError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = CaptureConsoleError(() => CliArgs.Parse(["--reporter", "unknown-reporter"]));

            Assert.Contains("unknown-reporter", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_NoErrors_DoesNotSetExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CaptureConsoleOutput(() => CliArgs.Parse(["--seed", "42"]));
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void PrintHelp_WritesUsageToStdout()
    {
        var stdout = CaptureConsoleOutput(() => CliArgs.PrintHelp());

        Assert.Contains("Usage:", stdout);
        Assert.Contains("--filter", stdout);
        Assert.Contains("--reporter", stdout);
        Assert.Contains("--seed", stdout);
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return sw.ToString();
    }

    private static string CaptureConsoleError(Action action)
    {
        var sw = new StringWriter();
        var original = Console.Error;
        Console.SetError(sw);

        try
        {
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        return sw.ToString();
    }
}
