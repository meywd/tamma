using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.ApiKeys;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-7 deferred-item — direct-handler tests for platform-admin
/// API-key CRUD on <see cref="AdminApiKeysEndpoints"/>. Uses the EF
/// InMemory provider to avoid a real PG dependency.
/// </summary>
[TestFixture]
public class AdminApiKeysEndpointsTests
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

    private static T Unwrap<T>(IResult result) where T : class
    {
        // Minimal-API Results<T> returns a typed wrapper; reflection to pull
        // the response body is simplest and avoids a dependency on
        // Microsoft.AspNetCore.Mvc.Testing.
        var valueProp = result.GetType()
            .GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        return valueProp!.GetValue(result) as T
            ?? throw new InvalidOperationException("Missing response body");
    }

    [Test]
    public async Task CreateApiKey_Platform_RevealsKeyOnce_AndPersistsRow()
    {
        var req = new CreateApiKeyRequest(Label: "prod platform key");

        var result = await AdminApiKeysEndpoints.CreateApiKey(req, _apiKeyRepo, _indexRepo, _cp);

        result.Should().BeOfType<Created<CreateApiKeyResponse>>();
        var body = Unwrap<CreateApiKeyResponse>(result);
        body.Key.Should().StartWith("tamma_sk_pl_");
        body.Scope.Should().Be("platform");
        body.Warning.Should().Contain("never be shown again");

        var stored = await _cp.ApiKeys.SingleAsync();
        stored.KeyHash.Should().StartWith("argon2id$");
        ApiKeyHasher.Verify(body.Key, stored.KeyHash).Should().BeTrue();

        var index = await _cp.PlatformApiKeyIndex.SingleAsync();
        index.ApiKeyId.Should().Be(stored.Id);
        index.KeyPrefix.Should().Be(stored.KeyPrefix);
    }

    [Test]
    public async Task CreateApiKey_User_UsesUserPrefix()
    {
        var req = new CreateApiKeyRequest(Label: "me", Scope: "user");

        var result = await AdminApiKeysEndpoints.CreateApiKey(req, _apiKeyRepo, _indexRepo, _cp);

        var body = Unwrap<CreateApiKeyResponse>(result);
        body.Key.Should().StartWith("tamma_sk_u_");
        body.Scope.Should().Be("user");
    }

    [Test]
    public async Task CreateApiKey_InvalidScope_ReturnsBadRequest()
    {
        var req = new CreateApiKeyRequest(Label: "x", Scope: "bogus");

        var result = await AdminApiKeysEndpoints.CreateApiKey(req, _apiKeyRepo, _indexRepo, _cp);

        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task CreateApiKey_EmptyLabel_ReturnsBadRequest()
    {
        var req = new CreateApiKeyRequest(Label: "  ");
        var result = await AdminApiKeysEndpoints.CreateApiKey(req, _apiKeyRepo, _indexRepo, _cp);
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task CreateApiKey_NegativeRpm_ReturnsBadRequest()
    {
        var req = new CreateApiKeyRequest(Label: "x", RateLimitRpm: 0);
        var result = await AdminApiKeysEndpoints.CreateApiKey(req, _apiKeyRepo, _indexRepo, _cp);
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task CreateApiKey_StoresRateLimitRpmShadowColumn()
    {
        var req = new CreateApiKeyRequest(Label: "rate-limited", RateLimitRpm: 60);
        var result = await AdminApiKeysEndpoints.CreateApiKey(req, _apiKeyRepo, _indexRepo, _cp);
        var body = Unwrap<CreateApiKeyResponse>(result);
        body.RateLimitRpm.Should().Be(60);

        var stored = await _cp.ApiKeys.SingleAsync();
        _cp.Entry(stored).Property<int?>("RateLimitRpm").CurrentValue.Should().Be(60);
    }

    [Test]
    public async Task ListApiKeys_IncludesOnlyNonTenantRows()
    {
        // Seed: one platform key + one tenant key.
        await AdminApiKeysEndpoints.CreateApiKey(
            new CreateApiKeyRequest(Label: "platform"),
            _apiKeyRepo, _indexRepo, _cp);
        _cp.ApiKeys.Add(new ApiKey
        {
            Scope = "tenant",
            OwnerId = Guid.NewGuid().ToString(),
            KeyHash = "h",
            KeyPrefix = "p",
            Label = "tenant-key",
            TenantId = Guid.NewGuid(),
        });
        await _cp.SaveChangesAsync();

        var result = await AdminApiKeysEndpoints.ListApiKeys(_cp);

        var list = Unwrap<List<ApiKeySummaryResponse>>(result);
        list.Should().HaveCount(1);
        list[0].Scope.Should().Be("platform");
    }

    [Test]
    public async Task DeleteApiKey_SoftRevokes_AndMirrorsIndexRow()
    {
        var create = await AdminApiKeysEndpoints.CreateApiKey(
            new CreateApiKeyRequest(Label: "to-delete"),
            _apiKeyRepo, _indexRepo, _cp);
        var created = Unwrap<CreateApiKeyResponse>(create);

        var result = await AdminApiKeysEndpoints.DeleteApiKey(created.Id, _apiKeyRepo, _indexRepo);

        result.Should().BeOfType<NoContent>();
        var stored = await _cp.ApiKeys.FindAsync(created.Id);
        stored!.RevokedAt.Should().NotBeNull();
        var index = await _cp.PlatformApiKeyIndex.SingleAsync();
        index.RevokedAt.Should().NotBeNull();
    }

    [Test]
    public async Task DeleteApiKey_UnknownId_ReturnsNotFound()
    {
        var result = await AdminApiKeysEndpoints.DeleteApiKey(
            Guid.NewGuid(), _apiKeyRepo, _indexRepo);
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(404);
    }
}
