using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Octokit;
using Octokit.Internal;
using Tamma.Api.Services.GitHub;

namespace Tamma.Api.Tests.GitHub;

/// <summary>
/// Unit tests for <see cref="OctokitGitHubAppClient"/>. All GitHub traffic is
/// stubbed via the <see cref="IOctokitClientFactory"/> seam + Moq'd
/// <see cref="IGitHubClient"/> — no live calls.
/// </summary>
[TestFixture]
public class OctokitGitHubAppClientTests
{
    // RSA private key in PKCS#8 PEM (generated fresh, used only for unit
    // tests, never touches a real GitHub App).
    private static readonly string TestPrivateKey = GenerateTestPrivateKey();

    private static string GenerateTestPrivateKey()
    {
        using var rsa = RSA.Create(2048);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var b64 = Convert.ToBase64String(pkcs8);
        // Break into 64-char lines per PEM spec
        var lines = new List<string>();
        for (int i = 0; i < b64.Length; i += 64)
            lines.Add(b64.Substring(i, Math.Min(64, b64.Length - i)));
        return "-----BEGIN PRIVATE KEY-----\n" +
               string.Join("\n", lines) +
               "\n-----END PRIVATE KEY-----\n";
    }

    private GitHubAppOptions _options = null!;
    private Mock<IOctokitClientFactory> _factory = null!;
    private Mock<IGitHubClient> _appClient = null!;
    private Mock<IGitHubClient> _installationClient = null!;
    private Mock<IGitHubAppsClient> _apps = null!;
    private Mock<IGitHubAppInstallationsClient> _installations = null!;
    private Mock<ILogger<OctokitGitHubAppClient>> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new GitHubAppOptions
        {
            AppId = 42,
            PrivateKeyPem = TestPrivateKey,
            UserAgent = "Tamma-Test"
        };

        _factory = new Mock<IOctokitClientFactory>();
        _appClient = new Mock<IGitHubClient>();
        _installationClient = new Mock<IGitHubClient>();
        _apps = new Mock<IGitHubAppsClient>();
        _installations = new Mock<IGitHubAppInstallationsClient>();

        _appClient.SetupGet(c => c.GitHubApps).Returns(_apps.Object);
        _installationClient.SetupGet(c => c.GitHubApps).Returns(_apps.Object);
        _apps.SetupGet(a => a.Installation).Returns(_installations.Object);

