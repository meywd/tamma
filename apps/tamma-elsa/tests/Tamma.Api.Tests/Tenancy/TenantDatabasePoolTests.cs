using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Secrets;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Unified-tenancy Phase 2 Task 1 — <see cref="TenantDatabasePool"/> is the
/// accessor over the <c>tenant_databases</c> registry: it decrypts a pool
/// row's AES-GCM admin-connection envelope, runs DDL on the TARGET cluster
/// (roles are cluster-scoped, so CREATE ROLE / SCHEMA / GRANT must execute
/// on the assigned row's cluster — never blindly on the central one), and
/// mints tenant-facing connection strings carrying
/// <c>Search Path=t_&lt;hex&gt;</c>.
///
/// <para>Harness mirrors <see cref="SchemaPerTenantMigrationTests"/>: one
/// throwaway Postgres container plays the pool-member cluster. The
/// control-plane registry row lives in an EF in-memory context (the pool
/// only ever reads the row; the interesting behaviour — decrypt + fresh
/// Npgsql connections against the row's cluster — is exercised for real).
/// Key material is an explicit base64 32-byte KEK: the row's envelope is
/// written with <see cref="TenantSecretProtector"/> and read back through
/// the production <see cref="AesGcmConnectionStringDecryptor"/> over a
/// <see cref="KekProvider"/> configured with the same key.</para>
/// </summary>
[TestFixture]
public class TenantDatabasePoolTests
{
    private static readonly byte[] Kek = BuildKek(seed: 7);

    private PostgreSqlContainer _postgres = null!;
    private string _adminConnectionString = null!;
    private Guid _databaseId;
    private string _cpDbName = null!;
    private IConnectionStringDecryptor _decryptor = null!;

    private static byte[] BuildKek(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tenant_pool_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _adminConnectionString = _postgres.GetConnectionString();

        // Registry row: the container is pool member "shared-test-1". Its
        // admin connection string is sealed with the explicit test KEK —
        // exactly what the Phase 4 admin CRUD (or the bootstrap seeder)
        // writes in production.
        _databaseId = Guid.NewGuid();
        _cpDbName = $"tenant_pool_cp_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString);
        var protector = new TenantSecretProtector(Kek);
        await using (var cp = CreateCpContext(_cpDbName))
        {
            cp.TenantDatabases.Add(new TenantDatabase
            {
                Id = _databaseId,
                Label = "shared-test-1",
                Host = adminBuilder.Host ?? "localhost",
                Port = adminBuilder.Port,
                AdminConnectionStringEncrypted = protector.Encrypt(_adminConnectionString),
                PlacementClass = "shared",
                TierEligibility = ["free", "team"],
                TenantCount = 0,
                Status = "active",
                KekVersion = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await cp.SaveChangesAsync();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(Kek),
            })
            .Build();
        _decryptor = new AesGcmConnectionStringDecryptor(
            new KekProvider(config, NullLogger<KekProvider>.Instance),
            NullLogger<AesGcmConnectionStringDecryptor>.Instance);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _postgres.DisposeAsync();
    }

    private static ControlPlaneDbContext CreateCpContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ControlPlaneDbContext(options);
    }

    private TenantDatabasePool CreatePool() => new(
        new InMemoryCpFactory(_cpDbName),
        _decryptor,
        NullLogger<TenantDatabasePool>.Instance);

    [Test]
    public async Task GetAdminConnectionString_DecryptsEnvelope()
    {
        var pool = CreatePool();

        var result = await pool.GetAdminConnectionStringAsync(_databaseId);

        result.Should().Be(_adminConnectionString,
            "the accessor must round-trip the AES-GCM envelope back to the admin connection string");
    }

    [Test]
    public async Task GetAdminConnectionString_UnknownRow_ThrowsWithDatabaseId()
    {
        var pool = CreatePool();
        var missing = Guid.NewGuid();

        var act = async () => await pool.GetAdminConnectionStringAsync(missing);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*{missing}*");
    }

