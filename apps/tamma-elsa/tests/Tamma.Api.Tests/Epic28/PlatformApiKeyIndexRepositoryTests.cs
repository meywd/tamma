using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-7 deferred-item — unit tests for
/// <see cref="PlatformApiKeyIndexRepository"/>. Uses the EF InMemory
/// provider for O(1) CP routing-index semantics.
/// </summary>
[TestFixture]
public class PlatformApiKeyIndexRepositoryTests
{
    private ControlPlaneDbContext _db = null!;
    private PlatformApiKeyIndexRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ControlPlaneDbContext(options);
        _repo = new PlatformApiKeyIndexRepository(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static PlatformApiKeyIndex NewRow(
        string keyPrefix = "tamma_sk_a1",
        string hashedSuffix = "deadbeef",
        Guid? tenantId = null,
        Guid? apiKeyId = null,
        string scope = "platform")
    {
        return new PlatformApiKeyIndex
        {
            KeyPrefix = keyPrefix,
            HashedSuffix = hashedSuffix,
            TenantId = tenantId,
            ApiKeyId = apiKeyId ?? Guid.NewGuid(),
            Scope = scope,
        };
    }

    [Test]
    public async Task CreateAsync_PersistsRow_AndSetsCreatedAt()
    {
        var row = await _repo.CreateAsync(NewRow());

        row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        var stored = await _repo.GetByPrefixAsync(row.KeyPrefix);
        stored.Should().NotBeNull();
        stored!.ApiKeyId.Should().Be(row.ApiKeyId);
    }

    [Test]
    public async Task GetByPrefixAndSuffixAsync_MatchesOnBothFields()
    {
        var row = await _repo.CreateAsync(NewRow(keyPrefix: "tamma_sk_a1", hashedSuffix: "aaa"));

        var hit = await _repo.GetByPrefixAndSuffixAsync("tamma_sk_a1", "aaa");
        var miss = await _repo.GetByPrefixAndSuffixAsync("tamma_sk_a1", "bbb");

        hit.Should().NotBeNull();
        hit!.ApiKeyId.Should().Be(row.ApiKeyId);
        miss.Should().BeNull();
    }

    [Test]
    public async Task RevokeByApiKeyIdAsync_SetsRevokedAt()
    {
        var keyId = Guid.NewGuid();
        await _repo.CreateAsync(NewRow(apiKeyId: keyId));

        var before = DateTime.UtcNow;
        await _repo.RevokeByApiKeyIdAsync(keyId);

        var row = await _repo.GetByPrefixAsync("tamma_sk_a1");
        row.Should().NotBeNull();
        row!.RevokedAt.Should().NotBeNull();
        row.RevokedAt!.Value.Should().BeOnOrAfter(before);
    }

    [Test]
    public async Task RevokeByApiKeyIdAsync_WithFutureTimestamp_RespectsGracePeriod()
    {
        var keyId = Guid.NewGuid();
        await _repo.CreateAsync(NewRow(apiKeyId: keyId));

        var future = DateTime.UtcNow.AddHours(24);
        await _repo.RevokeByApiKeyIdAsync(keyId, future);

        var row = await _repo.GetByPrefixAsync("tamma_sk_a1");
        row!.RevokedAt.Should().Be(future);
    }

    [Test]
    public void RevokeByApiKeyIdAsync_NoopOnMissingRow()
    {
        Assert.DoesNotThrowAsync(async () =>
            await _repo.RevokeByApiKeyIdAsync(Guid.NewGuid()));
    }

    [Test]
    public async Task RevokeByApiKeyIdAsync_DoesNotReRevokeAlreadyRevoked()
    {
        var keyId = Guid.NewGuid();
        await _repo.CreateAsync(NewRow(apiKeyId: keyId));

        var first = DateTime.UtcNow.AddHours(-1);
        await _repo.RevokeByApiKeyIdAsync(keyId, first);
        await _repo.RevokeByApiKeyIdAsync(keyId); // Should skip: already revoked.

        var row = await _repo.GetByPrefixAsync("tamma_sk_a1");
        row!.RevokedAt.Should().Be(first);
    }

    [Test]
    public async Task DeleteByPrefixAsync_HardDeletesRow()
    {
        await _repo.CreateAsync(NewRow());

        await _repo.DeleteByPrefixAsync("tamma_sk_a1");

        var row = await _repo.GetByPrefixAsync("tamma_sk_a1");
        row.Should().BeNull();
    }

    [Test]
    public void DeleteByPrefixAsync_NoopOnMissingRow()
    {
        Assert.DoesNotThrowAsync(async () =>
            await _repo.DeleteByPrefixAsync("tamma_sk_missing"));
    }
}
