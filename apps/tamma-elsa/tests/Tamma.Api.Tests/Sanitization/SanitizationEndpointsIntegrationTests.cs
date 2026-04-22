using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Sanitization;

/// <summary>
/// End-to-end integration tests covering the three sanitization endpoints on
/// <c>SettingsEndpoints</c>. Uses <see cref="ApiTestFixture"/> — a real Postgres
/// container with EF migrations applied.
/// </summary>
[TestFixture]
public class SanitizationEndpointsIntegrationTests
{
    /// <summary>
    /// Per-class factory that layers <see cref="SanitizationServiceCollectionExtensions.AddSanitizationServices"/>
    /// on top of the shared <see cref="ApiTestFixture.Factory"/>. The parent
    /// composition root (Program.cs) is owned by another stream and doesn't
    /// yet call <c>AddSanitizationServices</c>; tests register it locally so
    /// they're independent of that wiring decision.
    /// </summary>
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _factory = ApiTestFixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSanitizationServices()));
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task GetSanitizationRules_ByDefault_ReturnsSystemDefaults()
    {
        var resp = await _client.GetAsync("/api/config/sanitize/rules");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // Expect the default ruleset to include at least these canonical rules.
        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        names.Should().Contain("anthropic-api-key");
        names.Should().Contain("openai-api-key");
        names.Should().Contain("email");
        names.Should().Contain("jwt-token");
        names.Should().Contain("ssn");
        names.Should().Contain("credit-card");
        names.Should().Contain("aws-access-key");
        names.Should().Contain("github-token");
    }

    [Test]
    public async Task Sanitize_WithDefaultRules_RedactsApiKeyInBody()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/config/sanitize",
            new { text = "key=sk-ant-api03-abcdef0123456789 and email a@b.com" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var sanitized = doc.RootElement.GetProperty("sanitizedText").GetString()!;
        sanitized.Should().NotContain("sk-ant-api03");
        sanitized.Should().NotContain("a@b.com");

        var hits = doc.RootElement.GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("ruleName").GetString())
            .ToList();
        hits.Should().Contain("anthropic-api-key");
        hits.Should().Contain("email");
    }

    [Test]
    public async Task UpsertRule_ThenGetRules_MergesWithDefaults()
    {
        // Seed a tenant-scoped custom rule via the repository directly (the
        // PUT /rules endpoint exercises the write path in a later assertion).
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISanitizationRepository>();

        var custom = new Tamma.Data.Entities.SanitizationRuleDefinition(
            Name: "custom-token",
            Pattern: @"TK-[A-Z0-9]{6}",
            Replacement: "[CUSTOM_REDACTED]",
            CaseSensitive: true,
            Priority: 5,
            Enabled: true);
        await repo.UpsertRuleAsync(null, custom);

        // GET merged rules — system defaults + tenant-null override
        var resp = await _client.GetAsync("/api/config/sanitize/rules");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var rules = doc.RootElement.EnumerateArray().ToList();

        rules.Should().Contain(r => r.GetProperty("name").GetString() == "custom-token");
        rules.Should().Contain(r => r.GetProperty("name").GetString() == "email");
    }

    [Test]
    public async Task UpdateSanitizationRules_ViaEndpoint_PersistsAndIsUsedBySanitize()
    {
        // PUT a new rule via the endpoint, then POST /sanitize and expect it applied.
        var rule = new
        {
            name = "internal-id",
            pattern = @"INT-\d{4}",
            replacement = "[INT_ID]",
            caseSensitive = true,
            priority = 1,
            enabled = true
        };

        var putResp = await _client.PutAsJsonAsync(
            "/api/config/sanitize/rules",
            new { rules = new[] { rule } });
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var saniResp = await _client.PostAsJsonAsync(
            "/api/config/sanitize",
            new { text = "ticket INT-9999 is urgent" });
        saniResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await saniResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("sanitizedText").GetString()
            .Should().Be("ticket [INT_ID] is urgent");
    }

    [Test]
    public async Task Sanitize_EmptyText_ReturnsEmptyString()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/config/sanitize",
            new { text = string.Empty });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("sanitizedText").GetString().Should().Be(string.Empty);
    }
}
