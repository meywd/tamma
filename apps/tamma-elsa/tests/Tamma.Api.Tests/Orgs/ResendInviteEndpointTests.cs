using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.RateLimit;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Orgs;

/// <summary>
/// Direct-handler tests for <see cref="OrgEndpoints.ResendInvite"/>
/// (story 18-7 task 3). Covers the success path, the four rejection
/// branches (not-found / accepted / expired / rate-limited), and the
/// invariants the brief calls out: token hash unchanged, expires_at
/// extended by ~72h, event emitted, email dispatched.
/// </summary>
[TestFixture]
public class ResendInviteEndpointTests
{
    private IServiceScope _scope = null!;
    private ITenantRepository _tenantRepo = null!;
    private ITenantMembershipRepository _membershipRepo = null!;
    private IInviteRepository _inviteRepo = null!;
    private IUserRepository _userRepo = null!;
    private IEventRepository _events = null!;
    private InMemoryEmailService _emailInbox = null!;
    private IConfiguration _config = null!;
    private ILoggerFactory _loggerFactory = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _membershipRepo = _scope.ServiceProvider.GetRequiredService<ITenantMembershipRepository>();
        _inviteRepo = _scope.ServiceProvider.GetRequiredService<IInviteRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _events = _scope.ServiceProvider.GetRequiredService<IEventRepository>();
        _emailInbox = new InMemoryEmailService();
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:Url"] = "http://localhost:3001",
            })
            .Build();
        _loggerFactory = NullLoggerFactory.Instance;
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    [Test]
    public async Task ResendInvite_Returns403_WhenRequesterIsMember()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var invite = await CreateInviteAsync(tenantId, ownerId);
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Member);

        var result = await OrgEndpoints.ResendInvite(
            tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task ResendInvite_Returns404_WhenInviteMissing()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.ResendInvite(
            tenantId, Guid.NewGuid(), _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task ResendInvite_Returns404_WhenCrossTenant()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var (otherTenantId, otherOwnerId) = await SeedTenantAsync();
        var crossInvite = await CreateInviteAsync(otherTenantId, otherOwnerId);

        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);
        var result = await OrgEndpoints.ResendInvite(
            tenantId, crossInvite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task ResendInvite_Returns400_WhenInviteAlreadyAccepted()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var invite = await CreateInviteAsync(tenantId, ownerId, acceptedAt: DateTime.UtcNow);
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.ResendInvite(
            tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ResendInvite_Returns400_WhenInviteExpired()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        // ExpiresAt in the past — already expired.
        var invite = await CreateInviteAsync(
            tenantId, ownerId, expiresAt: DateTime.UtcNow.AddHours(-1));
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.ResendInvite(
            tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ResendInvite_Returns200_ExtendsExpiry_DoesNotRotateTokenHash()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var originalExpiresAt = DateTime.UtcNow.AddHours(2);
        var invite = await CreateInviteAsync(tenantId, ownerId, expiresAt: originalExpiresAt);
        var originalTokenHash = invite.InviteTokenHash;
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        var result = await OrgEndpoints.ResendInvite(
            tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);

        // Reload invite from DB; expiry extended, hash unchanged.
        var refreshed = await _inviteRepo.GetByIdAsync(invite.Id);
        refreshed.Should().NotBeNull();
        refreshed!.ExpiresAt.Should().BeAfter(originalExpiresAt);
        refreshed.InviteTokenHash.Should().Be(originalTokenHash);
        // Sanity: extension should land within +/- 5 min of "now + 72h".
        var expected = DateTime.UtcNow.AddHours(72);
        refreshed.ExpiresAt.Should().BeCloseTo(expected, TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task ResendInvite_EmitsResentEvent()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var invite = await CreateInviteAsync(tenantId, ownerId);
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        await OrgEndpoints.ResendInvite(
            tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        var rows = await _events.QueryAsync(tenantId, "TENANT.MEMBER_INVITE_RESENT.SUCCESS", null, 10);
        rows.Should().HaveCount(1);
        var data = System.Text.Json.JsonDocument.Parse(rows[0].Data).RootElement;
        data.GetProperty("inviteId").GetString().Should().Be(invite.Id.ToString());
        data.GetProperty("email").GetString().Should().Be(invite.Email);
    }

    [Test]
    public async Task ResendInvite_DispatchesEmail_ToInviteRecipient()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var invite = await CreateInviteAsync(tenantId, ownerId);
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        await OrgEndpoints.ResendInvite(
            tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            new InMemoryRateLimitService(), _events, _loggerFactory, _config,
            Principal(ownerId), ctx);

        // Email dispatch is fire-and-forget; give the background task a brief
        // window to land in the in-memory inbox before asserting.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (_emailInbox.SentMessages.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        _emailInbox.SentMessages.Should().ContainSingle();
        var msg = _emailInbox.SentMessages[0];
        msg.To.Should().Be(invite.Email);
        msg.TenantId.Should().Be(tenantId);
        msg.Template.Should().Be("tenant-invite");
    }

    [Test]
    public async Task ResendInvite_Returns429_WhenOverRateLimit()
    {
        var (tenantId, ownerId) = await SeedTenantAsync();
        var invite = await CreateInviteAsync(tenantId, ownerId);
        var rateLimits = new InMemoryRateLimitService();
        var ctx = HttpCtxWithRole(TenantRoleHierarchy.Admin);

        // Burn through the 3-per-hour cap.
        for (var i = 0; i < 3; i++)
        {
            var ok = await OrgEndpoints.ResendInvite(
                tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
                rateLimits, _events, _loggerFactory, _config,
                Principal(ownerId), ctx);
            (await ExecuteAndGetStatus(ok)).Should().Be(StatusCodes.Status200OK);
        }

        // 4th call within the window — over limit.
        var rejected = await OrgEndpoints.ResendInvite(
            tenantId, invite.Id, _tenantRepo, _inviteRepo, _emailInbox,
            rateLimits, _events, _loggerFactory, _config,
            Principal(ownerId), ctx);
        (await ExecuteAndGetStatus(rejected)).Should().Be(StatusCodes.Status429TooManyRequests);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

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
        // Phase 3 -- tenant events live in the tenant store, which is only
        // reachable for provisioned tenants.
        await ApiTestFixture.ProvisionTenantAsync(tenant.Id);
        return (tenant.Id, owner.Id);
    }

    private async Task<UserInvite> CreateInviteAsync(
        Guid tenantId,
        Guid invitedBy,
        DateTime? expiresAt = null,
        DateTime? acceptedAt = null)
    {
        var rawToken = Guid.NewGuid().ToString("N");
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();
        return await _inviteRepo.CreateAsync(new UserInvite
        {
            TenantId = tenantId,
            Email = $"invitee-{Guid.NewGuid():N}@example.com",
            Role = "member",
            InviteTokenHash = hash,
            InvitedBy = invitedBy,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(48),
            AcceptedAt = acceptedAt,
        });
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
