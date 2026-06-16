using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Authorization;
using Tamma.Api.Dtos.Orgs;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Email;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Orgs;

/// <summary>
/// Direct-handler integration tests for <see cref="OrgEndpoints"/>. The
/// test fixture's permissive-dev auth wouldn't satisfy our endpoint
/// filter; calling handlers directly lets us verify the in-handler
/// invariants (findings 007, 010, 012, 013, 020, 021).
/// </summary>
[TestFixture]
public class OrgEndpointHandlerTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032 // Disposed via _scope
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private ITenantRepository _tenantRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IInviteRepository _inviteRepo = null!;
    private IUserRepository _userRepo = null!;
    private IEventRepository _events = null!;
    private Tamma.Data.Abstractions.IPlatformEventPublisher _publisher = null!;
    private InMemoryEmailService _emailInbox = null!;
    private DeleteConfirmationService _confirmation = null!;
    private ITenantProvisioningService _provisioning = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _inviteRepo = _scope.ServiceProvider.GetRequiredService<IInviteRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
        _publisher = _scope.ServiceProvider.GetRequiredService<Tamma.Data.Abstractions.IPlatformEventPublisher>();
        _provisioning = _scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        _emailInbox = new InMemoryEmailService();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
            })
            .Build();
        _confirmation = new DeleteConfirmationService(config);
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<User> CreateUser(string email = "alice@example.com", string display = "Alice")
    {
        var user = await _userRepo.CreateAsync(new User
        {
            Email = email,
            DisplayName = display,
        });
        return user;
    }

    private static ClaimsPrincipal Principal(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("name", "Test User"),
            new Claim(ClaimTypes.Email, "test@example.com"),
        }, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static HttpContext HttpCtxWithRole(string role)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[RequireTenantMembershipFilter.TenantRoleItemKey] = role;
        return ctx;
    }

    // ── CreateOrg (findings 007, 008, 009) ──────────────────────────────────

    [Test]
    public async Task CreateOrg_Returns400_WhenSlugIsReserved()
    {
        var user = await CreateUser();
        var result = await OrgEndpoints.CreateOrg(
            new CreateOrgRequest("Acme", "admin"),
            _tenantRepo, _membershipRepo, _userRepo, _events, _provisioning, Principal(user.Id));

        // BadRequest result type
        result.Should().BeAssignableTo<IResult>();
        var status = await ExecuteAndGetStatus(result);
        status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CreateOrg_Returns400_WhenNameTooShort()
    {
        var user = await CreateUser();
        var result = await OrgEndpoints.CreateOrg(
            new CreateOrgRequest("A", "acme-corp"),
            _tenantRepo, _membershipRepo, _userRepo, _events, _provisioning, Principal(user.Id));
        var status = await ExecuteAndGetStatus(result);
        status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CreateOrg_Returns400_WhenSlugInvalid()
    {
        var user = await CreateUser();
        var result = await OrgEndpoints.CreateOrg(
            new CreateOrgRequest("Acme", "my.org"),  // '.' never valid in slug
            _tenantRepo, _membershipRepo, _userRepo, _events, _provisioning, Principal(user.Id));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CreateOrg_Returns409_OnDuplicateSlug()
    {
        var user = await CreateUser();
        await _tenantRepo.CreateAsync(new Tenant { Name = "Existing", Slug = "acme-corp", Type = "org" });

        var result = await OrgEndpoints.CreateOrg(
            new CreateOrgRequest("Acme Two", "acme-corp"),
            _tenantRepo, _membershipRepo, _userRepo, _events, _provisioning, Principal(user.Id));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task CreateOrg_PersistsActiveTenant_AndEmitsEvent()
    {
        var user = await CreateUser();
        var result = await OrgEndpoints.CreateOrg(
            new CreateOrgRequest("Acme Inc.", "acme-corp"),
            _tenantRepo, _membershipRepo, _userRepo, _events, _provisioning, Principal(user.Id));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status201Created);

        var refreshed = await _userRepo.GetByIdAsync(user.Id);
        refreshed!.TenantId.Should().NotBeNull();

        var events = await _events.QueryAsync(refreshed.TenantId, "TENANT.CREATED.SUCCESS", null, 10);
        events.Should().HaveCountGreaterThan(0);
    }

    // ── UpdateMemberRole (finding 012) ──────────────────────────────────────

    [Test]
    public async Task UpdateMemberRole_Returns400_WhenRoleUnknown()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.UpdateMemberRole(
            tenantId, ownerId, new UpdateMemberRoleRequest("root"),
            _membershipRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task UpdateMemberRole_Returns404_WhenTargetNotMember()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.UpdateMemberRole(
            tenantId, Guid.NewGuid(), new UpdateMemberRoleRequest("admin"),
            _membershipRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task UpdateMemberRole_Returns403_WhenAdminTriesToPromoteToOwner()
    {
        var (tenantId, ownerId, memberId) = await SeedTenantWithOwnerAndMember();
        // Promote member to admin
        await _membershipRepo.UpdateRoleAsync(tenantId, memberId, TenantRoleHierarchy.Admin);
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.UpdateMemberRole(
            tenantId, memberId, new UpdateMemberRoleRequest("owner"),
            _membershipRepo, _events, Principal(memberId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task UpdateMemberRole_Returns400_WhenDemotingLastOwner()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.UpdateMemberRole(
            tenantId, ownerId, new UpdateMemberRoleRequest("admin"),
            _membershipRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task UpdateMemberRole_Succeeds_WhenOwnerPromotesMember()
    {
        var (tenantId, ownerId, memberId) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.UpdateMemberRole(
            tenantId, memberId, new UpdateMemberRoleRequest("admin"),
            _membershipRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        (await _membershipRepo.GetRoleAsync(tenantId, memberId)).Should().Be("admin");
    }

    /// <summary>
    /// Story 18-7 task 1: every successful role change appends a
    /// <c>TENANT.MEMBER_ROLE_CHANGED.SUCCESS</c> event so the audit log
    /// is complete. Tags must include caller + target ids; data must
    /// include old + new role.
    /// </summary>
    [Test]
    public async Task UpdateMemberRole_EmitsRoleChangedEvent_OnSuccess()
    {
        var (tenantId, ownerId, memberId) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        await OrgEndpoints.UpdateMemberRole(
            tenantId, memberId, new UpdateMemberRoleRequest("admin"),
            _membershipRepo, _events, Principal(ownerId), ctx);

        var rows = await _events.QueryAsync(tenantId, "TENANT.MEMBER_ROLE_CHANGED.SUCCESS", null, 10);
        rows.Should().HaveCount(1);

        var evt = rows[0];
        evt.TenantId.Should().Be(tenantId);

        // Tags carries tenantId + caller userId.
        var tags = System.Text.Json.JsonDocument.Parse(evt.Tags).RootElement;
        tags.GetProperty("tenantId").GetString().Should().Be(tenantId.ToString());
        tags.GetProperty("userId").GetString().Should().Be(ownerId.ToString());

        // Data carries the role-change payload (target + old + new).
        var data = System.Text.Json.JsonDocument.Parse(evt.Data).RootElement;
        data.GetProperty("targetUserId").GetString().Should().Be(memberId.ToString());
        data.GetProperty("oldRole").GetString().Should().Be("member");
        data.GetProperty("newRole").GetString().Should().Be("admin");
    }

    [Test]
    public async Task UpdateMemberRole_DoesNotEmitEvent_OnRejection()
    {
        var (tenantId, ownerId, memberId) = await SeedTenantWithOwnerAndMember();
        await _membershipRepo.UpdateRoleAsync(tenantId, memberId, TenantRoleHierarchy.Admin);
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        // Admin tries to promote peer to owner — 403.
        await OrgEndpoints.UpdateMemberRole(
            tenantId, memberId, new UpdateMemberRoleRequest("owner"),
            _membershipRepo, _events, Principal(memberId), ctx);

        var rows = await _events.QueryAsync(tenantId, "TENANT.MEMBER_ROLE_CHANGED.SUCCESS", null, 10);
        rows.Should().BeEmpty();
    }

    // ── RemoveMember (finding 013) ──────────────────────────────────────────

    [Test]
    public async Task RemoveMember_Returns400_WhenSelfRemovingLastOwner()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.RemoveMember(
            tenantId, ownerId, _membershipRepo, _userRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task RemoveMember_Returns403_WhenAdminTriesToRemoveOwner()
    {
        var (tenantId, ownerId, memberId) = await SeedTenantWithOwnerAndMember();
        await _membershipRepo.UpdateRoleAsync(tenantId, memberId, TenantRoleHierarchy.Admin);
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.RemoveMember(
            tenantId, ownerId, _membershipRepo, _userRepo, _events, Principal(memberId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task RemoveMember_Returns404_WhenTargetNotMember()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.RemoveMember(
            tenantId, Guid.NewGuid(), _membershipRepo, _userRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task RemoveMember_Returns403_WhenRequesterIsMember()
    {
        var (tenantId, _, memberId) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Member);

        var result = await OrgEndpoints.RemoveMember(
            tenantId, memberId, _membershipRepo, _userRepo, _events, Principal(memberId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    // ── TransferOwnership (finding 020) ─────────────────────────────────────

    [Test]
    public async Task TransferOwnership_Returns400_WhenSameUser()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.TransferOwnership(
            tenantId, new TransferOwnershipRequest(ownerId),
            _db, _tenantRepo, _membershipRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task TransferOwnership_Returns400_WhenNewOwnerNotMember()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.TransferOwnership(
            tenantId, new TransferOwnershipRequest(Guid.NewGuid()),
            _db, _tenantRepo, _membershipRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task TransferOwnership_Returns403_WhenRequesterNotOwner()
    {
        var (tenantId, ownerId, memberId) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.TransferOwnership(
            tenantId, new TransferOwnershipRequest(memberId),
            _db, _tenantRepo, _membershipRepo, _events, Principal(memberId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task TransferOwnership_SwapsRoles_AndUpdatesOwnerColumn()
    {
        var (tenantId, ownerId, memberId) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.TransferOwnership(
            tenantId, new TransferOwnershipRequest(memberId),
            _db, _tenantRepo, _membershipRepo, _events, Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        (await _membershipRepo.GetRoleAsync(tenantId, memberId)).Should().Be("owner");
        (await _membershipRepo.GetRoleAsync(tenantId, ownerId)).Should().Be("admin");
        var tenant = await _tenantRepo.GetByIdAsync(tenantId);
        tenant!.OwnerId.Should().Be(memberId);
    }

    // ── DeleteOrg (finding 021) ─────────────────────────────────────────────

    [Test]
    public async Task DeleteOrg_Returns409_WhenLastTenant()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        var result = await OrgEndpoints.DeleteOrg(
            tenantId, _db, _tenantRepo, _membershipRepo, _inviteRepo, _userRepo,
            _confirmation, _publisher, Principal(ownerId), ctx, confirm: null);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task DeleteOrg_Returns202_OnPhase1AndMintsConfirmationToken()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        // Give the owner a second tenant so last-tenant guard passes.
        var second = await _tenantRepo.CreateAsync(new Tenant { Name = "Other", Slug = "other-co", Type = "org" });
        await _membershipRepo.AddAsync(second.Id, ownerId, "owner");

        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);
        var result = await OrgEndpoints.DeleteOrg(
            tenantId, _db, _tenantRepo, _membershipRepo, _inviteRepo, _userRepo,
            _confirmation, _publisher, Principal(ownerId), ctx, confirm: null);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status202Accepted);

        // Tenancy residual (post-#343): the terminal soft-delete event must
        // land in the CONTROL-PLANE store (the tenant's own store is
        // unreachable post-delete, defeating the audit purpose).
        var cpEvents = await _db.PlatformEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Type == "TENANT.DELETED.SUCCESS")
            .ToListAsync();
        cpEvents.Should().HaveCount(1);
        cpEvents[0].UserId.Should().Be(ownerId);
        cpEvents[0].Data.Should().Contain("soft-delete");
    }

    [Test]
    public async Task DeleteOrg_Phase2_EmitsPurgedEventToControlPlaneStore()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var second = await _tenantRepo.CreateAsync(new Tenant { Name = "Other", Slug = "other-co2", Type = "org" });
        await _membershipRepo.AddAsync(second.Id, ownerId, "owner");

        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);

        // Phase 2: hard-delete with a VALID token. (The handler 404s
        // already-soft-deleted rows, so phase 2 runs against the live row.)
        var token = _confirmation.Generate(tenantId, ownerId);
        var phase2 = await OrgEndpoints.DeleteOrg(
            tenantId, _db, _tenantRepo, _membershipRepo, _inviteRepo, _userRepo,
            _confirmation, _publisher, Principal(ownerId), ctx, confirm: token.Token);
        (await ExecuteAndGetStatus(phase2)).Should().Be(StatusCodes.Status204NoContent);

        // Terminal purge event survives in the control-plane store even
        // though the tenant (and its event store) is gone.
        var purged = await _db.PlatformEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Type == "TENANT.PURGED.SUCCESS")
            .ToListAsync();
        purged.Should().HaveCount(1);
        purged[0].UserId.Should().Be(ownerId);
        purged[0].Data.Should().Contain("hard-delete");
    }

    [Test]
    public async Task DeleteOrg_Phase2_Returns400_WhenConfirmationInvalid()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var second = await _tenantRepo.CreateAsync(new Tenant { Name = "Other", Slug = "other-co", Type = "org" });
        await _membershipRepo.AddAsync(second.Id, ownerId, "owner");

        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Owner);
        var result = await OrgEndpoints.DeleteOrg(
            tenantId, _db, _tenantRepo, _membershipRepo, _inviteRepo, _userRepo,
            _confirmation, _publisher, Principal(ownerId), ctx, confirm: "junk.deadbeef");
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── AcceptInvite (finding 017) ──────────────────────────────────────────

    [Test]
    public async Task AcceptInvite_Returns400_WhenInviteAlreadyAccepted()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var newUser = await CreateUser("invitee@example.com", "Invitee");

        var rawToken = "sometoken12345";
        var hash = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();
        var invite = await _inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantId,
            Email = "invitee@example.com",
            Role = "member",
            InviteTokenHash = hash,
            InvitedBy = ownerId,
            ExpiresAt = DateTime.UtcNow.AddHours(72),
            AcceptedAt = DateTime.UtcNow,
        });

        var result = await OrgEndpoints.AcceptInvite(
            new AcceptInviteRequest(rawToken),
            _inviteRepo, _membershipRepo, _userRepo, _events, Principal(newUser.Id));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task AcceptInvite_IsIdempotent_WhenAlreadyMember()
    {
        var (tenantId, _, memberId) = await SeedTenantWithOwnerAndMember();

        var rawToken = "idempotenttoken";
        var hash = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();
        await _inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantId,
            Email = "member@example.com",
            Role = "admin",
            InviteTokenHash = hash,
            InvitedBy = memberId,
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        });

        var result = await OrgEndpoints.AcceptInvite(
            new AcceptInviteRequest(rawToken),
            _inviteRepo, _membershipRepo, _userRepo, _events, Principal(memberId));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task AcceptInvite_AddsMembership_AndPersistsActiveTenant()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var newUser = await CreateUser("fresh@example.com", "Fresh");

        var rawToken = "freshtoken";
        var hash = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();
        await _inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantId,
            Email = "fresh@example.com",
            Role = "member",
            InviteTokenHash = hash,
            InvitedBy = ownerId,
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        });

        var result = await OrgEndpoints.AcceptInvite(
            new AcceptInviteRequest(rawToken),
            _inviteRepo, _membershipRepo, _userRepo, _events, Principal(newUser.Id));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        (await _membershipRepo.GetRoleAsync(tenantId, newUser.Id)).Should().Be("member");
        var refreshed = await _userRepo.GetByIdAsync(newUser.Id);
        refreshed!.TenantId.Should().Be(tenantId);
    }

    // ── DeleteInvite (finding 016) ──────────────────────────────────────────

    [Test]
    public async Task DeleteInvite_Returns404_WhenMissing()
    {
        var (tenantId, _, _) = await SeedTenantWithOwnerAndMember();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.DeleteInvite(
            tenantId, Guid.NewGuid(), _inviteRepo, ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task DeleteInvite_Returns404_WhenCrossTenant()
    {
        var (tenantId, ownerId, _) = await SeedTenantWithOwnerAndMember();
        var other = await _tenantRepo.CreateAsync(new Tenant { Name = "Other", Slug = "other-co", Type = "org" });
        var invite = await _inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = other.Id,
            Email = "x@example.com",
            Role = "member",
            InviteTokenHash = "abc",
            InvitedBy = ownerId,
            ExpiresAt = DateTime.UtcNow.AddHours(72),
        });

        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);
        var result = await OrgEndpoints.DeleteInvite(
            tenantId, invite.Id, _inviteRepo, ctx);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(Guid TenantId, Guid OwnerId, Guid MemberId)> SeedTenantWithOwnerAndMember()
    {
        var owner = await CreateUser($"owner-{Guid.NewGuid():N}@example.com", "Owner");
        var member = await CreateUser($"member-{Guid.NewGuid():N}@example.com", "Member");
        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "Acme Corp",
            Slug = $"acme-{Guid.NewGuid():N}".Substring(0, 12),
            Type = "org",
            OwnerId = owner.Id,
        });
        await _membershipRepo.AddAsync(tenant.Id, owner.Id, "owner");
        await _membershipRepo.AddAsync(tenant.Id, member.Id, "member");
        // Phase 3 -- handlers emit DCB events into the tenant store, which
        // is only reachable for provisioned tenants.
        await ApiTestFixture.ProvisionTenantAsync(tenant.Id);
        // Provisioning stamped the envelope/status shadow columns in its
        // own scope. Drop this scope's stale tracked Tenant (created before
        // provisioning) so a handler's full-entity Update() can't write the
        // pre-provisioning nulls back over the stored envelope.
        _db.ChangeTracker.Clear();
        return (tenant.Id, owner.Id, member.Id);
    }

    /// <summary>
    /// Executes the <see cref="IResult"/> against an in-memory HttpContext
    /// to capture the status code without a full HTTP pipeline.
    /// </summary>
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
