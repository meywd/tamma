using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Analytics;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// Story 28-11 — handler-direct unit tests for
/// <see cref="AdminTenantsEndpoints"/>. Uses an EF InMemory
/// <see cref="ControlPlaneDbContext"/> so the tests exercise the shadow
/// columns (Status, PlanId, FailureReason, etc.) without needing a
/// Postgres container, and a recording double for
/// <see cref="IPlatformEventPublisher"/> so we can assert the lifecycle
/// events are emitted.
///
/// <para>Coverage scopes: listing with filters + pagination, detail +
/// action-gate computation, state-machine gating on retry / delete /
/// force-delete, plan change, and the force-delete confirmation header
/// contract.</para>
/// </summary>
[TestFixture]
public class AdminTenantsTests
{
    private ControlPlaneDbContext _db = null!;
    private RecordingPlatformEventPublisher _publisher = null!;
    // Story 28-8 — admin endpoints take an ITenantStatusCache to
    // invalidate the cached status on flip. Tests use a no-op stub.
    private RecordingStatusCache _statusCache = null!;
    // Round-2 review M16 — admin endpoints take a TimeProvider so the
    // tests can pin / advance the clock. Default to TimeProvider.System
    // for tests that don't care about the wall-clock value; the
    // dedicated TimeProvider tests below use a fake provider.
    private TimeProvider _timeProvider = null!;
    // H12 #2 — admin endpoints also evict the connection-resolver pool
    // on Status flip. Tests use a recording resolver to assert the
    // EvictAsync call.
    private RecordingTenantConnectionResolver _connectionResolver = null!;
    // R2 follow-up — admin endpoints publish a NOTIFY for cluster-wide
    // invalidation. Tests use a recording bus to assert the publish
    // call wires through alongside the local invalidation.
    private RecordingInvalidationBus _invalidationBus = null!;
    // Story 28-11 AC2 — the detail endpoint joins to platform_analytics_hourly
    // for the 24h resourceSummary. The real service reads the fact table off
    // the same ControlPlaneDbContext, so tests use the production
    // implementation (no tenant factory needed — the summary is fact-table
    // only).
    private IPlatformAnalyticsService _analytics = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ControlPlaneDbContext(options);
        _publisher = new RecordingPlatformEventPublisher();
        _statusCache = new RecordingStatusCache();
        _timeProvider = TimeProvider.System;
        _connectionResolver = new RecordingTenantConnectionResolver();
        _invalidationBus = new RecordingInvalidationBus();
        _analytics = new PlatformAnalyticsService(_db, tenantFactory: null, _timeProvider);