    [Test]
    public async Task BuildTenantConnectionString_TargetsRowDatabaseWithSearchPath()
    {
        var pool = CreatePool();
        var tenantId = Guid.NewGuid();
        var roleName = TenantNaming.RoleName(tenantId);
        var schemaName = TenantNaming.SchemaName(tenantId);

        var cs = await pool.BuildTenantConnectionStringAsync(
            _databaseId, roleName, "s3cret-pw", schemaName);

        var adminBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString);
        var built = new NpgsqlConnectionStringBuilder(cs);
        built.Database.Should().Be(adminBuilder.Database,
            "the pool row's database IS the target — schema-per-tenant shares the row's DB");
        built.Username.Should().Be(roleName);
        built.Password.Should().Be("s3cret-pw");
        built.SearchPath.Should().Be(schemaName,
            "the schema is carried ONLY by the connection string's Search Path");
        built.Host.Should().Be(adminBuilder.Host, "Host comes from the pool row's admin string");
        built.Port.Should().Be(adminBuilder.Port);
        built.ApplicationName.Should().Be($"tamma-tenant;schema={schemaName}");
        built.IncludeErrorDetail.Should().BeFalse(
            "admin-only fields must be dropped from the tenant-facing string");
    }

    [Test]
    public async Task ExecuteOn_RunsOnTargetCluster()
    {
        var pool = CreatePool();

        await pool.ExecuteOnAsync(
            _databaseId,
            "CREATE TABLE IF NOT EXISTS pool_smoke (id int PRIMARY KEY)");

        // Verify against the container directly — the accessor must have
        // executed on the pool row's cluster, not anywhere else.
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'pool_smoke'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1, "ExecuteOnAsync must run DDL on the pool row's database");
    }

    [Test]
    public async Task GetDatabaseName_ParsesRowTargetDatabase()
    {
        // Task 3 interface growth (pre-authorized by the plan):
        // CreateSchemaAsync needs the placement row's database name for
        // GRANT CONNECT ON DATABASE / ALTER ROLE ... IN DATABASE.
        var pool = CreatePool();

        (await pool.GetDatabaseNameAsync(_databaseId)).Should().Be(
            "tenant_pool_test",
            "the target database name is parsed from the row's decrypted admin connection string");
    }

    [Test]
    public async Task RoleExistsOn_ProbesTargetClusterPgRoles()
    {
        var pool = CreatePool();

        (await pool.RoleExistsOnAsync(_databaseId, "tamma"))
            .Should().BeTrue("the container's login role exists in pg_roles");
        (await pool.RoleExistsOnAsync(_databaseId, "tamma_tenant_nonexistent"))
            .Should().BeFalse();
    }

    [Test]
    public async Task SchemaExistsOn_ProbesTargetDatabaseSchemata()
    {
        // Phase 2 Task 5 interface growth — the delete path's backup
        // step probes the schema before invoking pg_dump so a replay
        // after a successful drop skips cleanly.
        var pool = CreatePool();
        var schemaName = $"t_{Guid.NewGuid():N}";

        (await pool.SchemaExistsOnAsync(_databaseId, schemaName))
            .Should().BeFalse("the schema has not been created yet");

        await pool.ExecuteOnAsync(_databaseId, $"CREATE SCHEMA \"{schemaName}\"");

        (await pool.SchemaExistsOnAsync(_databaseId, schemaName))
            .Should().BeTrue("the schema now exists on the pool row's database");

        await pool.ExecuteOnAsync(_databaseId, $"DROP SCHEMA \"{schemaName}\" CASCADE");

        (await pool.SchemaExistsOnAsync(_databaseId, schemaName))
            .Should().BeFalse("the probe must observe the drop");
    }

    [Test]
    public async Task GetConnectionInfo_ExposesRowAdminPartsTargetingRowDatabase()
    {
        // Phase 2 Task 5 interface growth (pre-authorized by the plan) —
        // pg_dump needs the connection parts discretely; the Database is
        // the pool row's OWN database (schema-per-tenant shares it).
        var pool = CreatePool();
        var adminBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString);

        var info = await pool.GetConnectionInfoAsync(_databaseId);

        info.Host.Should().Be(adminBuilder.Host);
        info.Port.Should().Be(adminBuilder.Port);
        info.Username.Should().Be(adminBuilder.Username);
        info.Password.Should().Be(adminBuilder.Password,
            "pg_dump receives the password via PGPASSWORD — the caller needs it decrypted");
        info.Database.Should().Be("tenant_pool_test",
            "the dump targets the pool row's database, scoped by --schema");
    }

    private sealed class InMemoryCpFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName;
        public InMemoryCpFactory(string dbName) => _dbName = dbName;

        public ControlPlaneDbContext CreateDbContext() => CreateCpContext(_dbName);

        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}

