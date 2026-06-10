using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Context;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Activities.Tests.Context;

/// <summary>
/// Story 27-13 — tests for <see cref="ResolveConventionsActivity"/>. The
/// activity body inlines the Elsa <c>ActivityExecutionContext</c> interaction
/// (which can't be cheaply mocked — see <c>ResolvePromptFromRegistryActivityTests</c>
/// and <c>CheckBudgetActivityEmissionTests</c>), so we exercise the extracted
/// static helpers (<see cref="ResolveConventionsActivity.ValidateTaxonomy"/>
/// and <see cref="ResolveConventionsActivity.CallResolveAsync"/>) directly.
/// This covers the fail-loud contract: an invalid <c>(role, action)</c>
/// throws <see cref="ArgumentException"/>; a registry miss / unreachable
/// store throws <see cref="TammaError"/> with the documented codes; a happy
/// path returns the resolved body + source + version.
/// </summary>
[TestFixture]
public class ResolveConventionsActivityTests
{
    // ============================================================
    // ValidateTaxonomy — boundary parse mirrors the prompt activity
    // ============================================================

    [Test]
    public void ValidateTaxonomy_ValidPair_DoesNotThrow()
    {
        var act = () => ResolveConventionsActivity.ValidateTaxonomy(
            AgentRole.Developer.ToWire(), AgentAction.ImplementFeature.ToWire());

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateTaxonomy_UnknownRole_Throws()
    {
        var act = () => ResolveConventionsActivity.ValidateTaxonomy(
            "not-a-role", "implement-feature");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_UnknownAction_Throws()
    {
        var act = () => ResolveConventionsActivity.ValidateTaxonomy(
            "developer", "not-a-real-action");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_DeadLegacyGenericAction_Throws()
    {
        // 'implement' was the old flat-vocabulary generic action; per Story
        // 27-15/27-18 it's no longer a taxonomy token → fail-fast.
        var act = () => ResolveConventionsActivity.ValidateTaxonomy(
            "developer", "implement");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_LegacyRoleAlias_IsAccepted()
    {
        // RolePhaseMap normalises legacy TS role aliases (implementer→developer).
        var act = () => ResolveConventionsActivity.ValidateTaxonomy(
            "implementer", "implement-feature");

        act.Should().NotThrow();
    }

    // ============================================================
    // CallResolveAsync — HTTP boundary behaviour (the core contract)
    // ============================================================

    [Test]
    public async Task CallResolveAsync_HappyPath_ReturnsBodyAndSource()
    {
        var json = JsonSerializer.Serialize(new
        {
            role = "developer",
            action = "implement-feature",
            body = "Use camelCase for variables.",
            source = "tenant",
            version = 3,
        });
        var handler = new StubHandler(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/api/conventions/resolve");
            req.Method.Should().Be(HttpMethod.Post);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        using var client = new HttpClient(handler);
        var (body, source, version) = await ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "t-1");

        body.Should().Be("Use camelCase for variables.");
        source.Should().Be("tenant");
        version.Should().Be(3);
    }

    [Test]
    public async Task CallResolveAsync_PostsRoleAndActionInBody()
    {
        string? capturedBody = null;
        var handler = new StubHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { role = "developer", action = "implement-feature", body = "ok", source = "system", version = 1 }),
            };
        });

        using var client = new HttpClient(handler);
        await ResolveConventionsActivity.CallResolveAsync(
            client, "http://test/", "developer", "implement-feature", tenantId: "");

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("role").GetString().Should().Be("developer");
        doc.RootElement.GetProperty("action").GetString().Should().Be("implement-feature");
    }

