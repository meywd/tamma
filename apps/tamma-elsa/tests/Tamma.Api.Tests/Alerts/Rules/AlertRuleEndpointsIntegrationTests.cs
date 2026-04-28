using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Alerts.Rules;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — integration tests against the full API
/// booted from <see cref="ApiTestFixture"/>. Covers:
/// <list type="bullet">
///   <item><description>seeded built-ins are visible after startup</description></item>
///   <item><description>create custom rule with valid predicate</description></item>
///   <item><description>create custom rule with malformed predicate → 400</description></item>
///   <item><description>PATCH built-in event_type → 409 with lockedFields</description></item>
///   <item><description>PATCH built-in is_enabled = false → 200</description></item>
///   <item><description>DELETE built-in → 409</description></item>
///   <item><description>DELETE custom → 204</description></item>
///   <item><description>POST _test → returns would-be payload</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AlertRuleEndpointsIntegrationTests
{
    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        // Re-seed built-ins after every respawn — the seeder runs as
        // IHostedService at startup, which fires once per fixture
        // lifecycle. ResetDatabaseAsync wipes the table, so call the
        // seeder directly to get the built-ins back for each test.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var seeder = new BuiltInAlertRuleSeeder(
            ApiTestFixture.Factory.Services,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<BuiltInAlertRuleSeeder>.Instance);
        await seeder.SeedAsync(default);
    }

    [Test]
    public async Task BuiltInsPresent_AfterSeeder()
    {
        using var client = ApiTestFixture.CreateClient();
        var resp = await client.GetAsync("/api/v1/admin/alert-rules");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        foreach (var spec in BuiltInAlertRules.All)
        {
            body.Should().Contain(spec.BuiltInKey!,
                $"built-in {spec.BuiltInKey} must surface in list");
        }
    }

    [Test]
    public async Task CreateRule_ValidPayload_Returns201()
    {
        using var client = ApiTestFixture.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules",
            new CreateAlertRuleRequest(
                Name: "my-custom-rule",
                Description: "description",
                Severity: "warning",
                EventType: "CUSTOM.EVENT",
                Predicate: """{"op":"always"}""",
                ThrottleSeconds: 30,
                ChannelIds: null,
                IsEnabled: true));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task CreateRule_MalformedPredicate_Returns400WithFieldPath()
    {
        using var client = ApiTestFixture.CreateClient();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules",
            new CreateAlertRuleRequest(
                Name: "bad-rule",
                Description: "d",
                Severity: "warning",
                EventType: "CUSTOM.EVENT",
                Predicate: """{"op":"dance"}""",
                ThrottleSeconds: 0,
                ChannelIds: null,
                IsEnabled: true));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("invalid predicate");
        body.Should().Contain("fieldPath");
    }

    [Test]
    public async Task CreateRule_DuplicateName_Returns409()
    {
        using var client = ApiTestFixture.CreateClient();
        // "budget-exhausted" is a seeded built-in; a second create
        // with that name must collide.
        var resp = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules",
            new CreateAlertRuleRequest(
                Name: "budget-exhausted",
                Description: "d",
                Severity: "warning",
                EventType: "CUSTOM.EVENT",
                Predicate: """{"op":"always"}""",
                ThrottleSeconds: 0,
                ChannelIds: null,
                IsEnabled: true));
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task PatchBuiltIn_LockedField_Returns409()
    {
        using var client = ApiTestFixture.CreateClient();
        // Find the built-in rule id.
        Guid ruleId;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "budget-exhausted");
            ruleId = row.Id;
        }

        // Attempt to change the event_type on a built-in rule.
        var resp = await client.PatchAsJsonAsync(
            $"/api/v1/admin/alert-rules/{ruleId}",
            new UpdateAlertRuleRequest(
                Name: null,
                Description: null,
                IsEnabled: null,
                Severity: null,
                EventType: "BUDGET.EXHAUSTED.V2",
                Predicate: null,
                ThrottleSeconds: null,
                ChannelIds: null));
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("event_type");
        body.Should().Contain("lockedFields");
    }

    [Test]
    public async Task PatchBuiltIn_IsEnabledFalse_Returns200()
    {
        using var client = ApiTestFixture.CreateClient();
        Guid ruleId;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "budget-exhausted");
            ruleId = row.Id;
        }

        var resp = await client.PatchAsJsonAsync(
            $"/api/v1/admin/alert-rules/{ruleId}",
            new UpdateAlertRuleRequest(
                Name: null,
                Description: null,
                IsEnabled: false,
                Severity: null,
                EventType: null,
                Predicate: null,
                ThrottleSeconds: null,
                ChannelIds: null));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope2 = ApiTestFixture.Factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider
            .GetRequiredService<ControlPlaneDbContext>();
        var after = await db2.AlertRules.FindAsync(ruleId);
        after!.IsEnabled.Should().BeFalse();
    }

    [Test]
    public async Task DeleteBuiltIn_Returns409()
    {
        using var client = ApiTestFixture.CreateClient();
        Guid ruleId;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "budget-exhausted");
            ruleId = row.Id;
        }

        var resp = await client.DeleteAsync(
            $"/api/v1/admin/alert-rules/{ruleId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task DeleteCustomRule_Returns204()
    {
        using var client = ApiTestFixture.CreateClient();
        var create = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules",
            new CreateAlertRuleRequest(
                Name: "to-delete",
                Description: "d",
                Severity: "info",
                EventType: "X.Y",
                Predicate: """{"op":"always"}""",
                ThrottleSeconds: 0,
                ChannelIds: null,
                IsEnabled: true));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = payload.GetProperty("id").GetGuid();

        var del = await client.DeleteAsync($"/api/v1/admin/alert-rules/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task TestFire_AlwaysRule_ReturnsFiredTrueAndPayload()
    {
        using var client = ApiTestFixture.CreateClient();
        Guid ruleId;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "budget-exhausted");
            ruleId = row.Id;
        }
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/alert-rules/{ruleId}/_test",
            new TestFireAlertRuleRequest(
                TenantId: Guid.NewGuid(), Tags: null, Data: null));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("fired").GetBoolean().Should().BeTrue();
        body.GetProperty("payload").GetProperty("severity").GetString()
            .Should().Be("warning");
    }

    [Test]
    public async Task TestFire_CountGte_SingleEventBelowThreshold_ReturnsFiredFalse()
    {
        using var client = ApiTestFixture.CreateClient();
        Guid ruleId;
        using (var scope = ApiTestFixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ControlPlaneDbContext>();
            var row = await db.AlertRules
                .FirstAsync(r => r.BuiltInKey == "agent-dispatch-failed-3x-5min");
            ruleId = row.Id;
        }
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/alert-rules/{ruleId}/_test",
            new TestFireAlertRuleRequest(
                TenantId: Guid.NewGuid(), Tags: null, Data: null));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("fired").GetBoolean().Should().BeFalse(
            "count_gte threshold=3 needs >1 events to fire");
    }
}
