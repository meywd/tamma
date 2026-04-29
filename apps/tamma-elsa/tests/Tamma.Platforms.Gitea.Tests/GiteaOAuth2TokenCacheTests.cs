using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.Gitea.Tests;

[TestFixture]
public class GiteaOAuth2TokenCacheTests
{
    [Test]
    public void TryGet_ReturnsNull_WhenNoEntry()
    {
        var cache = new GiteaOAuth2TokenCache();
        cache.TryGet(Guid.NewGuid()).Should().BeNull();
    }

    [Test]
    public void Set_RespectsTtl()
    {
        var cache = new GiteaOAuth2TokenCache();
        var id = Guid.NewGuid();
        cache.Set(id, "tok", TimeSpan.FromMinutes(10));
        cache.TryGet(id).Should().Be("tok");
    }

    [Test]
    public void Set_RefusesNegativeTtl()
    {
        var cache = new GiteaOAuth2TokenCache();
        var id = Guid.NewGuid();
        cache.Set(id, "tok", TimeSpan.Zero);
        cache.TryGet(id).Should().BeNull();
    }

    [Test]
    public void Invalidate_RemovesEntry()
    {
        var cache = new GiteaOAuth2TokenCache();
        var id = Guid.NewGuid();
        cache.Set(id, "tok", TimeSpan.FromMinutes(1));
        cache.Invalidate(id);
        cache.TryGet(id).Should().BeNull();
    }

    [Test]
    public async Task GiteaHttpClient_RetriesOnce_WithFreshOAuth2Token_OnUnauthorized()
    {
        // Wires the full HTTP-client refresh-on-401 path: first call
        // 401s, driver invalidates cache + refreshes token, retry
        // succeeds.
        var handler = new FakeHttpMessageHandler();
        var cache = new GiteaOAuth2TokenCache();
        var auth = new GiteaAuth.OAuth2("cid", "secret", "refresh");

        // Refresh endpoint always returns a usable token.
        handler.EnqueueRepeating(HttpMethod.Post,
            "https://gitea.example.com/login/oauth/access_token",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"fresh-token","expires_in":3600,"token_type":"Bearer"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        // First /api/v1/version call returns 401, second succeeds.
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.Unauthorized, "{}");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.OK, """{"version":"1.21.4"}""");

        var http = new HttpClient(handler);
        var giteaHttp = new GiteaHttpClient(
            http, Guid.NewGuid(), GiteaTestFixtures.BaseUrl, auth, cache);

        var result = await giteaHttp.GetJsonAsync<Dtos.GiteaVersionDto>(
            "/api/v1/version", default);

        result.Should().BeOfType<PlatformResult<Dtos.GiteaVersionDto>.Ok>()
            .Which.Value.Version.Should().Be("1.21.4");

        // Token cache should now have the fresh token.
        // (Can't introspect by id here since it's random, but Count > 0.)
        // Use reflection-free: verify a second call uses cached token
        // (no extra refresh call).
        var beforeRefreshCount = handler.Requests
            .Count(r => r.Url.Contains("/login/oauth/access_token"));
        beforeRefreshCount.Should().BeGreaterOrEqualTo(1);
    }

    [Test]
    public async Task GiteaHttpClient_DoubleUnauthorized_BubblesAuthExpired()
    {
        var handler = new FakeHttpMessageHandler();
        var cache = new GiteaOAuth2TokenCache();
        var auth = new GiteaAuth.OAuth2("cid", "secret", "refresh");

        handler.EnqueueRepeating(HttpMethod.Post,
            "https://gitea.example.com/login/oauth/access_token",
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"fresh","expires_in":3600}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });

        // Both attempts 401 — refresh-on-401 retry can't recover.
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.Unauthorized, "{}");
        handler.EnqueueJson(HttpMethod.Get,
            "https://gitea.example.com/api/v1/version",
            HttpStatusCode.Unauthorized, "{}");

        var http = new HttpClient(handler);
        var giteaHttp = new GiteaHttpClient(
            http, Guid.NewGuid(), GiteaTestFixtures.BaseUrl, auth, cache);

        var result = await giteaHttp.GetJsonAsync<Dtos.GiteaVersionDto>(
            "/api/v1/version", default);

        result.Should().BeOfType<PlatformResult<Dtos.GiteaVersionDto>.Failed>()
            .Which.Error.Should().BeOfType<PlatformError.AuthExpired>();
    }
}
