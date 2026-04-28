using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-6 — unit tests for <see cref="PlatformEventRepository"/>.
/// Uses the EF InMemory provider; the partial unique step-dedup index
/// from Story 28-1 is Postgres-specific so the dedup contract is
/// asserted at the integration layer (PG only). The InMemory tests
/// exercise the rest of the contract: append, query filters,
/// ordering, limit clamping.
/// </summary>
[TestFixture]
public class PlatformEventRepositoryTests
{
    private ControlPlaneDbContext _db = null!;
    private PlatformEventRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _db = new ControlPlaneDbContext(options);
        _repo = new PlatformEventRepository(_db);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static PlatformEvent NewEvent(
        string type = "TENANT.CREATED",
        Guid? tenantId = null,
        Guid? userId = null,
        string tags = "{}")
    {
        return new PlatformEvent
        {
            Type = type,
            TenantId = tenantId,
            UserId = userId,
            Tags = tags,
            Metadata = """{"eventSource":"system"}""",
            Data = "{}",
        };
    }

    // ── Append ────────────────────────────────────────────────────────────────

    [Test]
    public async Task AppendAsync_AssignsId_AndCreatedAt()
    {
        var evt = await _repo.AppendAsync(NewEvent());

        evt.Should().NotBeNull();
        evt!.Id.Should().NotBe(Guid.Empty);
        evt.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        var stored = await _repo.GetByIdAsync(evt.Id);
        stored.Should().NotBeNull();
        stored!.Type.Should().Be("TENANT.CREATED");
    }

    [Test]
    public void AppendAsync_RejectsNullEntity()
    {
        var act = async () => await _repo.AppendAsync(null!);
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public void AppendAsync_RejectsEmptyType()
    {
        var act = async () => await _repo.AppendAsync(NewEvent(type: ""));
        act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task AppendAsync_HonoursPreSetCreatedAt()
    {
        var stamp = DateTime.UtcNow.AddDays(-1);
        var evt = NewEvent();
        evt.CreatedAt = stamp;

        var stored = await _repo.AppendAsync(evt);

        stored!.CreatedAt.Should().Be(stamp,
            "callers that supply a CreatedAt expect it preserved (event replay)");
    }

    // ── Query: tenant filter ──────────────────────────────────────────────────

    [Test]
    public async Task QueryAsync_FiltersByTenantId()
    {
        var tA = Guid.NewGuid();
        var tB = Guid.NewGuid();
        await _repo.AppendAsync(NewEvent("TENANT.CREATED", tenantId: tA));
        await _repo.AppendAsync(NewEvent("TENANT.UPDATED", tenantId: tB));

        var rows = await _repo.QueryAsync(tenantId: tA);

        rows.Should().HaveCount(1);
        rows[0].TenantId.Should().Be(tA);
    }

    [Test]
    public async Task QueryAsync_IncludePlatformWide_ReturnsTenantPlusNullRows()
    {
        var t = Guid.NewGuid();
        await _repo.AppendAsync(NewEvent("TENANT.CREATED", tenantId: t));
        await _repo.AppendAsync(NewEvent("ORCHESTRATOR.TICK")); // null tenant
        await _repo.AppendAsync(NewEvent("TENANT.CREATED", tenantId: Guid.NewGuid())); // other tenant

        var rows = await _repo.QueryAsync(tenantId: t, includePlatformWide: true);

        rows.Should().HaveCount(2);
        rows.Should().Contain(e => e.TenantId == t);
        rows.Should().Contain(e => e.TenantId == null);
    }

    // ── Query: type prefix ────────────────────────────────────────────────────

    [Test]
    public async Task QueryAsync_FiltersByTypePrefix_CaseSensitive()
    {
        await _repo.AppendAsync(NewEvent("TENANT.CREATED"));
        await _repo.AppendAsync(NewEvent("TENANT.UPDATED"));
        await _repo.AppendAsync(NewEvent("USER.REGISTERED"));

        var rows = await _repo.QueryAsync(typePrefix: "TENANT.");

        rows.Should().HaveCount(2);
        rows.Select(r => r.Type).Should().BeEquivalentTo(
            new[] { "TENANT.CREATED", "TENANT.UPDATED" });
    }

    // ── Query: user filter ────────────────────────────────────────────────────

    [Test]
    public async Task QueryAsync_FiltersByUserId()
    {
        var u = Guid.NewGuid();
        await _repo.AppendAsync(NewEvent("USER.LOGIN.SUCCESS", userId: u));
        await _repo.AppendAsync(NewEvent("USER.LOGIN.SUCCESS", userId: Guid.NewGuid()));

        var rows = await _repo.QueryAsync(userId: u);

        rows.Should().HaveCount(1);
        rows[0].UserId.Should().Be(u);
    }

    // ── Query: time filter ────────────────────────────────────────────────────

    [Test]
    public async Task QueryAsync_FiltersBySinceTimestamp()
    {
        var older = NewEvent("TENANT.CREATED");
        older.CreatedAt = DateTime.UtcNow.AddDays(-2);
        await _repo.AppendAsync(older);

        var newer = NewEvent("TENANT.UPDATED");
        newer.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        await _repo.AppendAsync(newer);

        var rows = await _repo.QueryAsync(since: DateTime.UtcNow.AddHours(-1));

        rows.Should().HaveCount(1);
        rows[0].Type.Should().Be("TENANT.UPDATED");
    }

    // ── Query: ordering ──────────────────────────────────────────────────────

    [Test]
    public async Task QueryAsync_ReturnsMostRecentFirst()
    {
        var earliest = NewEvent("E1");
        earliest.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        await _repo.AppendAsync(earliest);

        var middle = NewEvent("E2");
        middle.CreatedAt = DateTime.UtcNow.AddMinutes(-20);
        await _repo.AppendAsync(middle);

        var latest = NewEvent("E3");
        latest.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        await _repo.AppendAsync(latest);

        var rows = await _repo.QueryAsync();

        rows.Select(r => r.Type).Should().ContainInOrder("E3", "E2", "E1");
    }

    // ── Query: limit clamping ────────────────────────────────────────────────

    [Test]
    public async Task QueryAsync_LimitIsClampedToOneOrMore()
    {
        await _repo.AppendAsync(NewEvent("E1"));

        var zero = await _repo.QueryAsync(limit: 0);
        zero.Should().HaveCount(1, "limit <= 0 is clamped to 1");

        var huge = await _repo.QueryAsync(limit: 5000);
        huge.Should().HaveCount(1, "limit > 1000 is clamped to 1000");
    }

    // ── GetByIdAsync ─────────────────────────────────────────────────────────

    [Test]
    public async Task GetByIdAsync_ReturnsNull_ForUnknownId()
    {
        var row = await _repo.GetByIdAsync(Guid.NewGuid());
        row.Should().BeNull();
    }
}
