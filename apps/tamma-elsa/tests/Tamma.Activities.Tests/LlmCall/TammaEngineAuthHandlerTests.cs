using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.Context;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Production blocker fix — verifies <see cref="TammaEngineAuthHandler"/>
/// stamps <c>Authorization: Bearer &lt;token&gt;</c> on every outgoing
/// request when <c>Tamma:ApiToken</c> is configured, AND that the named
/// <c>"tamma-engine"</c> client wiring actually carries the header all the
/// way through both resolve activities' static <c>CallResolveAsync</c>
/// helpers (which build their own <see cref="HttpRequestMessage"/>).
///
/// <para>
/// The two activity-level tests assert the end-to-end contract: when a
/// caller wires the auth handler into the HttpClient passed into
/// <c>CallResolveAsync</c>, the API receives a Bearer token. This is the
/// real production-blocker fix.
/// </para>
/// </summary>
[TestFixture]
public class TammaEngineAuthHandlerTests
{
    // ============================================================
    // Handler-level behaviour
    // ============================================================

    [Test]
    public async Task TokenConfigured_AddsBearerHeader()
    {
        var captured = new CapturingHandler();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiToken"] = "test-token-abc",
            })
            .Build();
        var authHandler = new TammaEngineAuthHandler(cfg) { InnerHandler = captured };
        using var client = new HttpClient(authHandler);

        var response = await client.GetAsync("http://test/foo");

        response.IsSuccessStatusCode.Should().BeTrue();
        captured.LastRequest!.Headers.Authorization.Should().NotBeNull();
        captured.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.LastRequest.Headers.Authorization.Parameter.Should().Be("test-token-abc");
    }

    [Test]
    public async Task NoToken_IsNoOp()
    {
        var captured = new CapturingHandler();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var authHandler = new TammaEngineAuthHandler(cfg) { InnerHandler = captured };
        using var client = new HttpClient(authHandler);

        await client.GetAsync("http://test/foo");

        captured.LastRequest!.Headers.Authorization.Should().BeNull();
    }

    [Test]
    public async Task ExplicitCallerHeader_NotClobbered()
    {
        // If the caller already set an Authorization header, the handler
        // must NOT clobber it. (None of our activities do this today, but
        // it's the canonical DelegatingHandler contract.)
        var captured = new CapturingHandler();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiToken"] = "handler-token",
            })
            .Build();
        var authHandler = new TammaEngineAuthHandler(cfg) { InnerHandler = captured };
        using var client = new HttpClient(authHandler);

        using var req = new HttpRequestMessage(HttpMethod.Get, "http://test/foo");
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "caller-token");

        await client.SendAsync(req);

        captured.LastRequest!.Headers.Authorization!.Parameter.Should().Be("caller-token");
    }

    // ============================================================
    // End-to-end: header flows through the resolve activities'
    // static CallResolveAsync helpers when the handler is wired in.
    // ============================================================

    [Test]
    public async Task ConventionsActivity_WithAuthHandler_SendsBearerHeader()
    {
        string? capturedAuth = null;
        var capturing = new CapturingHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    role = "developer",
                    action = "implement-feature",
                    body = "ok",
                    source = "system",
                    version = 1,
                }),
            };
        });
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiToken"] = "prod-token-xyz",
            })
            .Build();
        var authHandler = new TammaEngineAuthHandler(cfg) { InnerHandler = capturing };
        using var client = new HttpClient(authHandler);

        await ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "");

        capturedAuth.Should().Be("Bearer prod-token-xyz");
    }

    [Test]
    public async Task PromptActivity_WithAuthHandler_SendsBearerHeader()
    {
        string? capturedAuth = null;
        var capturing = new CapturingHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    renderedTemplate = "Hello",
                    renderedSystemPrompt = "Sys",
                    enableTools = false,
                    maxTokens = 4096,
                }),
            };
        });
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ApiToken"] = "prod-token-xyz",
            })
            .Build();
        var authHandler = new TammaEngineAuthHandler(cfg) { InnerHandler = capturing };
        using var client = new HttpClient(authHandler);

        await ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature",
            tenantId: "", new Dictionary<string, object>());

        capturedAuth.Should().Be("Bearer prod-token-xyz");
    }

    [Test]
    public async Task ConventionsActivity_NoToken_NoAuthHeader_DevMode()
    {
        // Dev mode (no Tamma:ApiToken configured): the handler is a no-op
        // and the request goes out without an Authorization header — the
        // API's AllowAnonymousHandler short-circuits in Development and
        // accepts it.
        bool hasAuthHeader = true;
        var capturing = new CapturingHandler(req =>
        {
            hasAuthHeader = req.Headers.Authorization is not null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    role = "developer",
                    action = "implement-feature",
                    body = "ok",
                    source = "system",
                    version = 1,
                }),
            };
        });
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var authHandler = new TammaEngineAuthHandler(cfg) { InnerHandler = capturing };
        using var client = new HttpClient(authHandler);

        await ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "");

        hasAuthHeader.Should().BeFalse();
    }

    // ============================================================
    // Test helpers
    // ============================================================

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler() { }
        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = _responder?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(response);
        }
    }
}
