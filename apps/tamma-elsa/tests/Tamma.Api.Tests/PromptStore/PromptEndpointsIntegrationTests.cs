using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Api.Tests.PromptStore;

/// <summary>
/// HTTP-level integration coverage for <c>/api/prompts</c>. Exercises the
/// CLAUDE.md-spec routes (<c>/defaults*</c>, <c>POST /reset</c>) and the
/// camelCase wire-format contract that the audit findings 003/006/013 lock in.
/// </summary>
[TestFixture]
public class PromptEndpointsIntegrationTests
{
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _client = ApiTestFixture.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
    }

    // ------------------------------------------------------------------
    // Audit prompts/006 — CLAUDE.md /defaults* aliases and POST /reset.
    // ------------------------------------------------------------------

    [Test]
    public async Task GetDefaults_AliasOfGetSystem_Returns200()
    {
        var fromDefaults = await _client.GetAsync("/api/prompts/defaults");
        var fromSystem = await _client.GetAsync("/api/prompts/system");

        fromDefaults.StatusCode.Should().Be(HttpStatusCode.OK);
        fromSystem.StatusCode.Should().Be(HttpStatusCode.OK);

        var defaultsBody = await fromDefaults.Content.ReadAsStringAsync();
        var systemBody = await fromSystem.Content.ReadAsStringAsync();
        defaultsBody.Should().Be(systemBody, "/defaults must be an exact alias of /system");
    }

    [Test]
    public async Task GetDefaultsRoleAction_AliasOfGetSystemRoleAction_Returns200()
    {
        var fromDefaults = await _client.GetAsync("/api/prompts/defaults/developer/plan-implementation");
        var fromSystem = await _client.GetAsync("/api/prompts/system/developer/plan-implementation");

        fromDefaults.StatusCode.Should().Be(HttpStatusCode.OK);
        fromSystem.StatusCode.Should().Be(HttpStatusCode.OK);

        var defaultsBody = await fromDefaults.Content.ReadAsStringAsync();
        var systemBody = await fromSystem.Content.ReadAsStringAsync();
        defaultsBody.Should().Be(systemBody);
    }

    [Test]
    public async Task GetSystemRoleAction_UnknownPair_Returns404()
    {
        // Story 27-18 — there is no generic action-default tier. A pair the role
        // does not own (deploy is devops-only) has no system default → 404.
        var response = await _client.GetAsync("/api/prompts/system/developer/deploy");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------
    // Audit prompts/013 — explicit camelCase wire format.
    // ------------------------------------------------------------------

    [Test]
    public async Task GetSystem_EmitsCamelCaseProperties()
    {
        var response = await _client.GetAsync("/api/prompts/system");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);

        // camelCase exists, PascalCase does not
        doc.RootElement.TryGetProperty("roleActionTemplates", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("RoleActionTemplates", out _).Should().BeFalse(
            "ConfigureHttpJsonOptions locks the framework default to CamelCase");

        doc.RootElement.TryGetProperty("systemPrompts", out _).Should().BeTrue();
        // Story 27-18 — the action-default tier is gone; the payload no longer
        // carries an actionDefaults map.
        doc.RootElement.TryGetProperty("actionDefaults", out _).Should().BeFalse(
            "the generic action-default tier was removed (Story 27-18)");
    }

    // ------------------------------------------------------------------
    // Audit prompts/003 — render response field names match the TS contract.
    // ------------------------------------------------------------------

    [Test]
    public async Task RenderPrompt_Returns_AllEightFields_MatchingTsContract()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/prompts/developer/plan-implementation/render",
            new { variables = new Dictionary<string, string>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // Per TS RenderedPrompt interface and audit prompts/003.
        root.TryGetProperty("role", out var role).Should().BeTrue();
        role.GetString().Should().Be("developer");

        root.TryGetProperty("action", out var action).Should().BeTrue();
        action.GetString().Should().Be("plan-implementation");

        root.TryGetProperty("version", out var version).Should().BeTrue();
        version.GetInt32().Should().Be(1, "system defaults are unversioned (default 1)");

        root.TryGetProperty("renderedTemplate", out _).Should().BeTrue(
            "field name must match TS contract — earlier port renamed to userPrompt");
        root.TryGetProperty("renderedSystemPrompt", out _).Should().BeTrue();
        root.TryGetProperty("enableTools", out _).Should().BeTrue();
        root.TryGetProperty("maxTokens", out _).Should().BeTrue();
        root.TryGetProperty("unresolvedVariables", out _).Should().BeTrue(
            "field name must match TS contract — earlier port shortened to 'unresolved'");

        // Old-shape field names are gone
        root.TryGetProperty("userPrompt", out _).Should().BeFalse();
        root.TryGetProperty("systemPrompt", out _).Should().BeFalse();
        root.TryGetProperty("unresolved", out _).Should().BeFalse();
    }
}
