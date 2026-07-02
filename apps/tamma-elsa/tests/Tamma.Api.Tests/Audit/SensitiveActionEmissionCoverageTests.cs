using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.PromptStore;
using Tamma.Core.Audit;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Audit;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-10 (AC15) — end-to-end coverage: drive the REAL
/// <see cref="SensitiveActionEmitter"/> for each sensitive-action site, then run
/// the Story 37-1 <see cref="AuditProjectorBackgroundService"/> and assert the
/// curated <c>audit_records</c> row lands with the right catalog code, category,
/// and scope. Proves the emitter's OUTPUT (tags/data/scope) projects correctly —
/// the missing link between "the action happened" and "the audit trail has it".
/// </summary>
[TestFixture]
public class SensitiveActionEmissionCoverageTests
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
            .WithDatabase("audit_emission_test")
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
    public async Task ResetTables()
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();
        await Exec(conn, "TRUNCATE audit_records, audit_projector_cursor, platform_events, users RESTART IDENTITY CASCADE;");
        foreach (var schema in new[] { _schemaA, _schemaB })
        {
            await Exec(conn, $"TRUNCATE \"{schema}\".audit_records RESTART IDENTITY;");
            await Exec(conn, $"TRUNCATE \"{schema}\".domain_events RESTART IDENTITY;");
        }
        await SeedTenant(_tenantA, _schemaA);
        await SeedTenant(_tenantB, _schemaB);
    }

    // ════════════════════ BYOK (tenant scope) ════════════════════

    [Test]
    public async Task Byok_ProviderKeyChanged_Emits_And_Projects_Tenant_Scoped()
    {
        var actor = Guid.NewGuid();
        await Emitter().EmitAsync(SensitiveAction.ForTenant(
            SensitiveActionCatalog.ProviderKeyChanged, _tenantA, actor,
            new Dictionary<string, string?> { ["provider"] = "anthropic", ["operation"] = "set", ["mode"] = "byok" },
            new Dictionary<string, object?> { ["provider"] = "anthropic", ["operation"] = "set", ["version"] = 1 }));

        await RunProjector();

        var row = await SingleTenantRow(_tenantA, _schemaA);
        row.ActionCode.Should().Be(SensitiveActionCatalog.ProviderKeyChanged);
        row.Category.Should().Be("byok");
        row.TenantId.Should().Be(_tenantA);
        row.ActorUserId.Should().Be(actor);

        (await CountTenant(_tenantB, _schemaB)).Should().Be(0, "tenant-B never sees tenant-A's BYOK event");
        (await CountCp()).Should().Be(0, "a tenant-scoped BYOK event does not land in the control plane");
    }

    [Test]
    public async Task Byok_KeySet_With_Real_Looking_Secret_Persists_Zero_Key_Bytes()
    {
        const string plaintext = "tamma_sk_LIVEDEADBEEF0123456789";
        await Emitter().EmitAsync(SensitiveAction.ForTenant(
            SensitiveActionCatalog.ProviderKeyChanged, _tenantA, Guid.NewGuid(),
            new Dictionary<string, string?> { ["provider"] = "anthropic" },
            // A buggy caller shoves the raw key into data — the emitter + projector
            // must ensure zero key bytes reach the stored audit record.
            new Dictionary<string, object?> { ["provider"] = "anthropic", ["apiKey"] = plaintext }));

        await RunProjector();

        var row = await SingleTenantRow(_tenantA, _schemaA);
        row.PayloadJson.Should().NotContain(plaintext, "no key material may ever land in the audit record");
        row.PayloadJson.Should().Contain("anthropic", "the redaction-safe metadata is preserved");
    }

    // ════════════════════ AUTH (platform edge) ════════════════════

    [Test]
    public async Task Login_Success_Platform_With_Tenant_Projects_Into_Tenant_Schema()
    {
        var actor = Guid.NewGuid();
        await Emitter().EmitAsync(SensitiveAction.ForPlatform(
            SensitiveActionCatalog.LoginSuccess, _tenantA, actor,
            new Dictionary<string, string?>
            {
                ["actorEmail"] = "user@example.com",
                ["ip"] = "203.0.113.10",
                ["userAgent"] = "Mozilla/5.0",
            }));

        await RunProjector();

        var row = await SingleTenantRow(_tenantA, _schemaA);
        row.ActionCode.Should().Be(SensitiveActionCatalog.LoginSuccess);
        row.Category.Should().Be("auth");
        row.Outcome.Should().Be("success");
        row.ActorUserId.Should().Be(actor);
        row.ActorEmailSnapshot.Should().Be("user@example.com");
        row.IpAddress.Should().Be("203.0.113.10");
    }

    [Test]
    public async Task Login_Failure_Platform_Null_Tenant_Projects_To_ControlPlane_With_Reason()
    {
        await Emitter().EmitAsync(SensitiveAction.ForPlatform(
            SensitiveActionCatalog.LoginFailure, tenantId: null, actorUserId: null,
            new Dictionary<string, string?>
            {
                ["reason"] = "bad_credentials",
                ["actorEmail"] = "attacker@example.com",
                ["ip"] = "198.51.100.7",
            },
            new Dictionary<string, object?> { ["reason"] = "bad_credentials" }));

        await RunProjector();

        (await CountCp()).Should().Be(1, "a login failure has no trusted tenant → control plane");
        (await CountTenant(_tenantA, _schemaA)).Should().Be(0);

        await using var cp = NewCp();
        var row = await cp.AuditRecords.SingleAsync();
        row.ActionCode.Should().Be(SensitiveActionCatalog.LoginFailure);
        row.Category.Should().Be("auth");
        row.Outcome.Should().Be("failure");
        row.TenantId.Should().BeNull();
        row.ActorEmailSnapshot.Should().Be("attacker@example.com");
        row.PayloadJson.Should().Contain("bad_credentials");
    }

    [Test]
    public async Task TokenRefreshed_And_ApiKeyUsed_Project_Auth_Category()
    {
        await Emitter().EmitAsync(SensitiveAction.ForPlatform(
            SensitiveActionCatalog.TokenRefreshed, _tenantA, Guid.NewGuid(),
            new Dictionary<string, string?> { ["ip"] = "203.0.113.20" }));
        await Emitter().EmitAsync(SensitiveAction.ForPlatform(
            SensitiveActionCatalog.ApiKeyUsed, _tenantA, actorUserId: null,
            new Dictionary<string, string?> { ["apiKeyPrefix"] = "tamma_sk_t_", ["scope"] = "tenant" }));

        await RunProjector();

        await using var t = NewTenant(_tenantA, _schemaA);
        var rows = await t.AuditRecords.OrderBy(r => r.ActionCode).ToListAsync();
        rows.Select(r => r.ActionCode).Should().BeEquivalentTo(new[]
        {
            SensitiveActionCatalog.ApiKeyUsed, SensitiveActionCatalog.TokenRefreshed,
        });
        rows.Should().OnlyContain(r => r.Category == "auth");
    }

    // ════════════════════ Catalog coverage enumeration ════════════════════

    [Test]
    public void Every_Site_Code_This_Story_Wires_Is_Catalogued()
    {
        // AC1/AC15 — each site's emitted code must be in the 37-1 catalog so the
        // projector materialises it. A drift here means an emission that never
        // reaches audit_records.
        foreach (var code in new[]
        {
            SensitiveActionCatalog.LoginSuccess,
            SensitiveActionCatalog.LoginFailure,
            SensitiveActionCatalog.TokenRefreshed,
            SensitiveActionCatalog.ApiKeyUsed,
            SensitiveActionCatalog.ProviderKeyChanged,
            SensitiveActionCatalog.PlanUpdated,
            SensitiveActionCatalog.RefreshReuseDetected,
            SensitiveActionCatalog.SecretWrite,
            SensitiveActionCatalog.ImpersonationStarted,
            SensitiveActionCatalog.TenantMemberRoleChanged,
        })
        {
            SensitiveActionCatalog.IsSensitive(code).Should()
                .BeTrue($"the site code '{code}' must be catalogued so it projects");
        }
    }

    // ── infra ────────────────────────────────────────────────────────────

    private SensitiveActionEmitter Emitter() =>
        new(new ContainerEventRepository(_cs),
            new ContainerPlatformPublisher(_cs),
            TimeProvider.System,
            NullLogger<SensitiveActionEmitter>.Instance);

    private async Task RunProjector()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ControlPlaneDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton<ITammaModeProvider>(new FixedModeProvider(TammaMode.SaaS));
        services.AddSingleton<ITenantDbContextFactory>(new SearchPathTenantFactory(_cs));
        services.AddSingleton<IAuditProjector, AuditProjector>();
        services.AddSingleton<IAuditRecordRepository, AuditRecordRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditProjectionMetrics>();
        services.AddSingleton(new AuditProjectorOptions { RunOnStartup = false });
        await using var sp = services.BuildServiceProvider();
        var svc = new AuditProjectorBackgroundService(
            sp, sp.GetRequiredService<AuditProjectorOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuditProjectionMetrics>(),
            NullLogger<AuditProjectorBackgroundService>.Instance);
        await svc.ProcessOnceAsync(default);
    }

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_cs) { SearchPath = schema }.ConnectionString;

    private ControlPlaneDbContext NewCp() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private TenantDbContext NewTenant(Guid tenantId, string schema) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options, tenantId);

    private async Task<AuditRecord> SingleTenantRow(Guid tenantId, string schema)
    {
        await using var t = NewTenant(tenantId, schema);
        return await t.AuditRecords.SingleAsync();
    }

    private async Task<int> CountTenant(Guid tenantId, string schema)
    {
        await using var t = NewTenant(tenantId, schema);
        return await t.AuditRecords.CountAsync();
    }

    private async Task<int> CountCp()
    {
        await using var cp = NewCp();
        return await cp.AuditRecords.CountAsync();
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

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class FixedModeProvider(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
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

    /// <summary>Minimal IEventRepository that writes the emitted DomainEvent into
    /// the tenant's schema (where the projector reads it). Only AppendAsync is
    /// exercised.</summary>
    private sealed class ContainerEventRepository(string baseCs) : IEventRepository
    {
        public async Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            var schema = TenantNaming.SchemaName(evt.TenantId!.Value);
            var cs = new NpgsqlConnectionStringBuilder(baseCs) { SearchPath = schema }.ConnectionString;
            await using var ctx = new TenantDbContext(
                new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(cs).Options, evt.TenantId.Value);
            if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
            ctx.DomainEvents.Add(evt);
            await ctx.SaveChangesAsync();
            return evt;
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit) => throw new NotSupportedException();
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type) => throw new NotSupportedException();
        public Task ClearAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset) => throw new NotSupportedException();
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset) => throw new NotSupportedException();
    }

    /// <summary>Minimal IPlatformEventPublisher that writes the emitted
    /// PlatformEvent into the control plane (where the projector reads it).</summary>
    private sealed class ContainerPlatformPublisher(string baseCs) : IPlatformEventPublisher
    {
        public async Task<PlatformEvent?> AppendAndPublishAsync(PlatformEvent evt, CancellationToken ct = default)
        {
            await using var cp = new ControlPlaneDbContext(
                new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(baseCs).Options);
            if (evt.Id == Guid.Empty) evt.Id = Guid.NewGuid();
            cp.PlatformEvents.Add(evt);
            await cp.SaveChangesAsync(ct);
            return evt;
        }
    }
}
