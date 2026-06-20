namespace NBenchmark.Engine;

internal static class RuntimeMonikerExtensions
{
    public static string ToTargetFramework(this RuntimeMoniker moniker) => moniker switch
    {
        RuntimeMoniker.Net8 => "net8.0",
        RuntimeMoniker.Net9 => "net9.0",
        RuntimeMoniker.Net10 => "net10.0",
        _ => throw new ArgumentOutOfRangeException(nameof(moniker), moniker, "Unknown runtime moniker."),
    };
}
