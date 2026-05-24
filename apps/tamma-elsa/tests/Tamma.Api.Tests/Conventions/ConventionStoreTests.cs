using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Story 27-9 — <see cref="ConventionStore"/> exact <c>(tenant, role, action)</c>
/// resolution against a real Postgres testcontainer.
///
/// <para>EF InMemory doesn't honour <c>NULLS NOT DISTINCT</c> (the Story 27-8
/// unique index) or DB-side defaults, and resolution depends on real
/// system-default rows (<c>tenant_id IS NULL</c>) seeded by
/// <see cref="ConventionStoreSeeder"/>, so the only faithful path is a
/// container — same pattern as <see cref="ConventionStoreSeederTests"/>.</para>
///
/// <para>Covers: tenant-override resolution (<c>Source=Tenant</c>),
/// system-default resolution (<c>Source=System</c>), missing pair →
/// <see cref="TammaError"/> (<c>CONVENTION_NOT_FOUND</c>), <c>enabled=false</c>
/// override falls through to system (AC9), Upsert/Delete touch ONLY tenant rows
/// (never system defaults — AC2), and List returns resolved bodies (AC3).</para>
/// </summary>
[TestFixture]
public class ConventionStoreTests
{
    // Canonical taxonomy cell: developer / implement-feature.
    private const AgentRole Role = AgentRole.Developer;
    private const AgentAction Action = AgentAction.ImplementFeature;
    private static readonly string RoleWire = Role.ToWire();
    private static readonly string ActionWire = Action.ToWire();

    // A typed but NON-taxonomy pair: developer does not own 'deploy'
    // (devops-only) → no seeded system default → fail-loud.
    private const AgentAction UnseededAction = AgentAction.Deploy;

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private NpgsqlDataSource _dataSource = null!;
    private StubTenantConnectionResolver _resolver = null!;

    // The ambient request tenant id that routes the physical DB (shared in
    // the transitional model). Distinct from the row-scoping tenant id passed
    // to the service methods.
    private static readonly Guid AmbientTenant =
        Guid.Parse("aaaaaaaa-1111-2222-3333-aaaaaaaaaaaa");

    private static int ExpectedCellCount =>
        RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("convention_store_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using (var ext = new NpgsqlConnection(_connectionString))
        {
            await ext.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";"
              + "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";",
                ext);
            await cmd.ExecuteNonQueryAsync();
        }

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(_connectionString);

        _dataSource = NpgsqlDataSource.Create(_connectionString);
        _resolver = new StubTenantConnectionResolver(_dataSource);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _resolver.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SeedSystemDefaults()
    {
        // Fresh table each test, then re-seed the system defaults so resolution
        // has the tenant_id IS NULL rows it depends on.
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("TRUNCATE TABLE conventions;", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var seeder = new ConventionStoreSeeder(
            _resolver, TimeProvider.System,
            NullLogger<ConventionStoreSeeder>.Instance);
        await using var db = NewContext();
        await seeder.SeedAsync(db, default);
    }

    private TenantDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;
        return new TenantDbContext(options);
    }

    /// <summary>
    /// Build a service whose repository routes the physical DB via the
    /// ambient tenant id (mirrors production: the factory + ITenantContext
    /// pick the DB; the method's tenantId arg picks the row tier).
    /// </summary>
    private ConventionStore NewStore()
    {
        var factory = new TenantDbContextFactory(_resolver);
        var tc = new TenantContext();
        tc.SetTenantId(AmbientTenant);
        var repo = new ConventionRepository(factory, tc);
        return new ConventionStore(repo);
    }

    // ------------------------------------------------------------------
    // Resolution (AC4)
    // ------------------------------------------------------------------

