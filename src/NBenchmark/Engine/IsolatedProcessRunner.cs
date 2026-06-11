using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NBenchmark.Engine;

/// <summary>
///     Dispatches an <c>[IsolatedProcess]</c> benchmark to a child process and reads its
///     result back. The child re-runs the same entry assembly with internal
///     <c>--nb-isolated-run</c>/<c>--nb-isolated-output</c> flags, executes just that one
///     benchmark in a fresh CLR, and writes the serialized outcome to a temp file the
///     parent then reads. Communication goes through a file (not stdout) so the child's
///     own console output cannot corrupt the payload.
/// </summary>
internal static class IsolatedProcessRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public static async Task<MeasurementOutcome> RunAsync(
        string fullName,
        IReadOnlyList<string> originalArgs,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(), $"nbench-isolated-{Guid.NewGuid():N}.json");

        try
        {
            using var process = new Process { StartInfo = BuildStartInfo(fullName, originalArgs, outputPath) };
            var stderr = new StringBuilder();

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderr.AppendLine(e.Data);
            };

            // Drain stdout so a chatty child cannot fill the pipe buffer and deadlock.
            process.OutputDataReceived += (_, _) => { };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                var detail = stderr.Length > 0 ? $" {stderr.ToString().Trim()}" : "";

                return Errored(fullName,
                    $"Isolated process for '{fullName}' exited with code {process.ExitCode}.{detail}");
            }

            return await ReadResultAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Errored(fullName, $"Failed to run '{fullName}' in an isolated process: {ex.Message}");
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    public static async Task WriteResultAsync(
        string outputPath,
        MeasurementOutcome outcome,
        CancellationToken cancellationToken)
    {
        var payload = new IsolatedPayload { Result = outcome.Result, RawSamples = outcome.RawSamples };
        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<MeasurementOutcome> ReadResultAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(outputPath);

        var payload = await JsonSerializer
            .DeserializeAsync<IsolatedPayload>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Result is null)
            return Errored("(unknown)", "Isolated process produced an unreadable result payload.");

        return new MeasurementOutcome
        {
            Result = payload.Result,
            RawSamples = payload.RawSamples ?? [],
        };
    }

    private static ProcessStartInfo BuildStartInfo(
        string fullName,
        IReadOnlyList<string> originalArgs,
        string outputPath)
    {
        var processPath = Environment.ProcessPath;
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        // For `dotnet myapp.dll` the host process is the muxer, so the managed entry
        // assembly must be passed as the first argument. For a native apphost
        // (`./myapp`) the executable runs the assembly directly.
        var isMuxer = processPath is null
                      || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        if (isMuxer)
        {
            psi.FileName = processPath ?? "dotnet";

            if (!string.IsNullOrEmpty(entryAssembly))
                psi.ArgumentList.Add(entryAssembly);
        }
        else
        {
            psi.FileName = processPath!;
        }

        foreach (var arg in originalArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add("--nb-isolated-run");
        psi.ArgumentList.Add(fullName);
        psi.ArgumentList.Add("--nb-isolated-output");
        psi.ArgumentList.Add(outputPath);

        return psi;
    }

    private static MeasurementOutcome Errored(string name, string message) => new()
    {
        RawSamples = [],
        Result = new BenchmarkResult
        {
            Name = name,
            Mean = 0,
            Median = 0,
            P95 = 0,
            P99 = 0,
            Min = 0,
            Max = 0,
            StandardDeviation = 0,
            Errored = true,
            ErrorMessage = message,
        },
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a temp file; nothing actionable on failure.
        }
    }

    private sealed record IsolatedPayload
    {
        public required BenchmarkResult Result { get; init; }
        public required double[] RawSamples { get; init; }
    }
}
