using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Endpoints;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Pins the Story 16.2 response contract for the per-user API-key handlers
/// at <see cref="AdminEndpoints.CreateUserApiKey"/>,
/// <see cref="AdminEndpoints.ListUserApiKeys"/>, and
/// <see cref="AdminEndpoints.DeleteUserApiKey"/>.
///
/// The dashboard's <c>apiKeysApi</c> client at
/// <c>packages/dashboard/src/services/admin/admin-api-client.ts</c> reads
/// these specific shapes:
///   • GET  → <c>{ apiKeys: ApiKeyEntry[] }</c>
///   • POST → <c>{ id, key, prefix, label, createdAt }</c> (raw key once)
///   • DELETE → <c>{ ok: true }</c>
///
/// Earlier in this PR the GET handler returned a bare array with the wider
/// <c>ServiceKeyResponse</c> shape, which broke <c>MyApiKeysPage</c> at
/// runtime ("Cannot convert undefined or null to object" — the page tried
/// to read <c>data.keys</c> on what was actually <c>data[]</c>). These
/// tests fail loudly if anyone re-introduces that drift.
/// </summary>
[TestFixture]
public class UserApiKeysEndpointsTests
{
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<ITenantContext> _tenantContext = null!;

    [SetUp]
    public void SetUp()
    {
        _apiKeyRepo = new Mock<IApiKeyRepository>(MockBehavior.Strict);
        _tenantContext = new Mock<ITenantContext>(MockBehavior.Loose);
        _tenantContext.SetupGet(t => t.TenantId).Returns((Guid?)null);
    }

    private static T Unwrap<T>(IResult result) where T : class
    {
        var valueProp = result.GetType()
            .GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        return valueProp!.GetValue(result) as T
            ?? throw new InvalidOperationException("Missing response body");
    }

    [Test]
    public async Task ListUserApiKeys_ReturnsApiKeysWrapper_WithEntryShape()
    {
        var userId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddDays(-1);
        _apiKeyRepo.Setup(r => r.ListByOwnerAsync(userId.ToString()))
            .ReturnsAsync(new List<ApiKey>
            {
                new()
                {
                    Id = keyId,
                    Scope = "user",
                    OwnerId = userId.ToString(),
                    KeyHash = "argon2id$abc",
                    KeyPrefix = "tamma_sk_us_abcd1234",
                    Label = "ci pipeline",
                    Permissions = ["dashboard:view"],
                    CreatedAt = createdAt,
                    LastUsedAt = null,
                    RevokedAt = null,
                },
            });

        var result = await AdminEndpoints.ListUserApiKeys(userId, _apiKeyRepo.Object);

        result.Should().BeOfType<Ok<UserApiKeyListResponse>>(
            "the dashboard apiKeysApi client unwraps r.apiKeys");
        var body = Unwrap<UserApiKeyListResponse>(result);
        body.ApiKeys.Should().HaveCount(1);
        var entry = body.ApiKeys[0];
        entry.Id.Should().Be(keyId);
        entry.KeyPrefix.Should().Be("tamma_sk_us_abcd1234");
        entry.Label.Should().Be("ci pipeline");
        entry.UserId.Should().Be(userId.ToString());
        entry.LastUsedAt.Should().BeNull();
        entry.CreatedAt.Should().Be(createdAt);
        entry.RevokedAt.Should().BeNull();
    }

    [Test]
    public async Task CreateUserApiKey_ReturnsCreatedResponse_WithRawKeyOnce()
    {
        var userId = Guid.NewGuid();
        var assignedId = Guid.NewGuid();
        ApiKey? captured = null;
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .Callback<ApiKey>(k => captured = k)
            .ReturnsAsync((ApiKey k) =>
            {
                k.Id = assignedId;
                k.CreatedAt = DateTime.UtcNow;
                return k;
            });

        var result = await AdminEndpoints.CreateUserApiKey(
            userId,
            new CreateUserApiKeyRequest("dev box"),
            _apiKeyRepo.Object,
            _tenantContext.Object);

        result.Should().BeOfType<Created<CreateUserApiKeyResponse>>();
        var body = Unwrap<CreateUserApiKeyResponse>(result);
        body.Id.Should().Be(assignedId);
        body.Label.Should().Be("dev box");
        body.Key.Should().NotBeNullOrEmpty(
            "the raw key is returned ONCE so the dashboard can show it to the user");
        body.Prefix.Should().NotBeNullOrEmpty();
        // The repository never sees the raw key — only the hash. This is a
        // critical invariant: a leaked key column would defeat the whole
        // scheme.
        captured!.KeyHash.Should().NotContain(body.Key);
    }

    [Test]
    public async Task DeleteUserApiKey_ReturnsOkTrue()
    {
        var userId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        _apiKeyRepo.Setup(r => r.RevokeAsync(keyId)).Returns(Task.CompletedTask);

        var result = await AdminEndpoints.DeleteUserApiKey(userId, keyId, _apiKeyRepo.Object);

        // The handler returns Results.Ok(new { ok = true }) — an anonymous
        // type, so we introspect via reflection rather than assert a typed
        // shape. The dashboard client reads exactly { ok: true }.
        var valueProp = result.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        valueProp.Should().NotBeNull("the result must be an Ok-with-value, not bare Ok or 204");
        var raw = valueProp!.GetValue(result)!;
        var okProp = raw.GetType().GetProperty("ok");
        okProp.Should().NotBeNull("response must include an `ok` field");
        okProp!.GetValue(raw).Should().Be(true);

        _apiKeyRepo.Verify(r => r.RevokeAsync(keyId), Times.Once);
    }
}
