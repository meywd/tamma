using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Orgs;

/// <summary>
/// Tests for the sole-owner guard and membership-cascade behaviour of
/// <see cref="AdminEndpoints.DeleteUser"/> (audit finding auth/019
/// follow-up). Verifies:
/// <list type="bullet">
///   <item>Deleting a sole owner returns 409 with remediation hint.</item>
///   <item>Deleting a user who is not a sole owner succeeds and cascades.</item>
///   <item>Transferring ownership then deleting succeeds.</item>
/// </list>
/// </summary>
[TestFixture]
public class AdminDeleteUserTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private IUserRepository _userRepo = null!;
    private IApiKeyRepository _apiKeyRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private ITenantRepository _tenantRepo = null!;
#pragma warning disable NUnit1032 // Provided by the shared scope / disposed with _scope
    private ILoggerFactory _lf = null!;
#pragma warning restore NUnit1032

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _apiKeyRepo = _scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _lf = _scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    // ── Self-delete + not-found guards (regression coverage) ────────────────

    [Test]
    public async Task DeleteUser_Returns400_WhenSelf()
    {
        var user = await _userRepo.CreateAsync(new User { Email = "self@example.com" });
        var principal = PrincipalFor(user.Id);

        var result = await AdminEndpoints.DeleteUser(
            user.Id, _userRepo, _apiKeyRepo, _membershipRepo, principal, _lf);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task DeleteUser_Returns404_WhenTargetMissing()
    {
        var caller = await _userRepo.CreateAsync(new User { Email = "caller@example.com" });
        var principal = PrincipalFor(caller.Id);

        var result = await AdminEndpoints.DeleteUser(
            Guid.NewGuid(), _userRepo, _apiKeyRepo, _membershipRepo, principal, _lf);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Sole-owner guard ────────────────────────────────────────────────────

    [Test]
    public async Task DeleteUser_Returns409_WhenTargetIsSoleOwner()
    {
        var caller = await _userRepo.CreateAsync(new User { Email = "caller@example.com" });
        var target = await _userRepo.CreateAsync(new User { Email = "solo@example.com" });

        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "Solo Org",
            Slug = $"solo-{Guid.NewGuid():N}".Substring(0, 12),
            Type = "org",
            OwnerId = target.Id,
        });
        await _membershipRepo.AddAsync(tenant.Id, target.Id, "owner");

        var principal = PrincipalFor(caller.Id);
        var result = await AdminEndpoints.DeleteUser(
            target.Id, _userRepo, _apiKeyRepo, _membershipRepo, principal, _lf);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status409Conflict);

        // Target still active — delete was blocked.
        var still = await _userRepo.GetByIdAsync(target.Id);
        still.Should().NotBeNull();
        still!.DeletedAt.Should().BeNull();
        still.IsActive.Should().BeTrue();
    }

    [Test]
    public async Task DeleteUser_Succeeds_WhenAnotherOwnerExists()
    {
        var caller = await _userRepo.CreateAsync(new User { Email = "caller@example.com" });
        var target = await _userRepo.CreateAsync(new User { Email = "co-owner@example.com" });
        var coOwner = await _userRepo.CreateAsync(new User { Email = "other-owner@example.com" });

        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "Duo Org",
            Slug = $"duo-{Guid.NewGuid():N}".Substring(0, 12),
            Type = "org",
            OwnerId = target.Id,
        });
        await _membershipRepo.AddAsync(tenant.Id, target.Id, "owner");
        await _membershipRepo.AddAsync(tenant.Id, coOwner.Id, "owner");

        var principal = PrincipalFor(caller.Id);
        var result = await AdminEndpoints.DeleteUser(
            target.Id, _userRepo, _apiKeyRepo, _membershipRepo, principal, _lf);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        // Target is soft-deleted and memberships cascaded. Use
        // IgnoreQueryFilters to bypass the DeletedAt filter.
        var deleted = await _db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == target.Id);
        deleted.Should().NotBeNull();
        deleted!.DeletedAt.Should().NotBeNull();
        (await _membershipRepo.GetRoleAsync(tenant.Id, target.Id)).Should().BeNull();
        // Co-owner's membership still intact.
        (await _membershipRepo.GetRoleAsync(tenant.Id, coOwner.Id)).Should().Be("owner");
    }

    [Test]
    public async Task DeleteUser_TransferThenDelete_Succeeds()
    {
        var caller = await _userRepo.CreateAsync(new User { Email = "admin@example.com" });
        var target = await _userRepo.CreateAsync(new User { Email = "owner@example.com" });
        var member = await _userRepo.CreateAsync(new User { Email = "member@example.com" });

        var tenant = await _tenantRepo.CreateAsync(new Tenant
        {
            Name = "Xfer Org",
            Slug = $"xfer-{Guid.NewGuid():N}".Substring(0, 12),
            Type = "org",
            OwnerId = target.Id,
        });
        await _membershipRepo.AddAsync(tenant.Id, target.Id, "owner");
        await _membershipRepo.AddAsync(tenant.Id, member.Id, "member");

        // Sole-owner guard blocks first.
        var principal = PrincipalFor(caller.Id);
        var blocked = await AdminEndpoints.DeleteUser(
            target.Id, _userRepo, _apiKeyRepo, _membershipRepo, principal, _lf);
        (await ExecuteAndGetStatus(blocked)).Should().Be(StatusCodes.Status409Conflict);

        // Promote member to owner (simulating a transfer-ownership call).
        await _membershipRepo.UpdateRoleAsync(tenant.Id, member.Id, "owner");
        // Also update the tenants.OwnerId denorm column for completeness.
        var t = await _tenantRepo.GetByIdAsync(tenant.Id);
        t!.OwnerId = member.Id;
        await _tenantRepo.UpdateAsync(t);

        // Now delete the original owner; should succeed.
        var result = await AdminEndpoints.DeleteUser(
            target.Id, _userRepo, _apiKeyRepo, _membershipRepo, principal, _lf);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        // Fresh owner still in place; target's membership cascaded.
        (await _membershipRepo.GetRoleAsync(tenant.Id, member.Id)).Should().Be("owner");
        (await _membershipRepo.GetRoleAsync(tenant.Id, target.Id)).Should().BeNull();
    }

    [Test]
    public async Task DeleteUser_Succeeds_WhenTargetHasNoTenants()
    {
        var caller = await _userRepo.CreateAsync(new User { Email = "caller@example.com" });
        var target = await _userRepo.CreateAsync(new User { Email = "loner@example.com" });

        var principal = PrincipalFor(caller.Id);
        var result = await AdminEndpoints.DeleteUser(
            target.Id, _userRepo, _apiKeyRepo, _membershipRepo, principal, _lf);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);
        var deleted = await _db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == target.Id);
        deleted.Should().NotBeNull();
        deleted!.DeletedAt.Should().NotBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ClaimsPrincipal PrincipalFor(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
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
