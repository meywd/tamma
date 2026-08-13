using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.GitLab;

namespace Tamma.Platforms.GitLab.Tests.Support;

/// <summary>
/// Test wiring helpers — produce a <see cref="GitLabPlatformClient"/>
/// + <see cref="GitLabActionsClient"/> backed by a
/// <see cref="FakeHttpMessageHandler"/>.
/// </summary>
internal static class TestFactory
{
    public const string DefaultBaseUrl = "https://gitlab.example.com";

    /// <summary>Version above the 13.9 PR-lifecycle floor — lifecycle-verb
    /// tests pass this so the REAL paths run. The parameter default stays
    /// null (the version-unknown conservative shape).</summary>
    public static readonly Version LifecycleCapableVersion = new(16, 11);

    public static (GitLabPlatformClient Client, FakeHttpMessageHandler Handler) BuildClient(
        GitLabAuth? auth = null,
        string baseUrl = DefaultBaseUrl,
        Version? detectedVersion = null)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = null };
        var typed = new GitLabHttpClient(
            http,
            auth ?? new GitLabAuth.PersonalAccessToken("glpat-test-token"),
            baseUrl,
            ownsHttpClient: true);
        return (new GitLabPlatformClient(
            typed, NullLogger<GitLabPlatformClient>.Instance, detectedVersion), handler);
    }

    public static (GitLabActionsClient Client, FakeHttpMessageHandler Handler) BuildActions(
        GitLabAuth? auth = null,
        string baseUrl = DefaultBaseUrl,
        long maxArtifactBytes = GitLabActionsClient.DefaultMaxArtifactBytes)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        var typed = new GitLabHttpClient(
            http,
            auth ?? new GitLabAuth.PersonalAccessToken("glpat-test-token"),
            baseUrl,
            ownsHttpClient: true);
        return (new GitLabActionsClient(typed, NullLogger<GitLabActionsClient>.Instance, maxArtifactBytes), handler);
    }
}
