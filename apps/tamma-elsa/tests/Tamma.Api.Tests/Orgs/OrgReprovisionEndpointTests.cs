using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.TenantStatus;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Orgs;

/// <summary>
/// Direct-handler tests for the self-service re-provision endpoint
/// (<c>POST /api/v1/orgs/{tenantId}/reprovision</c> →
/// <see cref="OrgEndpoints.ReprovisionOrg"/>). Mirrors the
/// <see cref="OrgEndpointHandlerTests"/> style: handlers are invoked
/// directly so the in-handler invariants (role gate, status state
/// machine, CP-store audit events) are testable without minting JWTs.
/// Cross-tenant protection is owned by
/// <see cref="RequireTenantMembershipFilter"/> on the route — pinned by
/// the existing filter test suite — so these tests focus on the handler
/// body.
/// </summary>
[TestFixture]
public class OrgReprovisionEndpointTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032 // Disposed via _scope
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private ITenantRepository _tenantRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IUserRepository _userRepo = null!;
    private ITenantProvisioningService _provisioning = null!;
    private IPlatformEventPublisher _publisher = null!;
    private ITenantStatusCache _statusCache = null!;
    private ITenantConnectionResolver _resolver = null!;
    private ITenantStatusInvalidationBus _invalidationBus = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _provisioning = _scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        _publisher = _scope.ServiceProvider.GetRequiredService<IPlatformEventPublisher>();
        _statusCache = _scope.ServiceProvider.GetRequiredService<ITenantStatusCache>();
        _resolver = _scope.ServiceProvider.GetRequiredService<ITenantConnectionResolver>();
        _invalidationBus = _scope.ServiceProvider.GetRequiredService<ITenantStatusInvalidationBus>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── Authorization ───────────────────────────────────────────────────────

    [Test]
    public async Task Reprovision_Returns403_WhenCallerIsMemberRole()
    {
        var (tenantId, _, memberId) = await SeedUnprovisionedTenant(status: "failed");

        var result = await Invoke(tenantId, memberId, role: TenantRoleHierarchy.Member);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task Reprovision_Returns404_WhenTenantSoftDeleted()
    {
        var (tenantId, ownerId, _) = await SeedUnprovisionedTenant(status: "failed");
        await _tenantRepo.SoftDeleteAsync(tenantId);
        _db.ChangeTracker.Clear();

        var result = await Invoke(tenantId, ownerId, role: TenantRoleHierarchy.Owner);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── State machine ───────────────────────────────────────────────────────

    [Test]
    public async Task Reprovision_Returns409_WhenProvisioningAlreadyInFlight()
    {
        var (tenantId, ownerId, _) = await SeedUnprovisionedTenant(status: "provisioning");

        var result = await Invoke(tenantId, ownerId, role: TenantRoleHierarchy.Owner);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status409Conflict);

        // Still in flight — the handler must not have touched the status.
        (await ReadStatus(tenantId)).Should().Be("provisioning");
    }

    [Test]
    public async Task Reprovision_Returns409_WhenAlreadyProvisioned()
    {
        // Seed via the real pipeline → Status 'active' + stored envelope.
        var (tenantId, ownerId, _) = await SeedProvisionedTenant();

        var result = await Invoke(tenantId, ownerId, role: TenantRoleHierarchy.Owner);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task Reprovision_Returns409_WhenTenantSuspended()
    {
        // ck_tenants_connection_string_present requires an envelope for
        // 'suspended', so provision first, then flip the status.
        var (tenantId, ownerId, _) = await SeedProvisionedTenant();
        var tracked = await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
        _db.Entry(tracked).Property("Status").CurrentValue = "suspended";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await Invoke(tenantId, ownerId, role: TenantRoleHierarchy.Owner);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status409Conflict);
        (await ReadStatus(tenantId)).Should().Be("suspended");
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Test]
    public async Task Reprovision_Succeeds_FromFailedState_AsTenantAdmin()
    {
        var (tenantId, _, _) = await SeedUnprovisionedTenant(status: "failed");
        var admin = await CreateUser($"admin-{Guid.NewGuid():N}@example.com");
        await _membershipRepo.AddAsync(tenantId, admin.Id, TenantRoleHierarchy.Admin);

        var result = await Invoke(tenantId, admin.Id, role: TenantRoleHierarchy.Admin);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        (await ReadStatus(tenantId)).Should().Be("active");
        (await HasEnvelope(tenantId)).Should().BeTrue(
            "the real provisioning pipeline must have minted + persisted the encrypted connection string");

        // Audit trail lands in the CONTROL-PLANE store.
        var types = await _db.PlatformEvents
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Select(e => e.Type)
            .ToListAsync();
        types.Should().Contain("TENANT.PROVISIONING_REQUESTED");
        types.Should().Contain("TENANT.PROVISIONED.SUCCESS");
    }

    [Test]
    public async Task Reprovision_Succeeds_FromDegradedCreateOrgLeftover()
    {
        // CreateOrg leftover shape: row exists, Status never advanced past
        // its default (NULL → treated as legacy-active), NO envelope.
        var (tenantId, ownerId, _) = await SeedUnprovisionedTenant(status: null);

        var result = await Invoke(tenantId, ownerId, role: TenantRoleHierarchy.Owner);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        (await ReadStatus(tenantId)).Should().Be("active");
        (await HasEnvelope(tenantId)).Should().BeTrue();
    }

    [Test]
    public async Task Reprovision_IsIdempotent_SecondCallReturns409()
    {
        var (tenantId, ownerId, _) = await SeedUnprovisionedTenant(status: "failed");

        var first = await Invoke(tenantId, ownerId, role: TenantRoleHierarchy.Owner);
        (await ExecuteAndGetStatus(first)).Should().Be(StatusCodes.Status200OK);

        _db.ChangeTracker.Clear();
        var second = await Invoke(tenantId, ownerId, role: TenantRoleHierarchy.Owner);
        (await ExecuteAndGetStatus(second)).Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private Task<IResult> Invoke(Guid tenantId, Guid callerId, string role)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = _scope.ServiceProvider,
        };
        ctx.Items[RequireTenantMembershipFilter.TenantRoleItemKey] = role;

        return OrgEndpoints.ReprovisionOrg(
            tenantId, _db, _provisioning, _publisher, _statusCache,
            _resolver, _invalidationBus, Principal(callerId), ctx);
    }

    private async Task<User> CreateUser(string email)
        => await _userRepo.CreateAsync(new User { Email = email, DisplayName = "Test" });

    /// <summary>
    /// Tenant row + owner/member memberships WITHOUT running the
    /// provisioning pipeline (no role/schema/envelope), then pins the
    /// Status shadow column to <paramref name="status"/>.
    /// </summary>
    private async Task<(Guid TenantId, Guid OwnerId, Guid MemberId)> SeedUnprovisionedTenant(string? status)
    {
        var owner = await CreateUser($"owner-{Guid.NewGuid():N}@example.com");
        var member = await CreateUser($"member-{Guid.NewGuid():N}@example.com");
        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "Reprov Org",
            Slug = $"rp-{Guid.NewGuid():N}".Substring(0, 12),
            Type = "org",
            OwnerId = owner.Id,
        });
        await _membershipRepo.AddAsync(tenant.Id, owner.Id, TenantRoleHierarchy.Owner);
        await _membershipRepo.AddAsync(tenant.Id, member.Id, TenantRoleHierarchy.Member);

        if (status is not null)
        {
            var tracked = await _db.Tenants.IgnoreQueryFilters()
                .FirstAsync(t => t.Id == tenant.Id);
            _db.Entry(tracked).Property("Status").CurrentValue = status;
            await _db.SaveChangesAsync();
        }

        _db.ChangeTracker.Clear();
        return (tenant.Id, owner.Id, member.Id);
    }

    private async Task<(Guid TenantId, Guid OwnerId, Guid MemberId)> SeedProvisionedTenant()
    {
        var seeded = await SeedUnprovisionedTenant(status: null);
        await ApiTestFixture.ProvisionTenantAsync(seeded.TenantId);
        _db.ChangeTracker.Clear();
        return seeded;
    }

    private async Task<string?> ReadStatus(Guid tenantId)
        => await _db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => EF.Property<string?>(t, "Status"))
            .FirstOrDefaultAsync();

    private async Task<bool> HasEnvelope(Guid tenantId)
    {
        var envelope = await _db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .Select(t => EF.Property<byte[]?>(t, "EncryptedConnectionString"))
            .FirstOrDefaultAsync();
        return envelope is { Length: > 0 };
    }

    private static ClaimsPrincipal Principal(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private async Task<int> ExecuteAndGetStatus(IResult result)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = ApiTestFixture.Factory.Services,
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }
}