    [Test]
    public async Task Resolve_TenantOverride_Wins_SourceTenant()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var admin = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-OVERRIDDEN", admin, default);

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);

        resolved.Body.Should().Be("TENANT-OVERRIDDEN");
        resolved.Source.Should().Be(ConventionSource.Tenant);
        resolved.Role.Should().Be(RoleWire);
        resolved.Action.Should().Be(ActionWire);
    }

    [Test]
    public async Task Resolve_NoOverride_FallsBackToSystemDefault_SourceSystem()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);

        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().Be(ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire));
        resolved.Body.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Resolve_NullTenant_ResolvesSystemDefault()
    {
        // tenantId=null (e.g. unprovisioned context) resolves system defaults.
        var store = NewStore();

        var resolved = await store.ResolveAsync(null, Role, Action, default);

        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().Be(ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire));
    }

    [Test]
    public async Task Resolve_MissingPair_ThrowsTammaError_ConventionNotFound()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        // developer does not own 'deploy' → no seeded system default.
        var act = async () =>
            await store.ResolveAsync(tenantId, Role, UnseededAction, default);

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("CONVENTION_NOT_FOUND");
        ex.Which.Severity.Should().Be(TammaErrorSeverity.High);
    }

    [Test]
    public async Task Resolve_DisabledTenantOverride_FallsThroughToSystem_AC9()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var admin = Guid.NewGuid();

        // Create a tenant override then disable it directly in the DB.
        await store.UpsertAsync(tenantId, Role, Action, "TENANT-DISABLED", admin, default);
        await using (var db = NewContext())
        {
            var row = await db.Conventions.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == tenantId
                    && c.Role == RoleWire && c.Action == ActionWire);
            row.Enabled = false;
            await db.SaveChangesAsync();
        }

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);

        // Disabled override must NOT win and must NOT blank — falls to system.
        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().Be(ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire));
        resolved.Body.Should().NotBe("TENANT-DISABLED");
    }

    [Test]
    public async Task Resolve_TenantOverridesDoNotLeakBetweenTenants()
    {
        var store = NewStore();
        var acme = Guid.NewGuid();
        var globex = Guid.NewGuid();

        await store.UpsertAsync(acme, Role, Action, "ACME-ONLY", Guid.NewGuid(), default);

        var acmeResolved = await store.ResolveAsync(acme, Role, Action, default);
        var globexResolved = await store.ResolveAsync(globex, Role, Action, default);

        acmeResolved.Body.Should().Be("ACME-ONLY");
        acmeResolved.Source.Should().Be(ConventionSource.Tenant);
        globexResolved.Source.Should().Be(ConventionSource.System);
        globexResolved.Body.Should().NotBe("ACME-ONLY");
    }

    // ------------------------------------------------------------------
    // GetAsync — raw fetch (AC1)
    // ------------------------------------------------------------------

    [Test]
    public async Task Get_ReturnsTenantOverride_WhenPresentAndEnabled()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "RAW-TENANT", Guid.NewGuid(), default);

        var row = await store.GetAsync(tenantId, Role, Action, default);

        row.Should().NotBeNull();
        row!.Body.Should().Be("RAW-TENANT");
        row.TenantId.Should().Be(tenantId);
    }

    [Test]
    public async Task Get_ReturnsSystemDefault_WhenNoOverride()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        var row = await store.GetAsync(tenantId, Role, Action, default);

        row.Should().NotBeNull();
        row!.TenantId.Should().BeNull();
        row.Body.Should().Be(ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire));
    }

    [Test]
    public async Task Get_ReturnsNull_ForUnseededPair()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        // Unlike ResolveAsync, GetAsync MAY return null.
        var row = await store.GetAsync(tenantId, Role, UnseededAction, default);

        row.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Upsert / Delete touch ONLY tenant rows (AC2)
    // ------------------------------------------------------------------

    [Test]
    public async Task Upsert_DoesNotMutateSystemDefault()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var systemBodyBefore = ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire);

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-BODY", Guid.NewGuid(), default);

        await using var db = NewContext();
        var systemRow = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == null
                && c.Role == RoleWire && c.Action == ActionWire);
        systemRow.Body.Should().Be(systemBodyBefore, "system default is untouched by tenant upsert");
        systemRow.Version.Should().Be(1);

        var tenantRow = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId
                && c.Role == RoleWire && c.Action == ActionWire);
        tenantRow.Body.Should().Be("TENANT-BODY");
    }

    [Test]
    public async Task Upsert_SetsAuditColumns_AndBumpsVersionOnUpdate()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var firstAdmin = Guid.NewGuid();
        var secondAdmin = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "v1", firstAdmin, default);
        await store.UpsertAsync(tenantId, Role, Action, "v2", secondAdmin, default);

        await using var db = NewContext();
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId
                && c.Role == RoleWire && c.Action == ActionWire);

        row.Body.Should().Be("v2");
        row.Version.Should().Be(2, "Version bumps on update");
        row.CreatedBy.Should().Be(firstAdmin, "CreatedBy is sticky");
        row.UpdatedBy.Should().Be(secondAdmin);
    }

    [Test]
    public async Task Delete_RemovesOnlyTenantOverride_LeavesSystemDefault()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-T", Guid.NewGuid(), default);

        var beforeDelete = await store.ResolveAsync(tenantId, Role, Action, default);
        beforeDelete.Source.Should().Be(ConventionSource.Tenant);

        await store.DeleteAsync(tenantId, Role, Action, default);

        // System default survives; resolution falls through to it.
        var afterDelete = await store.ResolveAsync(tenantId, Role, Action, default);
        afterDelete.Source.Should().Be(ConventionSource.System);

        await using var db = NewContext();
        (await db.Conventions.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == null
                && c.Role == RoleWire && c.Action == ActionWire))
            .Should().Be(1, "system default is never deleted");
        (await db.Conventions.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == tenantId
                && c.Role == RoleWire && c.Action == ActionWire))
            .Should().Be(0, "tenant override is gone");
    }

    [Test]
    public async Task Delete_IsNoOp_WhenNoOverrideExists()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        var act = async () => await store.DeleteAsync(tenantId, Role, Action, default);

        await act.Should().NotThrowAsync();
        (await store.ResolveAsync(tenantId, Role, Action, default)).Source
            .Should().Be(ConventionSource.System);
    }

    // ------------------------------------------------------------------
    // List (AC3)
    // ------------------------------------------------------------------

    [Test]
    public async Task List_ReturnsResolvedBodyForEveryTaxonomyCell()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        // One override on top of the full system-default set.
        await store.UpsertAsync(tenantId, Role, Action, "LIST-OVERRIDE", Guid.NewGuid(), default);

        var list = await store.ListAsync(tenantId, default);

        list.Should().HaveCount(ExpectedCellCount, "one resolved row per taxonomy cell");

        var overridden = list.Single(s => s.Role == RoleWire && s.Action == ActionWire);
        overridden.Body.Should().Be("LIST-OVERRIDE");
        overridden.Source.Should().Be(ConventionSource.Tenant);

        // Every other cell resolves to its (non-empty) system default.
        list.Where(s => !(s.Role == RoleWire && s.Action == ActionWire))
            .Should().OnlyContain(s => s.Source == ConventionSource.System);
        list.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Body));
    }

    [Test]
    public async Task List_NullTenant_ReturnsAllSystemDefaults()
    {
        var store = NewStore();

        var list = await store.ListAsync(null, default);

        list.Should().HaveCount(ExpectedCellCount);
        list.Should().OnlyContain(s => s.Source == ConventionSource.System);
    }

    [Test]
    public async Task List_DisabledOverride_ResolvesToSystem_AC9()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "DISABLED-IN-LIST", Guid.NewGuid(), default);
        await using (var db = NewContext())
        {
            var row = await db.Conventions.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == tenantId
                    && c.Role == RoleWire && c.Action == ActionWire);
            row.Enabled = false;
            await db.SaveChangesAsync();
        }

        var list = await store.ListAsync(tenantId, default);

        var cell = list.Single(s => s.Role == RoleWire && s.Action == ActionWire);
        cell.Source.Should().Be(ConventionSource.System);
        cell.Body.Should().NotBe("DISABLED-IN-LIST");
    }
}
