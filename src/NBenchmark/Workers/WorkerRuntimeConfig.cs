using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace NBenchmark.Workers;

/// <summary>
///     Gives the worker the shared frameworks the assembly under test needs but the worker itself
///     does not declare.
/// </summary>
/// <remarks>
///     <para>
///         <c>nbworker</c> is a plain <c>Microsoft.NET.Sdk</c> application, so its
///         <c>runtimeconfig.json</c> names one framework: <c>Microsoft.NETCore.App</c>. A benchmark
///         class that lives in a <c>Microsoft.NET.Sdk.Web</c> project - an API measuring its own
///         handlers is the ordinary case - sits in an assembly whose graph reaches
///         <c>Microsoft.AspNetCore.App</c>. Those assemblies are framework-provided, so the target's
///         own <c>deps.json</c> does not carry them and
///         <see cref="System.Runtime.Loader.AssemblyDependencyResolver" /> correctly resolves them to
///         nothing; they are expected on the process's trusted-platform-assembly list instead. In a
///         worker started without that framework they are on no list at all, and the load fails with
///         <c>Could not load file or assembly 'Microsoft.Extensions.Hosting.Abstractions'</c> before a
///         single benchmark is discovered. <c>Microsoft.WindowsDesktop.App</c> fails the same way.
///     </para>
///     <para>
///         The framework set is chosen by <c>hostfxr</c> from the runtimeconfig and fixed for the life
///         of the process, so this cannot be repaired from inside the worker any more than the runtime
///         profile can - it has to be decided before <c>Process.Start</c>. So the launch hands
///         <c>dotnet exec</c> a <c>--runtimeconfig</c> of our own: the worker's, with the target's
///         extra frameworks added.
///     </para>
///     <para>
///         <b>Frameworks only.</b> The worker's <c>tfm</c>, <c>rollForward</c> and
///         <c>configProperties</c> are carried over untouched, and the target's <c>configProperties</c>
///         are deliberately dropped. A <see cref="RuntimeProfile" /> exists precisely so the worker's
///         GC flavour and tiering are chosen rather than inherited; importing the target application's
///         <c>System.GC.Server</c> would quietly undo the thing isolation is for. Frameworks are
///         different in kind - they decide whether the assembly can load at all, not how fast it runs.
///     </para>
/// </remarks>
internal static class WorkerRuntimeConfig
{
    /// <summary>
    ///     Every replicate of every group asks the same question about the same two files, and the
    ///     answer involves reading and hashing both.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    ///     A runtimeconfig to launch the worker with, or <c>null</c> when the worker's own is
    ///     sufficient.
    /// </summary>
    /// <remarks>
    ///     <c>null</c> is the answer for every target that names no framework the worker is missing,
    ///     which is every plain console and test-host target - so the ordinary launch is unchanged,
    ///     down to the command line.
    /// </remarks>
    public static string? ResolveFor(string workerAssemblyPath, string? targetAssemblyPath)
    {
        if (string.IsNullOrEmpty(workerAssemblyPath) || string.IsNullOrEmpty(targetAssemblyPath))
            return null;

        return Cache.GetOrAdd(
            $"{workerAssemblyPath}\0{targetAssemblyPath}",
            _ => Synthesize(workerAssemblyPath, targetAssemblyPath));
    }

    private static string? Synthesize(string workerAssemblyPath, string targetAssemblyPath)
    {
        try
        {
            var workerConfigPath = Path.ChangeExtension(workerAssemblyPath, ".runtimeconfig.json");
            var targetConfigPath = Path.ChangeExtension(targetAssemblyPath, ".runtimeconfig.json");

            if (!File.Exists(workerConfigPath) || !File.Exists(targetConfigPath))
                return null;

            using var workerDocument = JsonDocument.Parse(File.ReadAllBytes(workerConfigPath));
            using var targetDocument = JsonDocument.Parse(File.ReadAllBytes(targetConfigPath));

            if (!TryGetRuntimeOptions(workerDocument, out var workerOptions)
                || !TryGetRuntimeOptions(targetDocument, out var targetOptions))
                return null;

            // A self-contained target ships its framework inside its own output directory, where its
            // deps.json already resolves it. There is no shared framework to ask the host for, and
            // naming one would be a version the machine may well not have.
            if (targetOptions.TryGetProperty("includedFrameworks", out _))
                return null;

            var workerFrameworks = ReadFrameworks(workerOptions);

            if (workerFrameworks.Count == 0)
                return null;

            var declared = workerFrameworks
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var extras = ReadFrameworks(targetOptions)
                .Where(f => !declared.Contains(f.Name))
                .ToList();

            if (extras.Count == 0)
                return null;

            var merged = Render(workerOptions, [.. workerFrameworks, .. extras]);

            return Write(merged);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or NotSupportedException)
        {
            // Falling back to the worker's own config reproduces today's behaviour exactly. A
            // configuration file we cannot parse is not a reason to fail a run that may not have
            // needed us at all.
            return null;
        }
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
    ///     The worker's own <c>runtimeOptions</c>, verbatim, with the framework declaration replaced by
    ///     the merged set.
    /// </summary>
    /// <remarks>
    ///     Copying every other property rather than naming the ones we know about: <c>rollForward</c>
    ///     and <c>tfm</c> are the two that matter today, but a property added by a future SDK is far
    ///     more likely to be needed than to be harmful, and dropping one silently is the kind of
    ///     failure that presents as a worker that will not start.
    /// </remarks>
    private static byte[] Render(JsonElement workerOptions, IReadOnlyList<Framework> frameworks)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("runtimeOptions");
            writer.WriteStartObject();

            foreach (var property in workerOptions.EnumerateObject())
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
    ///         Not written beside the worker: under a global-tool or read-only package-cache
    ///         deployment that directory cannot be written to, and a file appearing next to
    ///         <c>nbworker.dll</c> would be one more thing the deployment checks have to know is not
    ///         part of the shipped set.
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

        var path = Path.Combine(directory, $"nbworker-{hash}.runtimeconfig.json");

        if (File.Exists(path))
            return path;

        var staging = Path.Combine(directory, $"nbworker-{hash}.{Environment.ProcessId}.tmp");

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
