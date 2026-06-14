using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NBenchmark.Engine;

internal enum IsolatedRunMode
{
    Quick,
    Suite,
}

internal sealed record IsolatedRunRequest
{
    public required IsolatedRunMode Mode { get; init; }
    public required int InvocationOrdinal { get; init; }
    public required string CallerFilePath { get; init; }
    public required int CallerLineNumber { get; init; }
    public required string CallerMemberName { get; init; }
    public required string BenchmarkName { get; init; }

    public string? SuiteName { get; init; }

    public required MeasurementOptions Options { get; init; }
}

internal static class IsolatedRunContext
{
    private const string RequestPathEnvVar = "NBENCHMARK_ISOLATED_REQUEST_PATH";
    private const string OutputPathEnvVar = "NBENCHMARK_ISOLATED_OUTPUT_PATH";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private static int QuickInvocationSequence;
    private static int SuiteInvocationSequence;
    private static readonly AsyncLocal<IsolatedRunScope?> Scope = new();

    public static bool IsActive => Scope.Value is not null;

    public static bool TryGetActiveRequest(out IsolatedRunRequest request)
    {
        var scope = Scope.Value;

        if (scope is null)
        {
            request = null!;
            return false;
        }

        request = scope.Request;
        return true;
    }

    public static int NextInvocationOrdinal(IsolatedRunMode mode)
        => mode switch
        {
            IsolatedRunMode.Quick => Interlocked.Increment(ref QuickInvocationSequence),
            IsolatedRunMode.Suite => Interlocked.Increment(ref SuiteInvocationSequence),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown isolated run mode."),
        };

    internal static void ResetInvocationOrdinalsForTesting()
    {
        Interlocked.Exchange(ref QuickInvocationSequence, 0);
        Interlocked.Exchange(ref SuiteInvocationSequence, 0);
    }

    public static bool IsRequestMatch(
        IsolatedRunMode mode,
        int invocationOrdinal,
        string callerFilePath,
        int callerLineNumber,
        string callerMemberName,
        string? benchmarkName = null,
        string? suiteName = null)
    {
        if (!TryGetActiveRequest(out var request))
            return false;

        if (request.Mode != mode)
            return false;

        if (request.InvocationOrdinal != invocationOrdinal)
            return false;

        if (!PathEquals(request.CallerFilePath, callerFilePath))
            return false;

        if (request.CallerLineNumber != callerLineNumber)
            return false;

        if (!string.Equals(request.CallerMemberName, callerMemberName, StringComparison.Ordinal))
            return false;

        if (benchmarkName is not null
            && !string.Equals(request.BenchmarkName, benchmarkName, StringComparison.Ordinal))
            return false;

        if (suiteName is not null
            && !string.Equals(request.SuiteName, suiteName, StringComparison.Ordinal))
            return false;

        return true;
    }

    public static bool IsRequestedInvocation(IsolatedRunMode mode, int invocationOrdinal)
        => TryGetActiveRequest(out var request)
           && request.Mode == mode
           && request.InvocationOrdinal == invocationOrdinal;

    public static MeasurementOutcome BuildCallsiteMismatchOutcome(
        string name,
        MeasurementOptions options,
        IsolatedRunRequest request,
        string actualCallerFilePath,
        int actualCallerLineNumber,
        string actualCallerMemberName,
        int actualInvocationOrdinal,
        string? actualSuiteName = null)
    {
        var expectedSuite = request.SuiteName is null ? "(none)" : request.SuiteName;
        var actualSuite = actualSuiteName is null ? "(none)" : actualSuiteName;

        var message =
            $"Isolated replay mismatch for '{name}'. "
            + $"Requested invocation #{request.InvocationOrdinal} at "
            + $"{request.CallerFilePath}:{request.CallerLineNumber} ({request.CallerMemberName}), "
            + $"suite={expectedSuite}; "
            + $"child executed invocation #{actualInvocationOrdinal} at "
            + $"{actualCallerFilePath}:{actualCallerLineNumber} ({actualCallerMemberName}), "
            + $"suite={actualSuite}. "
            + "The replayed call order or callsite identity differs between parent and child.";

        return BuildErroredOutcome(name, options, message);
    }