        _factory.Setup(f => f.CreateAppAuthenticatedClient(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(_appClient.Object);
        _factory.Setup(f => f.CreateInstallationAuthenticatedClient(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(_installationClient.Object);

        _logger = new Mock<ILogger<OctokitGitHubAppClient>>();
    }

    // ─── JWT construction ────────────────────────────────────────────────────

    [Test]
    public void Constructor_SignsJwtWithExpectedClaims()
    {
        string? capturedJwt = null;
        _factory.Setup(f => f.CreateAppAuthenticatedClient(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, jwt) => capturedJwt = jwt)
            .Returns(_appClient.Object);

        var installation = BuildInstallation(12345L, "acme", "Organization", 42);
        _apps.Setup(a => a.GetInstallationForCurrent(12345L)).ReturnsAsync(installation);

        using var client = new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        _ = client.GetInstallationAsync(12345L).GetAwaiter().GetResult();

        capturedJwt.Should().NotBeNullOrEmpty();
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(capturedJwt);
        token.Header.Alg.Should().Be("RS256");
        token.Issuer.Should().Be("42"); // AppId
        // Must be <= 10 minutes from now (per GitHub docs).
        var lifetime = token.ValidTo - DateTime.UtcNow;
        lifetime.TotalMinutes.Should().BeLessThanOrEqualTo(10.5);
        lifetime.TotalMinutes.Should().BeGreaterThan(0);
    }

    [Test]
    public void Constructor_ThrowsWhenAppIdMissing()
    {
        _options.AppId = 0;
        Action act = () => new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        act.Should().Throw<ArgumentException>().WithMessage("*AppId*");
    }

    [Test]
    public void Constructor_ThrowsWhenPrivateKeyMissing()
    {
        _options.PrivateKeyPem = string.Empty;
        Action act = () => new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        act.Should().Throw<ArgumentException>().WithMessage("*PrivateKey*");
    }

    // ─── GetInstallationAsync ────────────────────────────────────────────────

    [Test]
    public async Task GetInstallationAsync_ReturnsParsedAccount()
    {
        var installation = BuildInstallation(12345L, "acme-org", "Organization", 42);
        _apps.Setup(a => a.GetInstallationForCurrent(12345L)).ReturnsAsync(installation);

        using var client = new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        var result = await client.GetInstallationAsync(12345L);

        result.ServiceUnavailable.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.InstallationId.Should().Be(12345L);
        result.Result.AccountLogin.Should().Be("acme-org");
        result.Result.AccountType.Should().Be("Organization");
        result.Result.AppId.Should().Be(42);
    }

    [Test]
    public async Task GetInstallationAsync_OnNotFound_ReturnsFailed()
    {
        _apps.Setup(a => a.GetInstallationForCurrent(12345L))
            .ThrowsAsync(new NotFoundException("not found", System.Net.HttpStatusCode.NotFound));

        using var client = new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        var result = await client.GetInstallationAsync(12345L);

        result.ServiceUnavailable.Should().BeFalse();
        result.Result.Should().BeNull();
        result.ErrorReason.Should().Be("installation_not_found");
    }

    [Test]
    public async Task GetInstallationAsync_OnRateLimit_ReturnsFailedWithRateLimitReason()
    {
        // RateLimitExceededException takes an IResponse — we build a minimal
        // fake via a stubbed Mock.
        var response = BuildRateLimitedResponse();
        _apps.Setup(a => a.GetInstallationForCurrent(12345L))
            .ThrowsAsync(new RateLimitExceededException(response));

        using var client = new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        var result = await client.GetInstallationAsync(12345L);

        result.ErrorReason.Should().Be("github_rate_limited");
        _logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    // ─── ListInstallationReposAsync ──────────────────────────────────────────

    [Test]
    public async Task ListInstallationReposAsync_MintsTokenAndReturnsRepos()
    {
        _apps.Setup(a => a.CreateInstallationToken(12345L))
            .ReturnsAsync(new AccessToken("ghs_testtoken", DateTimeOffset.UtcNow.AddMinutes(60)));

        var repoResponse = new RepositoriesResponse(
            2,
            new[]
            {
                BuildRepository(1, "acme", "repo1"),
                BuildRepository(2, "acme", "repo2")
            });
        _installations.Setup(i => i.GetAllRepositoriesForCurrent(It.IsAny<ApiOptions>()))
            .ReturnsAsync(repoResponse);

        using var client = new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        var result = await client.ListInstallationReposAsync(12345L);

        result.ServiceUnavailable.Should().BeFalse();
        result.Result.Should().HaveCount(2);
        result.Result![0].FullName.Should().Be("acme/repo1");
        result.Result[1].FullName.Should().Be("acme/repo2");
    }

    [Test]
    public async Task ListInstallationReposAsync_CachesInstallationToken()
    {
        _apps.Setup(a => a.CreateInstallationToken(12345L))
            .ReturnsAsync(new AccessToken("ghs_testtoken", DateTimeOffset.UtcNow.AddMinutes(60)));
        _installations.Setup(i => i.GetAllRepositoriesForCurrent(It.IsAny<ApiOptions>()))
            .ReturnsAsync(new RepositoriesResponse(0, Array.Empty<Repository>()));

        using var client = new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        await client.ListInstallationReposAsync(12345L);
        await client.ListInstallationReposAsync(12345L);
        await client.ListInstallationReposAsync(12345L);

        // Token was minted exactly once (cached for 55 min).
        _apps.Verify(a => a.CreateInstallationToken(12345L), Times.Once);
    }

    [Test]
    public async Task GetInstallationClientAsync_ReturnsInstallationAuthedClient()
    {
        _apps.Setup(a => a.CreateInstallationToken(12345L))
            .ReturnsAsync(new AccessToken("ghs_test", DateTimeOffset.UtcNow.AddMinutes(60)));

        using var client = new OctokitGitHubAppClient(_options, _logger.Object, _factory.Object);
        var result = await client.GetInstallationClientAsync(12345L);

        result.Should().BeSameAs(_installationClient.Object);
        _factory.Verify(f => f.CreateInstallationAuthenticatedClient(
            It.IsAny<string>(), "ghs_test"), Times.Once);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // Octokit model types have mostly-constructors-with-many-arguments that
    // change between versions; JSON deserialization is the most stable seam.
    // The JSON is read-only for tests — no network involved.
    private static Installation BuildInstallation(long id, string accountLogin, string accountType, long appId)
    {
        var json = $$"""
            {
              "id": {{id}},
              "app_id": {{appId}},
              "account": { "login": "{{accountLogin}}", "type": "{{accountType}}", "id": 1 },
              "permissions": { "contents": "write", "metadata": "read" },
              "suspended_at": null,
              "target_id": 1,
              "target_type": "Organization",
              "repository_selection": "all"
            }
            """;
        return new SimpleJsonSerializer().Deserialize<Installation>(json);
    }

    private static Repository BuildRepository(long id, string owner, string name)
    {
        var json = $$"""
            {
              "id": {{id}},
              "name": "{{name}}",
              "full_name": "{{owner}}/{{name}}",
              "owner": { "login": "{{owner}}", "id": 1, "type": "Organization" }
            }
            """;
        return new SimpleJsonSerializer().Deserialize<Repository>(json);
    }

    /// <summary>
    /// Construct a minimal <see cref="IResponse"/> that makes
    /// <see cref="RateLimitExceededException"/> happy — it reads status code
    /// 403 plus <c>X-RateLimit-*</c> headers.
    /// </summary>
    private static IResponse BuildRateLimitedResponse()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-RateLimit-Limit"] = "5000",
            ["X-RateLimit-Remaining"] = "0",
            ["X-RateLimit-Reset"] = reset.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return new FakeResponse(
            System.Net.HttpStatusCode.Forbidden,
            "{\"message\":\"API rate limit exceeded\"}",
            headers);
    }

    /// <summary>
    /// Minimal <see cref="IResponse"/> — Octokit's <c>Internal.Response</c> is
    /// inaccessible from tests, so we shim our own. Only the members
    /// <see cref="RateLimitExceededException"/> reads need meaningful values.
    /// </summary>
    private sealed class FakeResponse : IResponse
    {
        public FakeResponse(System.Net.HttpStatusCode status, string body, IDictionary<string, string> headers)
        {
            StatusCode = status;
            Body = body;
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            ApiInfo = ApiInfoParserShim.Build(Headers);
            ContentType = "application/json";
        }

        public object? Body { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public ApiInfo ApiInfo { get; }
        public System.Net.HttpStatusCode StatusCode { get; }
        public string ContentType { get; }
    }

    /// <summary>Mirror of Octokit.Internal.ApiInfoParser — extracts the
    /// rate-limit headers we care about into a real <see cref="ApiInfo"/>.
    /// We can't call the internal parser, so we build the shape by hand.</summary>
    private static class ApiInfoParserShim
    {
        public static ApiInfo Build(IReadOnlyDictionary<string, string> headers)
        {
            var limit = headers.TryGetValue("X-RateLimit-Limit", out var l) ? int.Parse(l) : 0;
            var remaining = headers.TryGetValue("X-RateLimit-Remaining", out var r) ? int.Parse(r) : 0;
            var reset = headers.TryGetValue("X-RateLimit-Reset", out var rr)
                ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(rr))
                : DateTimeOffset.UtcNow;
            return new ApiInfo(
                links: new Dictionary<string, Uri>(),
                oauthScopes: new List<string>(),
                acceptedOauthScopes: new List<string>(),
                etag: string.Empty,
                rateLimit: new RateLimit(limit, remaining, reset.ToUnixTimeSeconds()),
                serverTimeDifference: TimeSpan.Zero);
        }
    }
}
