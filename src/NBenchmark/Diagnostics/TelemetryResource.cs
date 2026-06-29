using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NBenchmark.Diagnostics;

/// <summary>
///     Builds the OpenTelemetry resource attributes stamped on the root <c>benchmark.suite</c>
///     span. The attributes identify the run across commit, branch, CI pipeline, and machine so a
///     downstream backend (Grafana, Jaeger, Honeycomb) can render cross-commit trend lines and
///     regression alarms without NBenchmark shipping its own storage layer.
/// </summary>
/// <remarks>
///     <para>
///         The values are read once per process from environment variables. The well-known
///         variable set covers the major hosted CI providers (GitHub Actions, GitLab CI,
///         Azure Pipelines, CircleCI, AppVeyor, TeamCity, Jenkins, Travis CI, Buildkite) and
///         falls back to git local state (<c>GIT_COMMIT</c>/<c>GIT_BRANCH</c> or the git CLI)
///         when no CI variables are present. The result is cached for the process lifetime; a
///         re-run in the same process reuses the cached values.
///     </para>
///     <para>
///         The <c>OTEL_RESOURCE_ATTRIBUTES</c> and <c>OTEL_SERVICE_NAME</c> environment variables
///         are honoured verbatim - they are the OpenTelemetry-standard way to attach arbitrary
///         resource attributes, so a user who has already configured them for the rest of their
///         service does not have to repeat themselves. NBenchmark-specific attributes use the
///         <c>nbenchmark.*</c> namespace to avoid collisions with the standard OTel schema.
///     </para>
///     <para>
///         <see cref="NBenchmarkDiagnostics.OnSuiteStarting" /> stamps every returned attribute
///         onto the root span, so a backend that joins on resource attributes (the OTel
///         convention) sees them on every child span and metric without each emit point having
///         to repeat them. The BCL <c>ActivitySource</c>/<c>Meter</c> emit only what the SDK
///         listens for, so attributes that nothing consumes are free.
///     </para>
/// </remarks>
internal static class TelemetryResource
{
    private static IReadOnlyDictionary<string, object?>? _cached;

    /// <summary>
    ///     Returns the resource attributes for the current process, read once and cached. The
    ///     returned dictionary is keyed by the OTel attribute name (e.g.
    ///     <c>service.name</c>, <c>nbenchmark.commit_sha</c>) and is safe to enumerate while
    ///     stamping onto an <c>Activity</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> Attributes => _cached ??= Build();

    /// <summary>
    ///     Clears the cached attributes so the next <see cref="Attributes" /> access re-reads the
    ///     environment. Test-only: the production path reads once per process and caches.
    /// </summary>
    internal static void ResetForTesting() => _cached = null;

    internal static IReadOnlyDictionary<string, object?> Build()
    {
        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal);

        PopulatetelemetryProvider(attrs);
        PopulateCiIds(attrs);
        PopulateGit(attrs);
        PopulateHost(attrs);
        PopulateOpenTelemetryStandard(attrs);

