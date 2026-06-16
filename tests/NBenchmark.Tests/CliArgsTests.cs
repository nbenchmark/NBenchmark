using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

[Collection("ConsoleCapture")]
public class CliArgsTests
{
    [Fact]
    public void Parse_EmptyArgs_ReturnsDefaults()
    {
        var result = CliArgs.Parse([]);

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
        Assert.Empty(result.CliReporters);
    }

    [Fact]
    public void Parse_Help_SetsShowHelp()
    {
        var result = CliArgs.Parse(["--help"]);
        Assert.True(result.ShowHelp);
    }

    [Fact]
    public void Parse_Help_ShortForm_SetsShowHelp()
    {
        var result = CliArgs.Parse(["-h"]);
        Assert.True(result.ShowHelp);
    }

    [Fact]
    public void Parse_List_SetsListOnly()
    {
        var result = CliArgs.Parse(["--list"]);
        Assert.True(result.ListOnly);
    }

    [Fact]
    public void Parse_DryRun_SetsDryRun()
    {
        var result = CliArgs.Parse(["--dry-run"]);
        Assert.True(result.DryRun);
    }

    [Fact]
    public void Parse_Filter_SetsFilter()
    {
        var result = CliArgs.Parse(["--filter", "Foo*"]);
        Assert.Equal("Foo*", result.Filter);
    }

    [Fact]
    public void Parse_Seed_Valid_SetsSeed()
    {
        var result = CliArgs.Parse(["--seed", "42"]);
        Assert.Equal(42, result.Seed);
    }

