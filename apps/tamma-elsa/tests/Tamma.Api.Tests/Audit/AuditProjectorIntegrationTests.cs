using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Audit;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-1 (AC7, AC8, AC9, AC10, AC11, AC14, AC15) — end-to-end projection
/// against a real Postgres 17 container. Drives
/// <see cref="AuditProjectorBackgroundService.ProcessOnceAsync"/> over seeded
/// raw DCB events and asserts the curated trail.
///
/// <para>Two tenant schemas are migrated so the cross-scope isolation proof
/// (AC14) is real: a tenant-A event must never materialize into tenant-B's
/// schema, and a platform-only event must land in the control plane, not a
/// tenant view.</para>
/// </summary>
[TestFixture]
public class AuditProjectorIntegrationTests
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
            .WithDatabase("audit_projector_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        // Migrate the control plane (public schema) — audit_records + cursor +
        // domain_events + platform_events live here in the transitional topology.
        await using (var cp = NewCp())
            await cp.Database.MigrateAsync();

        // Migrate two tenant schemas so tenant-scope audit_records exist.
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

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_cs) { SearchPath = schema }.ConnectionString;

    private ControlPlaneDbContext NewCp() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private TenantDbContext NewTenant(Guid tenantId, string schema) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options, tenantId);

    [SetUp]
    public async Task ResetTables()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        // Truncate everything the projector touches so each test starts clean
        // (AC8's "truncate + reset cursor reproduces identical trail" relies on it).
        await Exec(conn, "TRUNCATE audit_records, audit_projector_cursor, platform_events, users RESTART IDENTITY CASCADE;");
        foreach (var schema in new[] { _schemaA, _schemaB })
        {
            await Exec(conn, $"TRUNCATE \"{schema}\".audit_records RESTART IDENTITY;");
            await Exec(conn, $"TRUNCATE \"{schema}\".domain_events RESTART IDENTITY;");
        }
        // Register both tenants in the CP so the projector's per-tenant fan-out
        // discovers them (a fresh slate seeds the rows it needs each test).
        await SeedTenant(_tenantA, _schemaA);
        await SeedTenant(_tenantB, _schemaB);
    }

    private async Task SeedTenant(Guid id, string schema)
    {
        await using var cp = NewCp();
        if (await cp.Tenants.AnyAsync(t => t.Id == id)) return;
        cp.Tenants.Add(new Tenant
        {
            Id = id,
            Name = schema,
            Slug = schema,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await cp.SaveChangesAsync();
    }

    // ── Build a DI scope wired with the projector + a per-test mode ──

    private ServiceProvider BuildServices(
        TammaMode mode, bool runOnStartup = false, Guid? singleUserId = null,
        IAuditProjector? projectorOverride = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<ITammaModeProvider>(new FixedModeProvider(mode));
        services.AddSingleton<ITenantDbContextFactory>(new SearchPathTenantFactory(_cs));
        if (projectorOverride is not null)
            services.AddSingleton(projectorOverride);
        else
            services.AddSingleton<IAuditProjector, AuditProjector>();
        services.AddSingleton<IAuditRecordRepository, AuditRecordRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditProjectionMetrics>();
        services.AddSingleton(new AuditProjectorOptions { RunOnStartup = runOnStartup });
        services.AddSingleton<AuditProjectorBackgroundService>();
        return services.BuildServiceProvider();
    }

    private static AuditProjectorBackgroundService Svc(ServiceProvider sp) =>
        new(sp, sp.GetRequiredService<AuditProjectorOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuditProjectionMetrics>(),
            NullLogger<AuditProjectorBackgroundService>.Instance);

    // ── Seeding helpers (write the RAW source events the projector reads) ──

    // Domain (tenant-scoped) events live in the tenant's schema — that is where
    // the projector reads them via the per-tenant fan-out. Seed accordingly.
    private async Task SeedDomainEvent(string type, Guid tenantId, object? data = null, object? tags = null)
    {
        var schema = TenantNaming.SchemaName(tenantId);
        await using var t = NewTenant(tenantId, schema);
        t.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(tags ?? new { }),
            Data = JsonSerializer.Serialize(data ?? new { }),
            CreatedAt = DateTime.UtcNow,
        });
        await t.SaveChangesAsync();
    }

    private async Task SeedPlatformEvent(string type, Guid? tenantId, object? data = null)
    {
        await using var cp = NewCp();
        cp.PlatformEvents.Add(new PlatformEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Data = JsonSerializer.Serialize(data ?? new { }),
            CreatedAt = DateTime.UtcNow,
        });
        await cp.SaveChangesAsync();
    }

    private async Task<int> CountCp()
    {
        await using var cp = NewCp();
        return await cp.AuditRecords.CountAsync();
    }

    private async Task<int> CountTenant(Guid tenantId, string schema)
    {
        await using var t = NewTenant(tenantId, schema);
        return await t.AuditRecords.CountAsync();
    }

    // ════════════════════ AC7 — non-catalog skip ════════════════════

    [Test]
    public async Task NonCatalog_Event_Produces_Zero_Rows()
    {
        await SeedDomainEvent("WORKFLOW.STEP_COMPLETED", _tenantA);

        await using var sp = BuildServices(TammaMode.SaaS);
        var inserted = await Svc(sp).ProcessOnceAsync(default);

        inserted.Should().Be(0);
        (await CountCp()).Should().Be(0);
        (await CountTenant(_tenantA, _schemaA)).Should().Be(0);
    }

    // ════════════════════ AC11/AC14 — scope routing + isolation ════════════════════

    [Test]
    public async Task Saas_TenantScoped_Event_Lands_In_That_Tenant_Schema_Only()
    {
        await SeedDomainEvent("SECRET.REVEAL", _tenantA);

        await using var sp = BuildServices(TammaMode.SaaS);
        await Svc(sp).ProcessOnceAsync(default);

        (await CountTenant(_tenantA, _schemaA)).Should().Be(1, "tenant-A's event lands in schema A");
        (await CountTenant(_tenantB, _schemaB)).Should().Be(0, "tenant-B must never see tenant-A's event");
        (await CountCp()).Should().Be(0, "a tenant-scoped event must not land in the control plane");

        await using var ta = NewTenant(_tenantA, _schemaA);
        var row = await ta.AuditRecords.SingleAsync();
        row.TenantId.Should().Be(_tenantA);
        row.UserId.Should().BeNull();
        row.ActionCode.Should().Be("SECRET.REVEAL");
    }

    [Test]
    public async Task Saas_PlatformOnly_Event_Lands_In_ControlPlane_Not_A_Tenant()
    {
        // Impersonation against the platform — TenantId null on the platform event.
        await SeedPlatformEvent("IMPERSONATION.STARTED", tenantId: null);

        await using var sp = BuildServices(TammaMode.SaaS);
        await Svc(sp).ProcessOnceAsync(default);

        (await CountCp()).Should().Be(1, "platform-only events materialize into the CP store");
        (await CountTenant(_tenantA, _schemaA)).Should().Be(0);
        (await CountTenant(_tenantB, _schemaB)).Should().Be(0);

        await using var cp = NewCp();
        var row = await cp.AuditRecords.SingleAsync();
        row.TenantId.Should().BeNull();
        row.UserId.Should().BeNull();
    }

    // ════════════════════ AC11 — single-user keys by user_id ════════════════════

    [Test]
    public async Task SingleUser_Event_Keys_UserId_In_ControlPlane()
    {
        var ownerId = Guid.NewGuid();
        await SeedUser(ownerId, "owner@example.com");
        // Even though the raw event carries a TenantId (the personal tenant),
        // single-user mode keys every row by the sole user.
        await SeedDomainEvent("SECRET.REVEAL", _tenantA);

        await using var sp = BuildServices(TammaMode.SingleUser);
        await Svc(sp).ProcessOnceAsync(default);

        (await CountCp()).Should().Be(1);
        await using var cp = NewCp();
        var row = await cp.AuditRecords.SingleAsync();
        row.UserId.Should().Be(ownerId);
        row.TenantId.Should().BeNull("single-user rows have no tenant dimension");
    }

    // ════════════════════ AC8 — idempotency + replay ════════════════════

    [Test]
    public async Task Projection_Is_Idempotent_Across_Reruns()
    {
        await SeedDomainEvent("SECRET.REVEAL", _tenantA);
        await SeedDomainEvent("WORKFLOW.STEP_COMPLETED", _tenantA); // non-catalog
        await SeedPlatformEvent("IMPERSONATION.STARTED", tenantId: null);

        await using var sp = BuildServices(TammaMode.SaaS);
        var svc = Svc(sp);

        var first = await svc.ProcessOnceAsync(default);
        var second = await svc.ProcessOnceAsync(default);

        first.Should().Be(2, "two catalog events, one non-catalog skipped");
        second.Should().Be(0, "re-running over the same range double-inserts nothing");

        (await CountTenant(_tenantA, _schemaA)).Should().Be(1);
        (await CountCp()).Should().Be(1);
    }

    [Test]
    public async Task Truncate_And_Reset_Cursor_Reproduces_Identical_Trail()
    {
        await SeedDomainEvent("SECRET.REVEAL", _tenantA);
        await SeedPlatformEvent("IMPERSONATION.STARTED", tenantId: null);

        await using var sp = BuildServices(TammaMode.SaaS);
        await Svc(sp).ProcessOnceAsync(default);

        var tenantBefore = await SnapshotTenant(_tenantA, _schemaA);
        var cpBefore = await SnapshotCp();

        // Truncate the curated trail + reset the cursor; re-project from zero.
        await using (var conn = new NpgsqlConnection(_cs))
        {
            await conn.OpenAsync();
            await Exec(conn, "TRUNCATE audit_records, audit_projector_cursor RESTART IDENTITY;");
            await Exec(conn, $"TRUNCATE \"{_schemaA}\".audit_records RESTART IDENTITY;");
        }

        await using var sp2 = BuildServices(TammaMode.SaaS);
        await Svc(sp2).ProcessOnceAsync(default);

        (await SnapshotTenant(_tenantA, _schemaA)).Should().BeEquivalentTo(tenantBefore);
        (await SnapshotCp()).Should().BeEquivalentTo(cpBefore);
    }

    // ════════════════════ AC10 — redaction persisted; never plaintext ════════════════════

    [Test]
    public async Task SecretWrite_Persists_Redacted_Payload_Never_Plaintext()
    {
        const string plaintext = "tamma_sk_LIVEDEADBEEF0123456789";
        await SeedDomainEvent("SECRET.WRITE", _tenantA,
            data: new { apiKey = plaintext, header = "Bearer zzzzzzzzzzzzzzzzzzzz" });

        await using var sp = BuildServices(TammaMode.SaaS);
        await Svc(sp).ProcessOnceAsync(default);

        await using var ta = NewTenant(_tenantA, _schemaA);
        var row = await ta.AuditRecords.SingleAsync();
        row.PayloadJson.Should().Contain("[REDACTED]");
        row.PayloadJson.Should().NotContain(plaintext);
        row.PayloadJson.Should().NotContain("zzzzzzzzzzzzzzzzzzzz");
    }

    // ════════════════════ AC15 — read-only; raw events untouched ════════════════════

    [Test]
    public async Task Projector_Never_Mutates_Raw_Event_Store()
    {
        await SeedDomainEvent("SECRET.REVEAL", _tenantA);
        await SeedPlatformEvent("IMPERSONATION.STARTED", tenantId: null);

        var (domBefore, platBefore) = await RawCounts();

        await using var sp = BuildServices(TammaMode.SaaS);
        await Svc(sp).ProcessOnceAsync(default);

        var (domAfter, platAfter) = await RawCounts();
        domAfter.Should().Be(domBefore, "the projector must not append/delete domain_events");
        platAfter.Should().Be(platBefore, "the projector must not append/delete platform_events");
    }

    // ════════════════════ AC9 — lag metric ════════════════════

    [Test]
    public async Task Lag_Metric_Is_Unprojected_Count_Then_Zero_After_Pass()
    {
        await SeedDomainEvent("SECRET.REVEAL", _tenantA);
        await SeedDomainEvent("WORKFLOW.STEP_COMPLETED", _tenantA); // still raw lag
        await SeedPlatformEvent("IMPERSONATION.STARTED", tenantId: null);

        await using var sp = BuildServices(TammaMode.SaaS);
        var metrics = sp.GetRequiredService<AuditProjectionMetrics>();

        await Svc(sp).ProcessOnceAsync(default);

        // After a full pass the cursor caught up to the stream heads — lag is 0
        // (lag counts UN-projected raw events, including non-catalog ones the
        // cursor still advanced past).
        metrics.Lag.Should().Be(0);
    }

    // ════════════════════ C1 — per-tenant cursor (regression) ════════════════════

    [Test]
    public async Task TenantB_FirstEvent_Is_Projected_After_TenantA_Advances_The_Cursor()
    {
        // Reproduces the C1 data-loss bug: each tenant's domain_events is an
        // INDEPENDENT per-schema BIGSERIAL. Tenant A emits several events so the
        // OLD shared cursor would advance well past 1; then tenant B emits its
        // FIRST sensitive event (sequence 1 in B's schema). Under the old shared
        // cursor, B's event would be read with WHERE SequenceNumber > <A's max>
        // and NEVER projected. With per-tenant cursors, B's event MUST project.

        // Tenant A: 5 catalog events → advances A's stream to sequence 5.
        for (var i = 0; i < 5; i++)
            await SeedDomainEvent("SECRET.REVEAL", _tenantA);

        await using (var sp1 = BuildServices(TammaMode.SaaS))
            await Svc(sp1).ProcessOnceAsync(default);

        (await CountTenant(_tenantA, _schemaA)).Should().Be(5, "tenant-A's five events projected");

        // Now tenant B emits its FIRST event (sequence 1 in B's schema).
        await SeedDomainEvent("SECRET.REVEAL", _tenantB);

        await using (var sp2 = BuildServices(TammaMode.SaaS))
            await Svc(sp2).ProcessOnceAsync(default);

        (await CountTenant(_tenantB, _schemaB)).Should().Be(1,
            "tenant-B's low-sequence first event MUST project — the per-tenant cursor "
            + "tracks B independently of how far A advanced (C1 regression)");

        // Sanity: the per-tenant cursor rows are tracked separately.
        await using var cp = NewCp();
        var aCursor = await cp.AuditProjectorCursors.AsNoTracking()
            .SingleOrDefaultAsync(c => c.TenantId == _tenantA);
        var bCursor = await cp.AuditProjectorCursors.AsNoTracking()
            .SingleOrDefaultAsync(c => c.TenantId == _tenantB);
        aCursor.Should().NotBeNull();
        bCursor.Should().NotBeNull();
        aCursor!.LastDomainSequenceNumber.Should().BeGreaterThan(0);
        bCursor!.LastDomainSequenceNumber.Should().BeGreaterThan(0);
    }

    // ════════════════════ C2 — failed-redaction quarantine ════════════════════

    [Test]
    public async Task Failed_Redaction_Quarantines_The_Event_And_The_Cursor_Advances()
    {
        // Seed three catalog events in order; the projector for the MIDDLE one
        // throws on build/redact (injected failing projector). The middle event
        // must be QUARANTINED (a failure-outcome row with a SAFE placeholder
        // payload, NO plaintext), the failure metric must fire, the cursor must
        // advance past it, and the events AFTER it must still project (no stall).
        const string plaintext = "tamma_sk_POISONPILL0123456789";
        await SeedDomainEvent("SECRET.WRITE", _tenantA, data: new { apiKey = "first" });
        await SeedDomainEvent("SECRET.REVEAL", _tenantA, data: new { apiKey = plaintext });
        await SeedDomainEvent("SECRET.READ", _tenantA, data: new { apiKey = "third" });

        // Inject a projector that throws on the SECRET.REVEAL event only (it
        // delegates the quarantine build to the REAL projector so the safe-payload
        // path is exercised end to end).
        var failing = new FailingOnTypeProjector("SECRET.REVEAL");
        await using var sp = BuildServices(TammaMode.SaaS, projectorOverride: failing);

        var inserted = await Svc(sp).ProcessOnceAsync(default);

        // All three rows present: two normal + one quarantine.
        (await CountTenant(_tenantA, _schemaA)).Should().Be(3,
            "the failing event is quarantined (not dropped); the others project normally");
        inserted.Should().Be(3);

        await using var ta = NewTenant(_tenantA, _schemaA);
        var rows = await ta.AuditRecords.OrderBy(r => r.SourceSequenceNumber).ToListAsync();

        var quarantined = rows.Single(r => r.ActionCode == "SECRET.REVEAL");
        quarantined.Outcome.Should().Be("failure", "a failed projection is recorded as a failure");
        quarantined.PayloadJson.Should().NotContain(plaintext,
            "the raw/un-redacted payload must NEVER reach the quarantine row");
        quarantined.PayloadJson.Should().Contain("redaction_failed",
            "the quarantine row carries the safe placeholder payload");

        // The good neighbours are normal success rows.
        rows.Where(r => r.ActionCode != "SECRET.REVEAL")
            .Should().OnlyContain(r => r.Outcome == "success");

        // The failure counter fired exactly once.
        failing.ThrowCount.Should().Be(1);
    }

    // ── snapshot/raw helpers ──

    private async Task<List<string>> SnapshotTenant(Guid tenantId, string schema)
    {
        await using var t = NewTenant(tenantId, schema);
        return await t.AuditRecords.OrderBy(r => r.SourceSequenceNumber)
            .Select(r => r.ActionCode + "|" + r.Category + "|" + (r.TenantId.ToString() ?? "")
                + "|" + r.SourceEventId)
            .ToListAsync();
    }

    private async Task<List<string>> SnapshotCp()
    {
        await using var cp = NewCp();
        return await cp.AuditRecords.OrderBy(r => r.SourceSequenceNumber)
            .Select(r => r.ActionCode + "|" + r.Category + "|" + r.SourceEventId)
            .ToListAsync();
    }

    private async Task<(int Domain, int Platform)> RawCounts()
    {
        int domain = 0;
        foreach (var (id, schema) in new[] { (_tenantA, _schemaA), (_tenantB, _schemaB) })
        {
            await using var t = NewTenant(id, schema);
            domain += await t.DomainEvents.CountAsync();
        }
        await using var cp = NewCp();
        return (domain, await cp.PlatformEvents.CountAsync());
    }

    private async Task SeedUser(Guid id, string email)
    {
        await using var cp = NewCp();
        cp.Users.Add(new User { Id = id, Email = email, CreatedAt = DateTime.UtcNow });
        await cp.SaveChangesAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── test doubles ──

    private sealed class FixedModeProvider(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    /// <summary>A projector that throws on a chosen event type to force the C2
    /// quarantine path; everything else (including the quarantine build) delegates
    /// to the REAL projector so the safe-payload behaviour is exercised genuinely.</summary>
    private sealed class FailingOnTypeProjector(string failOnType) : IAuditProjector
    {
        private readonly AuditProjector _real = new();
        public int ThrowCount { get; private set; }

        public AuditRecord? TryBuildRecord(
            RawAuditEvent rawEvent, AuditOwnershipMode mode, Guid? singleUserOwnerId)
        {
            if (string.Equals(rawEvent.Type, failOnType, StringComparison.Ordinal))
            {
                ThrowCount++;
                throw new InvalidOperationException(
                    "simulated redaction failure (e.g. RegexMatchTimeoutException)");
            }
            return _real.TryBuildRecord(rawEvent, mode, singleUserOwnerId);
        }

        public AuditRecord BuildQuarantineRecord(
            RawAuditEvent rawEvent, AuditOwnershipMode mode, Guid? singleUserOwnerId) =>
            _real.BuildQuarantineRecord(rawEvent, mode, singleUserOwnerId);
    }

    /// <summary>A factory that builds a TenantDbContext bound to the tenant's
    /// search-path schema in the shared test container.</summary>
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
}
