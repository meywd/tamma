using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Data;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Story 27-16 (AC1 + AC3 + AC4) — <see cref="ConventionStoreSeeder"/> behaviour
/// against a real Postgres testcontainer (EF InMemory doesn't honour
/// <c>NULLS NOT DISTINCT</c> or DB-side defaults, so the only faithful path is a
/// container — same pattern as <see cref="ConventionStoreMigrationTests"/>).
///
/// <para>The seeder is INSERT-MISSING-ONLY (product decision 2026-05-25):
/// convention system defaults are DB-managed at runtime via admin CRUD, so the
/// seeder only initial-populates absent taxonomy cells and MUST NOT revert an
/// admin-edited system default on re-run.</para>
///
/// <para>Asserts: first run populates EXACTLY the taxonomy cells; every seeded
/// body is non-empty; system-default rows are <c>tenant_id IS NULL</c>; the
/// seeder is idempotent (re-run = no-op, zero writes); an admin-edited
/// system-default row is PRESERVED (not reverted) on re-run; tenant overrides
/// are never touched.</para>
/// </summary>
[TestFixture]
public class ConventionStoreSeederTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private NpgsqlDataSource _dataSource = null!;

    private static int ExpectedCellCount =>
        RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("convention_seeder_test")
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

        // Run the full tenant migration graph so we seed against the live schema.
        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(_connectionString);

        _dataSource = NpgsqlDataSource.Create(_connectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task ClearTable()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE TABLE conventions;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private TenantDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;
        return new TenantDbContext(options);
    }

    private ConventionStoreSeeder NewSeeder() =>
        new(
            // resolver path is exercised by the SeedAsync(ct) overload elsewhere;
            // here we drive the SeedAsync(TenantDbContext, ct) seam directly.
            new StubTenantConnectionResolver(_dataSource),
            TimeProvider.System,
            NullLogger<ConventionStoreSeeder>.Instance);

    [Test]
    public async Task FirstRun_SeedsExactlyTheTaxonomyCells_AllSystemDefaults()
    {
        var seeder = NewSeeder();
        await using var db = NewContext();

        var result = await seeder.SeedAsync(db, default);

        result.Inserted.Should().Be(ExpectedCellCount);
        result.Unchanged.Should().Be(0);

        await using var verify = NewContext();
        var rows = await verify.Conventions.ToListAsync();
        rows.Should().HaveCount(ExpectedCellCount);

        // Every seeded row is a system default (tenant_id IS NULL) with a
        // non-empty body, and matches a real taxonomy cell.
        var taxonomy = RolePhaseMap.EligibleActions
            .SelectMany(kv => kv.Value.Select(a => (Role: kv.Key.ToWire(), Action: a.ToWire())))
            .ToHashSet();

        rows.Should().AllSatisfy(r =>
        {
            r.TenantId.Should().BeNull("system defaults carry tenant_id IS NULL");
            r.Body.Should().NotBeNullOrWhiteSpace("every seeded body must be non-empty (AC1)");
            r.Enabled.Should().BeTrue();
            r.Version.Should().Be(1);
            taxonomy.Should().Contain((r.Role, r.Action));
        });

        rows.Select(r => (r.Role, r.Action)).ToHashSet()
            .Should().BeEquivalentTo(taxonomy, "seeded cells == taxonomy cells");
    }

    [Test]
    public async Task Rerun_IsNoOp()
    {
        var seeder = NewSeeder();

        await using (var db1 = NewContext())
        {
            await seeder.SeedAsync(db1, default);
        }

        ConventionStoreSeeder.SeedResult second;
        await using (var db2 = NewContext())
        {
            second = await seeder.SeedAsync(db2, default);
        }

        second.Inserted.Should().Be(0);
        second.Unchanged.Should().Be(ExpectedCellCount);

        await using var verify = NewContext();
        (await verify.Conventions.CountAsync())
            .Should().Be(ExpectedCellCount, "re-run must not duplicate rows");
    }

    [Test]
    public async Task AdminEditedSystemDefault_IsPreserved_NotReverted_OnRerun()
    {
        // Product decision (2026-05-25): convention system defaults are
        // DB-managed at runtime via admin CRUD. The seeder is initial-populate
        // ONLY — it must NEVER revert an admin's edit to an existing
        // system-default row on a later deploy / re-run.
        var seeder = NewSeeder();

        await using (var db1 = NewContext())
        {
            await seeder.SeedAsync(db1, default);
        }

        // Simulate a platform admin editing a system default via the CRUD
        // surface (Story 27-10): change the body, bump version, toggle Enabled.
        Guid id;
        int versionAfterEdit;
        const string adminBody = "ADMIN-EDITED system default — must survive re-seed";
        await using (var mutate = NewContext())
        {
            var row = await mutate.Conventions.FirstAsync(c => c.TenantId == null);
            id = row.Id;
            row.Body = adminBody;
            row.Version += 1; // admin CRUD bumps version
            row.Enabled = false;
            versionAfterEdit = row.Version;
            await mutate.SaveChangesAsync();
        }

        ConventionStoreSeeder.SeedResult result;
        await using (var db2 = NewContext())
        {
            result = await seeder.SeedAsync(db2, default);
        }

        // The seeder performs NO updates — every cell already exists, so the
        // re-run is a pure no-op (zero inserts).
        result.Inserted.Should().Be(0, "all cells already present → nothing to insert");
        result.Unchanged.Should().Be(ExpectedCellCount,
            "every existing system default is left untouched");

        await using var verify = NewContext();
        var preserved = await verify.Conventions.FirstAsync(c => c.Id == id);
        preserved.Body.Should().Be(adminBody,
            "the admin-edited body must be PRESERVED, never reverted to the code baseline");
        preserved.Body.Should().NotBe(
            ConventionSeedSpecs.DefaultBody(preserved.Role, preserved.Action),
            "the seeder must not re-apply the code-baseline body over an admin edit");
        preserved.Version.Should().Be(versionAfterEdit,
            "the seeder must not bump Version on an existing row");
        preserved.Enabled.Should().BeFalse("the admin's Enabled toggle survives re-seed");
    }

    [Test]
    public async Task TenantOverrideRows_AreNeverTouchedByTheSeeder()
    {
        var tenantId = Guid.NewGuid();

        // Pre-seed a tenant override for a cell the seeder also covers.
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO conventions ("TenantId", "Role", "Action", "Body")
                VALUES (@tid, 'developer', 'implement-feature', 'tenant body');
                """, conn);
            cmd.Parameters.AddWithValue("tid", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        var seeder = NewSeeder();
        await using (var db = NewContext())
        {
            var result = await seeder.SeedAsync(db, default);
            // The tenant override is not a system default, so it is neither
            // counted nor overwritten — a fresh system default is inserted
            // alongside it.
            result.Inserted.Should().Be(ExpectedCellCount);
        }

        await using var verify = NewContext();
        var tenantRow = await verify.Conventions
            .FirstAsync(c => c.TenantId == tenantId
                && c.Role == "developer" && c.Action == "implement-feature");
        tenantRow.Body.Should().Be("tenant body", "the tenant override is untouched");

        var systemRow = await verify.Conventions
            .FirstAsync(c => c.TenantId == null
                && c.Role == "developer" && c.Action == "implement-feature");
        systemRow.Body.Should().NotBe("tenant body");
        systemRow.Body.Should().Be(ConventionSeedSpecs.DefaultBody("developer", "implement-feature"));
    }

    [Test]
    public async Task SeedAsync_ResolverOverload_SeedsViaTheStartupPath()
    {
        // Exercise the production startup path: resolve the data source from
        // ITenantConnectionResolver and build the context internally.
        var seeder = NewSeeder();

        var result = await seeder.SeedAsync(default);

        result.Inserted.Should().Be(ExpectedCellCount);

        await using var verify = NewContext();
        (await verify.Conventions.CountAsync(c => c.TenantId == null))
            .Should().Be(ExpectedCellCount);
    }
}
