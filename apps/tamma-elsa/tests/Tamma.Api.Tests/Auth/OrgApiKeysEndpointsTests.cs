using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Authorization;
using Tamma.Api.Dtos.ApiKeys;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-7 deferred-item — direct-handler tests for tenant-scoped
/// API-key CRUD on <see cref="OrgApiKeysEndpoints"/>. Tests the role gate
/// + reveal-once + RateLimitRpm shadow column + index row wiring.
/// </summary>
[TestFixture]
public class OrgApiKeysEndpointsTests
{
    private ControlPlaneDbContext _cp = null!;
    private ApiKeyRepository _apiKeyRepo = null!;
    private PlatformApiKeyIndexRepository _indexRepo = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _cp = new ControlPlaneDbContext(options);
        _apiKeyRepo = new ApiKeyRepository(_cp);
        _indexRepo = new PlatformApiKeyIndexRepository(_cp);
    }

    [TearDown]
    public void TearDown() => _cp.Dispose();

    private static HttpContext HttpCtxWithRole(string role)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[RequireTenantMembershipFilter.TenantRoleItemKey] = role;
        return ctx;
    }

    private static T Unwrap<T>(IResult result) where T : class
    {
        var valueProp = result.GetType()
            .GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        return valueProp!.GetValue(result) as T
            ?? throw new InvalidOperationException("Missing response body");
    }

    [Test]
    public async Task CreateApiKey_AsMember_Returns403()
    {
        var tid = Guid.NewGuid();
        var result = await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: "x"),
            HttpCtxWithRole(TenantRoleHierarchy.Member),
            _cp, _apiKeyRepo, _indexRepo);

        // Results.Json returns a JsonHttpResult with StatusCode 403
        result.Should().BeAssignableTo<IStatusCodeHttpResult>();
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(403);
    }

    [Test]
    public async Task CreateApiKey_AsAdmin_MintsTenantKey_AndRevealsOnce()
    {
        var tid = Guid.NewGuid();
        var result = await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: "tenant-key", RateLimitRpm: 100),
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _cp, _apiKeyRepo, _indexRepo);

        result.Should().BeOfType<Created<CreateApiKeyResponse>>();
        var body = Unwrap<CreateApiKeyResponse>(result);
        body.Key.Should().StartWith("tamma_sk_t_");
        body.TenantId.Should().Be(tid);
        body.RateLimitRpm.Should().Be(100);

        var stored = await _cp.ApiKeys.SingleAsync();
        stored.Scope.Should().Be("tenant");
        stored.TenantId.Should().Be(tid);
        stored.KeyHash.Should().StartWith("argon2id$");
        ApiKeyHasher.Verify(body.Key, stored.KeyHash).Should().BeTrue();
        _cp.Entry(stored).Property<int?>("RateLimitRpm").CurrentValue.Should().Be(100);

        var index = await _cp.PlatformApiKeyIndex.SingleAsync();
        index.TenantId.Should().Be(tid);
        index.Scope.Should().Be("tenant");
    }

    [Test]
    public async Task CreateApiKey_MissingLabel_ReturnsBadRequest()
    {
        var tid = Guid.NewGuid();
        var result = await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: string.Empty),
            HttpCtxWithRole(TenantRoleHierarchy.Owner),
            _cp, _apiKeyRepo, _indexRepo);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task ListApiKeys_OnlyReturnsPathTenantRows()
    {
        var tid = Guid.NewGuid();
        await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: "ours"),
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _cp, _apiKeyRepo, _indexRepo);
        // Seed a different tenant's key.
        _cp.ApiKeys.Add(new ApiKey
        {
            Scope = "tenant",
            OwnerId = Guid.NewGuid().ToString(),
            KeyHash = "h", KeyPrefix = "p", Label = "other",
            TenantId = Guid.NewGuid(),
        });
        await _cp.SaveChangesAsync();

        var result = await OrgApiKeysEndpoints.ListApiKeys(tid, _cp);
        var list = Unwrap<List<ApiKeySummaryResponse>>(result);

        list.Should().HaveCount(1);
        list[0].TenantId.Should().Be(tid);
    }

    [Test]
    public async Task GetApiKey_WrongTenant_ReturnsNotFound()
    {
        var tid = Guid.NewGuid();
        var create = await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: "k"),
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _cp, _apiKeyRepo, _indexRepo);
        var created = Unwrap<CreateApiKeyResponse>(create);

        var wrongTid = Guid.NewGuid();
        var result = await OrgApiKeysEndpoints.GetApiKey(wrongTid, created.Id, _cp);
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(404);
    }

    [Test]
    public async Task DeleteApiKey_AsAdmin_Revokes()
    {
        var tid = Guid.NewGuid();
        var create = await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: "k"),
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _cp, _apiKeyRepo, _indexRepo);
        var created = Unwrap<CreateApiKeyResponse>(create);

        var result = await OrgApiKeysEndpoints.DeleteApiKey(
            tid, created.Id,
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _apiKeyRepo, _indexRepo);

        result.Should().BeOfType<NoContent>();
        var stored = await _cp.ApiKeys.FindAsync(created.Id);
        stored!.RevokedAt.Should().NotBeNull();
        var index = await _cp.PlatformApiKeyIndex.SingleAsync();
        index.RevokedAt.Should().NotBeNull();
    }

    [Test]
    public async Task DeleteApiKey_AsMember_Returns403()
    {
        var tid = Guid.NewGuid();
        var create = await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: "k"),
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _cp, _apiKeyRepo, _indexRepo);
        var created = Unwrap<CreateApiKeyResponse>(create);

        var result = await OrgApiKeysEndpoints.DeleteApiKey(
            tid, created.Id,
            HttpCtxWithRole(TenantRoleHierarchy.Member),
            _apiKeyRepo, _indexRepo);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(403);
    }

    [Test]
    public async Task DeleteApiKey_WrongTenant_ReturnsNotFound()
    {
        var tid = Guid.NewGuid();
        var create = await OrgApiKeysEndpoints.CreateApiKey(
            tid,
            new CreateApiKeyRequest(Label: "k"),
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _cp, _apiKeyRepo, _indexRepo);
        var created = Unwrap<CreateApiKeyResponse>(create);

        var result = await OrgApiKeysEndpoints.DeleteApiKey(
            Guid.NewGuid(), created.Id,
            HttpCtxWithRole(TenantRoleHierarchy.Admin),
            _apiKeyRepo, _indexRepo);
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(404);
    }
}
