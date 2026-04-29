using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.GitLab;
using Tamma.Platforms.GitLab.Tests.Support;

namespace Tamma.Platforms.GitLab.Tests;

[TestFixture]
public sealed class GitLabHttpClientTests
{
    [Test]
    public void NormalizeBaseUrl_appends_api_v4_when_missing()
    {
        var uri = GitLabHttpClient.NormalizeBaseUrl("https://gitlab.example.com");
        uri.ToString().Should().Be("https://gitlab.example.com/api/v4/");
    }

    [Test]
    public void NormalizeBaseUrl_preserves_api_v4_when_present()
    {
        var uri = GitLabHttpClient.NormalizeBaseUrl("https://gitlab.example.com/api/v4");
        uri.ToString().Should().Be("https://gitlab.example.com/api/v4/");
    }

    [Test]
    public void NormalizeBaseUrl_handles_trailing_slash()
    {
        var uri = GitLabHttpClient.NormalizeBaseUrl("https://gitlab.example.com/api/v4/");
        uri.ToString().Should().Be("https://gitlab.example.com/api/v4/");
    }

    [Test]
    public void NormalizeBaseUrl_handles_root_with_trailing_slash()
    {
        var uri = GitLabHttpClient.NormalizeBaseUrl("https://gitlab.example.com/");
        uri.ToString().Should().Be("https://gitlab.example.com/api/v4/");
    }

    [Test]
    public async Task PrivateToken_header_attached_for_PAT()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(HttpMethod.Get, "/projects/1", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var http = new HttpClient(handler);
        using var typed = new GitLabHttpClient(
            http, new GitLabAuth.PersonalAccessToken("glpat-secret"),
            "https://gitlab.example.com");

        using var req = new HttpRequestMessage(HttpMethod.Get, typed.BuildUri("projects/1"));
        using var resp = await typed.SendAsync(req, CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Headers.Should().ContainKey("PRIVATE-TOKEN");
        handler.Requests[0].Headers["PRIVATE-TOKEN"].Should().Be("glpat-secret");
    }

    [Test]
    public async Task Authorization_Bearer_header_attached_for_OAuth2()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(HttpMethod.Get, "/projects/1", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var http = new HttpClient(handler);
        using var typed = new GitLabHttpClient(
            http, new GitLabAuth.OAuth2("oauth-token"),
            "https://gitlab.example.com");

        using var req = new HttpRequestMessage(HttpMethod.Get, typed.BuildUri("projects/1"));
        using var resp = await typed.SendAsync(req, CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Headers.Should().ContainKey("Authorization");
        handler.Requests[0].Headers["Authorization"].Should().Be("Bearer oauth-token");
    }

    [Test]
    public async Task RetryAfter_header_surfaces_on_429()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(HttpMethod.Get, "/projects/1", _ =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"message\":\"rate limited\"}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.TryAddWithoutValidation("Retry-After", "60");
            return resp;
        });
        var http = new HttpClient(handler);
        using var typed = new GitLabHttpClient(
            http, new GitLabAuth.PersonalAccessToken("t"), "https://gitlab.example.com");

        using var req = new HttpRequestMessage(HttpMethod.Get, typed.BuildUri("projects/1"));
        using var resp = await typed.SendAsync(req, CancellationToken.None);

        resp.Response.StatusCode.Should().Be((HttpStatusCode)429);
        resp.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task RateLimit_remaining_and_reset_headers_parsed()
    {
        var handler = new FakeHttpMessageHandler();
        var resetUnix = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds();
        handler.AddRoute(HttpMethod.Get, "/projects/1", _ =>
        {
            var resp = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
            resp.Headers.TryAddWithoutValidation("RateLimit-Remaining", "42");
            resp.Headers.TryAddWithoutValidation("RateLimit-Reset", resetUnix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return resp;
        });
        var http = new HttpClient(handler);
        using var typed = new GitLabHttpClient(
            http, new GitLabAuth.PersonalAccessToken("t"), "https://gitlab.example.com");

        using var req = new HttpRequestMessage(HttpMethod.Get, typed.BuildUri("projects/1"));
        using var resp = await typed.SendAsync(req, CancellationToken.None);

        resp.RateLimitRemaining.Should().Be(42);
        resp.RateLimitResetsAt.Should().BeCloseTo(DateTimeOffset.FromUnixTimeSeconds(resetUnix), TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task EnumeratePagesAsync_follows_link_header_next()
    {
        var handler = new FakeHttpMessageHandler();
        // Page 1 returns 2 items + Link: <...?page=2>; rel="next"
        handler.EnqueueResponse(_ =>
        {
            var resp = FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                "[{\"name\":\"a\"},{\"name\":\"b\"}]");
            resp.Headers.TryAddWithoutValidation(
                "Link",
                "<https://gitlab.example.com/api/v4/projects/1/repository/branches?per_page=100&page=2>; rel=\"next\"");
            return resp;
        });
        // Page 2 returns 1 item, no next.
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "[{\"name\":\"c\"}]"));
        var http = new HttpClient(handler);
        using var typed = new GitLabHttpClient(
            http, new GitLabAuth.PersonalAccessToken("t"), "https://gitlab.example.com");

        var items = new List<TestItem>();
        await foreach (var item in typed.EnumeratePagesAsync<TestItem>("projects/1/repository/branches"))
        {
            items.Add(item);
        }

        items.Should().HaveCount(3);
        items.Select(i => i.Name).Should().BeEquivalentTo(new[] { "a", "b", "c" });
        handler.Requests.Should().HaveCount(2);
    }

    [Test]
    public async Task EnumeratePagesAsync_caps_at_max_items()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueResponse(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                "[{\"name\":\"a\"},{\"name\":\"b\"},{\"name\":\"c\"}]"));
        var http = new HttpClient(handler);
        using var typed = new GitLabHttpClient(
            http, new GitLabAuth.PersonalAccessToken("t"), "https://gitlab.example.com");