        await PlansSeeder.SeedAsync(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    /// <summary>
    /// Story 28-R2 / Finding M2 — actor-bearing principal for handler tests.
    /// Mints a <see cref="ClaimsPrincipal"/> with <c>sub</c>, <c>email</c>,
    /// and <c>platformRole</c> claims so <see cref="AdminTenantsEndpoints.BuildAdminEvent"/>
    /// has something to project into the audit-event tags + data.
    /// </summary>
    internal static ClaimsPrincipal AdminPrincipal(
        Guid? userId = null,
        string email = "ops@tamma.dev",
        string platformRole = "platform_admin")
    {
        var id = userId ?? Guid.Parse("99999999-9999-9999-9999-999999999999");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("platformRole", platformRole),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    // ── Fixture helpers ──

    private async Task<Guid> SeedTenantAsync(
        string name = "Acme",
        string? status = "active",
        Guid? planId = null,
        Guid? ownerId = null,
        string? failureReason = null,
        DateTime? deleteRequestedAt = null,
        int? kekVersion = null,
        byte[]? encryptedConn = null)
    {
        var id = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = id,
            Name = name,
            Slug = name.ToLowerInvariant() + "-" + id.ToString("N").Substring(0, 6),
            Type = "team",
            Plan = "free",
            OwnerId = ownerId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.Entry(tenant).Property("Status").CurrentValue = status;
        _db.Entry(tenant).Property("PlanId").CurrentValue = planId ?? PlansSeeder.FreePlanId;
        _db.Entry(tenant).Property("FailureReason").CurrentValue = failureReason;
        _db.Entry(tenant).Property("DeleteRequestedAt").CurrentValue = deleteRequestedAt;
        _db.Entry(tenant).Property("KekVersion").CurrentValue = (short)(kekVersion ?? 1);
        _db.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = encryptedConn;
        await _db.SaveChangesAsync();
        return id;
    }

    private async Task<TenantProvisioningResponseT> InvokeListAsync(
        string? status = null,
        string? plan = null,
        string? search = null,
        int? page = null,
        int? pageSize = null)
    {
        var result = await AdminTenantsEndpoints.ListTenants(
            _db, status, plan, search, page, pageSize);
        result.Should().BeOfType<Ok<AdminTenantListResponse>>();
        return new TenantProvisioningResponseT((Ok<AdminTenantListResponse>)result);
    }

    /// <summary>Tiny shim so tests can read the Value without casts everywhere.</summary>
    private sealed record TenantProvisioningResponseT(Ok<AdminTenantListResponse> Result)
    {
        public AdminTenantListResponse Body => Result.Value!;
    }

    // ── Listing ──

    [Test]
    public async Task ListTenants_NoFilters_ReturnsAllActiveTenants_OrderedByUpdatedAtDesc()
    {
        var oldId = await SeedTenantAsync("Oldest");
        // SQLite/InMemory providers don't auto-set UpdatedAt — ensure
        // determinism by offsetting manually.
        var oldest = await _db.Tenants.FirstAsync(t => t.Id == oldId);
        oldest.UpdatedAt = DateTime.UtcNow.AddHours(-5);
        await _db.SaveChangesAsync();
        var newerId = await SeedTenantAsync("Newer");

        var resp = await InvokeListAsync();

        resp.Body.Total.Should().Be(2);
        resp.Body.Tenants.Should().HaveCount(2);
        resp.Body.Tenants[0].Id.Should().Be(newerId);
        resp.Body.Tenants[1].Id.Should().Be(oldId);
    }

    [Test]
    public async Task ListTenants_FiltersByStatus()
    {
        await SeedTenantAsync("Alpha", status: "active");
        await SeedTenantAsync("Beta", status: "failed");
        await SeedTenantAsync("Gamma", status: "failed");

        var resp = await InvokeListAsync(status: "failed");

        resp.Body.Total.Should().Be(2);
        resp.Body.Tenants.Should().OnlyContain(t => t.Status == "failed");
    }

    [Test]
    public async Task ListTenants_FiltersByPlanSlug()
    {
        await SeedTenantAsync("Alpha", planId: PlansSeeder.FreePlanId);
        await SeedTenantAsync("Beta", planId: PlansSeeder.TeamPlanId);
        await SeedTenantAsync("Gamma", planId: PlansSeeder.TeamPlanId);

        var resp = await InvokeListAsync(plan: "team");

        resp.Body.Total.Should().Be(2);
        resp.Body.Tenants.Should().OnlyContain(t => t.PlanSlug == "team");
    }

    [Test]
    public async Task ListTenants_FiltersBySearch_OnNameOrSlug_CaseInsensitive()
    {
        await SeedTenantAsync("Acme");
        await SeedTenantAsync("Initech");
        await SeedTenantAsync("ACME-Subsidiary");

        var resp = await InvokeListAsync(search: "acme");

        resp.Body.Total.Should().Be(2);
        resp.Body.Tenants.Should().OnlyContain(t => t.Name.ToLowerInvariant().Contains("acme"));
    }

    [Test]
    public async Task ListTenants_PaginatesResults()
    {
        for (var i = 0; i < 12; i++)
        {
            var id = await SeedTenantAsync($"Tenant{i:D2}");
            // Stagger UpdatedAt so ordering is deterministic.
            var t = await _db.Tenants.FirstAsync(x => x.Id == id);
            t.UpdatedAt = DateTime.UtcNow.AddSeconds(i);
            await _db.SaveChangesAsync();
        }

        var page1 = await InvokeListAsync(page: 1, pageSize: 5);
        var page2 = await InvokeListAsync(page: 2, pageSize: 5);
        var page3 = await InvokeListAsync(page: 3, pageSize: 5);

        page1.Body.Total.Should().Be(12);
        page1.Body.Tenants.Should().HaveCount(5);
        page2.Body.Tenants.Should().HaveCount(5);
        page3.Body.Tenants.Should().HaveCount(2);
        // No overlap
        var ids1 = page1.Body.Tenants.Select(t => t.Id).ToHashSet();
        var ids2 = page2.Body.Tenants.Select(t => t.Id).ToHashSet();
        ids1.Intersect(ids2).Should().BeEmpty();
    }

    [Test]
    public async Task ListTenants_PageSizeClampedTo200()
    {
        await SeedTenantAsync("One");

        var resp = await InvokeListAsync(pageSize: 5000);

        resp.Body.PageSize.Should().Be(200);
    }

    [Test]
    public async Task ListTenants_InvalidStatus_Returns400()
    {
        var result = await AdminTenantsEndpoints.ListTenants(
            _db, status: "garbage");

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ListTenants_InvalidPlanSlug_Returns400()
    {
        var result = await AdminTenantsEndpoints.ListTenants(
            _db, plan: "nonexistent-plan");

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ListTenants_HidesSoftDeletedTenants()
    {
        var activeId = await SeedTenantAsync("Alive");
        var deletedId = await SeedTenantAsync("Dead");
        var dead = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == deletedId);
        dead.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var resp = await InvokeListAsync();

        resp.Body.Total.Should().Be(1);
        resp.Body.Tenants[0].Id.Should().Be(activeId);
    }

    [Test]
    public async Task ListTenants_NeverLeaksEncryptedConnectionString()
    {
        var tenantId = await SeedTenantAsync(
            "Secret",
            encryptedConn: new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });

        var resp = await InvokeListAsync();

        resp.Body.Tenants[0].HasEncryptedConnectionString.Should().BeTrue();
        // Sanity: the DTO has no field for the bytes — compile-time check.
        typeof(AdminTenantListItem).GetProperties()
            .Should().NotContain(p => p.PropertyType == typeof(byte[]),
                "encrypted connection string bytes must never leak through the DTO");
    }

    // ── Phase 4 — tenant→DB view (DatabaseId + SchemaName shadow columns) ──

    [Test]
    public async Task ListAndDetail_SurfacePlacementShadowColumns_DatabaseIdAndSchemaName()
    {
        var poolRow = new TenantDatabase
        {
            Id = Guid.NewGuid(),
            Label = "central-test",
            Host = "db.internal",
            Port = 5432,
            AdminConnectionStringEncrypted = new byte[] { 0x01 },
            TierEligibility = [],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TenantDatabases.Add(poolRow);
        var tenantId = await SeedTenantAsync("Placed");
        var tenant = await _db.Tenants.FirstAsync(t => t.Id == tenantId);
        _db.Entry(tenant).Property("DatabaseId").CurrentValue = (Guid?)poolRow.Id;
        _db.Entry(tenant).Property("SchemaName").CurrentValue = "t_0123456789abcdef";
        await _db.SaveChangesAsync();

        var resp = await InvokeListAsync();
        var item = resp.Body.Tenants.Single(t => t.Id == tenantId);
        item.DatabaseId.Should().Be(poolRow.Id,
            "the list projection must surface which pool row hosts the tenant");
        item.SchemaName.Should().Be("t_0123456789abcdef",
            "the list projection must surface the tenant's schema name");

        var detail = await AdminTenantsEndpoints.GetTenantDetail(
            tenantId, _db, _publisher, _analytics);
        var ok = detail.Should().BeOfType<Ok<AdminTenantDetailResponse>>().Subject;
        ok.Value!.Tenant.DatabaseId.Should().Be(poolRow.Id);
        ok.Value.Tenant.SchemaName.Should().Be("t_0123456789abcdef");
    }

    [Test]
    public async Task ListTenants_UnplacedTenant_CarriesNullPlacementColumns()
    {
        var tenantId = await SeedTenantAsync("Unplaced");

        var resp = await InvokeListAsync();

        var item = resp.Body.Tenants.Single(t => t.Id == tenantId);
        item.DatabaseId.Should().BeNull();
        item.SchemaName.Should().BeNull();
    }

    // ── Detail ──

    [Test]
    public async Task GetTenantDetail_ReturnsTenant_AndRecentEvents()
    {
        var tenantId = await SeedTenantAsync("DetailTenant");
        // Seed a couple of platform events for this tenant
        for (var i = 0; i < 3; i++)
        {
            _db.PlatformEvents.Add(new PlatformEvent
            {
                Type = "TENANT.PROVISION.STEP_COMPLETED",
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow.AddMinutes(-i),
            });
        }
        await _db.SaveChangesAsync();

        var result = await AdminTenantsEndpoints.GetTenantDetail(
            tenantId, _db, _publisher, _analytics);

        var ok = result.Should().BeOfType<Ok<AdminTenantDetailResponse>>().Subject;
        ok.Value!.Tenant.Id.Should().Be(tenantId);
        ok.Value.RecentEvents.Should().HaveCount(3);
        ok.Value.Actions.Should().NotBeNull();
    }

    [Test]
    public async Task GetTenantDetail_Returns404_WhenTenantMissing()
    {
        var result = await AdminTenantsEndpoints.GetTenantDetail(
            Guid.NewGuid(), _db, _publisher, _analytics);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Detail: resourceSummary (Story 28-11 AC2) ──

    [Test]
    public async Task GetTenantDetail_ResourceSummary_Aggregates24hFactTableRows()
    {
        var tenantId = await SeedTenantAsync("Busy");
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Two hourly rows inside the 24h window for this tenant.
        await SeedFactRowAsync(tenantId, now.AddHours(-1),
            workflowsStarted: 10, workflowsCompleted: 8, workflowsFailed: 1,
            agentDispatches: 4, tokensIn: 1000, tokensOut: 500, costUsd: 0.50m);
        await SeedFactRowAsync(tenantId, now.AddHours(-2),
            workflowsStarted: 5, workflowsCompleted: 4, workflowsFailed: 0,
            agentDispatches: 2, tokensIn: 250, tokensOut: 125, costUsd: 0.25m);

        var result = await AdminTenantsEndpoints.GetTenantDetail(
            tenantId, _db, _publisher, _analytics);

        var ok = result.Should().BeOfType<Ok<AdminTenantDetailResponse>>().Subject;
        var rs = ok.Value!.ResourceSummary;
        rs.Should().NotBeNull("resourceSummary must always be present, never null");
        rs!.WorkflowsLast24h.Should().Be(15);   // 10 + 5
        rs.WorkflowsCompletedLast24h.Should().Be(12);  // 8 + 4
        rs.WorkflowsFailedLast24h.Should().Be(1);
        rs.AgentDispatchesLast24h.Should().Be(6);  // 4 + 2
        rs.TokensInLast24h.Should().Be(1250);
        rs.TokensOutLast24h.Should().Be(625);
        rs.LlmCostUsdLast24h.Should().Be(0.75m);
    }

    [Test]
    public async Task GetTenantDetail_ResourceSummary_FreshTenant_ReturnsZeroedSummary_Not404()
    {
        var tenantId = await SeedTenantAsync("Fresh");

        var result = await AdminTenantsEndpoints.GetTenantDetail(
            tenantId, _db, _publisher, _analytics);

        var ok = result.Should().BeOfType<Ok<AdminTenantDetailResponse>>().Subject;
        var rs = ok.Value!.ResourceSummary;
        rs.Should().NotBeNull("a tenant with no analytics rows yet must get a zeroed summary, not null");
        rs!.WorkflowsLast24h.Should().Be(0);
        rs.WorkflowsCompletedLast24h.Should().Be(0);
        rs.WorkflowsFailedLast24h.Should().Be(0);
        rs.AgentDispatchesLast24h.Should().Be(0);
        rs.TokensInLast24h.Should().Be(0);
        rs.TokensOutLast24h.Should().Be(0);
        rs.LlmCostUsdLast24h.Should().Be(0m);
    }

    [Test]
    public async Task GetTenantDetail_ResourceSummary_ExcludesRowsOlderThan24h()
    {
        var tenantId = await SeedTenantAsync("Aging");
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await SeedFactRowAsync(tenantId, now.AddHours(-1), workflowsStarted: 3, costUsd: 0.10m);
        // 25h old — outside the window, must be excluded.
        await SeedFactRowAsync(tenantId, now.AddHours(-25), workflowsStarted: 999, costUsd: 9.99m);

        var result = await AdminTenantsEndpoints.GetTenantDetail(
            tenantId, _db, _publisher, _analytics);

        var ok = result.Should().BeOfType<Ok<AdminTenantDetailResponse>>().Subject;
        var rs = ok.Value!.ResourceSummary;
        rs!.WorkflowsLast24h.Should().Be(3, "rows older than 24h are excluded");
        rs.LlmCostUsdLast24h.Should().Be(0.10m);
    }

    [Test]
    public async Task GetTenantDetail_ResourceSummary_ExcludesOtherTenantsRows()
    {
        var tenantId = await SeedTenantAsync("Mine");
        var otherTenantId = await SeedTenantAsync("Theirs");
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await SeedFactRowAsync(tenantId, now.AddHours(-1), workflowsStarted: 2, costUsd: 0.20m);
        await SeedFactRowAsync(otherTenantId, now.AddHours(-1), workflowsStarted: 50, costUsd: 5.00m);
        // Platform-wide row (TenantId null) must also be ignored.
        await SeedFactRowAsync(null, now.AddHours(-1), workflowsStarted: 100, costUsd: 10.00m);

        var result = await AdminTenantsEndpoints.GetTenantDetail(
            tenantId, _db, _publisher, _analytics);

        var ok = result.Should().BeOfType<Ok<AdminTenantDetailResponse>>().Subject;
        var rs = ok.Value!.ResourceSummary;
        rs!.WorkflowsLast24h.Should().Be(2, "only the target tenant's rows count");
        rs.LlmCostUsdLast24h.Should().Be(0.20m);
    }

    private async Task SeedFactRowAsync(
        Guid? tenantId,
        DateTime hour,
        long workflowsStarted = 0,
        long workflowsCompleted = 0,
        long workflowsFailed = 0,
        long agentDispatches = 0,
        long tokensIn = 0,
        long tokensOut = 0,
        decimal costUsd = 0m)
    {
        _db.PlatformAnalyticsHourly.Add(new PlatformAnalyticsHourly
        {
            Id = Guid.NewGuid(),
            Hour = hour,
            TenantId = tenantId,
            WorkflowsStarted = workflowsStarted,
            WorkflowsCompleted = workflowsCompleted,
            WorkflowsFailed = workflowsFailed,
            AgentDispatches = agentDispatches,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            CostUsd = costUsd,
            ComputedAt = hour.AddMinutes(5),
        });
        await _db.SaveChangesAsync();
    }

    // ── Action gate computation ──

    [Test]
    public void ComputeActions_ActiveTenant_AllowsDeleteAndChangePlan()
    {
        var gate = AdminTenantsEndpoints.ComputeActions("active");
        gate.CanDelete.Should().BeTrue();
        gate.CanChangePlan.Should().BeTrue();
        gate.CanRetry.Should().BeFalse();
        gate.CanForceDelete.Should().BeFalse();
    }

    [Test]
    public void ComputeActions_FailedTenant_AllowsRetryAndForceDelete()
    {
        var gate = AdminTenantsEndpoints.ComputeActions("failed");
        gate.CanRetry.Should().BeTrue();
        gate.CanForceDelete.Should().BeTrue();
        gate.CanDelete.Should().BeFalse();
    }

    [Test]
    public void ComputeActions_DeletingTenant_AllowsOnlyForceDelete()
    {
        var gate = AdminTenantsEndpoints.ComputeActions("deleting");
        gate.CanForceDelete.Should().BeTrue();
        gate.CanRetry.Should().BeFalse();
        gate.CanDelete.Should().BeFalse();
        gate.CanChangePlan.Should().BeFalse();
    }

    [Test]
    public void ComputeActions_NullLegacyTenant_TreatedAsActive()
    {
        var gate = AdminTenantsEndpoints.ComputeActions(null);
        gate.CanDelete.Should().BeTrue();
        gate.CanChangePlan.Should().BeTrue();
    }

    // ── Retry action ──

    [Test]
    public async Task RetryTenant_InFailedState_FlipsToPendingVerification_AndEmitsEvent()
    {
        var tenantId = await SeedTenantAsync(
            "Retryable", status: "failed", failureReason: "db-create-timeout");

        var result = await AdminTenantsEndpoints.RetryTenant(tenantId, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        var ok = result.Should().BeOfType<Ok<AdminTenantActionResponse>>().Subject;
        ok.Value!.Status.Should().Be("pending_verification");

        // DB side effect
        var reloaded = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        ((string?)_db.Entry(reloaded).Property("Status").CurrentValue)
            .Should().Be("pending_verification");
        ((string?)_db.Entry(reloaded).Property("FailureReason").CurrentValue)
            .Should().BeNull("retry clears the prior failure reason");

        // Platform event emitted
        _publisher.Events.Should().ContainSingle(
            e => e.Type == "TENANT.PROVISIONING_REQUESTED" && e.TenantId == tenantId);

        // H7 + H12 #2 — status cache invalidated AND resolver pool evicted.
        _statusCache.Invalidations.Should().Contain(tenantId);
        _connectionResolver.Evictions.Should().Contain(tenantId,
            "the resolver's data-source pool must be torn down so the next " +
            "request rebuilds against the post-flip CP row");
        // R2 follow-up — cluster-wide NOTIFY also fans out so sibling
        // pods invalidate their copies within ms.
        _invalidationBus.Publishes.Should().Contain(tenantId,
            "admin actions must publish a NOTIFY for cluster-wide invalidation");
    }

    [Test]
    public async Task RetryTenant_InActiveState_Returns409()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.RetryTenant(tenantId, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _publisher.Events.Should().BeEmpty();
    }

    [Test]
    public async Task RetryTenant_Returns404_WhenTenantMissing()
    {
        var result = await AdminTenantsEndpoints.RetryTenant(Guid.NewGuid(), _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Delete action ──

    [Test]
    public async Task DeleteTenant_InActiveState_FlipsToDeleting_AndStampsDeleteRequestedAt()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.DeleteTenant(tenantId, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        var ok = result.Should().BeOfType<Ok<AdminTenantActionResponse>>().Subject;
        ok.Value!.Status.Should().Be("deleting");

        var reloaded = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        ((string?)_db.Entry(reloaded).Property("Status").CurrentValue)
            .Should().Be("deleting");
        ((DateTime?)_db.Entry(reloaded).Property("DeleteRequestedAt").CurrentValue)
            .Should().NotBeNull();

        _publisher.Events.Should().ContainSingle(
            e => e.Type == "TENANT.DELETE.REQUESTED" && e.TenantId == tenantId);

        _statusCache.Invalidations.Should().Contain(tenantId);
        _connectionResolver.Evictions.Should().Contain(tenantId);
        _invalidationBus.Publishes.Should().Contain(tenantId);
    }

    [Test]
    public async Task DeleteTenant_InFailedState_Returns409()
    {
        var tenantId = await SeedTenantAsync(status: "failed");

        var result = await AdminTenantsEndpoints.DeleteTenant(tenantId, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task DeleteTenant_InDeletingState_Returns409_UseForceDeleteInstead()
    {
        var tenantId = await SeedTenantAsync(status: "deleting");

        var result = await AdminTenantsEndpoints.DeleteTenant(tenantId, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Cancel-delete action (Story 28-5 AC4) ──

    [Test]
    public async Task CancelDeleteTenant_InDeletingState_FlipsToActive_ClearsDeleteRequestedAt_AndEmitsCancelled()
    {
        var tenantId = await SeedTenantAsync(
            status: "deleting", deleteRequestedAt: DateTime.UtcNow.AddMinutes(-1));

        var result = await AdminTenantsEndpoints.CancelDeleteTenant(
            tenantId, _db, _publisher, _statusCache, _connectionResolver,
            _invalidationBus, _timeProvider, AdminPrincipal());

        var ok = result.Should().BeOfType<Ok<AdminTenantActionResponse>>().Subject;
        ok.Value!.Status.Should().Be("active");

        var reloaded = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        ((string?)_db.Entry(reloaded).Property("Status").CurrentValue)
            .Should().Be("active");
        ((DateTime?)_db.Entry(reloaded).Property("DeleteRequestedAt").CurrentValue)
            .Should().BeNull("cancel clears the delete-requested stamp");

        _publisher.Events.Should().ContainSingle(
            e => e.Type == "TENANT.DELETE_CANCELLED" && e.TenantId == tenantId);

        _statusCache.Invalidations.Should().Contain(tenantId);
        _connectionResolver.Evictions.Should().Contain(tenantId);
        _invalidationBus.Publishes.Should().Contain(tenantId);
    }

    [Test]
    public async Task CancelDeleteTenant_InActiveState_Returns409()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.CancelDeleteTenant(
            tenantId, _db, _publisher, _statusCache, _connectionResolver,
            _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _publisher.Events.Should().BeEmpty();
    }

    [Test]
    public async Task CancelDeleteTenant_Returns404_WhenTenantMissing()
    {
        var result = await AdminTenantsEndpoints.CancelDeleteTenant(
            Guid.NewGuid(), _db, _publisher, _statusCache, _connectionResolver,
            _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Force-delete action ──

    [Test]
    public async Task ForceDeleteTenant_WithoutConfirmHeader_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(tenantId, http, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ForceDeleteTenant_WithWrongConfirmHeader_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = Guid.NewGuid().ToString();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(tenantId, http, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ForceDeleteTenant_InFailedState_WithConfirm_FlipsToDeleting()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(tenantId, http, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        var ok = result.Should().BeOfType<Ok<AdminTenantActionResponse>>().Subject;
        ok.Value!.Status.Should().Be("deleting");

        _publisher.Events.Should().ContainSingle(
            e => e.Type == "TENANT.DELETE.REQUESTED" && e.TenantId == tenantId);

        _statusCache.Invalidations.Should().Contain(tenantId);
        _connectionResolver.Evictions.Should().Contain(tenantId);
        _invalidationBus.Publishes.Should().Contain(tenantId);
    }

    [Test]
    public async Task ForceDeleteTenant_InActiveState_Returns409()
    {
        var tenantId = await SeedTenantAsync(status: "active");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(tenantId, http, _db, _publisher, _statusCache, _connectionResolver, _invalidationBus, _timeProvider, AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Plan change ──

    // Story 34-4 — UpdateTenantPlan now delegates to IPlanAssignmentService.
    // Build a real service over the InMemory context (PlansSeeder ran in SetUp).
    private Tamma.Api.Services.Pricing.IPlanAssignmentService BuildAssignments()
    {
        var catalog = new Tamma.Api.Services.Pricing.PlanCatalogService(
            _db, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Tamma.Api.Services.Pricing.PlanCatalogService>.Instance);
        var usage = new Tamma.Api.Services.Pricing.NullTenantUsageReader(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Tamma.Api.Services.Pricing.NullTenantUsageReader>.Instance);
        return new Tamma.Api.Services.Pricing.PlanAssignmentService(
            _db, catalog, usage, _publisher,
            new RecordingPlatformQueuedTaskRepository(),
            new FakeModeProvider(Tamma.Api.Services.PromptStore.TammaMode.SaaS),
            _timeProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Tamma.Api.Services.Pricing.PlanAssignmentService>.Instance);
    }

    private sealed class FakeModeProvider : Tamma.Api.Services.PromptStore.ITammaModeProvider
    {
        public FakeModeProvider(Tamma.Api.Services.PromptStore.TammaMode mode) => Mode = mode;
        public Tamma.Api.Services.PromptStore.TammaMode Mode { get; }
    }

    [Test]
    public async Task UpdateTenantPlan_SetsPlanIdAndLegacySlug_AndEmitsEvent()
    {
        var tenantId = await SeedTenantAsync(status: "active", planId: PlansSeeder.FreePlanId);

        var result = await AdminTenantsEndpoints.UpdateTenantPlan(
            tenantId,
            new UpdateTenantPlanRequest(PlansSeeder.TeamPlanId),
            _db,
            BuildAssignments(),
            AdminPrincipal());

        result.Should().BeOfType<Ok<AdminTenantActionResponse>>();
        var reloaded = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        ((Guid?)_db.Entry(reloaded).Property("PlanId").CurrentValue)
            .Should().Be(PlansSeeder.TeamPlanId);
        reloaded.Plan.Should().Be("team", "legacy plan string stays in lockstep with PlanId FK");
        // Story 34-4 — PLAN.UPDATED is superseded by TENANT.PLAN.CHANGED.
        _publisher.Events.Should().ContainSingle(e => e.Type == "TENANT.PLAN.CHANGED");
    }

    [Test]
    public async Task UpdateTenantPlan_Returns400_WhenPlanUnknown()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.UpdateTenantPlan(
            tenantId,
            new UpdateTenantPlanRequest(Guid.NewGuid()),
            _db,
            BuildAssignments(),
            AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task UpdateTenantPlan_Returns400_WhenPlanIdEmpty()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.UpdateTenantPlan(
            tenantId,
            new UpdateTenantPlanRequest(Guid.Empty),
            _db,
            BuildAssignments(),
            AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task UpdateTenantPlan_Returns409_WhenTenantDeleting()
    {
        var tenantId = await SeedTenantAsync(status: "deleting");

        var result = await AdminTenantsEndpoints.UpdateTenantPlan(
            tenantId,
            new UpdateTenantPlanRequest(PlansSeeder.TeamPlanId),
            _db,
            BuildAssignments(),
            AdminPrincipal());

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Helpers ──

    /// <summary>
    /// Extracts the HTTP status code from a minimal-API <see cref="IResult"/>
    /// without relying on the concrete generic Result types. Every
    /// typed-result flavour we return (<c>NotFound&lt;T&gt;</c>,
    /// <c>BadRequest&lt;T&gt;</c>, <c>Ok&lt;T&gt;</c>,
    /// <c>JsonHttpResult&lt;T&gt;</c>) implements
    /// <see cref="IStatusCodeHttpResult"/>, so the status code is the
    /// reliable assertion surface — the generic arg changes with the
    /// anonymous-type body and would otherwise fail <c>BeOfType&lt;T&gt;</c>
    /// matches.
    /// </summary>
    private static int StatusCodeOf(IResult result)
    {
        if (result is IStatusCodeHttpResult s && s.StatusCode.HasValue)
            return s.StatusCode.Value;
        throw new InvalidOperationException(
            $"Result type {result.GetType().FullName} does not expose a status code.");
    }

}
