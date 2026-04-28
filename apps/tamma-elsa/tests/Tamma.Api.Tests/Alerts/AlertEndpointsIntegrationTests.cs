using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Alerts;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.1) — integration tests for the
/// /api/v1/admin/alerts/* and /api/v1/admin/alert-channels/*
/// endpoints. Uses the shared <see cref="ApiTestFixture"/> which
/// boots a Postgres container and mounts the API in Development
/// mode (permissive auth).
/// </summary>
[TestFixture]
public class AlertEndpointsIntegrationTests
{
    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
    }

    // ── Channels ────────────────────────────────────────────────

    [Test]
    public async Task CreateChannel_EmailChannel_Persists_WithoutCredentialsSecretId()
    {
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-channels",
            new CreateChannelRequest(
                Name: "Ops Email",
                ChannelType: "email",
                TenantId: null,
                Config: """{"toAddress":"ops@tamma.dev","subjectPrefix":"[ALERT] "}""",
                CredentialsSecretId: null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var saved = await db.AlertChannels.SingleAsync();
        saved.Name.Should().Be("Ops Email");
        saved.ChannelType.Should().Be("email");
        saved.CredentialsSecretId.Should().BeNull();
    }

    [Test]
    public async Task CreateChannel_SlackChannel_WithoutCredentialsSecretId_Rejected()
    {
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-channels",
            new CreateChannelRequest(
                Name: "Bad Slack",
                ChannelType: "slack",
                TenantId: null,
                Config: "{}",
                CredentialsSecretId: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("credentialsSecretId");
    }

    [Test]
    public async Task CreateChannel_RejectsConfigWithPlaintextCredential()
    {
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-channels",
            new CreateChannelRequest(
                Name: "naughty",
                ChannelType: "slack",
                TenantId: null,
                Config: """{"webhookUrl":"https://hooks.slack.com/xxx"}""",
                CredentialsSecretId: Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("plaintext credentials");
    }

    [Test]
    public async Task CreateChannel_UnknownSecretId_WithoutSecretsSchema_SucceedsAsDegradedMode()
    {
        // Secret-existence check tolerates a missing secrets schema
        // (42P01) in environments that don't run AddTammaPostgresSecrets
        // — the endpoint falls through to creating the channel and the
        // dispatcher will fail-loud on first delivery attempt when it
        // can't resolve the plaintext. The test fixture does NOT
        // migrate SecretsDbContext, so this path is what we exercise
        // here; a full end-to-end 404 contract test lives alongside
        // the secrets store tests in a follow-up wave.
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-channels",
            new CreateChannelRequest(
                Name: "ghost",
                ChannelType: "slack",
                TenantId: null,
                Config: "{}",
                CredentialsSecretId: Guid.NewGuid()));

        // Created because the 42P01 catch turned the existence check
        // into a no-op. In production (secrets schema present) this
        // returns 404.
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Test]
    public async Task UpdateChannel_CanToggleEnabledFlag()
    {
        var channelId = await SeedEmailChannelAsync();
        using var client = ApiTestFixture.CreateClient();

        var patch = await client.PatchAsJsonAsync(
            $"/api/v1/admin/alert-channels/{channelId}",
            new UpdateChannelRequest(Name: null, IsEnabled: false, Config: null));

        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        (await db.AlertChannels.FindAsync(channelId))!.IsEnabled
            .Should().BeFalse();
    }

    [Test]
    public async Task DeleteChannel_SoftDeletes_PreservingRow()
    {
        var channelId = await SeedEmailChannelAsync();
        using var client = ApiTestFixture.CreateClient();

        var response = await client.DeleteAsync(
            $"/api/v1/admin/alert-channels/{channelId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.AlertChannels.FindAsync(channelId);
        row.Should().NotBeNull("soft-delete keeps the row for audit");
        row!.IsEnabled.Should().BeFalse();
    }

    [Test]
    public async Task ListChannels_ReturnsPlatformScopedByDefault()
    {
        await SeedEmailChannelAsync();
        using var client = ApiTestFixture.CreateClient();

        var response = await client.GetAsync("/api/v1/admin/alert-channels");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
    }

    // ── Alerts ──────────────────────────────────────────────────

    [Test]
    public async Task TestRaiseAlert_CreatesAlertAndReturnsDeliveredTrue()
    {
        await SeedEmailChannelAsync();
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/alerts/_test",
            new TestRaiseAlertRequest(
                Severity: "critical",
                Title: "Smoke test",
                Description: "dispatcher check",
                CorrelationId: "smoke-1",
                TenantId: null));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"response body was: {body}");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("delivered").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("matchedChannels").GetInt32().Should().Be(1);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var alert = await db.Alerts.SingleAsync();
        alert.Title.Should().Be("Smoke test");
        alert.Status.Should().Be("active");
    }

    [Test]
    public async Task TestRaiseAlert_InvalidSeverity_Returns400()
    {
        using var client = ApiTestFixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/admin/alerts/_test",
            new TestRaiseAlertRequest(
                Severity: "spicy",
                Title: "x",
                Description: "y",
                CorrelationId: null,
                TenantId: null));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task AcknowledgeAlert_FlipsStatusAndStampsAck()
    {
        var alertId = await SeedActiveAlertAsync();
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/alerts/{alertId}/acknowledge",
            new AcknowledgeAlertRequest("seen, investigating"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var alert = await db.Alerts.FindAsync(alertId);
        alert!.Status.Should().Be("acknowledged");
        alert.AcknowledgedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ResolveAlert_FlipsStatusToResolvedAndStoresResolution()
    {
        var alertId = await SeedActiveAlertAsync();
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/alerts/{alertId}/resolve",
            new ResolveAlertRequest("redeployed"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var alert = await db.Alerts.FindAsync(alertId);
        alert!.Status.Should().Be("resolved");
        alert.Resolution.Should().Be("redeployed");
    }

    [Test]
    public async Task ResolveAlert_OnAlreadyResolved_Returns409()
    {
        var alertId = await SeedActiveAlertAsync();
        using var client = ApiTestFixture.CreateClient();

        await client.PostAsJsonAsync(
            $"/api/v1/admin/alerts/{alertId}/resolve",
            new ResolveAlertRequest("fix"));

        var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/alerts/{alertId}/resolve",
            new ResolveAlertRequest("again"));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task ResolveAlert_WithoutResolutionBody_Returns400()
    {
        var alertId = await SeedActiveAlertAsync();
        using var client = ApiTestFixture.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/alerts/{alertId}/resolve",
            new ResolveAlertRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task GetAlert_ReturnsAlertAndDeliveryAttempts()
    {
        var channelId = await SeedEmailChannelAsync();
        using var client = ApiTestFixture.CreateClient();

        // Raise via _test then fetch by id
        var raiseResponse = await client.PostAsJsonAsync(
            "/api/v1/admin/alerts/_test",
            new TestRaiseAlertRequest(
                Severity: "warning", Title: "t", Description: "d",
                CorrelationId: null, TenantId: null));
        var raiseBody = await raiseResponse.Content.ReadAsStringAsync();
        using var raiseDoc = JsonDocument.Parse(raiseBody);
        var alertId = Guid.Parse(raiseDoc.RootElement.GetProperty("alertId").GetString()!);

        var getResponse = await client.GetAsync($"/api/v1/admin/alerts/{alertId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResponse.Content.ReadAsStringAsync();
        using var getDoc = JsonDocument.Parse(getBody);
        getDoc.RootElement.GetProperty("alert")
            .GetProperty("id").GetString().Should().Be(alertId.ToString());
        getDoc.RootElement.GetProperty("deliveryAttempts")
            .GetArrayLength().Should().Be(1);
    }

    [Test]
    public async Task ListAlerts_FiltersBySeverity()
    {
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.Alerts.Add(new Alert
        {
            Severity = "critical", Title = "c1", Description = "x",
            Status = "active", CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        db.Alerts.Add(new Alert
        {
            Severity = "info", Title = "i1", Description = "x",
            Status = "active", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        using var client = ApiTestFixture.CreateClient();
        var response = await client.GetAsync(
            "/api/v1/admin/alerts?severity=critical");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static async Task<Guid> SeedEmailChannelAsync()
    {
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            Name = "Fixture Email",
            ChannelType = "email",
            IsEnabled = true,
            Config = """{"toAddress":"ops@tamma.dev"}""",
            CredentialsSecretId = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AlertChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private static async Task<Guid> SeedActiveAlertAsync()
    {
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            Severity = "warning",
            Title = "seeded",
            Description = "desc",
            Status = "active",
            CreatedAt = DateTime.UtcNow,
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        return alert.Id;
    }
}
