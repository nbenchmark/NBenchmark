using System.Globalization;
using System.Text.RegularExpressions;
using NBenchmark.Engine;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Holds every default <c>--help</c> states to the value a run actually uses.
/// </summary>
/// <remarks>
///     <para>
///         Help text is API, and its defaults drift silently: the number lives in a string literal
///         and the behavior lives in an options record, and until this existed nothing tied the two
///         together. A flag can advertise a 200 ms floor for releases after the record moved to 500
///         without a single test failing.
///     </para>
///     <para>
///         Nothing below retypes a default. Numeric ones are read from the options records; enum
///         ones are put back through <see cref="CliArgs.ParseCore" /> and compared to the record's
///         default value, which also pins the help's spelling to the spelling the parser accepts -
///         <c>per-sample-collect</c>, not <c>PerSampleCollect</c>.
///     </para>
///     <para>
///         The three tests close the loop in both directions: a stated default that disagrees with
///         the record fails, a newly stated default missing from the tables fails, and a default
///         these tables check that the help stopped stating fails. A hand-written list of assertions
///         catches only the first.
///     </para>
/// </remarks>
[Collection("ConsoleCapture")]
public class CliHelpDefaultsTests
{
    /// <summary>
    ///     Flags whose stated default is a number, mapped to the value the run uses.
    /// </summary>
    private static readonly Dictionary<string, double> NumericDefaults = BuildNumericDefaults();

    /// <summary>
    ///     Flags whose stated default is a word, mapped to the check that the word the help prints
    ///     parses back to the value the run uses. <c>ParseCore</c> rather than <c>Enum.Parse</c> on
    ///     purpose: the CLI's spelling is its own, and the help must match the CLI, not the enum.
    /// </summary>
    private static readonly Dictionary<string, Func<string, bool>> EnumDefaults = BuildEnumDefaults();