        var items = new List<TestItem>();
        await foreach (var item in typed.EnumeratePagesAsync<TestItem>("x", maxItems: 2))
        {
            items.Add(item);
        }

        items.Should().HaveCount(2);
    }

    [Test]
    public void EnumeratePagesAsync_throws_GitLabRequestException_on_4xx()
    {
        var handler = new FakeHttpMessageHandler();
        handler.AddRoute(HttpMethod.Get, "/projects/1", _ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"404 Not Found\"}"));
        var http = new HttpClient(handler);
        var typed = new GitLabHttpClient(
            http, new GitLabAuth.PersonalAccessToken("t"), "https://gitlab.example.com",
            ownsHttpClient: true);

        Func<Task> act = async () =>
        {
            await foreach (var _ in typed.EnumeratePagesAsync<TestItem>("projects/1"))
            {
                // drain
            }
        };
        act.Should().ThrowAsync<GitLabRequestException>();
    }

    [Test]
    public void ExtractNextLink_returns_null_when_no_link_header()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK);
        var next = GitLabHttpClient.ExtractNextLink(resp.Headers);
        next.Should().BeNull();
    }

    [Test]
    public void ExtractNextLink_parses_multi_rel_link()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Headers.TryAddWithoutValidation(
            "Link",
            "<https://gitlab.example.com/p?page=2>; rel=\"next\", <https://gitlab.example.com/p?page=10>; rel=\"last\"");

        var next = GitLabHttpClient.ExtractNextLink(resp.Headers);
        next.Should().Be("https://gitlab.example.com/p?page=2");
    }

    [Test]
    public void ExtractNextLink_returns_null_when_only_last_rel_present()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Headers.TryAddWithoutValidation(
            "Link",
            "<https://gitlab.example.com/p?page=10>; rel=\"last\"");

        var next = GitLabHttpClient.ExtractNextLink(resp.Headers);
        next.Should().BeNull();
    }

    private sealed class TestItem
    {
        public string? Name { get; set; }
    }
}
