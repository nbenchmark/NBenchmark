using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace NBenchmark.Workers;

/// <summary>
///     Gives a process the shared frameworks the assembly under test needs but the process's own
///     <c>runtimeconfig.json</c> does not declare.
/// </summary>
/// <remarks>
///     <para>
///         Both of NBenchmark's launched processes are plain <c>Microsoft.NET.Sdk</c> applications, so
///         each declares one framework: <c>Microsoft.NETCore.App</c>. A benchmark class that lives in a
///         <c>Microsoft.NET.Sdk.Web</c> project - an API measuring its own handlers is the ordinary
///         case - sits in an assembly whose graph reaches <c>Microsoft.AspNetCore.App</c>. Those
///         assemblies are framework-provided, so the target's own <c>deps.json</c> does not carry them
///         and <see cref="System.Runtime.Loader.AssemblyDependencyResolver" /> correctly resolves them
///         to nothing; they are expected on the process's trusted-platform-assembly list instead. In a
///         process started without that framework they are on no list at all, and the load fails with
///         <c>Could not load file or assembly 'Microsoft.Extensions.Hosting.Abstractions'</c> before a
///         single benchmark is discovered. <c>Microsoft.WindowsDesktop.App</c> fails the same way.
///     </para>
///     <para>
///         The framework set is chosen by <c>hostfxr</c> from the runtimeconfig and fixed for the life
///         of the process, so this cannot be repaired from inside it any more than the runtime profile
///         can - it has to be decided before the process starts. So the launch hands <c>dotnet exec</c>
///         a <c>--runtimeconfig</c> of our own: the host's, with the target's extra frameworks added.
///     </para>
///     <para>
///         Two hosts use this. <c>nbworker</c> is launched with one by
///         <see cref="WorkerLauncher" />; the <c>dotnet benchmark</c> tool has the same problem about
///         itself, because it loads the target into its own process to discover benchmarks, and
///         relaunches itself with one.
///     </para>
///     <para>
///         <b>Frameworks only.</b> The host's <c>tfm</c>, <c>rollForward</c> and
///         <c>configProperties</c> are carried over untouched, and the target's <c>configProperties</c>
///         are deliberately dropped. A <see cref="RuntimeProfile" /> exists precisely so a worker's
///         GC flavour and tiering are chosen rather than inherited; importing the target application's
///         <c>System.GC.Server</c> would quietly undo the thing isolation is for. Frameworks are
///         different in kind - they decide whether the assembly can load at all, not how fast it runs.
///     </para>
/// </remarks>
internal static class SharedFrameworkConfig
{
    /// <summary>
    ///     Every replicate of every group asks the same question about the same files, and the answer
    ///     involves reading and hashing all of them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    ///     A runtimeconfig to launch <paramref name="hostAssemblyPath" /> with, or <c>null</c> when its
    ///     own is sufficient.
    /// </summary>
    /// <remarks>
    ///     <c>null</c> is the answer for every target that names no framework the host is missing,
    ///     which is every plain console and test-host target - so the ordinary launch is unchanged,
    ///     down to the command line.
    /// </remarks>
    public static string? ResolveFor(string hostAssemblyPath, string? targetAssemblyPath)
        => string.IsNullOrEmpty(targetAssemblyPath)
            ? null
            : ResolveFor(hostAssemblyPath, [targetAssemblyPath]);

    /// <summary>
    ///     The same question over several targets at once, whose framework requirements are unioned.
    /// </summary>
    /// <remarks>
    ///     The tool takes repeatable <c>--project</c> and <c>--assembly</c> arguments and loads every
    ///     one of them into a single process, so it needs one configuration that satisfies all of them
    ///     rather than the first.
    /// </remarks>
    public static string? ResolveFor(string hostAssemblyPath, IReadOnlyList<string> targetAssemblyPaths)
    {
        if (string.IsNullOrEmpty(hostAssemblyPath) || targetAssemblyPaths.Count == 0)
            return null;

        return Cache.GetOrAdd(
            string.Join('\0', targetAssemblyPaths.Prepend(hostAssemblyPath)),
            _ => Synthesize(hostAssemblyPath, targetAssemblyPaths));
    }

    private static string? Synthesize(string hostAssemblyPath, IReadOnlyList<string> targetAssemblyPaths)
    {
        try
        {
            var hostConfigPath = Path.ChangeExtension(hostAssemblyPath, ".runtimeconfig.json");

            if (!File.Exists(hostConfigPath))
                return null;

            using var hostDocument = JsonDocument.Parse(File.ReadAllBytes(hostConfigPath));

            if (!TryGetRuntimeOptions(hostDocument, out var hostOptions))
                return null;

            var hostFrameworks = ReadFrameworks(hostOptions);

            if (hostFrameworks.Count == 0)
                return null;

            var declared = hostFrameworks
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var extras = new List<Framework>();

            foreach (var targetAssemblyPath in targetAssemblyPaths)
            {
                foreach (var framework in RequiredFrameworks(targetAssemblyPath))
                {
                    if (declared.Add(framework.Name))
                        extras.Add(framework);
                }
            }

            if (extras.Count == 0)
                return null;

            var merged = Render(hostOptions, [.. hostFrameworks, .. extras]);

            return Write(merged);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or NotSupportedException)
        {
            // Falling back to the host's own config reproduces the behaviour that predates this
            // class exactly. A configuration file we cannot parse is not a reason to fail a run that
            // may not have needed us at all.
            return null;
        }
    }

