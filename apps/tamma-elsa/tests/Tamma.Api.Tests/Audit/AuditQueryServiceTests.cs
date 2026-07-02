using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-3 — end-to-end query tests for <see cref="AuditQueryService"/>
/// against a real Postgres 17 container. Seeds curated <c>audit_records</c> rows
/// directly (this story is read-only over the 37-1 read-model) into two tenant
/// schemas + the control plane, then exercises every filter dimension, the
/// <c>q</c> search, keyset pagination (incl. concurrent-insert stability), the
/// per-mode scoping, the cross-tenant / cross-scope isolation walls, and the
/// <c>AUDIT.QUERIED</c> meta-audit emission.
/// </summary>
[TestFixture]
public class AuditQueryServiceTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;
    private Guid _tenantA;
    private Guid _tenantB;
    private string _schemaA = null!;
    private string _schemaB = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("audit_query_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using (var cp = NewCp())
            await cp.Database.MigrateAsync();

        _tenantA = Guid.NewGuid();
        _tenantB = Guid.NewGuid();
        _schemaA = TenantNaming.SchemaName(_tenantA);
        _schemaB = TenantNaming.SchemaName(_tenantB);
        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(CsFor(_schemaA));
        await migrator.MigrateTenantAppAsync(CsFor(_schemaB));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task Reset()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await Exec(conn, "TRUNCATE audit_records, platform_events RESTART IDENTITY CASCADE;");
        foreach (var schema in new[] { _schemaA, _schemaB })
            await Exec(conn, $"TRUNCATE \"{schema}\".audit_records RESTART IDENTITY;");
    }

    // ════════════════════ per-dimension filters ════════════════════

    [Test]
    public async Task Each_Structured_Filter_Isolates_The_Expected_Subset()
    {
        var actor1 = Guid.NewGuid();
        var actor2 = Guid.NewGuid();
        await SeedTenant(_tenantA, s => s with
        {
            Category = "secret", ActionCode = "SECRET.REVEAL", ActorUserId = actor1,
            TargetType = "secret", TargetId = "sec-1", Severity = "critical", Outcome = "success",
            IpAddress = "10.0.0.1", OccurredAt = D(2026, 1, 10), Seq = 1,
        });
        await SeedTenant(_tenantA, s => s with
        {
            Category = "rbac", ActionCode = "TENANT.MEMBER_ROLE_CHANGED", ActorUserId = actor2,
            TargetType = "user", TargetId = "usr-9", Severity = "warning", Outcome = "denied",
            IpAddress = "10.0.0.2", OccurredAt = D(2026, 2, 10), Seq = 2,
        });

        var svc = NewService(out _, out _);

        (await QueryTenant(svc, F(category: "secret"))).Records.Should().ContainSingle()
            .Which.ActionCode.Should().Be("SECRET.REVEAL");
        (await QueryTenant(svc, F(action: "TENANT.MEMBER_ROLE_CHANGED"))).Records.Should().ContainSingle();
        (await QueryTenant(svc, F(actorUserId: actor1))).Records.Should().ContainSingle()
            .Which.ActorUserId.Should().Be(actor1);
        (await QueryTenant(svc, F(targetType: "user"))).Records.Should().ContainSingle();
        (await QueryTenant(svc, F(targetId: "sec-1"))).Records.Should().ContainSingle();
        (await QueryTenant(svc, F(severity: "critical"))).Records.Should().ContainSingle();
        (await QueryTenant(svc, F(outcome: "denied"))).Records.Should().ContainSingle();
        (await QueryTenant(svc, F(ipAddress: "10.0.0.2"))).Records.Should().ContainSingle();

        // Half-open [from, to): Jan row only.
        (await QueryTenant(svc, F(from: D(2026, 1, 1), to: D(2026, 2, 1))))
            .Records.Should().ContainSingle().Which.OccurredAt.Should().Be(D(2026, 1, 10));
    }

    [Test]
    public async Task Filters_AND_Combine()
    {
        var actor = Guid.NewGuid();
        await SeedTenant(_tenantA, s => s with { Category = "secret", ActorUserId = actor, Severity = "critical", Seq = 1, TargetId = "a" });
        await SeedTenant(_tenantA, s => s with { Category = "secret", ActorUserId = actor, Severity = "info", Seq = 2, TargetId = "b" });
        await SeedTenant(_tenantA, s => s with { Category = "rbac", ActorUserId = actor, Severity = "critical", Seq = 3, TargetId = "c" });

        var svc = NewService(out _, out _);
        var result = await QueryTenant(svc, F(category: "secret", severity: "critical"));
        result.Records.Should().ContainSingle().Which.TargetId.Should().Be("a");
    }

    // ════════════════════ search (q) ════════════════════

    [Test]
    public async Task Search_Matches_Actor_Target_And_Payload_CaseInsensitively()
    {
        await SeedTenant(_tenantA, s => s with { Seq = 1, ActorEmailSnapshot = "Alice@Example.com", TargetId = "t-1", PayloadJson = "{\"note\":\"routine\"}" });
        await SeedTenant(_tenantA, s => s with { Seq = 2, ActorEmailSnapshot = "bob@example.com", TargetId = "NEEDLE-42", PayloadJson = "{\"note\":\"routine\"}" });
        await SeedTenant(_tenantA, s => s with { Seq = 3, ActorEmailSnapshot = "carol@example.com", TargetId = "t-3", PayloadJson = "{\"secretName\":\"haystack-token\"}" });

        var svc = NewService(out _, out _);

        (await QueryTenant(svc, F(q: "alice"))).Records.Should().ContainSingle("actor email matched case-insensitively");
        (await QueryTenant(svc, F(q: "needle"))).Records.Should().ContainSingle("target id matched case-insensitively");
        (await QueryTenant(svc, F(q: "haystack"))).Records.Should().ContainSingle("payload json matched");
    }

    [Test]
    public async Task Injection_Shaped_Search_Is_Inert()
    {
        await SeedTenant(_tenantA, s => s with { Seq = 1, ActorEmailSnapshot = "alice@example.com", TargetId = "t-1" });
        await SeedTenant(_tenantA, s => s with { Seq = 2, ActorEmailSnapshot = "bob@example.com", TargetId = "t-2" });

        var svc = NewService(out _, out _);
        var result = await QueryTenant(svc, F(q: "' OR 1=1 --"));
        result.Records.Should().BeEmpty("a parameterized ILIKE treats the injection string as a literal");
    }

    // ════════════════════ keyset pagination ════════════════════

    [Test]
    public async Task Keyset_Pages_Through_Without_Overlap_Or_Gap()
    {
        for (var i = 1; i <= 250; i++)
            await SeedTenant(_tenantA, s => s with { Seq = i, TargetId = $"t-{i}" });

        var svc = NewService(out _, out _);

        var page1 = await QueryTenant(svc, F(limit: 100));
        page1.Records.Should().HaveCount(100);
        page1.Records[0].SourceSequenceNumber.Should().Be(250, "most-recent first");
        page1.Records[^1].SourceSequenceNumber.Should().Be(151);
        page1.NextCursor.Should().NotBeNull();

        var page2 = await QueryTenant(svc, F(limit: 100, cursor: page1.NextCursor));
        page2.Records.Should().HaveCount(100);
        page2.Records[0].SourceSequenceNumber.Should().Be(150);
        page2.Records[^1].SourceSequenceNumber.Should().Be(51);
        page2.NextCursor.Should().NotBeNull();

        var page3 = await QueryTenant(svc, F(limit: 100, cursor: page2.NextCursor));
        page3.Records.Should().HaveCount(50);
        page3.Records[^1].SourceSequenceNumber.Should().Be(1);
        page3.NextCursor.Should().BeNull("last page");

        // No overlap across pages.
        var all = page1.Records.Concat(page2.Records).Concat(page3.Records)
            .Select(r => r.SourceSequenceNumber).ToList();
        all.Should().OnlyHaveUniqueItems();
        all.Should().HaveCount(250);
    }

    [Test]
    public async Task Keyset_Does_Not_Skip_Rows_That_Share_A_NonUnique_SourceSequenceNumber()
    {
        // The control-plane audit_records table is fed by TWO independent identity
        // sequences (domain_events.SequenceNumber AND platform_events.SequenceNumber,
        // both starting at 1), so source_sequence_number values COLLIDE — the table
        // is unique only on source_event_id. A keyset that seeks on the sequence
        // ALONE (cursor encodes seq=N; next page does `seq < N`) silently drops the
        // OTHER row sharing the boundary sequence — a compliance completeness bug.
        // Seed two platform rows with the SAME sequence (distinct id/source_event_id)
        // and page one-at-a-time: BOTH must surface, none skipped, none duplicated.
        await SeedControlPlane(s => s with { Seq = 7, TargetId = "collide-A", TenantId = null, UserId = null });
        await SeedControlPlane(s => s with { Seq = 7, TargetId = "collide-B", TenantId = null, UserId = null });

        var svc = NewService(out _, out _);

        var page1 = await svc.QueryPlatformAsync(null, F(limit: 1), TammaMode.SaaS, default);
        page1.Records.Should().HaveCount(1);
        page1.NextCursor.Should().NotBeNull(
            "a second row shares the boundary sequence and must not be skipped");

        var page2 = await svc.QueryPlatformAsync(null, F(limit: 1, cursor: page1.NextCursor), TammaMode.SaaS, default);
        page2.Records.Should().HaveCount(1,
            "the OTHER row sharing source_sequence_number=7 surfaces on the next page "
                + "(a single-key cursor skipped it)");
        page2.NextCursor.Should().BeNull("both rows are now consumed");

        var seen = page1.Records.Concat(page2.Records).Select(r => r.TargetId).ToList();
        seen.Should().OnlyHaveUniqueItems("no row is duplicated across pages");
        seen.Should().BeEquivalentTo(new[] { "collide-A", "collide-B" },
            "both colliding-sequence rows surface across the pages — none skipped");
    }

    [Test]
    public async Task Keyset_Is_Stable_Under_Concurrent_Inserts()
    {
        for (var i = 1; i <= 250; i++)
            await SeedTenant(_tenantA, s => s with { Seq = i, TargetId = $"t-{i}" });

        var svc = NewService(out _, out _);
        var page1 = await QueryTenant(svc, F(limit: 100));

        // 10 NEWER rows appear between page 1 and page 2.
        for (var i = 251; i <= 260; i++)
            await SeedTenant(_tenantA, s => s with { Seq = i, TargetId = $"t-{i}" });

        var page2 = await QueryTenant(svc, F(limit: 100, cursor: page1.NextCursor));
        page2.Records[0].SourceSequenceNumber.Should().Be(150,
            "the keyset cursor pins the boundary — newer rows never shift page 2");
        page2.Records[^1].SourceSequenceNumber.Should().Be(51);
        page2.Records.Select(r => r.SourceSequenceNumber)
            .Should().NotContain(new long[] { 251, 252, 260 });
    }

    // ════════════════════ cross-tenant / cross-scope isolation ════════════════════

    [Test]
    public async Task Tenant_Query_Never_Returns_Another_Tenants_Rows()
    {
        await SeedTenant(_tenantA, s => s with { Seq = 1, TargetId = "A-only" });
        await SeedTenant(_tenantB, s => s with { Seq = 1, TargetId = "B-only" });

        var svc = NewService(out _, out _);
        var a = await QueryTenant(svc, F(), _tenantA);
        a.Records.Should().ContainSingle().Which.TargetId.Should().Be("A-only");
        a.Records.Should().NotContain(r => r.TargetId == "B-only");
    }

    [Test]
    public async Task Foreign_TenantId_Injected_Into_Filter_Returns_Zero_Foreign_Rows()
    {
        // Defence-in-depth: even if the explicit predicate targeted tenant B, the
        // read opens tenant A's PHYSICAL schema — B's rows are unreachable.
        await SeedTenant(_tenantA, s => s with { Seq = 1, TargetId = "A-only" });
        await SeedTenant(_tenantB, s => s with { Seq = 1, TargetId = "B-only" });

        var svc = NewService(out _, out _);
        // Query tenant A but filter for B's target id — physically only A's schema
        // is open, so zero rows (never B-only).
        var result = await QueryTenant(svc, F(targetId: "B-only"), _tenantA);
        result.Records.Should().BeEmpty();
    }

    [Test]
    public async Task Platform_Query_Reads_ControlPlane_Rows_Only_Never_Tenant_Rows()
    {
        // A tenant-scoped row lives in tenant A's schema; a platform row lives in CP.
        await SeedTenant(_tenantA, s => s with { Seq = 1, TargetId = "tenant-row" });
        await SeedControlPlane(s => s with { Seq = 1, TargetId = "platform-row", TenantId = null, UserId = null });

        var svc = NewService(out _, out _);
        var result = await svc.QueryPlatformAsync(null, F(), TammaMode.SaaS, default);

        result.Records.Should().ContainSingle().Which.TargetId.Should().Be("platform-row");
        result.Records.Should().NotContain(r => r.TargetId == "tenant-row");
    }

    // ════════════════════ per-mode scoping ════════════════════

    [Test]
    public async Task SingleUser_Tenant_Query_Reads_CP_Rows_Keyed_By_User()
    {
        var userX = Guid.NewGuid();
        var userY = Guid.NewGuid();
        await SeedControlPlane(s => s with { Seq = 1, TargetId = "mine", UserId = userX, TenantId = null });
        await SeedControlPlane(s => s with { Seq = 2, TargetId = "theirs", UserId = userY, TenantId = null });

        var svc = NewService(out _, out _);
        var result = await svc.QueryTenantAsync(_tenantA, userX, F(), TammaMode.SingleUser, default);

        result.Records.Should().ContainSingle().Which.TargetId.Should().Be("mine");
        result.Records.Should().NotContain(r => r.TargetId == "theirs");
    }

    // ════════════════════ total (estimate) ════════════════════

    [Test]
    public async Task Total_Reflects_Filtered_Count_Not_Just_The_Page()
    {
        for (var i = 1; i <= 5; i++)
            await SeedTenant(_tenantA, s => s with { Seq = i, Category = "secret" });
        for (var i = 6; i <= 8; i++)
            await SeedTenant(_tenantA, s => s with { Seq = i, Category = "rbac" });

        var svc = NewService(out _, out _);
        var result = await QueryTenant(svc, F(category: "secret", limit: 2));
        result.Records.Should().HaveCount(2, "page is limited");
        result.Total.Should().Be(5, "total counts the whole filtered set, not just the page");
        result.TotalIsCapped.Should().BeFalse();
    }

    // ════════════════════ meta-audit (AUDIT.QUERIED) ════════════════════

    [Test]
    public async Task Successful_Tenant_Read_Emits_One_AuditQueried_Tenant_Event()
    {
        await SeedTenant(_tenantA, s => s with { Seq = 1 });
        var svc = NewService(out var events, out var platform);

        await svc.QueryTenantAsync(_tenantA, Guid.NewGuid(), F(category: "secret"), TammaMode.SaaS, default);

        events.Appended.Should().ContainSingle();
        var evt = events.Appended[0];
        evt.Type.Should().Be(AuditQueryEventTypes.Queried);
        evt.TenantId.Should().Be(_tenantA);
        evt.Tags.Should().Contain("\"scope\":\"tenant\"");
        platform.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task Successful_Platform_Read_Emits_One_AuditQueried_Platform_Event()
    {
        await SeedControlPlane(s => s with { Seq = 1, TenantId = null, UserId = null });
        var svc = NewService(out var events, out var platform);

        await svc.QueryPlatformAsync(Guid.NewGuid(), F(), TammaMode.SaaS, default);

        platform.Appended.Should().ContainSingle();
        var evt = platform.Appended[0];
        evt.Type.Should().Be(AuditQueryEventTypes.Queried);
        evt.TenantId.Should().BeNull();
        evt.Tags.Should().Contain("\"scope\":\"platform\"");
        events.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task MetaAudit_Append_Failure_Does_Not_Fail_The_Read()
    {
        await SeedTenant(_tenantA, s => s with { Seq = 1 });
        var svc = NewService(
            new ThrowingEventRepository(), new RecordingPlatformEventRepository());

        // The read still succeeds even though the meta-audit append throws.
        var result = await svc.QueryTenantAsync(_tenantA, Guid.NewGuid(), F(), TammaMode.SaaS, default);
        result.Records.Should().ContainSingle();
    }

    // ── service construction ──

    private AuditQueryService NewService(
        out RecordingEventRepository events, out RecordingPlatformEventRepository platform)
    {
        events = new RecordingEventRepository();
        platform = new RecordingPlatformEventRepository();
        return NewService(events, platform);
    }

    private AuditQueryService NewService(IEventRepository events, IPlatformEventRepository platform) =>
        new(new SearchPathTenantFactory(_cs), NewCp(), events, platform,
            new TestTenantContext(), TimeProvider.System, NullLogger<AuditQueryService>.Instance);

    private Task<Tamma.Api.Dtos.Audit.AuditQueryResponse> QueryTenant(
        AuditQueryService svc, AuditQueryFilter f, Guid? tenantId = null) =>
        svc.QueryTenantAsync(tenantId ?? _tenantA, Guid.NewGuid(), f, TammaMode.SaaS, default);

    private static AuditQueryFilter F(
        string? category = null, string? action = null, Guid? actorUserId = null,
        string? targetType = null, string? targetId = null, string? severity = null,
        string? outcome = null, string? ipAddress = null, DateTime? from = null,
        DateTime? to = null, string? q = null, int? limit = null, string? cursor = null)
    {
        var (filter, error) = AuditQueryFilter.TryParse(
            category, action, actorUserId?.ToString(), targetType, targetId, severity,
            outcome, ipAddress, from, to, q, limit, cursor);
        error.Should().BeNull("test filters must be valid");
        return filter!;
    }

    // ── seeding ──

    private sealed record Seed(
        long Seq, string Category, string ActionCode, Guid? ActorUserId,
        string? ActorEmailSnapshot, string? TargetType, string? TargetId,
        string Severity, string Outcome, string? IpAddress, DateTime OccurredAt,
        string PayloadJson, Guid? TenantId, Guid? UserId);

    private static Seed Default(Guid? tenantId, Guid? userId) => new(
        Seq: 1, Category: "secret", ActionCode: "SECRET.REVEAL", ActorUserId: null,
        ActorEmailSnapshot: null, TargetType: "secret", TargetId: "t-1",
        Severity: "info", Outcome: "success", IpAddress: null, OccurredAt: D(2026, 1, 1),
        PayloadJson: "{}", TenantId: tenantId, UserId: userId);

    private async Task SeedTenant(Guid tenantId, Func<Seed, Seed> mutate)
    {
        var s = mutate(Default(tenantId, null));
        await using var db = NewTenant(tenantId, TenantNaming.SchemaName(tenantId));
        db.AuditRecords.Add(ToRecord(s));
        await db.SaveChangesAsync();
    }

    private async Task SeedControlPlane(Func<Seed, Seed> mutate)
    {
        var s = mutate(Default(null, null));
        await using var cp = NewCp();
        cp.AuditRecords.Add(ToRecord(s));
        await cp.SaveChangesAsync();
    }

    private static AuditRecord ToRecord(Seed s) => new()
    {
        Id = Guid.NewGuid(),
        Category = s.Category,
        ActionCode = s.ActionCode,
        Severity = s.Severity,
        ActorUserId = s.ActorUserId,
        ActorEmailSnapshot = s.ActorEmailSnapshot,
        TargetType = s.TargetType,
        TargetId = s.TargetId,
        Outcome = s.Outcome,
        IpAddress = s.IpAddress,
        OccurredAt = s.OccurredAt,
        SourceEventId = Guid.NewGuid(),
        SourceSequenceNumber = s.Seq,
        PayloadJson = s.PayloadJson,
        TenantId = s.TenantId,
        UserId = s.UserId,
    };

    private static DateTime D(int y, int m, int d) =>
        new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_cs) { SearchPath = schema }.ConnectionString;

    private ControlPlaneDbContext NewCp() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private TenantDbContext NewTenant(Guid tenantId, string schema) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options, tenantId);

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── test doubles ──

    private sealed class TestTenantContext : ITenantContext
    {
        public Guid? TenantId { get; private set; }
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class SearchPathTenantFactory(string baseCs) : ITenantDbContextFactory
    {
        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
        {
            var schema = TenantNaming.SchemaName(tenantId);
            var cs = new NpgsqlConnectionStringBuilder(baseCs) { SearchPath = schema }.ConnectionString;
            var ctx = new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(cs).Options, tenantId);
            return ValueTask.FromResult(ctx);
        }
    }

    private sealed class RecordingEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => throw new NotSupportedException();
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => throw new NotSupportedException();
        public Task ClearAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset) => throw new NotSupportedException();
    }

    private sealed class ThrowingEventRepository : IEventRepository
    {
        public Task<DomainEvent> AppendAsync(DomainEvent evt) => throw new InvalidOperationException("boom");
        public Task<DomainEvent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => throw new NotSupportedException();
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => throw new NotSupportedException();
        public Task ClearAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(Guid? tenantId, string? type, int? issueNumber, int limit, int offset) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(Guid tenantId, string? typePrefix, int limit, int offset) => throw new NotSupportedException();
    }

    private sealed class RecordingPlatformEventRepository : IPlatformEventRepository
    {
        public List<PlatformEvent> Appended { get; } = new();

        public Task<PlatformEvent?> AppendAsync(PlatformEvent evt, CancellationToken ct = default)
        {
            Appended.Add(evt);
            return Task.FromResult<PlatformEvent?>(evt);
        }

        public Task<PlatformEvent?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlatformEvent>> QueryAsync(Guid? tenantId = null, Guid? userId = null, string? typePrefix = null, DateTime? since = null, bool includePlatformWide = false, int limit = 100, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