    [Test]
    public async Task CallResolveAsync_NotFound_ThrowsNoRow()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"CONVENTION_NOT_FOUND\"}", Encoding.UTF8, "application/json"),
        });

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "t-1");

        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("LLM.CONVENTIONS.RESOLVE.NO_ROW");
    }

    [Test]
    public async Task CallResolveAsync_NetworkError_ThrowsRegistryUnavailable()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("connection refused")));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "t-1");

        var ex = (await act.Should().ThrowAsync<TammaError>())
            .Which;
        ex.Code.Should().Be("LLM.CONVENTIONS.RESOLVE.REGISTRY_UNAVAILABLE");
        ex.Retryable.Should().BeTrue();
    }

    [Test]
    public async Task CallResolveAsync_500_ThrowsRegistryUnavailableRetryable()
    {
        // 5xx is a transient server fault (DB outage, unhandled exception).
        // Must be REGISTRY_UNAVAILABLE + retryable=true, never NO_ROW.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "");

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.CONVENTIONS.RESOLVE.REGISTRY_UNAVAILABLE");
        ex.Retryable.Should().BeTrue();
        ex.Context.Should().ContainKey("status");
        ex.Context["status"].Should().Be(500);
    }

    [Test]
    public async Task CallResolveAsync_503_ThrowsRegistryUnavailableRetryable()
    {
        // 503 Service Unavailable — also a transient 5xx.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "");

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.CONVENTIONS.RESOLVE.REGISTRY_UNAVAILABLE");
        ex.Retryable.Should().BeTrue();
    }

    [Test]
    public async Task CallResolveAsync_TrimsTrailingSlashOnCallbackUrl()
    {
        Uri? capturedUri = null;
        var handler = new StubHandler(req =>
        {
            capturedUri = req.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { role = "developer", action = "implement-feature", body = "x", source = "system", version = 1 }),
            };
        });

        using var client = new HttpClient(handler);
        await ResolveConventionsActivity.CallResolveAsync(
            client, "http://test/", "developer", "implement-feature", tenantId: "");

        capturedUri!.AbsoluteUri.Should().Be("http://test/api/conventions/resolve");
    }

    [Test]
    public async Task CallResolveAsync_WithTenantId_SendsXTenantIdHeader()
    {
        // Fix D: X-Tenant-Id must be set on the outgoing request (not on
        // DefaultRequestHeaders) so it is visible in per-request tests.
        string? capturedTenantId = null;
        var handler = new StubHandler(req =>
        {
            req.Headers.TryGetValues("X-Tenant-Id", out var values);
            capturedTenantId = values?.FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { role = "developer", action = "implement-feature", body = "ok", source = "system", version = 1 }),
            };
        });

        using var client = new HttpClient(handler);
        await ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "tenant-abc");

        capturedTenantId.Should().Be("tenant-abc");
    }

    [Test]
    public async Task CallResolveAsync_NoTenantId_DoesNotSendXTenantIdHeader()
    {
        // When tenantId is empty, no X-Tenant-Id header should be forwarded.
        bool headerPresent = false;
        var handler = new StubHandler(req =>
        {
            headerPresent = req.Headers.Contains("X-Tenant-Id");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { role = "developer", action = "implement-feature", body = "ok", source = "system", version = 1 }),
            };
        });

        using var client = new HttpClient(handler);
        await ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "");

        headerPresent.Should().BeFalse();
    }

    [Test]
    public async Task CallResolveAsync_401_ThrowsNoRowNonRetryable()
    {
        // 4xx other than 404: permanent client-side fault — non-retryable NO_ROW.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolveConventionsActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "");

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.CONVENTIONS.RESOLVE.NO_ROW");
        ex.Retryable.Should().BeFalse();
    }

    // ============================================================
    // Empty-action legacy passthrough
    // ============================================================

    [Test]
    public void ValidateTaxonomy_EmptyAction_ShouldNotBeCalledOnLegacyPath()
    {
        // When action is empty the activity short-circuits BEFORE calling
        // ValidateTaxonomy — the legacy conventions string is passed through
        // directly without any HTTP call or taxonomy parse. This test documents
        // the contract: ValidateTaxonomy itself rejects empty action (ParseAction
        // would throw), confirming that the RunAsync guard is load-bearing.
        var act = () => ResolveConventionsActivity.ValidateTaxonomy("developer", "");

        act.Should().Throw<ArgumentException>(
            "an empty action is not a taxonomy token; RunAsync guards it before this call");
    }

    // ============================================================
    // Test helpers
    // ============================================================

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> stub — invokes a caller-
    /// supplied delegate so each test owns its response shape.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(req => Task.FromResult(handler(req))) { }

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }
}
