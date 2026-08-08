using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P4 M4 — the GitHub App installation-metadata plane, absorbed into
/// the driver project as plain REST (replacing Tamma.Api's Octokit-backed
/// <c>IGitHubAppClient</c>, whose tests these port). Covers: App-JWT auth on
/// the metadata read, typed failures (not-found / rate-limit / API error),
/// installation-token minting + caching for the repo listing, and the Null
/// impl's <c>github_client_not_configured</c> degraded mode.
/// </summary>
[TestFixture]
public sealed class GitHubAppInstallationReaderTests
{
    private const string Api = "https://api.github.com";

    private static RSA NewKey() => RSA.Create(2048);

    private static RestGitHubAppInstallationReader Reader(
        FakeHttpMessageHandler handler, RSA key, long appId = 42) =>
        new(new HttpClient(handler), appId, key.ExportRSAPrivateKeyPem());

    private static string InstallationJson(long id = 12345, string login = "acme-org") =>
        JsonSerializer.Serialize(new
        {
            id,
            app_id = 42,
            account = new { login, type = "Organization" },
            permissions = new { contents = "write", issues = "write" },
            suspended_at = (string?)null,
        });

    [Test]
    public async Task GetInstallation_ParsesAccount_AndSendsAppJwt()
    {
        using var key = NewKey();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/app/installations/12345",
            HttpStatusCode.OK, InstallationJson());

        var result = await Reader(handler, key).GetInstallationAsync(12345);

        result.ServiceUnavailable.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.InstallationId.Should().Be(12345);
        result.Result.AccountLogin.Should().Be("acme-org");
        result.Result.AccountType.Should().Be("Organization");
        result.Result.AppId.Should().Be(42);
        result.Result.PermissionsJson.Should().Contain("contents");

        var req = handler.Requests.Single();
        req.Headers.Should().ContainKey("Authorization");
        req.Headers["Authorization"].Should().StartWith("Bearer ",
            "the metadata read authenticates with the RS256 App JWT");
        req.Headers["Authorization"].Split('.').Should().HaveCount(3, "a JWT has three segments");
    }

    [Test]
    public async Task GetInstallation_NotFound_ReturnsTypedFailure()
    {
        using var key = NewKey();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/app/installations/404404",
            HttpStatusCode.NotFound, "{}");

        var result = await Reader(handler, key).GetInstallationAsync(404404);

        result.ServiceUnavailable.Should().BeFalse();
        result.Result.Should().BeNull();
        result.ErrorReason.Should().Be("installation_not_found");
    }

    [Test]
    public async Task ListInstallationRepos_MintsToken_ThenLists_AndCachesTheToken()
    {
        using var key = NewKey();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/app/installations/777/access_tokens",
            HttpStatusCode.Created,
            """{"token":"ghs_test_token","expires_at":"2099-01-01T00:00:00Z"}""");
        var reposJson = JsonSerializer.Serialize(new
        {
            total_count = 1,
            repositories = new[]
            {
                new { id = 9, name = "widgets", full_name = "acme/widgets", owner = new { login = "acme" } },
            },
        });
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/installation/repositories",
            HttpStatusCode.OK, reposJson);
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/installation/repositories",
            HttpStatusCode.OK, reposJson);

        var reader = Reader(handler, key);
        var first = await reader.ListInstallationReposAsync(777);
        var second = await reader.ListInstallationReposAsync(777);

        first.Result.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new GitHubAppInstallationRepo(9, "acme", "widgets", "acme/widgets"));
        second.Result.Should().ContainSingle();

        // ONE token mint for two listings — the ~55-minute cache.
        handler.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1,
            "the installation token is cached across calls (the retired Octokit client's behavior)");

        // The listing authenticates with the MINTED token, not the App JWT.
        handler.Requests.Last(r => r.Method == HttpMethod.Get)
            .Headers["Authorization"].Should().Be("Bearer ghs_test_token");
    }

    [Test]
    public async Task ListInstallationRepos_TokenMintFails_ReturnsTypedFailure()
    {
        using var key = NewKey();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/app/installations/777/access_tokens",
            HttpStatusCode.Unauthorized, "{}");

        var result = await Reader(handler, key).ListInstallationReposAsync(777);

        result.Result.Should().BeNull();
        result.ErrorReason.Should().Be("github_api_error");
    }

    [Test]
    public void Constructor_FailsLoud_OnMalformedKey()
    {
        var act = () => new RestGitHubAppInstallationReader(
            new HttpClient(new FakeHttpMessageHandler()), 42, "not-a-pem");
        act.Should().Throw<Exception>("a bad credential must surface at construction, not first use");
    }

    [Test]
    public async Task NullReader_AnswersNotConfigured_TheDocumentedDegradedMode()
    {
        var nullReader = new NullGitHubAppInstallationReader();

        var install = await nullReader.GetInstallationAsync(1);
        install.ServiceUnavailable.Should().BeTrue();
        install.ErrorReason.Should().Be("github_client_not_configured");

        var repos = await nullReader.ListInstallationReposAsync(1);
        repos.ServiceUnavailable.Should().BeTrue();
        repos.ErrorReason.Should().Be("github_client_not_configured");
    }
}