        return attrs;
    }

    private static void PopulatetelemetryProvider(Dictionary<string, object?> attrs)
    {
        // Identify which CI provider is running, if any. The provider name is stamped on the
        // span so a backend can filter "all NBenchmark runs from GitHub Actions" vs "all local
        // developer runs" without parsing the CI id strings.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            attrs["nbenchmark.ci_provider"] = "github_actions";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITLAB_CI")))
            attrs["nbenchmark.ci_provider"] = "gitlab_ci";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AZURE_PIPELINES"))
                 || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))
            attrs["nbenchmark.ci_provider"] = "azure_pipelines";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CIRCLECI")))
            attrs["nbenchmark.ci_provider"] = "circleci";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPVEYOR")))
            attrs["nbenchmark.ci_provider"] = "appveyor";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAMCITY_VERSION")))
            attrs["nbenchmark.ci_provider"] = "teamcity";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JENKINS_URL")))
            attrs["nbenchmark.ci_provider"] = "jenkins";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRAVIS")))
            attrs["nbenchmark.ci_provider"] = "travis_ci";
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BUILDKITE")))
            attrs["nbenchmark.ci_provider"] = "buildkite";
    }

    private static void PopulateCiIds(Dictionary<string, object?> attrs)
    {
        // GitHub Actions: GITHUB_RUN_ID is the unique run number; GITHUB_RUN_ATTEMPT is the
        // retry count within the same run; GITHUB_REPOSITORY is owner/repo; GITHUB_REF is the
        // fully-qualified ref (refs/heads/main, refs/tags/v1.0, refs/pull/123/merge).
        AddIfPresent(attrs, "nbenchmark.ci_run_id",
            "GITHUB_RUN_ID", "CI_PIPELINE_ID", "BUILD_BUILDID", "CIRCLE_BUILD_NUM",
            "APPVEYOR_BUILD_ID", "TEAMCITY_BUILDID", "BUILDKITE_BUILD_ID", "TRAVIS_BUILD_ID");
        AddIfPresent(attrs, "nbenchmark.ci_run_url",
            "GITHUB_SERVER_URL", "CI_JOB_URL", "BUILD_BUILDURI", "CIRCLE_BUILD_URL");
        AddIfPresent(attrs, "nbenchmark.ci_repository",
            "GITHUB_REPOSITORY", "CI_REPOSITORY_URL");
        AddIfPresent(attrs, "nbenchmark.ci_ref",
            "GITHUB_REF", "CI_COMMIT_REF_NAME");
        AddIfPresent(attrs, "nbenchmark.ci_attempt",
            "GITHUB_RUN_ATTEMPT");
    }

    private static void PopulateGit(Dictionary<string, object?> attrs)
    {
        // Fall back to explicit git env vars (often exported by user scripts or pinned by the
        // harness) before reading local git state via the CLI. The CLI path is the slowest and
        // may fail outside a repo, so it is last.
        AddIfPresent(attrs, "nbenchmark.commit_sha", "GITHUB_SHA", "CI_COMMIT_SHA", "GIT_COMMIT");
        AddIfPresent(attrs, "nbenchmark.branch", "GITHUB_HEAD_REF", "CI_COMMIT_BRANCH", "GIT_BRANCH");

        if (attrs.ContainsKey("nbenchmark.commit_sha"))
            return;

        // Last resort: read from `git` in the working directory. Cheap to attempt, ignored on
        // any failure (no repo, no git binary, detached HEAD with no name). A short SHA is
        // plenty for cross-commit trend lines and keeps the attribute compact.
        TryAddGitCli(attrs);
    }

    private static void PopulateHost(Dictionary<string, object?> attrs)
    {
        // Machine and OS identify the runner so a backend can separate "the noisy CI shared
        // runner" from "the developer's quiet workstation" - the same jitter-detection signal
        // the autotune loop produces, but at the run level. The framework description uses the
        // `nbenchmark.host.runtime` key to avoid colliding with the `nbenchmark.runtime` tag
        // OnSuiteStarting sets from the runtimes parameter (the TFM list, e.g. "net8,net9").
        attrs["nbenchmark.host.machine_name"] = Environment.MachineName;
        attrs["nbenchmark.host.os"] =
            OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : Environment.OSVersion.Platform.ToString().ToLowerInvariant();
        attrs["nbenchmark.host.arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        attrs["nbenchmark.host.runtime"] = RuntimeInformation.FrameworkDescription;
    }

    private static void PopulateOpenTelemetryStandard(Dictionary<string, object?> attrs)
    {
        // The OTel spec defines OTEL_RESOURCE_ATTRIBUTES as a comma-separated key=value list and
        // OTEL_SERVICE_NAME as a shortcut for service.name. A user who has set these for the rest
        // of their service expects them to appear on NBenchmark spans too, so copy them through
        // verbatim rather than asking the user to repeat themselves.
        var resourceAttrs = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");

        if (!string.IsNullOrWhiteSpace(resourceAttrs))
        {
            foreach (var pair in resourceAttrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = pair.IndexOf('=');

                if (eq <= 0 || eq == pair.Length - 1)
                    continue;

                var key = pair[..eq].Trim();
                var value = pair[(eq + 1)..].Trim();
                attrs[key] = value;
            }
        }

        AddIfPresent(attrs, "service.name", "OTEL_SERVICE_NAME");
    }

    private static void AddIfPresent(
        Dictionary<string, object?> attrs,
        string targetKey,
        params string[] envVars)
    {
        foreach (var name in envVars)
        {
            var value = Environment.GetEnvironmentVariable(name);

            if (!string.IsNullOrWhiteSpace(value))
            {
                attrs[targetKey] = value;
                return;
            }
        }
    }

    private static void TryAddGitCli(Dictionary<string, object?> attrs)
    {
        try
        {
            var sha = ReadGit("rev-parse --short HEAD");
            var branch = ReadGit("rev-parse --abbrev-ref HEAD");

            if (!string.IsNullOrEmpty(sha))
                attrs["nbenchmark.commit_sha"] = sha;

            // `rev-parse --abbrev-ref HEAD` returns "HEAD" for a detached checkout; treat that
            // as "no branch" so a backend does not see a synthetic branch name.
            if (!string.IsNullOrEmpty(branch) && branch != "HEAD")
                attrs["nbenchmark.branch"] = branch;
        }
        catch
        {
            // Outside a repo, missing git, or permission errors: the run still works, it just
            // lacks git-stamped resource attributes.
        }
    }

    private static string? ReadGit(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);

        if (process is null)
            return null;

        var stdout = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(2000);
        return stdout.Length > 0 ? stdout : null;
    }
}