    public static MeasurementOptions ResolveOptions(MeasurementOptions fallback)
    {
        return TryGetActiveRequest(out var request)
            ? request.Options
            : fallback;
    }

    public static async Task WriteChildOutcomeIfRequestedAsync(
        MeasurementOutcome outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var outputPath = Scope.Value?.OutputPath
                         ?? Environment.GetEnvironmentVariable(OutputPathEnvVar);

        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        await IsolatedProcessRunner.WriteResultAsync(outputPath, outcome, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> WithActiveRequestForTestingAsync<T>(
        IsolatedRunRequest request,
        string? outputPath,
        Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);

        var prior = Scope.Value;
        Scope.Value = new IsolatedRunScope(null, request, outputPath);

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Scope.Value = prior;
        }
    }

    public static async Task<T> WithCurrentRequestAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var requestPath = Environment.GetEnvironmentVariable(RequestPathEnvVar);

        if (string.IsNullOrWhiteSpace(requestPath))
            return await action().ConfigureAwait(false);

        var request = await ReadRequestAsync(requestPath).ConfigureAwait(false);
        var outputPath = Environment.GetEnvironmentVariable(OutputPathEnvVar);

        var prior = Scope.Value;
        Scope.Value = new IsolatedRunScope(requestPath, request, outputPath);

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Scope.Value = prior;
        }
    }

    public static async Task<MeasurementOutcome> RunInIsolatedProcessAsync(
        IsolatedRunRequest request,
        CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(Path.GetTempPath(), $"nbench-isolated-request-{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-isolated-output-{Guid.NewGuid():N}.json");

        try
        {
            await WriteRequestAsync(requestPath, request, cancellationToken).ConfigureAwait(false);

            // Quick/Suite isolated replay always launches the current entry process and
            // carries request/output paths via env vars instead of CLI switches.
            using var process = new Process
            {
                StartInfo = IsolatedProcessRunner.BuildStartInfo(
                    [],
                    (RequestPathEnvVar, requestPath),
                    (OutputPathEnvVar, outputPath)),
            };

            var stderr = new StringBuilder();

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderr.AppendLine(e.Data);
            };

            process.OutputDataReceived += (_, _) => { };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                var detail = stderr.Length > 0 ? $" {stderr.ToString().Trim()}" : "";

                var noPayloadHint = process.ExitCode == 0 && !File.Exists(outputPath)
                    ? " The child exited successfully but produced no payload; this usually indicates that the requested isolated callsite was not replayed in the child process."
                    : "";

                var message =
                    $"Isolated process for '{request.BenchmarkName}' exited with code {process.ExitCode}.{detail}{noPayloadHint}";

                return BuildErroredOutcome(request.BenchmarkName, request.Options, message);
            }

            return await IsolatedProcessRunner.ReadResultAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BuildErroredOutcome(
                request.BenchmarkName,
                request.Options,
                $"Failed to run '{request.BenchmarkName}' in an isolated process: {ex.Message}");
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(outputPath);
        }
    }

    private static async Task WriteRequestAsync(
        string requestPath,
        IsolatedRunRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(requestPath);
        await JsonSerializer.SerializeAsync(stream, request, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IsolatedRunRequest> ReadRequestAsync(string requestPath)
    {
        await using var stream = File.OpenRead(requestPath);

        var request = await JsonSerializer.DeserializeAsync<IsolatedRunRequest>(stream, SerializerOptions)
            .ConfigureAwait(false);

        if (request is null)
            throw new InvalidOperationException("Isolated request payload was unreadable.");

        return request;
    }

    private static MeasurementOutcome BuildErroredOutcome(
        string name,
        MeasurementOptions options,
        string message)
    {
        return OutcomeBuilder.Build(
            new RunOutcome.Errored(new InvalidOperationException(message), message),
            name,
            null,
            false,
            options,
            TimeSpan.Zero,
            TimeSpan.Zero);
    }

    private static bool PathEquals(string left, string right)
    {
        if (OperatingSystem.IsWindows())
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort temp-file cleanup.
        }
    }

    private sealed record IsolatedRunScope(string? RequestPath, IsolatedRunRequest Request, string? OutputPath);
}
