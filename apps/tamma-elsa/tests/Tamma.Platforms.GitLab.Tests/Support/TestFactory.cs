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

    public static (GitLabPlatformClient Client, FakeHttpMessageHandler Handler) BuildClient(
        GitLabAuth? auth = null,
        string baseUrl = DefaultBaseUrl)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = null };
        var typed = new GitLabHttpClient(
            http,
            auth ?? new GitLabAuth.PersonalAccessToken("glpat-test-token"),
            baseUrl,
            ownsHttpClient: true);
        return (new GitLabPlatformClient(typed, NullLogger<GitLabPlatformClient>.Instance), handler);
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