    private static Dictionary<string, double> BuildNumericDefaults()
    {
        var measurement = MeasurementOptions.Default;
        var tune = AutoTuneOptions.Default;

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["--launch-count"] = LaunchCounts.HarnessDefault,

            ["--confidence"] = measurement.ConfidenceLevel,
            ["--significance-level"] = measurement.SignificanceLevel,
            ["--min-practical-effect"] = MeasurementOptions.DefaultMinimumPracticalEffect,
            ["--min-relative-shift"] = MeasurementOptions.DefaultMinimumRelativeShift,

            ["--ci-target"] = tune.CiTarget,
            ["--min-samples"] = tune.MinSamples,
            ["--max-samples"] = tune.MaxSamples,
            ["--min-warmup-samples"] = tune.MinWarmupSamples,
            ["--max-warmup-samples"] = tune.MaxWarmupSamples,
            ["--max-tuning-time"] = tune.MaxTuningTime.TotalSeconds,
            ["--warmup-budget-fraction"] = tune.WarmupBudgetFraction,
            ["--cap-grace-factor"] = tune.CapGraceFactor,
            ["--min-warmup-time"] = tune.MinWarmupTime.TotalMilliseconds,
            ["--jit-quiet-period"] = tune.JitQuietPeriod.TotalMilliseconds,
            ["--min-measurement-time"] = tune.MinMeasurementTime.TotalMilliseconds,
            ["--drift-tolerance"] = tune.MeasurementDriftTolerance,
            ["--max-drift-restarts"] = tune.MeasurementRestartLimit,
        };
    }

    private static Dictionary<string, Func<string, bool>> BuildEnumDefaults()
    {
        var measurement = MeasurementOptions.Default;
        var tune = AutoTuneOptions.Default;

        return new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal)
        {
            ["--detail"] = word => Parsed(word, "--detail").Detail == ReportContext.Default.Detail,
            ["--order"] = word => Parsed(word, "--order").RunOrder == NBenchmark.RunOrder.Random,
            ["--gc"] = word => Parsed(word, "--gc").GcBehavior == measurement.GcBehavior,
            ["--outlier"] = word => Parsed(word, "--outlier").OutlierMode == measurement.OutlierMode,
            ["--tail-basis"] = word => Parsed(word, "--tail-basis").TailMetricsBasis == measurement.TailMetricsBasis,
            ["--runtime-profile"] = word => Parsed(word, "--runtime-profile").RuntimeProfile == measurement.RuntimeProfile,
            ["--diagnostics"] = word => Parsed(word, "--diagnostics").Diagnostics == measurement.Diagnostics.ToMode(),
            ["--auto-tune-cap-behavior"] = word => Parsed(word, "--auto-tune-cap-behavior").AutoTuneCapBehavior == tune.CapBehavior,
        };
    }

    /// <summary>
    ///     The args the parser produces for <paramref name="flag" /> set to <paramref name="word" />,
    ///     asserting first that the word is one the parser accepts at all.
    /// </summary>
    private static CliArgs Parsed(string word, string flag)
    {
        var (args, errors) = CliArgs.ParseCore([flag, word]);

        Assert.True(errors.Count == 0, $"--help states '{word}' as the default for {flag}, which the parser rejects: {string.Join("; ", errors)}");

        return args;
    }

    /// <summary>
    ///     Both topics as one string, since the auto-tune defaults print only under
    ///     <c>--help advanced</c>.
    /// </summary>
    private static string HelpText()
        => Capture(() => CliArgs.PrintHelp()) + Capture(() => CliArgs.PrintHelp("advanced"));

    [Fact]
    public void EveryStatedDefaultMatchesTheRunsOwnValue()
    {
        var stated = StatedDefaults(HelpText());
        var wrong = new List<string>();

        foreach (var (flag, said) in stated)
        {
            if (NumericDefaults.TryGetValue(flag, out var expected))
            {
                var parsed = double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);

                if (!parsed || Math.Abs(number - expected) > 1e-9)
                    wrong.Add($"{flag}: help says '{said}', the run uses '{expected.ToString(CultureInfo.InvariantCulture)}'");
            }
            else if (EnumDefaults.TryGetValue(flag, out var matches) && !matches(said))
            {
                wrong.Add($"{flag}: help says '{said}', which is not the value the run uses");
            }
        }

        Assert.Empty(wrong);
    }

    /// <summary>
    ///     A flag whose help states a default must be in one of the tables above, so a newly
    ///     documented default cannot arrive unchecked.
    /// </summary>
    [Fact]
    public void EveryStatedDefaultIsChecked()
    {
        var unchecked_ = StatedDefaults(HelpText())
            .Keys
            .Where(flag => !NumericDefaults.ContainsKey(flag) && !EnumDefaults.ContainsKey(flag))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unchecked_);
    }

    /// <summary>
    ///     And a default these tables check must still be stated in the help, so deleting it from
    ///     the help text cannot quietly retire the assertion.
    /// </summary>
    [Fact]
    public void EveryCheckedDefaultIsStated()
    {
        var stated = StatedDefaults(HelpText());

        var missing = NumericDefaults.Keys
            .Concat(EnumDefaults.Keys)
            .Where(flag => !stated.ContainsKey(flag))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    ///     Every flag whose description states a concrete default, mapped to the word or number it
    ///     states.
    /// </summary>
    /// <remarks>
    ///     Two shapes are in use and both are read: the parenthesised <c>"(default: 0.95)"</c> -
    ///     which may carry a trailing clause, as <c>"(default: 500; 0 disables)"</c> does - and the
    ///     inline <c>"steady-state (default)"</c> the enum-valued flags use, where the value is the
    ///     word before the marker. <c>"(default: auto, CI-driven)"</c> states a policy rather than a
    ///     value and is skipped, as is <c>"(default: auto-calibrated)"</c>: there is no number in the
///     records to hold either to.
    /// </remarks>
    private static Dictionary<string, string> StatedDefaults(string help)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in help.Split('\n'))
        {
            var flag = Regex.Match(line, @"^\s{2}(--[a-z][a-z-]*)");

            if (!flag.Success)
                continue;

            var parenthesised = Regex.Match(line, @"\((?:harness )?default: ([^;),]+)");
            var value = parenthesised.Success
                ? parenthesised.Groups[1].Value.Trim()
                : Regex.Match(line, @"([A-Za-z0-9.\-]+) \(default\)") is { Success: true } inline
                    ? inline.Groups[1].Value.Trim()
                    : null;

            // "auto", "auto-calibrated", "auto, CI-driven": a policy, not a value, with nothing
            // in the records to hold it to.
            if (value is null || value.StartsWith("auto", StringComparison.Ordinal))
                continue;

            found[flag.Groups[1].Value] = value;
        }

        return found;
    }

    private static string Capture(Action action)
    {
        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);

        try
        {
            action();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }
}