    /// <summary>
    ///     The shared frameworks one target declares, or nothing when it declares none we can act on.
    /// </summary>
    private static List<Framework> RequiredFrameworks(string targetAssemblyPath)
    {
        var configPath = Path.ChangeExtension(targetAssemblyPath, ".runtimeconfig.json");

        if (!File.Exists(configPath))
            return [];

        using var document = JsonDocument.Parse(File.ReadAllBytes(configPath));

        if (!TryGetRuntimeOptions(document, out var options))
            return [];

        // A self-contained target ships its framework inside its own output directory, where its
        // deps.json already resolves it. There is no shared framework to ask the host for, and
        // naming one would be a version the machine may well not have.
        return options.TryGetProperty("includedFrameworks", out _) ? [] : ReadFrameworks(options);
    }

    private static bool TryGetRuntimeOptions(JsonDocument document, out JsonElement runtimeOptions)
    {
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("runtimeOptions", out runtimeOptions)
            && runtimeOptions.ValueKind == JsonValueKind.Object)
            return true;

        runtimeOptions = default;

        return false;
    }

    /// <summary>
    ///     The frameworks a <c>runtimeOptions</c> block declares, in declaration order.
    /// </summary>
    /// <remarks>
    ///     Both shapes are read. The SDK emits the singular <c>framework</c> object when there is
    ///     exactly one - which is what the worker's own config looks like - and the plural
    ///     <c>frameworks</c> array otherwise, which is what makes a web target's config differ.
    /// </remarks>
    private static List<Framework> ReadFrameworks(JsonElement runtimeOptions)
    {
        var frameworks = new List<Framework>(2);

        if (runtimeOptions.TryGetProperty("framework", out var single))
            Append(frameworks, single);

        if (runtimeOptions.TryGetProperty("frameworks", out var many)
            && many.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in many.EnumerateArray())
            {
                Append(frameworks, entry);
            }
        }

        return frameworks;

        static void Append(List<Framework> into, JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || name.GetString() is not { Length: > 0 } resolved)
                return;

            var version = element.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            if (into.Any(f => string.Equals(f.Name, resolved, StringComparison.OrdinalIgnoreCase)))
                return;

            into.Add(new Framework(resolved, version));
        }
    }

    /// <summary>
    ///     The host's own <c>runtimeOptions</c>, verbatim, with the framework declaration replaced by
    ///     the merged set.
    /// </summary>
    /// <remarks>
    ///     Copying every other property rather than naming the ones we know about: <c>rollForward</c>
    ///     and <c>tfm</c> are the two that matter today, but a property added by a future SDK is far
    ///     more likely to be needed than to be harmful, and dropping one silently is the kind of
    ///     failure that presents as a process that will not start.
    /// </remarks>
    private static byte[] Render(JsonElement hostOptions, IReadOnlyList<Framework> frameworks)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("runtimeOptions");
            writer.WriteStartObject();

            foreach (var property in hostOptions.EnumerateObject())
            {
                if (property.NameEquals("framework") || property.NameEquals("frameworks"))
                    continue;

                property.WriteTo(writer);
            }

            writer.WritePropertyName("frameworks");
            writer.WriteStartArray();

            foreach (var framework in frameworks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", framework.Name);

                if (framework.Version is not null)
                    writer.WriteString("version", framework.Version);

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     Writes the merged config to a path derived from its own content, and returns that path.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Content-addressed so repeat runs, replicates and concurrent processes all converge on
    ///         one file instead of littering the temp directory with a copy per launch.
    ///     </para>
    ///     <para>
    ///         Not written beside the host: a global tool's own directory is inside the package cache
    ///         and may not be writable at all, and a file appearing next to <c>nbworker.dll</c> would
    ///         be one more thing the deployment checks have to know is not part of the shipped set.
    ///     </para>
    ///     <para>
    ///         Written to a unique name and moved into place, so a second process reading the path
    ///         while this one writes it sees either the whole file or no file - never a truncated
    ///         config, which <c>hostfxr</c> would reject with an error naming a file the user never
    ///         wrote.
    ///     </para>
    /// </remarks>
    private static string Write(byte[] merged)
    {
        var hash = Convert.ToHexString(SHA256.HashData(merged))[..16].ToLowerInvariant();

        var directory = Path.Combine(Path.GetTempPath(), "nbenchmark");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"nbhost-{hash}.runtimeconfig.json");

        if (File.Exists(path))
            return path;

        var staging = Path.Combine(directory, $"nbhost-{hash}.{Environment.ProcessId}.tmp");

        File.WriteAllBytes(staging, merged);

        try
        {
            File.Move(staging, path, overwrite: true);
        }
        catch (IOException)
        {
            // Another process won the race with identical content, which is the only content this
            // name can hold.
            File.Delete(staging);
        }

        return path;
    }

    private readonly record struct Framework(string Name, string? Version);
}
