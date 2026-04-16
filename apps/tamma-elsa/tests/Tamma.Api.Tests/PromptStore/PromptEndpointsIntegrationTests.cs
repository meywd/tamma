using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Dtos.Prompts;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// Integration tests for the <c>/api/prompts</c> endpoint surface. Boots the full
/// ASP.NET pipeline via <see cref="ApiTestFixture"/> and hits endpoints over HTTP.
/// </summary>
[TestFixture]
public class PromptEndpointsIntegrationTests
{
    private ApiTestFixture _fixture = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new ApiTestFixture();
        _client = _fixture.CreateAuthenticatedClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _fixture.Dispose();
    }

    [Test]
    public async Task ListSystemDefaults_ReturnsAllEightyTemplates()
    {
        var response = await _client.GetAsync("/api/prompts/system");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<SystemDefaultsResponse>();
        payload.Should().NotBeNull();
        payload!.RoleActionTemplates.Should().HaveCount(80);
        payload.SystemPrompts.Should().HaveCount(8);
        payload.ActionDefaults.Should().HaveCount(10);
    }

    [Test]
    public async Task GetSystemDefault_ReturnsRealTemplate_NotStub()
    {
        var response = await _client.GetAsync("/api/prompts/system/developer/plan");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var prompt = await response.Content.ReadFromJsonAsync<PromptResponse>();
        prompt.Should().NotBeNull();
        prompt!.Role.Should().Be("developer");
        prompt.Action.Should().Be("plan");
        // System default template starts with role heading and mentions plan workflow
        prompt.Template.Should().Contain("implementation plan");
        prompt.Source.Should().Be("system");
    }

    [Test]
    public async Task GetSystemDefault_ReturnsNotFound_ForUnknownRoleAction()
    {
        var response = await _client.GetAsync("/api/prompts/system/nonexistent/ghost");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task UpsertAndRender_RoleActionPrompt_EndToEnd()
    {
        var upsertBody = new UpsertPromptRequest(
            Template: "Hello {{name}}",
            SystemPrompt: "You are a {{role}}",
            Variables: new[] { "name", "role" },
            EnableTools: false,
            MaxTokens: 1024);

        var upsert = await _client.PutAsJsonAsync("/api/prompts/developer/plan", upsertBody);
        upsert.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now render it
        var renderBody = new RenderPromptRequest(new Dictionary<string, string>
        {
            ["name"] = "Alice",
            ["role"] = "developer",
        });
        var render = await _client.PostAsJsonAsync("/api/prompts/developer/plan/render", renderBody);
        render.StatusCode.Should().Be(HttpStatusCode.OK);

        var rendered = await render.Content.ReadFromJsonAsync<RenderedPromptResponse>();
        rendered.Should().NotBeNull();
        rendered!.UserPrompt.Should().Be("Hello Alice");
        rendered.SystemPrompt.Should().Be("You are a developer");
    }

    [Test]
    public async Task UpsertPrompt_EmitsPromptUpdatedEvent()
    {
        var body = new UpsertPromptRequest("upd", "sys", null, null, null);
        var response = await _client.PutAsJsonAsync("/api/prompts/tester/write-tests", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await _fixture.WithDbAsync(async db =>
        {
            var hasEvent = await db.DomainEvents.IgnoreQueryFilters()
                .AnyAsync(e => e.Type == "PROMPT.UPDATED.SUCCESS");
            hasEvent.Should().BeTrue();
        });
    }

    [Test]
    public async Task DeletePrompt_RemovesOverride_AndEmitsDeletedEvent()
    {
        // Seed an override first
        var body = new UpsertPromptRequest("override", null, null, null, null);
        await _client.PutAsJsonAsync("/api/prompts/developer/plan", body);

        // Delete it
        var del = await _client.DeleteAsync("/api/prompts/developer/plan");
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        await _fixture.WithDbAsync(async db =>
        {
            var count = await db.PromptOverrides.IgnoreQueryFilters()
                .CountAsync(p => p.Role == "developer" && p.Action == "plan" && p.Scope == "role-action");
            count.Should().Be(0);

            var hasDeletedEvent = await db.DomainEvents.IgnoreQueryFilters()
                .AnyAsync(e => e.Type == "PROMPT.DELETED.SUCCESS");
            hasDeletedEvent.Should().BeTrue();
        });
    }

    [Test]
    public async Task DeletePrompt_ReturnsNotFound_WhenOverrideMissing()
    {
        var response = await _client.DeleteAsync("/api/prompts/developer/plan");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetPrompt_FallsBackToSystemDefault_WhenNoUserOverride()
    {
        var response = await _client.GetAsync("/api/prompts/developer/plan");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var prompt = await response.Content.ReadFromJsonAsync<PromptResponse>();
        prompt.Should().NotBeNull();
        prompt!.Source.Should().Be("system");
    }

    [Test]
    public async Task UpsertSystemPrompt_WritesRoleSystemOverride()
    {
        var body = new UpsertPromptRequest("Custom role identity", null, null, null, null);
        var response = await _client.PutAsJsonAsync("/api/prompts/system/architect/plan", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await _fixture.WithDbAsync(async db =>
        {
            var exists = await db.PromptOverrides.IgnoreQueryFilters()
                .AnyAsync(p => p.Scope == "role-system" && p.Role == "architect");
            exists.Should().BeTrue();
        });
    }

    [Test]
    public async Task DeleteSystemPrompt_RemovesOverride()
    {
        var body = new UpsertPromptRequest("Custom", null, null, null, null);
        await _client.PutAsJsonAsync("/api/prompts/system/architect/plan", body);

        var del = await _client.DeleteAsync("/api/prompts/system/architect/plan");
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        await _fixture.WithDbAsync(async db =>
        {
            var exists = await db.PromptOverrides.IgnoreQueryFilters()
                .AnyAsync(p => p.Scope == "role-system" && p.Role == "architect");
            exists.Should().BeFalse();
        });
    }

    [Test]
    public async Task RenderPrompt_ReturnsRendered_SystemAndUserPromptsFromSystemDefault()
    {
        var renderBody = new RenderPromptRequest(new Dictionary<string, string>
        {
            ["role"] = "developer",
            ["workItemType"] = "story",
            ["workItemJson"] = "{}",
            ["previousFindings"] = "none",
        });

        var response = await _client.PostAsJsonAsync("/api/prompts/developer/context-scan/render", renderBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rendered = await response.Content.ReadFromJsonAsync<RenderedPromptResponse>();
        rendered.Should().NotBeNull();
        rendered!.UserPrompt.Should().Contain("developer");
        rendered.SystemPrompt.Should().NotBeNullOrWhiteSpace();
    }

    // Response DTO matching the new system-defaults payload shape
    private sealed record SystemDefaultsResponse(
        IReadOnlyList<PromptResponse> RoleActionTemplates,
        IReadOnlyDictionary<string, string> SystemPrompts,
        IReadOnlyDictionary<string, PromptResponse> ActionDefaults);
}
