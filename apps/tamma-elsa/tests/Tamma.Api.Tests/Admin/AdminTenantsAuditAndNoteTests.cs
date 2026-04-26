using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Endpoints.Admin;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// Story 28-R2 / Findings M2 + M17 — handler-direct tests covering:
/// <list type="bullet">
///   <item><description>M2 — every admin action emits an audit event whose
///     <c>tags</c> + <c>data</c> capture the operator's <c>userId</c>,
///     <c>email</c>, and <c>platformRole</c> (sourced from the request
///     <see cref="ClaimsPrincipal"/>).</description></item>
///   <item><description>M17 — <c>X-Admin-Note</c> header is whitelisted
///     against <c>[A-Za-z0-9 .,;:_!@#$%&amp;()-]{0,500}</c>; out-of-charset
///     values 400, in-charset values pass through unchanged.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class AdminTenantsAuditAndNoteTests
{
    private ControlPlaneDbContext _db = null!;
    private RecordingEventPublisher _publisher = null!;
    private RecordingStatusCache _statusCache = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ControlPlaneDbContext(options);
        _publisher = new RecordingEventPublisher();
        _statusCache = new RecordingStatusCache();
        await PlansSeeder.SeedAsync(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static readonly Guid OperatorId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static ClaimsPrincipal Operator(string platformRole = "platform_admin")
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, OperatorId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, "ops@tamma.dev"),
            new Claim("platformRole", platformRole),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private async Task<Guid> SeedTenantAsync(string status = "active")
    {
        var id = Guid.NewGuid();
        _db.Tenants.Add(new Tenant
        {
            Id = id,
            Name = "TestOrg",
            Slug = "testorg-" + id.ToString("N").Substring(0, 6),
            Type = "team",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        var entry = _db.Entry(_db.Tenants.Local.First(t => t.Id == id));
        entry.Property("Status").CurrentValue = status;
        entry.Property("PlanId").CurrentValue = PlansSeeder.FreePlanId;
        await _db.SaveChangesAsync();
        return id;
    }

    // ── M2 — actor identity in admin events ────────────────────────────────

    [Test]
    public async Task RetryTenant_EmitsEvent_TaggedWithActorIdentity()
    {
        var tenantId = await SeedTenantAsync(status: "failed");

        await AdminTenantsEndpoints.RetryTenant(
            tenantId, _db, _publisher, _statusCache, Operator());

        _publisher.Events.Should().ContainSingle();
        var evt = _publisher.Events[0];
        evt.Type.Should().Be("TENANT.PROVISIONING_REQUESTED");
        AssertActorTagsAndData(evt);
    }

    [Test]
    public async Task DeleteTenant_EmitsEvent_TaggedWithActorIdentity()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        await AdminTenantsEndpoints.DeleteTenant(
            tenantId, _db, _publisher, _statusCache, Operator());

        _publisher.Events.Should().ContainSingle(e => e.Type == "TENANT.DELETE.REQUESTED");
        AssertActorTagsAndData(_publisher.Events[0]);
    }

    [Test]
    public async Task ForceDeleteTenant_EmitsEvent_TaggedWithActorIdentity()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();

        await AdminTenantsEndpoints.ForceDeleteTenant(
            tenantId, http, _db, _publisher, _statusCache, Operator());

        _publisher.Events.Should().ContainSingle(e => e.Type == "TENANT.DELETE.REQUESTED");
        AssertActorTagsAndData(_publisher.Events[0]);
    }

    [Test]
    public async Task CleanupTenant_EmitsEvent_TaggedWithActorIdentity()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();

        await AdminTenantsEndpoints.CleanupTenant(
            tenantId, http, _db, _publisher, Operator());

        _publisher.Events.Should().ContainSingle(e => e.Type == "TENANT.CLEANUP.REQUESTED");
        AssertActorTagsAndData(_publisher.Events[0]);
    }

    [Test]
    public async Task UpdateTenantPlan_EmitsEvent_TaggedWithActorIdentity()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        await AdminTenantsEndpoints.UpdateTenantPlan(
            tenantId,
            new UpdateTenantPlanRequest(PlansSeeder.TeamPlanId),
            _db,
            _publisher,
            Operator());

        _publisher.Events.Should().ContainSingle(e => e.Type == "PLAN.UPDATED");
        AssertActorTagsAndData(_publisher.Events[0]);
    }

    private static void AssertActorTagsAndData(PlatformEvent evt)
    {
        // Tags channel — searchable via JSONB ops
        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!);
        tags.Should().NotBeNull();
        tags!["actorUserId"].Should().Be(OperatorId.ToString("D"));
        tags["actorEmail"].Should().Be("ops@tamma.dev");
        tags["actorPlatformRole"].Should().Be("platform_admin");

        // Data channel — defence-in-depth (immutable record)
        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(evt.Data!);
        data.Should().NotBeNull();
        data!["actorUserId"]!.ToString().Should().Be(OperatorId.ToString("D"));
        data["actorEmail"]!.ToString().Should().Be("ops@tamma.dev");
        data["actorPlatformRole"]!.ToString().Should().Be("platform_admin");
    }

    // ── M17 — X-Admin-Note charset whitelist ────────────────────────────────

    [Test]
    public async Task CleanupTenant_WithBenignNote_PersistsItInEventData()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();
        http.Request.Headers["X-Admin-Note"] = "Cleanup approved by SRE on-call.";

        var result = await AdminTenantsEndpoints.CleanupTenant(
            tenantId, http, _db, _publisher, Operator());

        result.Should().BeOfType<Ok<AdminTenantActionResponse>>();
        _publisher.Events.Should().ContainSingle();
        var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            _publisher.Events[0].Data!);
        data!["note"]!.ToString().Should().Be("Cleanup approved by SRE on-call.");
    }

    [Test]
    public async Task CleanupTenant_WithNewlineInNote_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();
        http.Request.Headers["X-Admin-Note"] = "phase1\nphase2";  // log-forging payload

        var result = await AdminTenantsEndpoints.CleanupTenant(
            tenantId, http, _db, _publisher, Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
        _publisher.Events.Should().BeEmpty(
            "rejected note must not produce an audit event with the bad value");
    }

    [Test]
    public async Task CleanupTenant_WithHtmlMetacharsInNote_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();
        http.Request.Headers["X-Admin-Note"] = "<script>alert(1)</script>";

        var result = await AdminTenantsEndpoints.CleanupTenant(
            tenantId, http, _db, _publisher, Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CleanupTenant_WithControlCharInNote_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();
        http.Request.Headers["X-Admin-Note"] = "data truncated";  // NUL byte

        var result = await AdminTenantsEndpoints.CleanupTenant(
            tenantId, http, _db, _publisher, Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CleanupTenant_OverLengthNote_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();
        http.Request.Headers["X-Admin-Note"] = new string('a', 501);

        var result = await AdminTenantsEndpoints.CleanupTenant(
            tenantId, http, _db, _publisher, Operator());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CleanupTenant_WithoutNote_StillSucceeds()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();
        // No X-Admin-Note set — must still succeed.

        var result = await AdminTenantsEndpoints.CleanupTenant(
            tenantId, http, _db, _publisher, Operator());

        result.Should().BeOfType<Ok<AdminTenantActionResponse>>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int StatusCodeOf(IResult result)
    {
        if (result is IStatusCodeHttpResult s && s.StatusCode.HasValue)
            return s.StatusCode.Value;
        throw new InvalidOperationException(
            $"Result {result.GetType().Name} has no status code");
    }

    private sealed class RecordingEventPublisher : IPlatformEventPublisher
    {
        public List<PlatformEvent> Events { get; } = new();

        public Task<PlatformEvent?> AppendAndPublishAsync(
            PlatformEvent evt, CancellationToken ct = default)
        {
            evt.Id = Guid.NewGuid();
            evt.CreatedAt = DateTime.UtcNow;
            Events.Add(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }
    }

    private sealed class RecordingStatusCache : Tamma.Api.Services.TenantStatus.ITenantStatusCache
    {
        public bool TryGet(Guid tenantId, out string? status)
        { status = null; return false; }
        public void Set(Guid tenantId, string? status) { }
        public void Invalidate(Guid tenantId) { }
    }
}
