using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Orgs;

/// <summary>
/// Cross-tenant guard for <see cref="AdminEndpoints.DeleteInvite"/>
/// (<c>DELETE /api/admin/users/invites/{id}</c>).
///
/// <para>The handler used to call the id-only <c>IInviteRepository.DeleteAsync</c>
/// — whose own <c>[Obsolete]</c> message pointed at <c>DeleteScopedAsync</c> "for
/// the per-tenant invariant" — so an admin of tenant A could revoke tenant B's
/// pending invite by id. The id-only member is now GONE from the interface, and
/// these tests pin the behaviour that replaced it: a foreign id is refused with
/// 404 AND the row survives (a 404 that still deleted would be the same bug with
/// a quieter response).</para>
/// </summary>
[TestFixture]
public class AdminDeleteInviteTests
{
    private IServiceScope _scope = null!;
    private ITenantRepository _tenantRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IInviteRepository _inviteRepo = null!;
    private IUserRepository _userRepo = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _inviteRepo = _scope.ServiceProvider.GetRequiredService<IInviteRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    [Test]
    public async Task DeleteInvite_Returns404_AndKeepsTheRow_WhenTheInviteBelongsToAnotherTenant()
    {
        var (tenantA, ownerA) = await SeedTenantAsync();
        var (tenantB, ownerB) = await SeedTenantAsync();

        var victim = await CreateInviteAsync(tenantB, ownerB);

        // Caller is scoped to tenant A and passes tenant B's invite id.
        var result = await AdminEndpoints.DeleteInvite(
            victim.Id, _inviteRepo, new FakeTenantContext(tenantA));

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);

        (await _inviteRepo.GetByIdScopedAsync(tenantB, victim.Id))
            .Should().NotBeNull("a cross-tenant revoke must not delete the row");

        // Sanity: tenant A really did own something, so the 404 is about
        // ownership rather than an empty database.
        _ = ownerA;
        (await _inviteRepo.ListPendingByTenantAsync(tenantA)).Should().BeEmpty();
    }

    [Test]
    public async Task DeleteInvite_Deletes_WhenTheInviteBelongsToTheCallersTenant()
    {
        var (tenantA, ownerA) = await SeedTenantAsync();
        var invite = await CreateInviteAsync(tenantA, ownerA);

        var result = await AdminEndpoints.DeleteInvite(
            invite.Id, _inviteRepo, new FakeTenantContext(tenantA));

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);
        (await _inviteRepo.GetByIdScopedAsync(tenantA, invite.Id)).Should().BeNull();
    }

    [Test]
    public async Task DeleteInvite_Returns404_WhenTheIdDoesNotExist()
    {
        var (tenantA, _) = await SeedTenantAsync();

        var result = await AdminEndpoints.DeleteInvite(
            Guid.NewGuid(), _inviteRepo, new FakeTenantContext(tenantA));

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task DeleteInvite_Returns404_AndKeepsTheRow_WhenThereIsNoAmbientTenant()
    {
        var (tenantB, ownerB) = await SeedTenantAsync();
        var invite = await CreateInviteAsync(tenantB, ownerB);

        var result = await AdminEndpoints.DeleteInvite(
            invite.Id, _inviteRepo, new FakeTenantContext(null));

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
        (await _inviteRepo.GetByIdScopedAsync(tenantB, invite.Id)).Should().NotBeNull();
    }

    /// <summary>
    /// The unscoped delete is gone from the contract, not merely deprecated —
    /// so no future caller can re-open the hole by reaching past
    /// <c>DeleteScopedAsync</c>.
    /// </summary>
    [Test]
    public void IInviteRepository_hasNoIdOnlyDeleteMember()
    {
        typeof(IInviteRepository)
            .GetMethods()
            .Where(m => m.Name == "DeleteAsync")
            .Should().BeEmpty("the id-only delete was the cross-tenant revoke hole");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed class FakeTenantContext(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = id;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private async Task<(Guid TenantId, Guid OwnerId)> SeedTenantAsync()
    {
        var owner = await _userRepo.CreateAsync(new User
        {
            Email = $"owner-{Guid.NewGuid():N}@example.com",
            DisplayName = "Owner",
        });
        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "Acme",
            Slug = $"acme-{Guid.NewGuid():N}".Substring(0, 12),
            Type = "org",
            OwnerId = owner.Id,
        });
        await _membershipRepo.AddAsync(tenant.Id, owner.Id, TenantRoleHierarchy.Owner);
        return (tenant.Id, owner.Id);
    }

    private async Task<UserInvite> CreateInviteAsync(Guid tenantId, Guid invitedBy)
    {
        var rawToken = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();
        return await _inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantId,
            Email = $"invitee-{Guid.NewGuid():N}@example.com",
            Role = "member",
            InviteTokenHash = hash,
            InvitedBy = invitedBy,
            ExpiresAt = DateTime.UtcNow.AddHours(48),
        });
    }

    private static async Task<int> ExecuteAndGetStatus(IResult result)
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
