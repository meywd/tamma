using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Story 27-18 — boundary taxonomy validation and HTTP error-code contract for
/// <see cref="ResolvePromptFromRegistryActivity"/>. The activity body inlines
/// the Elsa <c>ActivityExecutionContext</c> interaction (which can't be cheaply
/// mocked — see <c>CheckBudgetActivityEmissionTests</c>), so we exercise the
/// extracted static helpers (<see cref="ResolvePromptFromRegistryActivity.ValidateTaxonomy"/>
/// and <see cref="ResolvePromptFromRegistryActivity.CallResolveAsync"/>) directly.
/// This proves the fail-fast contract: an invalid <c>(role, action)</c> throws
/// rather than degrading to a plain fallback; 5xx is retryable
/// REGISTRY_UNAVAILABLE; 404 is non-retryable NO_ROW.
/// </summary>
[TestFixture]
public class ResolvePromptFromRegistryActivityTests
{
    // ============================================================
    // ValidateTaxonomy — boundary parse
    // ============================================================

    [Test]
    public void ValidateTaxonomy_ValidPair_DoesNotThrow()
    {
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            AgentRole.Developer.ToWire(), AgentAction.ImplementFeature.ToWire());

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateTaxonomy_ValidPair_AcceptsSharedToken()
    {
        // context-scan is shared across roles; senior_developer owns it.
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "senior_developer", "context-scan");

        act.Should().NotThrow();
    }

    [Test]
    public void ValidateTaxonomy_UnknownRole_Throws()
    {
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "not-a-role", "implement-feature");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_UnknownAction_Throws()
    {
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "developer", "not-a-real-action");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_DeadLegacyGenericAction_Throws()
    {
        // 'implement' was the old flat-vocabulary generic action; it is no
        // longer a taxonomy token (Story 27-15/27-18) → fail-fast, never a
        // silent mismatch or plain fallback.
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "developer", "implement");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ValidateTaxonomy_LegacyRoleAlias_IsAccepted()
    {
        // RolePhaseMap normalises legacy TS role aliases (implementer→developer)
        // before parsing, so a suspended workflow emitting a legacy role still
        // validates against a taxonomy-valid action.
        var act = () => ResolvePromptFromRegistryActivity.ValidateTaxonomy(
            "implementer", "implement-feature");

        act.Should().NotThrow();
    }

    // ============================================================
    // CallResolveAsync — HTTP boundary behaviour (Fix A/B/D)
    // ============================================================

    [Test]
    public async Task CallResolveAsync_HappyPath_ReturnsRenderedPrompt()
    {
        var json = JsonSerializer.Serialize(new
        {
            renderedTemplate = "Write clean code.",
            renderedSystemPrompt = "You are a developer.",
            enableTools = true,
            maxTokens = 8192,
        });
        var handler = new StubHandler(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Contain("/api/prompts/");
            req.Method.Should().Be(HttpMethod.Post);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        using var client = new HttpClient(handler);
        var (rendered, systemPrompt, enableTools, maxTokens) = await ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "t-1",
            variables: new Dictionary<string, object>());

        rendered.Should().Be("Write clean code.");
        systemPrompt.Should().Be("You are a developer.");
        enableTools.Should().BeTrue();
        maxTokens.Should().Be(8192);
    }

    [Test]
    public async Task CallResolveAsync_NotFound_ThrowsNoRowNonRetryable()
    {
        // Fix B: 404 must produce NO_ROW (not the old REGISTRY_MISS).
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "t-1",
            variables: new Dictionary<string, object>());

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.PROMPT.RESOLVE.NO_ROW");
        ex.Retryable.Should().BeFalse();
        ex.Context.Should().ContainKey("status");
        ex.Context["status"].Should().Be(404);
    }

    [Test]
    public async Task CallResolveAsync_500_ThrowsRegistryUnavailableRetryable()
    {
        // Fix A: 5xx is a transient server fault — must be REGISTRY_UNAVAILABLE
        // with retryable=true, never NO_ROW.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "",
            variables: new Dictionary<string, object>());

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.PROMPT.RESOLVE.REGISTRY_UNAVAILABLE");
        ex.Retryable.Should().BeTrue();
        ex.Context.Should().ContainKey("status");
        ex.Context["status"].Should().Be(500);
    }

    [Test]
    public async Task CallResolveAsync_503_ThrowsRegistryUnavailableRetryable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "",
            variables: new Dictionary<string, object>());

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.PROMPT.RESOLVE.REGISTRY_UNAVAILABLE");
        ex.Retryable.Should().BeTrue();
    }

    [Test]
    public async Task CallResolveAsync_NetworkError_ThrowsRegistryUnavailableRetryable()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("connection refused")));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "",
            variables: new Dictionary<string, object>());

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.PROMPT.RESOLVE.REGISTRY_UNAVAILABLE");
        ex.Retryable.Should().BeTrue();
    }

    [Test]
    public async Task CallResolveAsync_WithTenantId_SendsXTenantIdHeader()
    {
        // Fix D (prompt activity): X-Tenant-Id must be set on the outgoing
        // request message (not DefaultRequestHeaders) so it is visible in tests.
        string? capturedTenantId = null;
        var handler = new StubHandler(req =>
        {
            req.Headers.TryGetValues("X-Tenant-Id", out var values);
            capturedTenantId = values?.FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    renderedTemplate = "ok",
                    renderedSystemPrompt = "",
                    enableTools = false,
                    maxTokens = 4096,
                }),
            };
        });

        using var client = new HttpClient(handler);
        await ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "tenant-xyz",
            variables: new Dictionary<string, object>());

        capturedTenantId.Should().Be("tenant-xyz");
    }

    [Test]
    public async Task CallResolveAsync_401_ThrowsNoRowNonRetryable()
    {
        // 4xx other than 404: permanent client fault — non-retryable NO_ROW.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using var client = new HttpClient(handler);
        Func<Task> act = () => ResolvePromptFromRegistryActivity.CallResolveAsync(
            client, "http://test", "developer", "implement-feature", tenantId: "",
            variables: new Dictionary<string, object>());

        var ex = (await act.Should().ThrowAsync<TammaError>()).Which;
        ex.Code.Should().Be("LLM.PROMPT.RESOLVE.NO_ROW");
        ex.Retryable.Should().BeFalse();
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
