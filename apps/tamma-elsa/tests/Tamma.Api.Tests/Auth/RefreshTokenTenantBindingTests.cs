using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 28-9 AC3 — model-shape + repository tests for the refresh-token
/// tenant binding. Two layers:
///
/// <list type="number">
///   <item><description><b>Model assertions</b> — the
///     <see cref="RefreshToken"/> entity exposes <c>TenantId</c>,
///     <c>JtiChainHead</c>, and <c>RevokedReason</c>; the
///     <see cref="ControlPlaneDbContext"/> model graph carries the
///     CHECK constraints and partial indexes.</description></item>
///   <item><description><b>Repository behaviour</b> — the
///     <see cref="IRefreshTokenRepository"/> methods set/respect the new
///     columns. Uses the shared Postgres testcontainer so the CHECK
///     constraints are exercised against the real engine.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class RefreshTokenTenantBindingTests
{
    private IServiceScope _scope = null!;
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private IRefreshTokenRepository _refreshTokenRepo = null!;
    private IUserRepository _userRepo = null!;

    [SetUp]
    public async Task Setup()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _refreshTokenRepo = _scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
        _userRepo = _scope.ServiceProvider.GetRequiredService<IUserRepository>();
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    private async Task<User> CreateUser(string email = "alice@example.com")
        => await _userRepo.CreateAsync(new User
        {
            Email = email,
            DisplayName = email.Split('@')[0],
            AuthMethod = "email",
        });

    // ── Entity / Model shape ────────────────────────────────────────────────

    [Test]
    public void Entity_HasTenantIdProperty_Nullable()
    {
        // Story 28-9 AC3 — refresh tokens carry the bound tenant; NULL
        // for rootless tokens issued before user picks a tenant.
        var prop = typeof(RefreshToken).GetProperty(nameof(RefreshToken.TenantId));
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(Guid?));
    }

    [Test]
    public void Entity_HasJtiChainHeadProperty_Nullable()
    {
        // Story 28-9 AC3 — session lineage pointer for reuse-detection.
        var prop = typeof(RefreshToken).GetProperty(nameof(RefreshToken.JtiChainHead));
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(Guid?));
    }

    [Test]
    public void Entity_HasRevokedReasonProperty_NullableString()
    {
        // Story 28-9 AC3 — closed-enum string column.
        var prop = typeof(RefreshToken).GetProperty(nameof(RefreshToken.RevokedReason));
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(string));
    }

    [Test]
    public void Model_RefreshToken_DeclaresNewColumns()
    {
        var entity = _db.Model.FindEntityType(typeof(RefreshToken));
        entity.Should().NotBeNull();

        var propNames = entity!.GetProperties().Select(p => p.Name).ToHashSet();
        propNames.Should().Contain("TenantId", "AC3 — tenant binding");
        propNames.Should().Contain("JtiChainHead", "AC3 — session lineage pointer");
        propNames.Should().Contain("RevokedReason", "AC3 — closed-enum reason");
    }

    [Test]
    public void Model_RefreshToken_TenantIdAndChainHead_AreNullable()
    {
        // AC3 — rootless refresh tokens (login with 0/2+ memberships) get
        // NULL TenantId; pre-migration rows get NULL JtiChainHead.
        var entity = _db.Model.FindEntityType(typeof(RefreshToken))!;
        entity.FindProperty("TenantId")!.IsNullable.Should().BeTrue();
        entity.FindProperty("JtiChainHead")!.IsNullable.Should().BeTrue();
        entity.FindProperty("RevokedReason")!.IsNullable.Should().BeTrue();
    }

    [Test]
    public void Model_RefreshToken_HasJtiChainHeadIndex_Partial()
    {
        var entity = _db.Model.FindEntityType(typeof(RefreshToken))!;
        var idx = entity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1
                && i.Properties[0].Name == "JtiChainHead");
        idx.Should().NotBeNull("AC3 — reuse-detection hot path needs an index");
        idx!.GetFilter().Should().Contain("JtiChainHead",
            "partial index keeps pre-migration rows out");
    }

    [Test]
    public void Model_RefreshToken_HasUserIdTenantIdIndex_Partial()
    {
        var entity = _db.Model.FindEntityType(typeof(RefreshToken))!;
        var idx = entity.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 2
                && i.Properties[0].Name == "UserId"
                && i.Properties[1].Name == "TenantId");
        idx.Should().NotBeNull("AC3 — per-tenant queries need a composite index");
        idx!.GetFilter().Should().Contain("TenantId",
            "partial index keeps rootless rows out");
    }

    [Test]
    public async Task DbLevel_UnknownRevokedReason_IsRejectedByCheckConstraint()
    {
        // End-to-end assertion that the CK_refresh_tokens_RevokedReason
        // CHECK constraint is in place: the DB itself rejects an
        // unknown enum value. Stronger than a model-graph assertion
        // because it catches the case where the migration ran but the
        // constraint was stripped by a downstream re-snapshot.
        var user = await CreateUser("model@example.com");
        var token = await _refreshTokenRepo.CreateAsync(
            user.Id, Guid.NewGuid(), "h-enum", DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.RefreshTokens.SingleAsync(t => t.Id == token.Id);
        row.RevokedAt = DateTime.UtcNow;
        row.RevokedReason = "made_up_value";
        Func<Task> act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the CHECK constraint rejects values outside the closed enum");
    }

    // ── Repository: Create with tenant binding ──────────────────────────────

    [Test]
    public async Task CreateAsync_WithTenantId_PersistsTenantBinding()
    {
        var user = await CreateUser();
        var tenantId = Guid.NewGuid();
        var chainHead = Guid.NewGuid();

        var token = await _refreshTokenRepo.CreateAsync(
            user.Id, tenantId, "hash-1", DateTime.UtcNow.AddDays(7), chainHead);

        token.TenantId.Should().Be(tenantId);
        token.JtiChainHead.Should().Be(chainHead);
        token.RevokedReason.Should().BeNull();
        token.RevokedAt.Should().BeNull();
    }

    [Test]
    public async Task CreateAsync_WithNullTenantId_PersistsRootlessToken()
    {
        // AC3 — rootless refresh tokens (login with 0/2+ memberships per AC4)
        // carry NULL TenantId.
        var user = await CreateUser();
        var token = await _refreshTokenRepo.CreateAsync(
            user.Id, tenantId: null, "hash-2", DateTime.UtcNow.AddDays(7), jtiChainHead: Guid.NewGuid());

        token.TenantId.Should().BeNull();
        token.JtiChainHead.Should().NotBeNull();
    }

    [Test]
    public async Task LegacyCreateAsync_LeavesTenantAndChainHeadNull()
    {
        // Backwards compat — the 3-arg overload (used by transitional
        // callers) mints a NULL-tenant, NULL-chain-head row.
        var user = await CreateUser();
        var token = await _refreshTokenRepo.CreateAsync(
            user.Id, "hash-legacy", DateTime.UtcNow.AddDays(7));

        token.TenantId.Should().BeNull();
        token.JtiChainHead.Should().BeNull();
    }

    // ── Repository: Revoke with reason ──────────────────────────────────────

    [Test]
    public async Task RevokeAsync_WithReason_StampsRevokedAtAndReason()
    {
        var user = await CreateUser();
        var token = await _refreshTokenRepo.CreateAsync(
            user.Id, Guid.NewGuid(), "h-r", DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        await _refreshTokenRepo.RevokeAsync(token.Id, RefreshTokenRevokedReasons.ManualLogout);

        // Re-read in a fresh scope so we don't see cached state.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var read = await db.RefreshTokens.SingleAsync(t => t.Id == token.Id);
        read.RevokedAt.Should().NotBeNull();
        read.RevokedReason.Should().Be(RefreshTokenRevokedReasons.ManualLogout);
    }

    [Test]
    public async Task RevokeAsync_WithUnknownReason_Throws()
    {
        // Defence-in-depth: client-side guard rejects typos before the
        // DB-level CHECK constraint fires, so the call site sees a
        // clearer ArgumentException with the offending value in scope.
        var user = await CreateUser();
        var token = await _refreshTokenRepo.CreateAsync(
            user.Id, Guid.NewGuid(), "h-x", DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        Func<Task> act = () => _refreshTokenRepo.RevokeAsync(token.Id, "made_up_reason");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task RevokeAllForUserAsync_WithReason_StampsAllRows()
    {
        var user = await CreateUser();
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "ha", DateTime.UtcNow.AddDays(7), Guid.NewGuid());
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "hb", DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        var count = await _refreshTokenRepo.RevokeAllForUserAsync(
            user.Id, RefreshTokenRevokedReasons.PasswordReset);
        count.Should().Be(2);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync();
        rows.Should().AllSatisfy(r =>
        {
            r.RevokedAt.Should().NotBeNull();
            r.RevokedReason.Should().Be(RefreshTokenRevokedReasons.PasswordReset);
        });
    }

    // ── Repository: Chain lookup + revoke ───────────────────────────────────

    [Test]
    public async Task FindByJtiChainHeadAsync_ReturnsActiveSiblings()
    {
        var user = await CreateUser();
        var chainHead = Guid.NewGuid();
        var otherChain = Guid.NewGuid();

        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "a", DateTime.UtcNow.AddDays(7), chainHead);
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "b", DateTime.UtcNow.AddDays(7), chainHead);
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "c", DateTime.UtcNow.AddDays(7), otherChain);

        var result = await _refreshTokenRepo.FindByJtiChainHeadAsync(chainHead);
        result.Should().HaveCount(2, "only the matching chain is returned");
    }

    [Test]
    public async Task FindByJtiChainHeadAsync_EmptyGuid_ReturnsEmpty()
    {
        // Defensive — a Guid.Empty chain head must NOT match the NULL-chain
        // rows that pre-date this story (otherwise reuse-detection would
        // burn every pre-migration session in one call).
        var result = await _refreshTokenRepo.FindByJtiChainHeadAsync(Guid.Empty);
        result.Should().BeEmpty();
    }

    [Test]
    public async Task FindByJtiChainHeadAsync_ExcludesRevokedRows()
    {
        var user = await CreateUser();
        var chainHead = Guid.NewGuid();
        var t1 = await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "a", DateTime.UtcNow.AddDays(7), chainHead);
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "b", DateTime.UtcNow.AddDays(7), chainHead);
        await _refreshTokenRepo.RevokeAsync(t1.Id, RefreshTokenRevokedReasons.RotationConsumed);

        var result = await _refreshTokenRepo.FindByJtiChainHeadAsync(chainHead);
        result.Should().HaveCount(1, "revoked siblings are filtered out");
    }

    [Test]
    public async Task RevokeChainAsync_BurnsEverySiblingInChain()
    {
        // AC3 — the core reuse-detection action: present a revoked token,
        // burn every sibling sharing its chain head in one update.
        var user = await CreateUser();
        var chainHead = Guid.NewGuid();
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "a", DateTime.UtcNow.AddDays(7), chainHead);
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "b", DateTime.UtcNow.AddDays(7), chainHead);
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "c", DateTime.UtcNow.AddDays(7), chainHead);

        var burned = await _refreshTokenRepo.RevokeChainAsync(
            chainHead, RefreshTokenRevokedReasons.ReuseDetected);
        burned.Should().Be(3);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var rows = await db.RefreshTokens
            .Where(t => t.JtiChainHead == chainHead)
            .ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().AllSatisfy(r =>
        {
            r.RevokedAt.Should().NotBeNull();
            r.RevokedReason.Should().Be(RefreshTokenRevokedReasons.ReuseDetected);
        });
    }

    [Test]
    public async Task RevokeChainAsync_DoesNotTouchOtherChains()
    {
        var user = await CreateUser();
        var targetChain = Guid.NewGuid();
        var otherChain = Guid.NewGuid();
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "x", DateTime.UtcNow.AddDays(7), targetChain);
        var safeToken = await _refreshTokenRepo.CreateAsync(
            user.Id, Guid.NewGuid(), "y", DateTime.UtcNow.AddDays(7), otherChain);

        await _refreshTokenRepo.RevokeChainAsync(
            targetChain, RefreshTokenRevokedReasons.ReuseDetected);

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var safe = await db.RefreshTokens.SingleAsync(t => t.Id == safeToken.Id);
        safe.RevokedAt.Should().BeNull("the other chain must be untouched");
    }

    [Test]
    public async Task RevokeChainAsync_EmptyChainHead_NoOp()
    {
        var user = await CreateUser();
        await _refreshTokenRepo.CreateAsync(user.Id, Guid.NewGuid(), "z", DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        var burned = await _refreshTokenRepo.RevokeChainAsync(
            Guid.Empty, RefreshTokenRevokedReasons.ReuseDetected);
        burned.Should().Be(0);
    }

    [Test]
    public async Task NullParityConstraint_RejectsRevokedReasonWithoutTimestamp()
    {
        // DB-level CHECK constraint — RevokedReason set without RevokedAt
        // must be rejected so SIEM queries on "WHERE RevokedReason='reuse_detected'"
        // can trust the column.
        var user = await CreateUser();
        var token = await _refreshTokenRepo.CreateAsync(
            user.Id, Guid.NewGuid(), "np", DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        // Reach past the repository to write a malformed row directly.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.RefreshTokens.SingleAsync(t => t.Id == token.Id);
        row.RevokedReason = RefreshTokenRevokedReasons.ManualLogout;
        row.RevokedAt = null;
        Func<Task> act = () => db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "CHECK constraint enforces (RevokedAt IS NULL) = (RevokedReason IS NULL)");
    }
}
