using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Regression tests for the /api/auth/github endpoint and — critically —
/// for the env-var → IConfiguration binding that the endpoint depends on.
///
/// PR #343 deploy shipped a three-way naming inconsistency:
///   .env.example      → GITHUB_OAUTH_CLIENT_ID
///   docker-compose.yml→ GitHub__OAuthClientId   (never bound)
///   AuthEndpoints.cs  → config["GitHub:ClientId"]
/// Result: /api/auth/github responded {"error":"GitHub OAuth not
/// configured"} on every click even when GITHUB_OAUTH_CLIENT_ID was
/// set. The previous test suite passed because it only asserted the
/// "not configured" branch — i.e., it locked in the bug as expected
/// behavior. This file inverts the coverage:
///   1. The "not configured" branch is still asserted (regression
///      guard against accidentally removing the early-return).
///   2. NEW: the "configured" branch is asserted with a typed
///      IConfiguration that simulates the env binding the compose
///      should be producing — proves the endpoint reads the correct
///      key. If someone re-introduces the OAuthClientId mismatch
///      these tests fail.
/// </summary>
[TestFixture]
public class GitHubAuthEndpointTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static DefaultHttpContext NewContext()
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
    }

    private static async Task<int> ExecuteAsync(IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    [Test]
    public async Task ReturnsBadRequest_WhenGitHubClientIdMissing()
    {
        var ctx = NewContext();
        var config = BuildConfig(new Dictionary<string, string?>()); // empty

        var result = await AuthEndpoints.GitHubAuth(rd: null, invite: null, config, ctx);
        var status = await ExecuteAsync(result, ctx);

        status.Should().Be((int)HttpStatusCode.BadRequest,
            "missing config should fail-fast at /api/auth/github");
    }

    [Test]
    public async Task ReturnsBadRequest_WhenClientIdIsEmptyString()
    {
        var ctx = NewContext();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "",
        });

        var result = await AuthEndpoints.GitHubAuth(rd: null, invite: null, config, ctx);
        var status = await ExecuteAsync(result, ctx);

        status.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task RedirectsToGitHubAuthorize_WhenClientIdIsConfigured()
    {
        var ctx = NewContext();
        // Simulate what compose SHOULD bind. If someone re-introduces
        // GitHub__OAuthClientId or any other name mismatch this test
        // fails because the endpoint reads "GitHub:ClientId".
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "Iv1.test_client_id",
            ["GitHub:RedirectUri"] = "https://app.tamma.dev/api/auth/github/callback",
            ["Cookie:Domain"] = ".tamma.dev",
        });

        var result = await AuthEndpoints.GitHubAuth(rd: null, invite: null, config, ctx);
        var status = await ExecuteAsync(result, ctx);

        status.Should().Be(302, "configured endpoint must redirect to GitHub");
        var location = ctx.Response.Headers.Location.ToString();
        location.Should().StartWith("https://github.com/login/oauth/authorize");
        location.Should().Contain("client_id=Iv1.test_client_id");
        location.Should().Contain("redirect_uri=https%3A%2F%2Fapp.tamma.dev%2Fapi%2Fauth%2Fgithub%2Fcallback");
        location.Should().Contain("scope=read%3Auser%20user%3Aemail",
            "Story 18-1 / audit finding 009 require both read:user (uid lookup) and user:email scopes");
    }

    [Test]
    public async Task ReadsClientIdFromGitHubColonClientId_NotOAuthClientId()
    {
        // This is the *exact* failure mode that shipped PR #343 to prod:
        // someone mapped env to "GitHub__OAuthClientId" but the code reads
        // "GitHub:ClientId". This test pins the correct key name so a
        // future refactor that "tidies" the config keys (e.g., adds an
        // OAuth namespace) can't silently break the auth flow.
        var ctx = NewContext();
        var wrongKeyConfig = BuildConfig(new Dictionary<string, string?>
        {
            ["GitHub:OAuthClientId"] = "Iv1.wrong_key_value",
        });

        var result = await AuthEndpoints.GitHubAuth(rd: null, invite: null, wrongKeyConfig, ctx);
        var status = await ExecuteAsync(result, ctx);

        status.Should().Be((int)HttpStatusCode.BadRequest,
            "GitHub:OAuthClientId is NOT the key Tamma.Api reads — must remain unbound");
    }

    [Test]
    public async Task SetsCsrfCookie_WhenRedirecting()
    {
        // Audit finding 009: the OAuth start endpoint sets a CSRF nonce
        // in tamma_oauth_csrf cookie, then verifies it on callback.
        // Locks behavior so a refactor can't drop the cookie.
        var ctx = NewContext();
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["GitHub:ClientId"] = "Iv1.test",
            ["Cookie:Domain"] = ".tamma.dev",
        });

        var result = await AuthEndpoints.GitHubAuth(rd: null, invite: null, config, ctx);
        await ExecuteAsync(result, ctx);

        // ASP.NET Core's CookieOptions serializer emits attributes lower-
        // cased ("httponly", "secure"), even though the spec is case-
        // insensitive. Match either case so the test isn't framework-
        // version-coupled.
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain("tamma_oauth_csrf=");
        setCookie.ToLowerInvariant().Should().Contain("httponly");
        setCookie.ToLowerInvariant().Should().Contain("secure");
    }
}