/// <summary>
/// Unified-tenancy Phase 2 Task 1 — <see cref="TenantDatabasesSeeder"/>
/// bootstraps the central database as pool member #1 (Label "central",
/// shared, all tiers) so dev/self-host and SaaS run the same placement
/// code path. Mirrors <see cref="Tamma.Data.Seeders.PlansSeeder"/>:
/// insert-missing-only, stable ID, no-op on re-run. EF in-memory provider
/// suffices — the seeder is Any + Add + SaveChanges.
/// </summary>
[TestFixture]
public class TenantDatabasesSeederTests
{
    private const string AdminConnectionString =
        "Host=db.internal;Port=5433;Database=tamma;Username=tamma;Password=hunter2";

    private static readonly byte[] Kek = BuildKek(seed: 21);

    private static byte[] BuildKek(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static ControlPlaneDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ControlPlaneDbContext(options);
    }

    private static ITenantConnectionStringProtector CreateProtector() =>
        new TenantSecretProtectorAdapter(new TenantSecretProtector(Kek));

    [Test]
    public async Task SeedAsync_InsertsCentralRowOnce()
    {
        var dbName = nameof(SeedAsync_InsertsCentralRowOnce);
        await using var ctx = CreateContext(dbName);

        await TenantDatabasesSeeder.SeedAsync(ctx, AdminConnectionString, CreateProtector());

        var rows = await ctx.TenantDatabases.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(1);

        var central = rows[0];
        central.Id.Should().Be(TenantDatabasesSeeder.CentralDatabaseId,
            "the central row's ID is a stable seed FK target");
        central.Label.Should().Be("central");
        central.Host.Should().Be("db.internal");
        central.Port.Should().Be(5433);
        central.PlacementClass.Should().Be("shared");
        central.TierEligibility.Should().BeEquivalentTo("free", "team", "enterprise");
        central.TenantCapacity.Should().BeNull("the central shared pool member is unbounded");
        central.TenantCount.Should().Be(0);
        central.Status.Should().Be("active");
        central.KekVersion.Should().Be(1);

        // The envelope must decrypt back to the admin connection string —
        // sealed under the SAME protector the lifecycle activities use.
        new TenantSecretProtector(Kek)
            .Decrypt(central.AdminConnectionStringEncrypted)
            .Should().Be(AdminConnectionString);
    }

    [Test]
    public async Task SeedAsync_NoopWhenRowsExist()
    {
        var dbName = nameof(SeedAsync_NoopWhenRowsExist);
        await using (var first = CreateContext(dbName))
        {
            await TenantDatabasesSeeder.SeedAsync(first, AdminConnectionString, CreateProtector());
        }

        // Re-run with a DIFFERENT admin string — the existing row must
        // win (insert-missing-only, never update).
        await using var second = CreateContext(dbName);
        await TenantDatabasesSeeder.SeedAsync(
            second, "Host=other;Database=other;Username=x;Password=y", CreateProtector());

        var rows = await second.TenantDatabases.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(1, "second SeedAsync must short-circuit on EXISTS");
        rows[0].Host.Should().Be("db.internal", "the seeder never updates an existing row");
    }
}