    [Fact]
    public void Parse_Seed_Invalid_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => result = CliArgs.Parse(["--seed", "abc"]));

            Assert.NotNull(result);
            Assert.Null(result!.Seed);
            Assert.Contains("Invalid --seed", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_Order_Declaration_SetsRunOrder()
    {
        var result = CliArgs.Parse(["--order", "declaration"]);
        Assert.Equal(RunOrder.Declaration, result.RunOrder);
    }

    [Fact]
    public void Parse_Order_Random_SetsRunOrder()
    {
        var result = CliArgs.Parse(["--order", "random"]);
        Assert.Equal(RunOrder.Random, result.RunOrder);
    }

    [Fact]
    public void Parse_Order_Invalid_PrintsErrorAndSetsExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => result = CliArgs.Parse(["--order", "bogus"]));

            Assert.NotNull(result);
            Assert.Null(result!.RunOrder);
            Assert.Contains("Invalid --order", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_Iterations_Valid_SetsIterations()
    {
        var result = CliArgs.Parse(["--iterations", "100"]);
        Assert.Equal(100, result.Iterations);
    }

    [Fact]
    public void Parse_Iterations_Invalid_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => result = CliArgs.Parse(["--iterations", "-1"]));

            Assert.NotNull(result);
            Assert.Null(result!.Iterations);
            Assert.Contains("Invalid --iterations", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_Warmup_Valid_SetsWarmup()
    {
        var result = CliArgs.Parse(["--warmup", "10"]);
        Assert.Equal(10, result.WarmupIterations);
    }

    [Fact]
    public void Parse_Warmup_Invalid_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => result = CliArgs.Parse(["--warmup", "-1"]));

            Assert.NotNull(result);
            Assert.Null(result!.WarmupIterations);
            Assert.Contains("Invalid --warmup", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_Confidence_Valid_SetsConfidence()
    {
        var result = CliArgs.Parse(["--confidence", "0.99"]);
        Assert.Equal(0.99, result.ConfidenceLevel);
    }

    [Fact]
    public void Parse_Confidence_OutOfRange_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => result = CliArgs.Parse(["--confidence", "1.5"]));

            Assert.NotNull(result);
            Assert.Null(result!.ConfidenceLevel);
            Assert.Contains("Invalid --confidence", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Theory]
    [InlineData("none", OutlierMode.None)]
    [InlineData("top5", OutlierMode.RemoveTop5Percent)]
    [InlineData("both5", OutlierMode.RemoveTopAndBottom5Percent)]
    [InlineData("iqr", OutlierMode.IqrFence)]
    [InlineData("mad", OutlierMode.MedianAbsoluteDeviation)]
    public void Parse_Outlier_Valid_SetsOutlierMode(string value, OutlierMode expected)
    {
        var result = CliArgs.Parse(["--outlier", value]);
        Assert.Equal(expected, result.OutlierMode);
    }

    [Fact]
    public void Parse_Outlier_Default_IsNull()
    {
        var result = CliArgs.Parse([]);
        Assert.Null(result.OutlierMode);
    }

    [Fact]
    public void Parse_Outlier_Invalid_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => result = CliArgs.Parse(["--outlier", "bogus"]));

            Assert.NotNull(result);
            Assert.Null(result!.OutlierMode);
            Assert.Contains("Invalid --outlier", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_MissingValue_PrintsErrorAndSetsExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = CaptureConsoleError(() => CliArgs.Parse(["--filter"]));

            Assert.Contains("Missing value", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_ThresholdPct_Valid_SetsThresholdPct()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => { result = CliArgs.Parse(["--threshold-pct", "5"]); });

            Assert.NotNull(result);
            Assert.Equal(5, result!.ThresholdPct);
            Assert.Empty(stderr);
            Assert.Equal(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_ThresholdPct_NonNumeric_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => { result = CliArgs.Parse(["--threshold-pct", "abc"]); });

            Assert.NotNull(result);
            Assert.Null(result!.ThresholdPct);
            Assert.Contains("Invalid --threshold-pct", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_ThresholdPct_Negative_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => { result = CliArgs.Parse(["--threshold-pct", "-5"]); });

            Assert.NotNull(result);
            Assert.Null(result!.ThresholdPct);
            Assert.Contains("Invalid --threshold-pct", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_ThresholdPct_Zero_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => { result = CliArgs.Parse(["--threshold-pct", "0"]); });

            Assert.NotNull(result);
            Assert.Null(result!.ThresholdPct);
            Assert.Contains("Invalid --threshold-pct", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_Output_SetsOutputDir()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "output-test");
        Directory.CreateDirectory(dir);

        try
        {
            var result = CliArgs.Parse(["--output", dir]);
            Assert.Equal(dir, result.OutputDir);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Parse_Reporter_Known_AddsReporter()
    {
        var result = CliArgs.Parse(["--reporter", "json"]);
        var reporter = Assert.Single(result.CliReporters);
        Assert.Equal("json", reporter.Name);
    }

    [Fact]
    public void Parse_Reporter_Unknown_PrintsHint()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => { result = CliArgs.Parse(["--reporter", "unknown-reporter"]); });

            Assert.NotNull(result);
            Assert.Empty(result!.CliReporters);
            Assert.Contains("unknown-reporter", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_MultipleReporters_AddsAll()
    {
        var result = CliArgs.Parse(["--reporter", "json", "--reporter", "csv"]);
        Assert.Equal(2, result.CliReporters.Count);
        Assert.Contains(result.CliReporters, r => r.Name == "json");
        Assert.Contains(result.CliReporters, r => r.Name == "csv");
    }

    [Fact]
    public void Parse_MultipleFlags_AllApplied()
    {
        var result = CliArgs.Parse(["--filter", "Foo*", "--iterations", "50", "--reporter", "json"]);

        Assert.Equal("Foo*", result.Filter);
        Assert.Equal(50, result.Iterations);
        var reporter = Assert.Single(result.CliReporters);
        Assert.Equal("json", reporter.Name);
    }

    [Fact]
    public void Parse_UnknownFlag_PrintsErrorAndSetsExitCode()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = CaptureConsoleError(() => CliArgs.Parse(["--bogus-flag"]));

            Assert.Contains("Unknown flag", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Theory]
    [InlineData("realistic", MeasurementProfile.Realistic)]
    [InlineData("independent", MeasurementProfile.Independent)]
    public void Parse_Profile_Valid_SetsProfile(string value, MeasurementProfile expected)
    {
        var result = CliArgs.Parse(["--profile", value]);
        Assert.Equal(expected, result.Profile);
    }

    [Fact]
    public void Parse_Profile_Default_IsNull()
    {
        var result = CliArgs.Parse([]);
        Assert.Null(result.Profile);
    }

    [Fact]
    public void Parse_Profile_Invalid_PrintsError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            CliArgs? result = null;
            var stderr = CaptureConsoleError(() => result = CliArgs.Parse(["--profile", "bogus"]));

            Assert.NotNull(result);
            Assert.Null(result!.Profile);
            Assert.Contains("Invalid --profile", stderr);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = prev;
        }
    }

    [Fact]
    public void Parse_ForceGc_SetsForceGc()
    {
        var result = CliArgs.Parse(["--force-gc"]);
        Assert.True(result.ForceGc);
    }

    [Fact]
    public void Parse_ForceGc_Default_IsNull()
    {
        var result = CliArgs.Parse([]);
        Assert.Null(result.ForceGc);
    }

    [Fact]
    public void Parse_NoAllocations_SetsNoAllocations()
    {
        var result = CliArgs.Parse(["--no-allocations"]);
        Assert.True(result.NoAllocations);
    }

    [Fact]
    public void Parse_NoAllocations_Default_IsNull()
    {
        var result = CliArgs.Parse([]);
        Assert.Null(result.NoAllocations);
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
