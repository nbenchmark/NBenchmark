using NBenchmark.Diagnostics;
using Xunit;

namespace NBenchmark.Tests;

public class TelemetryResourceTests
{
    // The well-known env var names exercised by the tests. Kept as constants so a test that
    // forgets to clean up one of them is obvious in code review.
    private const string GitHubActions = "GITHUB_ACTIONS";
    private const string GitHubRunId = "GITHUB_RUN_ID";
    private const string GitHubSha = "GITHUB_SHA";
    private const string GitHubRef = "GITHUB_REF";
    private const string GitLabCi = "GITLAB_CI";
    private const string GitCommit = "GIT_COMMIT";
    private const string GitBranch = "GIT_BRANCH";
    private const string OtelResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";
    private const string OtelServiceName = "OTEL_SERVICE_NAME";

    // Env vars that any test in this class might set. Cleared between tests so the cached
    // attributes never leak across cases.
    private static readonly string[] ManagedEnvVars =
    [
        GitHubActions, GitHubRunId, GitHubSha, GitHubRef,
        GitLabCi, GitCommit, GitBranch,
        OtelResourceAttributes, OtelServiceName,
        // GitLab-specific vars used by the GitLab test.
        "CI_PIPELINE_ID", "CI_COMMIT_SHA", "CI_COMMIT_BRANCH", "CI_REPOSITORY_URL", "CI_JOB_URL", "CI_COMMIT_REF_NAME",
        // GitHub-specific vars used by the GitHub test.
        "GITHUB_RUN_ATTEMPT", "GITHUB_REPOSITORY", "GITHUB_SERVER_URL", "GITHUB_HEAD_REF",
        // Azure Pipelines / others.
        "AZURE_PIPELINES", "TF_BUILD", "BUILD_BUILDID", "BUILD_BUILDURI",
        "CIRCLECI", "CIRCLE_BUILD_NUM", "CIRCLE_BUILD_URL",
        "APPVEYOR", "APPVEYOR_BUILD_ID",
        "TEAMCITY_VERSION", "TEAMCITY_BUILDID",
        "JENKINS_URL",
        "TRAVIS", "TRAVIS_BUILD_ID",
        "BUILDKITE", "BUILDKITE_BUILD_ID",
    ];

    public TelemetryResourceTests()
    {
        // The static cache survives across tests in the same process. Clear it before every
        // test so each sees a fresh read of the environment.
        TelemetryResource.ResetForTesting();
    }

    [Fact]
    public void Build_Reads_GitHub_Actions_RunId_And_Sha()
    {
        using var _ = WithEnv(new[]
        {
            (GitHubActions, "true"),
            (GitHubRunId, "1234567890"),
            (GitHubSha, "abcdef1234567890"),
        });

        var attrs = TelemetryResource.Build();

        Assert.Equal("github_actions", attrs["nbenchmark.ci_provider"]);
        Assert.Equal("1234567890", attrs["nbenchmark.ci_run_id"]);
        Assert.Equal("abcdef1234567890", attrs["nbenchmark.commit_sha"]);
    }

    [Fact]
    public void Build_Reads_GitLab_PipelineId()
    {
        using var _ = WithEnv(new[]
        {
            (GitLabCi, "true"),
            ("CI_PIPELINE_ID", "567890"),
            ("CI_COMMIT_SHA", "fedcba0987654321"),
        });

        var attrs = TelemetryResource.Build();

        Assert.Equal("gitlab_ci", attrs["nbenchmark.ci_provider"]);
        Assert.Equal("567890", attrs["nbenchmark.ci_run_id"]);
        Assert.Equal("fedcba0987654321", attrs["nbenchmark.commit_sha"]);
    }

    [Fact]
    public void Build_When_No_Ci_EnvVars_Are_Set_Provider_Attribute_Is_Omitted()
    {
        using var _ = WithEnv();

        var attrs = TelemetryResource.Build();

        Assert.False(attrs.ContainsKey("nbenchmark.ci_provider"));
        Assert.False(attrs.ContainsKey("nbenchmark.ci_run_id"));
    }

    [Fact]
    public void Build_Includes_Host_Machine_And_Os()
    {
        using var _ = WithEnv();

        var attrs = TelemetryResource.Build();

        Assert.Equal(Environment.MachineName, attrs["nbenchmark.host.machine_name"]);
        // OS is one of "windows", "macos", "linux" - the exact value depends on the runner.
        Assert.Contains((string)attrs["nbenchmark.host.os"]!, new[] { "windows", "macos", "linux" });
        Assert.True(attrs.ContainsKey("nbenchmark.host.arch"));
        Assert.True(attrs.ContainsKey("nbenchmark.host.runtime"));
    }

