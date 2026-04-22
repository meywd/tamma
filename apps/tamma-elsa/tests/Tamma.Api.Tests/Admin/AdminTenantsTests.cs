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
    private RecordingEventPublisher _publisher = null!;

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

        await PlansSeeder.SeedAsync(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

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
        _db.Entry(tenant).Property("KekVersion").CurrentValue = kekVersion;
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
            tenantId, _db, _publisher);

        var ok = result.Should().BeOfType<Ok<AdminTenantDetailResponse>>().Subject;
        ok.Value!.Tenant.Id.Should().Be(tenantId);
        ok.Value.RecentEvents.Should().HaveCount(3);
        ok.Value.Actions.Should().NotBeNull();
    }

    [Test]
    public async Task GetTenantDetail_Returns404_WhenTenantMissing()
    {
        var result = await AdminTenantsEndpoints.GetTenantDetail(
            Guid.NewGuid(), _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
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

        var result = await AdminTenantsEndpoints.RetryTenant(
            tenantId, _db, _publisher);

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
    }

    [Test]
    public async Task RetryTenant_InActiveState_Returns409()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.RetryTenant(
            tenantId, _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        _publisher.Events.Should().BeEmpty();
    }

    [Test]
    public async Task RetryTenant_Returns404_WhenTenantMissing()
    {
        var result = await AdminTenantsEndpoints.RetryTenant(
            Guid.NewGuid(), _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Delete action ──

    [Test]
    public async Task DeleteTenant_InActiveState_FlipsToDeleting_AndStampsDeleteRequestedAt()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.DeleteTenant(
            tenantId, _db, _publisher);

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
    }

    [Test]
    public async Task DeleteTenant_InFailedState_Returns409()
    {
        var tenantId = await SeedTenantAsync(status: "failed");

        var result = await AdminTenantsEndpoints.DeleteTenant(
            tenantId, _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task DeleteTenant_InDeletingState_Returns409_UseForceDeleteInstead()
    {
        var tenantId = await SeedTenantAsync(status: "deleting");

        var result = await AdminTenantsEndpoints.DeleteTenant(
            tenantId, _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Force-delete action ──

    [Test]
    public async Task ForceDeleteTenant_WithoutConfirmHeader_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(
            tenantId, http, _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ForceDeleteTenant_WithWrongConfirmHeader_Returns400()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = Guid.NewGuid().ToString();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(
            tenantId, http, _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ForceDeleteTenant_InFailedState_WithConfirm_FlipsToDeleting()
    {
        var tenantId = await SeedTenantAsync(status: "failed");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(
            tenantId, http, _db, _publisher);

        var ok = result.Should().BeOfType<Ok<AdminTenantActionResponse>>().Subject;
        ok.Value!.Status.Should().Be("deleting");

        _publisher.Events.Should().ContainSingle(
            e => e.Type == "TENANT.DELETE.REQUESTED" && e.TenantId == tenantId);
    }

    [Test]
    public async Task ForceDeleteTenant_InActiveState_Returns409()
    {
        var tenantId = await SeedTenantAsync(status: "active");
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Admin-Confirm"] = tenantId.ToString();

        var result = await AdminTenantsEndpoints.ForceDeleteTenant(
            tenantId, http, _db, _publisher);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Plan change ──

    [Test]
    public async Task UpdateTenantPlan_SetsPlanIdAndLegacySlug_AndEmitsEvent()
    {
        var tenantId = await SeedTenantAsync(status: "active", planId: PlansSeeder.FreePlanId);

        var result = await AdminTenantsEndpoints.UpdateTenantPlan(
            tenantId,
            new UpdateTenantPlanRequest(PlansSeeder.TeamPlanId),
            _db,
            _publisher);

        result.Should().BeOfType<Ok<AdminTenantActionResponse>>();
        var reloaded = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId);
        ((Guid?)_db.Entry(reloaded).Property("PlanId").CurrentValue)
            .Should().Be(PlansSeeder.TeamPlanId);
        reloaded.Plan.Should().Be("team", "legacy plan string stays in lockstep with PlanId FK");
        _publisher.Events.Should().ContainSingle(e => e.Type == "PLAN.UPDATED");
    }

    [Test]
    public async Task UpdateTenantPlan_Returns400_WhenPlanUnknown()
    {
        var tenantId = await SeedTenantAsync(status: "active");

        var result = await AdminTenantsEndpoints.UpdateTenantPlan(
            tenantId,
            new UpdateTenantPlanRequest(Guid.NewGuid()),
            _db,
            _publisher);

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
            _publisher);

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
            _publisher);

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

    // ── Test doubles ──

    private sealed class RecordingEventPublisher : IPlatformEventPublisher
    {
        public List<PlatformEvent> Events { get; } = new();

        public Task<PlatformEvent?> AppendAndPublishAsync(
            PlatformEvent evt,
            CancellationToken ct = default)
        {
            evt.Id = Guid.NewGuid();
            evt.CreatedAt = DateTime.UtcNow;
            Events.Add(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }
    }
}
