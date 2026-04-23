using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Alerts;

/// <summary>
/// Story 5.6 / 1.5-37 (Wave C.3) — tenant-scope alert endpoints under
/// <c>/api/v1/orgs/{tenantId}/alerts/*</c> and
/// <c>/api/v1/orgs/{tenantId}/alert-channels/*</c>. Handler-direct
/// tests that bypass the membership filter (which runs in the
/// pipeline) so we can isolate the in-handler invariants:
///
/// <list type="bullet">
///   <item><description>cross-tenant reads 404 (no leak).</description></item>
///   <item><description>non-admin mutations 403.</description></item>
///   <item><description>path-tenant hop attempts (body.tenantId mismatch) 400.</description></item>
///   <item><description>plaintext credential submission 400.</description></item>
///   <item><description>Admin+ acknowledge / resolve flip status and 404 on foreign alerts.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class TenantScopeEndpointsTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private IEventRepository _events = null!;
    private TimeProvider _time = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
        _time = _scope.ServiceProvider.GetRequiredService<TimeProvider>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private static HttpContext CtxWithRole(string? role, Guid? userId = null)
    {
        var ctx = new DefaultHttpContext();
        if (role is not null)
            ctx.Items[RequireTenantMembershipFilter.TenantRoleItemKey] = role;
        if (userId is Guid uid)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, uid.ToString()),
            }, authenticationType: "Test");
            ctx.User = new ClaimsPrincipal(identity);
        }
        ctx.RequestAborted = CancellationToken.None;
        return ctx;
    }

    private static async Task<int> Status(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private static async Task<string> Body(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private async Task<Guid> SeedAlertAsync(Guid? tenantId, string status = "active")
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Severity = "warning",
            Title = "t",
            Description = "d",
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Alerts.Add(alert);
        await _db.SaveChangesAsync();
        return alert.Id;
    }

    private async Task<Guid> SeedChannelAsync(Guid? tenantId)
    {
        var channel = new AlertChannel
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = $"ch-{Guid.NewGuid():N}",
            ChannelType = "email",
            IsEnabled = true,
            Config = """{"toAddress":"x@y.z"}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.AlertChannels.Add(channel);
        await _db.SaveChangesAsync();
        return channel.Id;
    }

    // ── ListTenantAlerts ─────────────────────────────────────────

    [Test]
    public async Task ListTenantAlerts_ReturnsOnlyPathTenantAlerts_NoLeak()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedAlertAsync(tenantA);
        await SeedAlertAsync(tenantA);
        await SeedAlertAsync(tenantB);
        await SeedAlertAsync(null); // platform-scoped

        var ctx = CtxWithRole("member");
        var result = await AlertEndpoints.ListTenantAlerts(ctx, _db, tenantA);
        (await Status(result)).Should().Be(StatusCodes.Status200OK);

        var body = await Body(result);
        body.Should().Contain("\"count\":2");
    }

    [Test]
    public async Task ListTenantAlerts_SeverityFilter_Works()
    {
        var tenantId = Guid.NewGuid();
        var a = new Alert
        {
            TenantId = tenantId, Severity = "critical", Title = "x",
            Description = "d", Status = "active", CreatedAt = DateTime.UtcNow,
        };
        var b = new Alert
        {
            TenantId = tenantId, Severity = "info", Title = "y",
            Description = "d", Status = "active", CreatedAt = DateTime.UtcNow,
        };
        _db.Alerts.AddRange(a, b);
        await _db.SaveChangesAsync();

        var ctx = CtxWithRole("member");
        var result = await AlertEndpoints.ListTenantAlerts(
            ctx, _db, tenantId, severity: "critical");
        var body = await Body(result);
        body.Should().Contain("\"count\":1");
    }

    [Test]
    public async Task ListTenantAlerts_EmptyTenantGuid_Returns400()
    {
        var ctx = CtxWithRole("member");
        var result = await AlertEndpoints.ListTenantAlerts(ctx, _db, Guid.Empty);
        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── GetTenantAlert ───────────────────────────────────────────

    [Test]
    public async Task GetTenantAlert_HappyPath_Returns200()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId);

        var ctx = CtxWithRole("member");
        var result = await AlertEndpoints.GetTenantAlert(ctx, _db, tenantId, alertId);
        (await Status(result)).Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task GetTenantAlert_CrossTenant_Returns404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var alertInB = await SeedAlertAsync(tenantB);

        // caller is member of A, asks for alert in B via A's path.
        var ctx = CtxWithRole("member");
        var result = await AlertEndpoints.GetTenantAlert(ctx, _db, tenantA, alertInB);
        (await Status(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task GetTenantAlert_PlatformScoped_Returns404FromTenantPath()
    {
        var tenantId = Guid.NewGuid();
        var platformAlert = await SeedAlertAsync(null);

        var ctx = CtxWithRole("member");
        var result = await AlertEndpoints.GetTenantAlert(
            ctx, _db, tenantId, platformAlert);
        (await Status(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── AcknowledgeTenantAlert ───────────────────────────────────

    [Test]
    public async Task AcknowledgeTenantAlert_MemberRole_Returns403()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId);
        var ctx = CtxWithRole("member", Guid.NewGuid());

        var result = await AlertEndpoints.AcknowledgeTenantAlert(
            ctx, _db, _events, _time, tenantId, alertId,
            new AcknowledgeAlertRequest("note"));
        (await Status(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task AcknowledgeTenantAlert_AdminRole_FlipsStatus()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.AcknowledgeTenantAlert(
            ctx, _db, _events, _time, tenantId, alertId,
            new AcknowledgeAlertRequest("seen"));
        (await Status(result)).Should().Be(StatusCodes.Status200OK);

        var refreshed = await _db.Alerts.AsNoTracking()
            .FirstAsync(a => a.Id == alertId);
        refreshed.Status.Should().Be("acknowledged");
        refreshed.AcknowledgedAt.Should().NotBeNull();
    }

    [Test]
    public async Task AcknowledgeTenantAlert_OwnerRole_Succeeds()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId);
        var ctx = CtxWithRole("owner", Guid.NewGuid());

        var result = await AlertEndpoints.AcknowledgeTenantAlert(
            ctx, _db, _events, _time, tenantId, alertId, null);
        (await Status(result)).Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task AcknowledgeTenantAlert_CrossTenant_Returns404_NotLeakedAs403()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var alertInB = await SeedAlertAsync(tenantB);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.AcknowledgeTenantAlert(
            ctx, _db, _events, _time, tenantA, alertInB, null);
        (await Status(result)).Should().Be(StatusCodes.Status404NotFound);

        // side-effect: foreign alert is untouched
        var foreign = await _db.Alerts.AsNoTracking()
            .FirstAsync(a => a.Id == alertInB);
        foreign.Status.Should().Be("active");
    }

    [Test]
    public async Task AcknowledgeTenantAlert_AlreadyResolved_Returns409()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId, status: "resolved");
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.AcknowledgeTenantAlert(
            ctx, _db, _events, _time, tenantId, alertId, null);
        (await Status(result)).Should().Be(StatusCodes.Status409Conflict);
    }

    // ── ResolveTenantAlert ───────────────────────────────────────

    [Test]
    public async Task ResolveTenantAlert_MemberRole_Returns403()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId);
        var ctx = CtxWithRole("member", Guid.NewGuid());

        var result = await AlertEndpoints.ResolveTenantAlert(
            ctx, _db, _events, _time, tenantId, alertId,
            new ResolveAlertRequest("fixed"));
        (await Status(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task ResolveTenantAlert_AdminRole_ClosesOut()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.ResolveTenantAlert(
            ctx, _db, _events, _time, tenantId, alertId,
            new ResolveAlertRequest("ship-it"));
        (await Status(result)).Should().Be(StatusCodes.Status200OK);

        var refreshed = await _db.Alerts.AsNoTracking()
            .FirstAsync(a => a.Id == alertId);
        refreshed.Status.Should().Be("resolved");
        refreshed.Resolution.Should().Be("ship-it");
    }

    [Test]
    public async Task ResolveTenantAlert_NoResolution_Returns400()
    {
        var tenantId = Guid.NewGuid();
        var alertId = await SeedAlertAsync(tenantId);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.ResolveTenantAlert(
            ctx, _db, _events, _time, tenantId, alertId,
            new ResolveAlertRequest(""));
        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ResolveTenantAlert_CrossTenant_Returns404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var alertInB = await SeedAlertAsync(tenantB);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.ResolveTenantAlert(
            ctx, _db, _events, _time, tenantA, alertInB,
            new ResolveAlertRequest("nope"));
        (await Status(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── ListTenantChannels ───────────────────────────────────────

    [Test]
    public async Task ListTenantChannels_OnlyReturnsPathTenantChannels_NoPlatformLeak()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedChannelAsync(tenantA);
        await SeedChannelAsync(tenantA);
        await SeedChannelAsync(tenantB);
        await SeedChannelAsync(null); // platform-scoped

        var ctx = CtxWithRole("member");
        var result = await AlertEndpoints.ListTenantChannels(ctx, _db, tenantA);
        (await Status(result)).Should().Be(StatusCodes.Status200OK);

        var body = await Body(result);
        body.Should().Contain("\"count\":2");
    }

    // ── CreateTenantChannel ──────────────────────────────────────

    [Test]
    public async Task CreateTenantChannel_MemberRole_Returns403()
    {
        var tenantId = Guid.NewGuid();
        var ctx = CtxWithRole("member", Guid.NewGuid());

        var result = await AlertEndpoints.CreateTenantChannel(
            ctx, _db, _time, tenantId,
            new CreateChannelRequest(
                Name: "x", ChannelType: "email",
                TenantId: null, Config: "{}", CredentialsSecretId: null));
        (await Status(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task CreateTenantChannel_EmailChannel_AdminRole_Persists_PathTenantOwned()
    {
        var tenantId = Guid.NewGuid();
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.CreateTenantChannel(
            ctx, _db, _time, tenantId,
            new CreateChannelRequest(
                Name: "Ops",
                ChannelType: "email",
                TenantId: null, // body omits → server fills from path
                Config: """{"toAddress":"ops@acme.dev"}""",
                CredentialsSecretId: null));

        (await Status(result)).Should().Be(StatusCodes.Status201Created);

        var saved = await _db.AlertChannels.AsNoTracking()
            .FirstAsync(c => c.Name == "Ops");
        saved.TenantId.Should().Be(tenantId,
            "server forces TenantId from the route path so a caller cannot hop tenants.");
    }

    [Test]
    public async Task CreateTenantChannel_BodyTenantIdMismatch_Returns400()
    {
        var pathTenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.CreateTenantChannel(
            ctx, _db, _time, pathTenantId,
            new CreateChannelRequest(
                Name: "Attempted hop",
                ChannelType: "email",
                TenantId: foreignTenantId, // attempted hop
                Config: "{}", CredentialsSecretId: null));

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CreateTenantChannel_WithPlaintextCredential_InConfig_Rejected()
    {
        var tenantId = Guid.NewGuid();
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.CreateTenantChannel(
            ctx, _db, _time, tenantId,
            new CreateChannelRequest(
                Name: "bad", ChannelType: "slack",
                TenantId: null,
                Config: """{"webhookUrl":"https://hooks.slack.com/x"}""",
                CredentialsSecretId: Guid.NewGuid()));

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
        (await Body(result)).Should().Contain("plaintext credentials");
    }

    [Test]
    public async Task CreateTenantChannel_SlackWithoutCredentialsSecretId_Rejected()
    {
        var tenantId = Guid.NewGuid();
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.CreateTenantChannel(
            ctx, _db, _time, tenantId,
            new CreateChannelRequest(
                Name: "bad", ChannelType: "slack",
                TenantId: null, Config: "{}", CredentialsSecretId: null));

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
        (await Body(result)).Should().Contain("credentialsSecretId");
    }

    [Test]
    public async Task CreateTenantChannel_UnknownChannelType_Rejected()
    {
        var tenantId = Guid.NewGuid();
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.CreateTenantChannel(
            ctx, _db, _time, tenantId,
            new CreateChannelRequest(
                Name: "weird", ChannelType: "carrier-pigeon",
                TenantId: null, Config: "{}", CredentialsSecretId: null));

        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── UpdateTenantChannel ──────────────────────────────────────

    [Test]
    public async Task UpdateTenantChannel_MemberRole_Returns403()
    {
        var tenantId = Guid.NewGuid();
        var channelId = await SeedChannelAsync(tenantId);
        var ctx = CtxWithRole("member", Guid.NewGuid());

        var result = await AlertEndpoints.UpdateTenantChannel(
            ctx, _db, _time, tenantId, channelId,
            new UpdateChannelRequest(Name: "renamed", IsEnabled: null, Config: null));
        (await Status(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task UpdateTenantChannel_CrossTenant_Returns404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var channelInB = await SeedChannelAsync(tenantB);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.UpdateTenantChannel(
            ctx, _db, _time, tenantA, channelInB,
            new UpdateChannelRequest(Name: "owned?", IsEnabled: false, Config: null));
        (await Status(result)).Should().Be(StatusCodes.Status404NotFound);

        // no side-effect on foreign channel
        var foreign = await _db.AlertChannels.AsNoTracking()
            .FirstAsync(c => c.Id == channelInB);
        foreign.IsEnabled.Should().BeTrue();
    }

    [Test]
    public async Task UpdateTenantChannel_PlatformScoped_Returns404FromTenantPath()
    {
        var tenantId = Guid.NewGuid();
        var platformChannel = await SeedChannelAsync(null);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.UpdateTenantChannel(
            ctx, _db, _time, tenantId, platformChannel,
            new UpdateChannelRequest(Name: null, IsEnabled: false, Config: null));
        (await Status(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task UpdateTenantChannel_Admin_TogglesEnabledFlag()
    {
        var tenantId = Guid.NewGuid();
        var channelId = await SeedChannelAsync(tenantId);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.UpdateTenantChannel(
            ctx, _db, _time, tenantId, channelId,
            new UpdateChannelRequest(Name: null, IsEnabled: false, Config: null));
        (await Status(result)).Should().Be(StatusCodes.Status200OK);

        var refreshed = await _db.AlertChannels.AsNoTracking()
            .FirstAsync(c => c.Id == channelId);
        refreshed.IsEnabled.Should().BeFalse();
    }

    [Test]
    public async Task UpdateTenantChannel_PlaintextCredentialConfig_Rejected()
    {
        var tenantId = Guid.NewGuid();
        var channelId = await SeedChannelAsync(tenantId);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.UpdateTenantChannel(
            ctx, _db, _time, tenantId, channelId,
            new UpdateChannelRequest(
                Name: null, IsEnabled: null,
                Config: """{"apiKey":"sk_live_xxx"}"""));
        (await Status(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── DeleteTenantChannel ──────────────────────────────────────

    [Test]
    public async Task DeleteTenantChannel_MemberRole_Returns403()
    {
        var tenantId = Guid.NewGuid();
        var channelId = await SeedChannelAsync(tenantId);
        var ctx = CtxWithRole("member", Guid.NewGuid());

        var result = await AlertEndpoints.DeleteTenantChannel(
            ctx, _db, _time, tenantId, channelId);
        (await Status(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task DeleteTenantChannel_CrossTenant_Returns404()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var channelInB = await SeedChannelAsync(tenantB);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.DeleteTenantChannel(
            ctx, _db, _time, tenantA, channelInB);
        (await Status(result)).Should().Be(StatusCodes.Status404NotFound);

        var foreign = await _db.AlertChannels.AsNoTracking()
            .FirstAsync(c => c.Id == channelInB);
        foreign.IsEnabled.Should().BeTrue("delete on cross-tenant must not have side-effects");
    }

    [Test]
    public async Task DeleteTenantChannel_Admin_SoftDeletes_PreservingRow()
    {
        var tenantId = Guid.NewGuid();
        var channelId = await SeedChannelAsync(tenantId);
        var ctx = CtxWithRole("admin", Guid.NewGuid());

        var result = await AlertEndpoints.DeleteTenantChannel(
            ctx, _db, _time, tenantId, channelId);
        (await Status(result)).Should().Be(StatusCodes.Status204NoContent);

        var row = await _db.AlertChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == channelId);
        row.Should().NotBeNull();
        row!.IsEnabled.Should().BeFalse();
    }
}
