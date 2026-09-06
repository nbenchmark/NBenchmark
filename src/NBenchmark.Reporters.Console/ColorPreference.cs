using Spectre.Console;

namespace NBenchmark.Reporters.Console;

/// <summary>
///     Honors the no-color convention (https://no-color.org) for everything this package prints.
/// </summary>
/// <remarks>
///     <para>
///         Spectre.Console does not read <c>NO_COLOR</c> itself, so the switch is applied here, at the
///         two points this package writes to the terminal: the progress renderer and the report. The
///         markup is still parsed - only the colour is dropped - so the tables keep their layout.
///     </para>
///     <para>
///         Read from the environment on each call rather than cached, because <c>--no-color</c> sets the
///         variable while parsing arguments, which happens after this assembly is loaded.
///     </para>
/// </remarks>
internal static class ColorPreference
{
    internal static void Apply(bool noColor = false)
    {
        if (!noColor && !NoColorRequestedByEnvironment())
            return;

        AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
    }

    /// <summary>
    ///     Any non-empty <c>NO_COLOR</c> value counts, which is what the convention specifies -
    ///     <c>NO_COLOR=0</c> still means no colour.
    /// </summary>
    private static bool NoColorRequestedByEnvironment()
        => !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("NO_COLOR"));
}
