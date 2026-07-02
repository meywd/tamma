using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.PromptStore;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Orgs;

/// <summary>
/// Story 37-3 — direct-handler tests for the rewritten
/// <see cref="OrgEndpoints.ListTenantAudit"/> (rich query over the curated
/// <c>audit_records</c> read-model) + <see cref="AdminEndpoints.ListPlatformAudit"/>.
/// Covers the RBAC gate (member 403, admin 200, missing-role 403), invalid-filter
/// 400, limit clamping, and the legacy <c>type</c>/<c>offset</c> compat shim.
/// Filter/pagination/scoping correctness is proven in
/// <c>AuditQueryServiceTests</c>.
/// </summary>
[TestFixture]
public class TenantAuditEndpointTests
{
    private IServiceScope _scope = null!;
    private ITenantRepository _tenantRepo = null!;
    private IUserRepository _userRepo = null!;
    private IAuditQueryService _auditQuery = null!;
    private ITammaModeProvider _mode = null!;
    private static readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _tenantRepo = _scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
        _auditQuery = _scope.ServiceProvider.GetRequiredService<IAuditQueryService>();
        _mode = _scope.ServiceProvider.GetRequiredService<ITammaModeProvider>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    [Test]
    public async Task ListTenantAudit_Returns403_WhenRequesterIsMember()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var result = await Invoke(tenantId, HttpCtxWithRole(TenantRoleHierarchy.Member));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task ListTenantAudit_Returns403_WhenNoMembershipRoleSet()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var result = await Invoke(tenantId, new DefaultHttpContext());
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task ListTenantAudit_Returns200_ForAdmin()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var result = await Invoke(tenantId, HttpCtxWithRole(TenantRoleHierarchy.Admin));
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task ListTenantAudit_Returns400_OnInvalidFilterValue()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var result = await Invoke(tenantId, HttpCtxWithRole(TenantRoleHierarchy.Admin), severity: "bogus");
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task ListTenantAudit_ClampsLimit_StillReturns200()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var result = await Invoke(tenantId, HttpCtxWithRole(TenantRoleHierarchy.Admin), limit: 5000);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task ListTenantAudit_LegacyTypeAndOffset_StillReturns200()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var result = await Invoke(
            tenantId, HttpCtxWithRole(TenantRoleHierarchy.Admin), type: "SECRET.REVEAL", offset: 20);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task ListPlatformAudit_Returns200_OnValidQuery()
    {
        var result = await AdminEndpoints.ListPlatformAudit(
            _auditQuery, _mode, EmptyPrincipal(), _loggerFactory,
            category: null, action: null, actorUserId: null, targetType: null, targetId: null,
            severity: null, outcome: null, ipAddress: null, from: null, to: null, q: null,
            limit: null, cursor: null, type: null, offset: null, ct: default);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task ListPlatformAudit_Returns400_OnInvalidFilterValue()
    {
        var result = await AdminEndpoints.ListPlatformAudit(
            _auditQuery, _mode, EmptyPrincipal(), _loggerFactory,
            category: "not-a-category", action: null, actorUserId: null, targetType: null, targetId: null,
            severity: null, outcome: null, ipAddress: null, from: null, to: null, q: null,
            limit: null, cursor: null, type: null, offset: null, ct: default);
        (await ExecuteAndGetStatus(result)).Should().Be(StatusCodes.Status400BadRequest);
    }

    // ── Helpers ──

    private Task<IResult> Invoke(
        Guid tenantId, HttpContext ctx, string? severity = null, int? limit = null,
        string? type = null, int? offset = null)
        => OrgEndpoints.ListTenantAudit(
            tenantId, _auditQuery, _mode, EmptyPrincipal(), ctx, _loggerFactory,
            category: null, action: null, actorUserId: null, targetType: null, targetId: null,
            severity: severity, outcome: null, ipAddress: null, from: null, to: null, q: null,
            limit: limit, cursor: null, type: type, offset: offset, ct: default);

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
        await ApiTestFixture.ProvisionTenantAsync(tenant.Id);
        return (tenant.Id, owner.Id);
    }

    private static ClaimsPrincipal EmptyPrincipal() => new(new ClaimsIdentity());

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
