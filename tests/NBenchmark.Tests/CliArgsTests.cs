using System.Diagnostics;
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
        Assert.Empty(result.ObserverNames);
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
    public void ParseCore_CrossClass_SetsCrossClass()
    {
        var (result, errors) = CliArgs.ParseCore(["--cross-class"]);
        Assert.Empty(errors);
        Assert.True(result.CrossClass);
    }

    [Fact]
    public void ParseCore_NoCrossClass_DefaultsFalse()
    {
        var (result, errors) = CliArgs.ParseCore([]);
        Assert.Empty(errors);
        Assert.False(result.CrossClass);
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

    [Theory]
    [InlineData("none", DiagnosticsMode.None)]
    [InlineData("gc", DiagnosticsMode.Gc)]
    [InlineData("gcandcpu", DiagnosticsMode.GcAndCpu)]
    [InlineData("all", DiagnosticsMode.All)]
    public void ParseCore_Diagnostics_Valid_SetsDiagnosticsMode(string value, DiagnosticsMode expected)
    {
        var (result, errors) = CliArgs.ParseCore(["--diagnostics", value]);

        Assert.Empty(errors);
        Assert.Equal(expected, result.Diagnostics);
    }

    [Fact]
    public void ParseCore_Diagnostics_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--diagnostics", "bogus"]);

        Assert.Null(result.Diagnostics);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --diagnostics value", error);
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
    public void ParseCore_Observer_Known_AddsName()
    {
        var (result, errors) = CliArgs.ParseCore(["--observer", "live"]);
        Assert.Empty(errors);
        var name = Assert.Single(result.ObserverNames);
        Assert.Equal("live", name);
    }

    [Fact]
    public void ParseCore_Observer_Unknown_StoresName()
    {
        // ParseCore is a pure parser; observer-name validation happens in Parse() against
        // ObserverRegistry, so an unknown name is stored without error here (mirrors reporter).
        var (result, errors) = CliArgs.ParseCore(["--observer", "unknown-observer"]);

        Assert.Empty(errors);
        Assert.Equal(["unknown-observer"], result.ObserverNames);
    }

    [Fact]
    public void ParseCore_MultipleObservers_AddsAll()
    {
        var (result, errors) = CliArgs.ParseCore(["--observer", "live", "--observer", "logging"]);
        Assert.Empty(errors);
        Assert.Equal(2, result.ObserverNames.Count);
        Assert.Contains("live", result.ObserverNames);
        Assert.Contains("logging", result.ObserverNames);
    }

    [Fact]
    public void ParseCore_Observer_And_Reporter_Can_Coexist()
    {
        var (result, errors) = CliArgs.ParseCore(["--reporter", "json", "--observer", "live"]);
        Assert.Empty(errors);
        Assert.Equal(["json"], result.ReporterNames);
        Assert.Equal(["live"], result.ObserverNames);
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

    [Theory]
    [InlineData("warn", AutoTuneCapBehavior.Warn)]
    [InlineData("error", AutoTuneCapBehavior.Error)]
    [InlineData("WARN", AutoTuneCapBehavior.Warn)]
    [InlineData("Error", AutoTuneCapBehavior.Error)]
    public void ParseCore_AutoTuneCapBehavior_Valid_SetsBehavior(string value, AutoTuneCapBehavior expected)
    {
        var (result, errors) = CliArgs.ParseCore(["--autotune-cap-behavior", value]);
        Assert.Empty(errors);
        Assert.Equal(expected, result.AutoTuneCapBehavior);
    }

    [Fact]
    public void ParseCore_AutoTuneCapBehavior_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.AutoTuneCapBehavior);
    }

    [Fact]
    public void ParseCore_AutoTuneCapBehavior_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--autotune-cap-behavior", "bogus"]);

        Assert.Null(result.AutoTuneCapBehavior);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --autotune-cap-behavior", error);
    }

    [Fact]
    public void ParseCore_AutoTuneCapBehavior_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--autotune-cap-behavior"]);

        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    // ---------- Grace-ceiling and budget-share CLI flags ----------

    [Theory]
    [InlineData("0.4", 0.4)]
    [InlineData("0.5", 0.5)]
    [InlineData("1", 1.0)]
    [InlineData("1.0", 1.0)]
    public void ParseCore_WarmupBudgetFraction_Valid_SetsFraction(string value, double expected)
    {
        var (result, _) = CliArgs.ParseCore(["--warmup-budget-fraction", value]);
        Assert.Equal(expected, result.WarmupBudgetFraction);
    }

    [Fact]
    public void ParseCore_WarmupBudgetFraction_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.WarmupBudgetFraction);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("bogus")]
    public void ParseCore_WarmupBudgetFraction_Invalid_ReturnsError(string value)
    {
        var (result, errors) = CliArgs.ParseCore(["--warmup-budget-fraction", value]);

        Assert.Null(result.WarmupBudgetFraction);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --warmup-budget-fraction", error);
    }

    [Fact]
    public void ParseCore_WarmupBudgetFraction_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--warmup-budget-fraction"]);

        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1", 1.0)]
    [InlineData("1.0", 1.0)]
    [InlineData("2", 2.0)]
    [InlineData("3.0", 3.0)]
    public void ParseCore_CapGraceFactor_Valid_SetsFactor(string value, double expected)
    {
        var (result, _) = CliArgs.ParseCore(["--cap-grace-factor", value]);
        Assert.Equal(expected, result.CapGraceFactor);
    }

    [Fact]
    public void ParseCore_CapGraceFactor_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.CapGraceFactor);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("-1")]
    [InlineData("bogus")]
    public void ParseCore_CapGraceFactor_Invalid_ReturnsError(string value)
    {
        var (result, errors) = CliArgs.ParseCore(["--cap-grace-factor", value]);

        Assert.Null(result.CapGraceFactor);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --cap-grace-factor", error);
    }

    [Fact]
    public void ParseCore_CapGraceFactor_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--cap-grace-factor"]);

        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    // ---------- Warmup time floor and JIT-quiescence gate CLI flags ----------

    [Theory]
    [InlineData("100", 100)]
    [InlineData("25", 25)]
    [InlineData("0", 0)]
    [InlineData("250.5", 250.5)]
    public void ParseCore_MinWarmupTime_Valid_SetsMilliseconds(string value, double expectedMs)
    {
        var (result, _) = CliArgs.ParseCore(["--min-warmup-time", value]);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), result.MinWarmupTime);
    }

    [Fact]
    public void ParseCore_MinWarmupTime_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.MinWarmupTime);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("bogus")]
    public void ParseCore_MinWarmupTime_Invalid_ReturnsError(string value)
    {
        var (result, errors) = CliArgs.ParseCore(["--min-warmup-time", value]);

        Assert.Null(result.MinWarmupTime);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid --min-warmup-time", error);
    }

    [Fact]
    public void ParseCore_MinWarmupTime_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--min-warmup-time"]);

        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    [Fact]
    public void ParseCore_NoJitQuiescence_SetsFlag()
    {
        var (result, errors) = CliArgs.ParseCore(["--no-jit-quiescence"]);

        Assert.True(result.NoJitQuiescence);
        Assert.Empty(errors);
    }

    [Fact]
    public void ParseCore_NoJitQuiescence_Default_IsFalse()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.False(result.NoJitQuiescence);
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
    public void Parse_ObserverUnknown_WritesError()
    {
        var prev = Environment.ExitCode;
        Environment.ExitCode = 0;

        try
        {
            var stderr = CaptureConsoleError(() => CliArgs.Parse(["--observer", "unknown-observer"]));

            Assert.Contains("Unknown observer", stderr);
            Assert.Contains("unknown-observer", stderr);
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
    public void ParseCore_LaunchCount_ParsesValidValue()
    {
        var (result, errors) = CliArgs.ParseCore(["--launch-count", "5"]);

        Assert.Empty(errors);
        Assert.Equal(5, result.LaunchCount);
    }

    [Fact]
    public void ParseCore_LaunchCount_MinimumValue()
    {
        var (result, errors) = CliArgs.ParseCore(["--launch-count", "1"]);

        Assert.Empty(errors);
        Assert.Equal(1, result.LaunchCount);
    }

    [Fact]
    public void ParseCore_LaunchCount_MaximumValue()
    {
        var (result, errors) = CliArgs.ParseCore(["--launch-count", "100"]);

        Assert.Empty(errors);
        Assert.Equal(100, result.LaunchCount);
    }

    [Fact]
    public void ParseCore_LaunchCount_OutOfRange_Errors()
    {
        var (result, errors) = CliArgs.ParseCore(["--launch-count", "101"]);

        Assert.NotEmpty(errors);
        Assert.Contains("launch-count", errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.LaunchCount);
    }

    [Fact]
    public void ParseCore_LaunchCount_Zero_Errors()
    {
        var (result, errors) = CliArgs.ParseCore(["--launch-count", "0"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.LaunchCount);
    }

    [Fact]
    public void ParseCore_LaunchCount_NonNumeric_Errors()
    {
        var (result, errors) = CliArgs.ParseCore(["--launch-count", "abc"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.LaunchCount);
    }

    [Fact]
    public void ParseCore_Percentiles_Parses_Valid_List()
    {
        var (result, errors) = CliArgs.ParseCore(["--percentiles", "0.95,0.99,1.0"]);

        Assert.Empty(errors);
        Assert.Equal([0.95, 0.99, 1.0], result.ReportedPercentiles);
    }

    [Fact]
    public void ParseCore_Percentiles_Invalid_Value_Returns_Error()
    {
        var (result, errors) = CliArgs.ParseCore(["--percentiles", "0.95,1.5"]);

        Assert.Null(result.ReportedPercentiles);
        var error = Assert.Single(errors);
        Assert.Contains("Invalid percentile value", error);
    }

    [Fact]
    public void ParseCore_NoHistogram_Sets_NoHistogram_Flag()
    {
        var (result, errors) = CliArgs.ParseCore(["--no-histogram"]);

        Assert.Empty(errors);
        Assert.True(result.NoHistogram);
    }

    [Fact]
    public void ParseCore_CpuAffinity_Single_Core_Parses()
    {
        var (result, errors) = CliArgs.ParseCore(["--cpu-affinity", "0"]);

        Assert.Empty(errors);
        Assert.Equal([0], result.CpuAffinity);
    }

    [Fact]
    public void ParseCore_CpuAffinity_Multiple_Cores_Parses()
    {
        var (result, errors) = CliArgs.ParseCore(["--cpu-affinity", "2,3"]);

        Assert.Empty(errors);
        Assert.Equal([2, 3], result.CpuAffinity);
    }

    [Fact]
    public void ParseCore_CpuAffinity_Trims_Whitespace()
    {
        var (result, errors) = CliArgs.ParseCore(["--cpu-affinity", " 0 , 1 "]);

        Assert.Empty(errors);
        Assert.Equal([0, 1], result.CpuAffinity);
    }

    [Fact]
    public void ParseCore_CpuAffinity_Negative_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--cpu-affinity", "-1"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.CpuAffinity);
        Assert.Contains("--cpu-affinity", errors[0]);
    }

    [Fact]
    public void ParseCore_CpuAffinity_NonNumeric_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--cpu-affinity", "foo"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.CpuAffinity);
    }

    [Fact]
    public void ParseCore_CpuAffinity_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--cpu-affinity"]);

        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    [Fact]
    public void ParseCore_CpuAffinity_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.CpuAffinity);
    }

    [Theory]
    [InlineData("normal", ProcessPriorityClass.Normal)]
    [InlineData("high", ProcessPriorityClass.High)]
    [InlineData("idle", ProcessPriorityClass.Idle)]
    [InlineData("belownormal", ProcessPriorityClass.BelowNormal)]
    [InlineData("abovenormal", ProcessPriorityClass.AboveNormal)]
    [InlineData("realtime", ProcessPriorityClass.RealTime)]
    [InlineData("HIGH", ProcessPriorityClass.High)]
    public void ParseCore_Priority_Valid_SetsPriority(string value, ProcessPriorityClass expected)
    {
        var (result, errors) = CliArgs.ParseCore(["--priority", value]);

        Assert.Empty(errors);
        Assert.Equal(expected, result.ProcessPriority);
    }

    [Fact]
    public void ParseCore_Priority_Invalid_ReturnsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--priority", "bogus"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.ProcessPriority);
        Assert.Contains("--priority", errors[0]);
    }

    [Fact]
    public void ParseCore_Priority_MissingValue_ReturnsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--priority"]);

        var error = Assert.Single(errors);
        Assert.Contains("Missing value", error);
    }

    [Fact]
    public void ParseCore_Priority_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.ProcessPriority);
    }

    [Fact]
    public void ParseCore_DedicatedHostGuidance_Sets_Flag()
    {
        var (result, errors) = CliArgs.ParseCore(["--dedicated-host-guidance"]);

        Assert.Empty(errors);
        Assert.True(result.DedicatedHostGuidance);
    }

    [Fact]
    public void ParseCore_DedicatedHostGuidance_Default_IsFalse()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.False(result.DedicatedHostGuidance);
    }

    [Fact]
    public void ParseCore_OtlpEndpoint_HttpUrl_SetsValue()
    {
        var (result, errors) = CliArgs.ParseCore(["--otlp-endpoint", "http://localhost:4317"]);

        Assert.Empty(errors);
        Assert.Equal("http://localhost:4317", result.OtlpEndpoint);
    }

    [Fact]
    public void ParseCore_OtlpEndpoint_HttpsUrl_SetsValue()
    {
        var (result, errors) = CliArgs.ParseCore(["--otlp-endpoint", "https://collector.example.com:4318"]);

        Assert.Empty(errors);
        Assert.Equal("https://collector.example.com:4318", result.OtlpEndpoint);
    }

    [Fact]
    public void ParseCore_OtlpEndpoint_Default_IsNull()
    {
        var (result, _) = CliArgs.ParseCore([]);
        Assert.Null(result.OtlpEndpoint);
    }

    [Fact]
    public void ParseCore_OtlpEndpoint_InvalidUrl_AddsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--otlp-endpoint", "not-a-url"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.OtlpEndpoint);
        Assert.Contains(errors, e => e.Contains("--otlp-endpoint") && e.Contains("http://"));
    }

    [Fact]
    public void ParseCore_OtlpEndpoint_RelativeUrl_AddsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--otlp-endpoint", "/path"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.OtlpEndpoint);
    }

    [Fact]
    public void ParseCore_OtlpEndpoint_FtpScheme_AddsError()
    {
        var (result, errors) = CliArgs.ParseCore(["--otlp-endpoint", "ftp://localhost:4317"]);

        Assert.NotEmpty(errors);
        Assert.Null(result.OtlpEndpoint);
    }

    [Fact]
    public void ParseCore_OtlpEndpoint_MissingValue_AddsError()
    {
        var (_, errors) = CliArgs.ParseCore(["--otlp-endpoint"]);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Missing value") && e.Contains("--otlp-endpoint"));
    }

    [Fact]
    public void PrintHelp_WritesUsageToStdout()
    {
        var stdout = CaptureConsoleOutput(() => CliArgs.PrintHelp());

        Assert.Contains("Usage:", stdout);
        Assert.Contains("--filter", stdout);
        Assert.Contains("--reporter", stdout);
        Assert.Contains("--seed", stdout);
        Assert.Contains("--autotune-cap-behavior", stdout);
        Assert.Contains("--percentiles", stdout);
        Assert.Contains("--no-histogram", stdout);
        Assert.Contains("--cpu-affinity", stdout);
        Assert.Contains("--priority", stdout);
        Assert.Contains("--dedicated-host-guidance", stdout);
        Assert.Contains("--otlp-endpoint", stdout);
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
