using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P1 stage 2 — tests for the absorbed App-installation token
/// minting: RS256 App JWT shape + signature, token caching, and the
/// refresh-on-401 seam via <see cref="GitHubHttpClient"/>.
/// </summary>
[TestFixture]
public sealed class GitHubAppTokenMinterTests
{
    private const string Api = "https://api.github.com";

    private static RSA NewKey() => RSA.Create(2048);

    private static GitHubAppTokenMinter Minter(
        FakeHttpMessageHandler handler, RSA key, long appId = 123, long installationId = 456) =>
        new(new HttpClient(handler), Api, appId, key.ExportRSAPrivateKeyPem(), installationId);

    [Test]
    public void CreateAppJwt_is_a_valid_RS256_jwt_with_app_id_issuer()
    {
        using var key = NewKey();
        var minter = Minter(new FakeHttpMessageHandler(), key);

        var jwt = minter.CreateAppJwt();

        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);

        var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        header.RootElement.GetProperty("alg").GetString().Should().Be("RS256");

        var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().Should().Be("123");
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        (exp - iat).Should().BeLessThanOrEqualTo(600, "GitHub caps App JWTs at 10 minutes");

        // Verify the signature against the public key — the JWT is
        // hand-rolled, so pin the actual crypto, not just the shape.
        var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
        var signature = Convert.FromBase64String(PadBase64(parts[2]));
        key.VerifyData(signingInput, signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
    }

    [Test]
    public async Task GetInstallationToken_mints_and_caches()
    {
        using var key = NewKey();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/app/installations/456/access_tokens",
            HttpStatusCode.Created,
            $$"""{ "token": "ghs_1", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
        var minter = Minter(handler, key);

        var first = await minter.GetInstallationTokenAsync(forceRefresh: false, CancellationToken.None);
        var second = await minter.GetInstallationTokenAsync(forceRefresh: false, CancellationToken.None);

        first.Should().Be("ghs_1");
        second.Should().Be("ghs_1");
        handler.Requests.Should().HaveCount(1, "the second call is served from cache");
    }

    [Test]
    public async Task Invalidate_forces_a_fresh_mint()
    {
        using var key = NewKey();
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/app/installations/456/access_tokens",
            HttpStatusCode.Created,
            $$"""{ "token": "ghs_1", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/app/installations/456/access_tokens",
            HttpStatusCode.Created,
            $$"""{ "token": "ghs_2", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
        var minter = Minter(handler, key);

        (await minter.GetInstallationTokenAsync(false, CancellationToken.None)).Should().Be("ghs_1");
        minter.Invalidate();
        (await minter.GetInstallationTokenAsync(false, CancellationToken.None)).Should().Be("ghs_2");
    }

    [Test]
    public async Task HttpClient_retries_once_with_fresh_token_on_401()
    {
        using var key = NewKey();
        var handler = new FakeHttpMessageHandler();
        // Two mints: the initial token and the refresh after the 401.
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/app/installations/456/access_tokens",
            HttpStatusCode.Created,
            $$"""{ "token": "ghs_stale", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
        handler.EnqueueJson(HttpMethod.Post, $"{Api}/app/installations/456/access_tokens",
            HttpStatusCode.Created,
            $$"""{ "token": "ghs_fresh", "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}" }""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r",
            HttpStatusCode.Unauthorized, """{ "message": "Bad credentials" }""");
        handler.EnqueueJson(HttpMethod.Get, $"{Api}/repos/o/r",
            HttpStatusCode.OK, """{ "name": "r" }""");

        var httpClient = new HttpClient(handler);
        var minter = new GitHubAppTokenMinter(
            httpClient, Api, 123, key.ExportRSAPrivateKeyPem(), 456);
        var app = new GitHubAuth.App(123, key.ExportRSAPrivateKeyPem(), 456);
        var github = new GitHubHttpClient(httpClient, Api, app, minter);
        var client = new GitHubPlatformClient(github, "github.com", appMode: true);

        var result = await client.GetRepoAsync("o", "r");

        result.IsOk.Should().BeTrue("a 401 in App mode invalidates the token and retries once");
        var repoCalls = handler.Requests.Where(r => r.Url.EndsWith("/repos/o/r")).ToList();
        repoCalls.Should().HaveCount(2);
        repoCalls[0].Headers["Authorization"].Should().Be("Bearer ghs_stale");
        repoCalls[1].Headers["Authorization"].Should().Be("Bearer ghs_fresh");
    }

    [Test]
    public void Constructor_fails_loud_on_malformed_pem()
    {
        Action act = () => new GitHubAppTokenMinter(
            new HttpClient(new FakeHttpMessageHandler()), Api, 123, "not-a-pem", 456);
        act.Should().Throw<ArgumentException>();
    }

    private static string FromBase64Url(string s) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(s)));

    private static string PadBase64(string s)
    {
        var normalized = s.Replace('-', '+').Replace('_', '/');
        return normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
    }
}