    [Fact]
    public void Build_Parses_OtelResourceAttributes_EnvVar()
    {
        using var _ = WithEnv(new[]
        {
            (OtelResourceAttributes, "deployment.environment=production,service.version=1.2.3"),
        });

        var attrs = TelemetryResource.Build();

        Assert.Equal("production", attrs["deployment.environment"]);
        Assert.Equal("1.2.3", attrs["service.version"]);
    }

    [Fact]
    public void Build_OtelResourceAttributes_With_Malformed_Pair_Is_Skipped()
    {
        using var _ = WithEnv(new[]
        {
            (OtelResourceAttributes, "valid=ok,missing-equals,bad=,also=good"),
        });

        var attrs = TelemetryResource.Build();

        Assert.Equal("ok", attrs["valid"]);
        Assert.False(attrs.ContainsKey("missing-equals"));
        Assert.False(attrs.ContainsKey("bad"));
        Assert.Equal("good", attrs["also"]);
    }

    [Fact]
    public void Build_OtelServiceName_Sets_ServiceName()
    {
        using var _ = WithEnv(new[]
        {
            (OtelServiceName, "my-benchmark-service"),
        });

        var attrs = TelemetryResource.Build();

        Assert.Equal("my-benchmark-service", attrs["service.name"]);
    }

    [Fact]
    public void Build_GitCommit_EnvVar_Fallback_When_No_Ci_Sha_Present()
    {
        using var _ = WithEnv(new[]
        {
            (GitCommit, "local-sha-123"),
            (GitBranch, "feature-branch"),
        });

        var attrs = TelemetryResource.Build();

        Assert.Equal("local-sha-123", attrs["nbenchmark.commit_sha"]);
        Assert.Equal("feature-branch", attrs["nbenchmark.branch"]);
    }

    [Fact]
    public void Build_GitHub_Sha_Takes_Precedence_Over_Git_Commit_EnvVar()
    {
        using var _ = WithEnv(new[]
        {
            (GitHubActions, "true"),
            (GitHubSha, "ci-sha"),
            (GitCommit, "local-sha"),
        });

        var attrs = TelemetryResource.Build();

        Assert.Equal("ci-sha", attrs["nbenchmark.commit_sha"]);
    }

    [Fact]
    public void Attributes_Is_Cached_Across_Calls()
    {
        using var _ = WithEnv(new[]
        {
            (GitHubRunId, "cached-run-id"),
        });

        var first = TelemetryResource.Attributes;
        // Mutate the env after the first read. The cache should still hold the original value.
        Environment.SetEnvironmentVariable(GitHubRunId, "different-run-id");
        var second = TelemetryResource.Attributes;

        Assert.Same(first, second);
        Assert.Equal("cached-run-id", second["nbenchmark.ci_run_id"]);
    }

    [Fact]
    public void ResetForTesting_Clears_The_Cache()
    {
        using var _ = WithEnv(new[]
        {
            (GitHubRunId, "first-run"),
        });

        var first = TelemetryResource.Attributes;
        Assert.Equal("first-run", first["nbenchmark.ci_run_id"]);

        Environment.SetEnvironmentVariable(GitHubRunId, "second-run");
        TelemetryResource.ResetForTesting();
        var second = TelemetryResource.Attributes;

        Assert.NotSame(first, second);
        Assert.Equal("second-run", second["nbenchmark.ci_run_id"]);
    }

    /// <summary>
    ///     Sets the supplied env vars for the duration of the returned scope and clears every
    ///     managed var on dispose. Ensures no test leaks env state into another.
    /// </summary>
    private static IDisposable WithEnv(IEnumerable<(string Name, string? Value)> vars)
    {
        var saved = new Dictionary<string, string?>();

        foreach (var name in ManagedEnvVars)
            saved[name] = Environment.GetEnvironmentVariable(name);

        foreach (var (name, value) in vars)
            Environment.SetEnvironmentVariable(name, value);

        return new EnvScope(saved);
    }

    /// <summary>
    ///     Convenience overload for the common case where every value is non-null. Avoids the
    ///     nullable-tuple mismatch warnings at every call site.
    /// </summary>
    private static IDisposable WithEnv(params (string Name, string Value)[] vars)
        => WithEnv(vars.Select(v => (v.Name, (string?)v.Value)));

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _saved;

        public EnvScope(Dictionary<string, string?> saved) => _saved = saved;

        public void Dispose()
        {
            foreach (var (name, value) in _saved)
                Environment.SetEnvironmentVariable(name, value);

            // Reset the cache so a subsequent test in the same process re-reads the
            // (now-restored) environment.
            TelemetryResource.ResetForTesting();
        }
    }
}