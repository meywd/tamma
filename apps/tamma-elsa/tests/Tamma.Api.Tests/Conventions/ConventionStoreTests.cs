using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Tests.TestDoubles;
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
    private SystemStoreDbContextFactory _systemStoreFactory = null!;

    // Unified-tenancy Phase 3 split harness: a SECOND physical database in the
    // same container acting as a tenant store that carries NO system-default
    // rows — proves the repository's system leg reads the system store, not
    // the tenant connection.
    private string _tenantOnlyConnectionString = null!;
    private NpgsqlDataSource _tenantOnlyDataSource = null!;
    private StubTenantConnectionResolver _tenantOnlyResolver = null!;

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

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(_connectionString);

        _dataSource = NpgsqlDataSource.Create(_connectionString);
        _resolver = new StubTenantConnectionResolver(_dataSource);
        _systemStoreFactory = new SystemStoreDbContextFactory(_connectionString);

        // Second database (same container) = a tenant store WITHOUT system
        // rows, for the Phase 3 system/tenant split tests.
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "CREATE DATABASE convention_tenant_only_store;", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        _tenantOnlyConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = "convention_tenant_only_store",
        }.ConnectionString;
        await migrator.MigrateTenantAppAsync(_tenantOnlyConnectionString);
        _tenantOnlyDataSource = NpgsqlDataSource.Create(_tenantOnlyConnectionString);
        _tenantOnlyResolver = new StubTenantConnectionResolver(_tenantOnlyDataSource);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _tenantOnlyResolver.DisposeAsync();
        await _tenantOnlyDataSource.DisposeAsync();
        await _resolver.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SeedSystemDefaults()
    {
        // Fresh tables each test (both stores), then re-seed the system
        // defaults into the SYSTEM store so resolution has the
        // tenant_id IS NULL rows it depends on. The tenant-only store stays
        // free of system rows by construction.
        foreach (var cs in new[] { _connectionString, _tenantOnlyConnectionString })
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("TRUNCATE TABLE conventions;", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var seeder = new ConventionStoreSeeder(
            _systemStoreFactory, TimeProvider.System,
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
    ///
    /// Story 27-14: pass a no-op <see cref="ConventionEventsService"/> so event
    /// emission is exercised structurally (best-effort: swallows silently) but
    /// does not require a running event-store DB. The
    /// <see cref="ConventionEventsServiceTests"/> fixture covers the event
    /// assertions in detail.
    /// </summary>
    private ConventionStore NewStore()
    {
        var factory = new TenantDbContextFactory(_resolver);
        var tc = new TenantContext();
        tc.SetTenantId(AmbientTenant);
        var repo = new ConventionRepository(factory, tc, _systemStoreFactory);
        var eventsService = new ConventionEventsService(new NullConventionEventRepository());
        return new ConventionStore(repo, eventsService);
    }

    /// <summary>
    /// Unified-tenancy Phase 3 split store: the TENANT leg routes to the
    /// tenant-only database (which never carries system rows) while the SYSTEM
    /// leg routes to the seeded central database — the two legs genuinely hit
    /// DIFFERENT physical stores.
    /// </summary>
    private ConventionStore NewSplitStore()
    {
        var factory = new TenantDbContextFactory(_tenantOnlyResolver);
        var tc = new TenantContext();
        tc.SetTenantId(AmbientTenant);
        var repo = new ConventionRepository(factory, tc, _systemStoreFactory);
        var eventsService = new ConventionEventsService(new NullConventionEventRepository());
        return new ConventionStore(repo, eventsService);
    }

    private TenantDbContext NewTenantOnlyContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_tenantOnlyDataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;
        return new TenantDbContext(options);
    }

    // Null event repository for store tests — events are verified separately in
    // ConventionEventsServiceTests; here we just need emission to not throw.
    private sealed class NullConventionEventRepository : IEventRepository
    {
        public Task<Tamma.Data.Entities.DomainEvent> AppendAsync(Tamma.Data.Entities.DomainEvent evt)
            => Task.FromResult(evt);
        public Task<Tamma.Data.Entities.DomainEvent?> GetByIdAsync(Guid id)
            => Task.FromResult<Tamma.Data.Entities.DomainEvent?>(null);
        public Task<List<Tamma.Data.Entities.DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<Tamma.Data.Entities.DomainEvent>());
        public Task<Tamma.Data.Entities.DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => Task.FromResult<Tamma.Data.Entities.DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<Tamma.Data.Entities.DomainEvent>, int)>((new List<Tamma.Data.Entities.DomainEvent>(), 0));
        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<Tamma.Data.Entities.DomainEvent>, int)>((new List<Tamma.Data.Entities.DomainEvent>(), 0));
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

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-OVERRIDDEN", enabled: true, admin, default);

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
        await store.UpsertAsync(tenantId, Role, Action, "TENANT-DISABLED", enabled: true, admin, default);
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

        await store.UpsertAsync(acme, Role, Action, "ACME-ONLY", enabled: true, Guid.NewGuid(), default);

        var acmeResolved = await store.ResolveAsync(acme, Role, Action, default);
        var globexResolved = await store.ResolveAsync(globex, Role, Action, default);

        acmeResolved.Body.Should().Be("ACME-ONLY");
        acmeResolved.Source.Should().Be(ConventionSource.Tenant);
        globexResolved.Source.Should().Be(ConventionSource.System);
        globexResolved.Body.Should().NotBe("ACME-ONLY");
    }

    // ------------------------------------------------------------------
    // Unified-tenancy Phase 3 — system/tenant store SPLIT. The tenant leg
    // points at a database that carries ZERO system-default rows; the system
    // leg points at the seeded central database. These tests fail if the
    // repository's system-default reads/writes ride the tenant connection.
    // ------------------------------------------------------------------

    [Test]
    public async Task Resolve_SystemDefault_ComesFromSystemStore_WhenTenantStoreHasNoSystemRows()
    {
        var store = NewSplitStore();
        var tenantId = Guid.NewGuid();

        // Precondition: the TENANT store has no system-default rows at all —
        // if resolution still succeeds, the system leg hit the system store.
        await using (var tenantDb = NewTenantOnlyContext())
        {
            (await tenantDb.Conventions.IgnoreQueryFilters()
                .CountAsync(c => c.TenantId == null))
                .Should().Be(0, "the tenant store must carry no system rows for this proof");
        }

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);

        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().Be(ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire),
            "the system default must resolve from the SYSTEM store even though "
            + "the tenant's own store has no tenant_id IS NULL rows");
    }

    [Test]
    public async Task Resolve_TenantOverrideInTenantStore_StillWins_OverSystemStoreDefault()
    {
        var store = NewSplitStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "SPLIT-TENANT-BODY", enabled: true, Guid.NewGuid(), default);

        // The override row landed in the TENANT store, not the system store.
        await using (var tenantDb = NewTenantOnlyContext())
        {
            (await tenantDb.Conventions.IgnoreQueryFilters()
                .CountAsync(c => c.TenantId == tenantId))
                .Should().Be(1, "tenant-override writes route to the tenant store");
        }
        await using (var systemDb = NewContext())
        {
            (await systemDb.Conventions.IgnoreQueryFilters()
                .CountAsync(c => c.TenantId == tenantId))
                .Should().Be(0, "tenant-override writes must NOT land in the system store");
        }

        // Resolution overlays the tenant store's override on the system store's defaults.
        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.Tenant);
        resolved.Body.Should().Be("SPLIT-TENANT-BODY");
    }

    [Test]
    public async Task UpsertSystemDefault_WritesToSystemStore_NotTenantStore()
    {
        var store = NewSplitStore();
        var admin = Guid.NewGuid();

        await store.UpsertSystemDefaultAsync(Role, Action, "SPLIT-SYSTEM-BODY", enabled: true, admin, default);

        await using (var tenantDb = NewTenantOnlyContext())
        {
            (await tenantDb.Conventions.IgnoreQueryFilters()
                .CountAsync(c => c.TenantId == null))
                .Should().Be(0, "system-default writes must NOT land in the tenant store");
        }

        await using var systemDb = NewContext();
        var row = await systemDb.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire);
        row.Body.Should().Be("SPLIT-SYSTEM-BODY", "system-default writes route to the system store");
    }

    // ------------------------------------------------------------------
    // GetAsync — raw fetch (AC1)
    // ------------------------------------------------------------------

    [Test]
    public async Task Get_ReturnsTenantOverride_WhenPresentAndEnabled()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "RAW-TENANT", enabled: true, Guid.NewGuid(), default);

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

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-BODY", enabled: true, Guid.NewGuid(), default);

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

        await store.UpsertAsync(tenantId, Role, Action, "v1", enabled: true, firstAdmin, default);
        await store.UpsertAsync(tenantId, Role, Action, "v2", enabled: true, secondAdmin, default);

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

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-T", enabled: true, Guid.NewGuid(), default);

        var beforeDelete = await store.ResolveAsync(tenantId, Role, Action, default);
        beforeDelete.Source.Should().Be(ConventionSource.Tenant);

        await store.DeleteAsync(tenantId, Role, Action, Guid.NewGuid(), default);

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

        var act = async () => await store.DeleteAsync(tenantId, Role, Action, Guid.NewGuid(), default);

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
        await store.UpsertAsync(tenantId, Role, Action, "LIST-OVERRIDE", enabled: true, Guid.NewGuid(), default);

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
    public async Task List_IsDeterministicallyOrderedByRoleThenAction()
    {
        // ListAsync must return a stable (Role ASC, Action ASC) order so that
        // Story 27-10's pagination / diff is reproducible regardless of
        // FrozenDictionary enumeration order.
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        var list = await store.ListAsync(tenantId, default);

        list.Should().NotBeEmpty();
        var expected = list
            .OrderBy(s => s.Role, StringComparer.Ordinal)
            .ThenBy(s => s.Action, StringComparer.Ordinal)
            .ToList();
        list.Should().Equal(expected,
            because: "ListAsync must return items sorted by (Role, Action) for deterministic pagination");
    }

    [Test]
    public async Task List_DisabledOverride_ResolvesToSystem_AC9()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "DISABLED-IN-LIST", enabled: true, Guid.NewGuid(), default);
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

    // ------------------------------------------------------------------
    // System-default admin CRUD + reset (Story 27-10 enablement).
    //
    // These mutate the SYSTEM-DEFAULT tier (tenant_id IS NULL) and MUST be the
    // mutation-safe mirror-image of the tenant methods: they never touch tenant
    // overrides, and the tenant methods never touch system defaults.
    // ------------------------------------------------------------------

    [Test]
    public async Task UpsertSystemDefault_UpdatesSeededRow_BumpsVersion_StampsAdmin()
    {
        var store = NewStore();
        var admin = Guid.NewGuid();
        var seededBody = ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire);

        await store.UpsertSystemDefaultAsync(Role, Action, "ADMIN-MANAGED DEFAULT", enabled: true, admin, default);

        await using var db = NewContext();
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire);

        row.Body.Should().Be("ADMIN-MANAGED DEFAULT").And.NotBe(seededBody);
        row.Version.Should().Be(2, "Version bumps when editing the seeded system default");
        row.UpdatedBy.Should().Be(admin);
        row.CreatedBy.Should().Be(admin,
            "the seeded row had a null creator; the first admin edit stamps CreatedBy");
        row.TenantId.Should().BeNull("it remains a system default");

        // Resolution (no tenant override) returns the admin-managed body.
        var resolved = await store.ResolveAsync(Guid.NewGuid(), Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().Be("ADMIN-MANAGED DEFAULT");
    }

    [Test]
    public async Task UpsertSystemDefault_InsertsRow_WhenNoSystemDefaultExists()
    {
        var store = NewStore();
        var admin = Guid.NewGuid();

        // Remove the seeded system default first, then upsert fresh.
        await store.DeleteSystemDefaultAsync(Role, Action, admin, default);
        await store.UpsertSystemDefaultAsync(Role, Action, "FRESH-DEFAULT", enabled: true, admin, default);

        await using var db = NewContext();
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire);
        row.Body.Should().Be("FRESH-DEFAULT");
        row.Version.Should().Be(1, "a freshly-inserted system default starts at Version 1");
        row.CreatedBy.Should().Be(admin);
        row.UpdatedBy.Should().Be(admin);
    }

    [Test]
    public async Task UpsertSystemDefault_RejectsEmptyBody()
    {
        var store = NewStore();

        var act = async () =>
            await store.UpsertSystemDefaultAsync(Role, Action, "  ", enabled: true, Guid.NewGuid(), default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task DeleteSystemDefault_RemovesSystemDefault()
    {
        var store = NewStore();
        var admin = Guid.NewGuid();

        await store.DeleteSystemDefaultAsync(Role, Action, admin, default);

        await using var db = NewContext();
        (await db.Conventions.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire))
            .Should().Be(0, "the system default is deleted");

        // With no system default and no tenant override, resolution fails loud.
        var act = async () => await store.ResolveAsync(Guid.NewGuid(), Role, Action, default);
        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("CONVENTION_NOT_FOUND");
    }

    [Test]
    public async Task ResetSystemDefault_ReappliesCodeBaseline_OverAdminEdit()
    {
        var store = NewStore();
        var admin = Guid.NewGuid();
        var baseline = ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire);

        // Admin edits the system default away from the baseline...
        await store.UpsertSystemDefaultAsync(Role, Action, "DRIFTED ADMIN BODY", enabled: true, admin, default);
        await using (var verify = NewContext())
        {
            (await verify.Conventions.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire))
                .Body.Should().Be("DRIFTED ADMIN BODY");
        }

        // ...then resets it back to the code baseline.
        await store.ResetSystemDefaultAsync(Role, Action, admin, default);

        await using var db = NewContext();
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire);
        row.Body.Should().Be(baseline, "reset restores the ConventionSeedSpecs code baseline");
        row.Version.Should().Be(3, "upsert(v2) → reset-as-upsert(v3) both bump Version");
    }

    [Test]
    public async Task ResetSystemDefault_NonTaxonomyCell_ThrowsTammaError()
    {
        var store = NewStore();

        // developer does not own 'deploy' (devops-only) → not a taxonomy cell,
        // so there is no code baseline to reset to.
        var act = async () =>
            await store.ResetSystemDefaultAsync(Role, UnseededAction, Guid.NewGuid(), default);

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("CONVENTION_NOT_A_TAXONOMY_CELL");
    }

    // -- Mutation safety: system-default ops never touch tenant overrides ------

    [Test]
    public async Task UpsertSystemDefault_DoesNotTouchTenantOverride()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-BODY", enabled: true, Guid.NewGuid(), default);

        await store.UpsertSystemDefaultAsync(Role, Action, "NEW-SYSTEM-BODY", enabled: true, Guid.NewGuid(), default);

        await using var db = NewContext();
        var tenantRow = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.Role == RoleWire && c.Action == ActionWire);
        tenantRow.Body.Should().Be("TENANT-BODY", "tenant override is untouched by a system-default upsert");
        tenantRow.Version.Should().Be(1);

        // The tenant override still wins resolution.
        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.Tenant);
        resolved.Body.Should().Be("TENANT-BODY");
    }

    [Test]
    public async Task DeleteSystemDefault_DoesNotTouchTenantOverride()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-SURVIVES", enabled: true, Guid.NewGuid(), default);

        await store.DeleteSystemDefaultAsync(Role, Action, Guid.NewGuid(), default);

        await using var db = NewContext();
        (await db.Conventions.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == tenantId && c.Role == RoleWire && c.Action == ActionWire))
            .Should().Be(1, "tenant override survives a system-default delete");
        (await db.Conventions.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire))
            .Should().Be(0, "only the system default was deleted");

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.Tenant);
        resolved.Body.Should().Be("TENANT-SURVIVES");
    }

    [Test]
    public async Task ResetSystemDefault_DoesNotTouchTenantOverride()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        await store.UpsertAsync(tenantId, Role, Action, "TENANT-KEEP", enabled: true, Guid.NewGuid(), default);

        await store.ResetSystemDefaultAsync(Role, Action, Guid.NewGuid(), default);

        await using var db = NewContext();
        var tenantRow = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == tenantId && c.Role == RoleWire && c.Action == ActionWire);
        tenantRow.Body.Should().Be("TENANT-KEEP", "tenant override is untouched by a system-default reset");
        tenantRow.Version.Should().Be(1);
    }

    // -- Mutation safety (mirror): tenant ops never touch system defaults ------
    // (Upsert/Delete tenant-vs-system already covered above; this asserts the
    //  reverse direction explicitly against the system-default ROW VALUES.)

    [Test]
    public async Task TenantUpsertAndDelete_DoNotTouchAdminManagedSystemDefault()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        // Admin manages the system default to a known value first.
        await store.UpsertSystemDefaultAsync(Role, Action, "ADMIN-MANAGED", enabled: true, Guid.NewGuid(), default);

        // A full tenant override lifecycle must leave that system default intact.
        await store.UpsertAsync(tenantId, Role, Action, "TENANT-V1", enabled: true, Guid.NewGuid(), default);
        await store.DeleteAsync(tenantId, Role, Action, Guid.NewGuid(), default);

        await using var db = NewContext();
        var systemRow = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire);
        systemRow.Body.Should().Be("ADMIN-MANAGED",
            "tenant upsert/delete must never mutate the admin-managed system default");
        systemRow.Version.Should().Be(2, "system default version unchanged by tenant ops (still the admin's v2)");

        // After the tenant override is gone, resolution falls back to the
        // admin-managed system default (not the original code baseline).
        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().Be("ADMIN-MANAGED");
    }
}
