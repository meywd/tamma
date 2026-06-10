using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
/// Unified-tenancy Phase 2 Task 3 — <see cref="TenantProvisioningService"/>
/// is the ONE step engine (placement → role → schema → conn-string →
/// migrate → encrypt+persist → active) shared by the SaaS workflow
/// activities and the single-user middleware.
///
/// <para><b>The phase proof</b> is
/// <see cref="Provision_PersonalTenant_EndToEnd_ResolvableByRealResolver"/>:
/// after one <c>ProvisionAsync</c> call against a real Postgres container,
/// (a) the REAL <see cref="LruPooledTenantConnectionResolver"/> — not the
/// stub — decrypts the minted envelope and serves a working
/// <see cref="TenantDbContext"/> whose <c>Search Path</c> lands in the
/// tenant schema, and (b) the minted tenant ROLE is genuinely isolated:
/// it cannot read <c>public.tenants</c> nor create tables in
/// <c>public</c>, while DDL inside its own schema succeeds. The isolation
/// assertions run on a connection opened with the MINTED tenant
/// credentials — never the admin/superuser connection.</para>
///
/// <para>Harness mirrors <see cref="TenantDatabasePoolTests"/>: one
/// throwaway Postgres container is the pool-member cluster; the control
/// plane is EF in-memory (fresh DB name per test); key material is an
/// explicit base64 32-byte KEK used BOTH to encrypt (protector) and to
/// decrypt (the resolver's <see cref="AesGcmConnectionStringDecryptor"/>).</para>
/// </summary>
[TestFixture]
public class TenantProvisioningServiceTests
{
    private static readonly byte[] Kek = BuildKek(seed: 42);

    private PostgreSqlContainer _postgres = null!;
    private string _adminConnectionString = null!;
    private IConnectionStringDecryptor _decryptor = null!;
    private ITenantConnectionStringProtector _protector = null!;

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
            .WithDatabase("tenant_provisioning_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _adminConnectionString = _postgres.GetConnectionString();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(Kek),
            })
            .Build();
        _decryptor = new AesGcmConnectionStringDecryptor(
            new KekProvider(config, NullLogger<KekProvider>.Instance),
            NullLogger<AesGcmConnectionStringDecryptor>.Instance);
        _protector = new TenantSecretProtectorAdapter(new TenantSecretProtector(Kek));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _postgres.DisposeAsync();
    }

    // ── harness ───────────────────────────────────────────────────────

    private static ControlPlaneDbContext CreateCpContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ControlPlaneDbContext(options);
    }

    /// <summary>
    /// Seeds the control plane the way API startup does: production
    /// plans (<see cref="PlansSeeder"/>) + the central bootstrap pool row
    /// (<see cref="TenantDatabasesSeeder"/>) pointing at the test
    /// container, then one tenant row shaped like the personal tenant the
    /// single-user middleware creates (free plan, no Status / envelope /
    /// placement).
    /// </summary>
    private async Task<Guid> SeedControlPlaneAsync(string cpDbName, string plan = "free")
    {
        var tenantId = Guid.NewGuid();
        await using var cp = CreateCpContext(cpDbName);
        await PlansSeeder.SeedAsync(cp);
        await TenantDatabasesSeeder.SeedAsync(cp, _adminConnectionString, _protector);
        cp.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Provisioning Test Tenant",
            Slug = $"prov-{tenantId:N}"[..20],
            Plan = plan,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await cp.SaveChangesAsync();
        return tenantId;
    }

    private TenantProvisioningService CreateService(
        IDbContextFactory<ControlPlaneDbContext> factory) => new(
        new TenantPlacementService(factory, NullLogger<TenantPlacementService>.Instance),
        new TenantDatabasePool(factory, _decryptor, NullLogger<TenantDatabasePool>.Instance),
        factory,
        new EfTenantDbMigrator(),
        _protector,
        NullLogger<TenantProvisioningService>.Instance);

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ── THE phase proof ───────────────────────────────────────────────

    [Test]
    public async Task Provision_PersonalTenant_EndToEnd_ResolvableByRealResolver()
    {
        var cpDbName = nameof(Provision_PersonalTenant_EndToEnd_ResolvableByRealResolver);
        var tenantId = await SeedControlPlaneAsync(cpDbName);
        var factory = new InMemoryCpFactory(cpDbName);

        await CreateService(factory).ProvisionAsync(tenantId);

        var schema = TenantNaming.SchemaName(tenantId);
        var role = TenantNaming.RoleName(tenantId);

        // ── cluster state: schema (with migrated tables) + role exist ──
        (await ScalarAsync(_adminConnectionString,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{schema}' "
            + "AND table_name IN ('conventions', '__TenantMigrationsHistory')"))
            .Should().Be(2L,
                "tenant migrations must land in the t_<hex> schema (conventions baseline "
                + "+ in-schema __TenantMigrationsHistory)");
        (await ScalarAsync(_adminConnectionString,
            $"SELECT count(*) FROM pg_roles WHERE rolname = '{role}'"))
            .Should().Be(1L, "the tenant login role must exist on the placement row's cluster");

        // ── control-plane state: envelope + kek + status + placement ──
        byte[] envelope;
        await using (var cp = CreateCpContext(cpDbName))
        {
            var tenant = await cp.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            var entry = cp.Entry(tenant);
            envelope = (byte[]?)entry.Property("EncryptedConnectionString").CurrentValue
                ?? throw new InvalidOperationException("envelope not persisted");
            envelope.Should().NotBeEmpty("ProvisionAsync must mint + persist the envelope");
            ((short?)entry.Property("KekVersion").CurrentValue).Should().Be((short)1);
            entry.Property<string?>("Status").CurrentValue.Should().Be("active");
            entry.Property<Guid?>("DatabaseId").CurrentValue
                .Should().Be(TenantDatabasesSeeder.CentralDatabaseId,
                    "the free tenant lands on the central bootstrap pool row");
            entry.Property<string?>("SchemaName").CurrentValue.Should().Be(schema);

            (await cp.TenantDatabases.SingleAsync(
                    d => d.Id == TenantDatabasesSeeder.CentralDatabaseId))
                .TenantCount.Should().Be(1, "placement claims one slot on the pool row");
        }

        // ── leg 1: the REAL resolver chain (decrypt → pool → search_path) ──
        using var metrics = new TenantConnectionPoolMetrics();
        await using (var resolver = new LruPooledTenantConnectionResolver(
            factory,
            _decryptor,
            metrics,
            Options.Create(new TenantConnectionPoolOptions()),
            NullLogger<LruPooledTenantConnectionResolver>.Instance))
        {
            var tenantCtxFactory = new TenantDbContextFactory(resolver);
            await using var ctx = await tenantCtxFactory.CreateAsync(tenantId);
            (await ctx.AgentConfigs.ToListAsync()).Should().BeEmpty(
                "the production LruPooledTenantConnectionResolver must decrypt the minted "
                + "envelope and serve a TenantDbContext that reads the tenant schema "
                + "end-to-end — this is the Phase 2 proof ahead of the Phase 3 stub removal");
        }

        // ── leg 2: role isolation, asserted AS THE TENANT ROLE ──
        // Simulate the central-DB layout where CP tables live in public on
        // the same database the tenant schema shares.
        await ExecAsync(_adminConnectionString,
            "CREATE TABLE IF NOT EXISTS public.tenants (id uuid PRIMARY KEY)");

        var tenantConnectionString = new TenantSecretProtector(Kek).Decrypt(envelope);
        var minted = new NpgsqlConnectionStringBuilder(tenantConnectionString);
        minted.Username.Should().Be(role, "the envelope must seal the tenant role's credentials");
        minted.SearchPath.Should().Be(schema);

        (await ScalarAsync(tenantConnectionString, "SELECT 1"))
            .Should().Be(1, "the tenant role must be able to connect and run queries");

        await ExecAsync(tenantConnectionString, "CREATE TABLE iso_probe (id int PRIMARY KEY)");
        (await ScalarAsync(_adminConnectionString,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{schema}' "
            + "AND table_name = 'iso_probe'"))
            .Should().Be(1L,
                "unqualified DDL by the tenant role must land inside its OWN schema "
                + "(search_path), proving the role owns/creates there");

        var readPublic = async () =>
            await ScalarAsync(tenantConnectionString, "SELECT count(*) FROM public.tenants");
        (await readPublic.Should().ThrowAsync<PostgresException>(
                "the tenant role must NOT read control-plane tables in public"))
            .Which.SqlState.Should().Be("42501", "expected: permission denied for table tenants");

        var createInPublic = async () =>
            await ExecAsync(tenantConnectionString, "CREATE TABLE public.iso_smuggle (id int)");
        (await createInPublic.Should().ThrowAsync<PostgresException>(
                "the tenant role must NOT create objects outside its schema"))
            .Which.SqlState.Should().Be("42501", "expected: permission denied for schema public");
    }

    // ── idempotency + failure modes ───────────────────────────────────

    [Test]
    public async Task Provision_IsIdempotent_SecondRunSkipsReencryptAndKeepsPoolCount()
    {
        var cpDbName = nameof(Provision_IsIdempotent_SecondRunSkipsReencryptAndKeepsPoolCount);
        var tenantId = await SeedControlPlaneAsync(cpDbName);
        var factory = new InMemoryCpFactory(cpDbName);
        var service = CreateService(factory);

        await service.ProvisionAsync(tenantId);
        var firstEnvelope = await ReadEnvelopeAsync(cpDbName, tenantId);

        // Second run: role exists (idempotent-skip, password unrecoverable)
        // but the envelope from run #1 is present — the pipeline must
        // complete WITHOUT re-encrypting (ShouldSkipReencrypt-equivalent
        // guard: fresh ciphertext under the same KEK would invalidate
        // consumers that snapshot the envelope).
        await service.ProvisionAsync(tenantId);

        var secondEnvelope = await ReadEnvelopeAsync(cpDbName, tenantId);
        secondEnvelope.Should().Equal(firstEnvelope,
            "a re-run with no fresh password must keep the stored envelope byte-identical");

        await using var cp = CreateCpContext(cpDbName);
        var tenant = await cp.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        cp.Entry(tenant).Property<string?>("Status").CurrentValue.Should().Be("active");
        (await cp.TenantDatabases.SingleAsync(
                d => d.Id == TenantDatabasesSeeder.CentralDatabaseId))
            .TenantCount.Should().Be(1, "placement is idempotent — no double-count");
    }

    [Test]
    public async Task Provision_RoleExistsWithoutEnvelope_ThrowsDropRoleGuidance()
    {
        var cpDbName = nameof(Provision_RoleExistsWithoutEnvelope_ThrowsDropRoleGuidance);
        var tenantId = await SeedControlPlaneAsync(cpDbName);
        var factory = new InMemoryCpFactory(cpDbName);

        // A prior partial run created the role but never persisted the
        // envelope — its password is unrecoverable by design.
        var quotedRole = TenantNaming.Quote(TenantNaming.RoleName(tenantId));
        await ExecAsync(_adminConnectionString,
            $"CREATE ROLE {quotedRole} WITH LOGIN PASSWORD 'lost-forever' "
            + "NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION");

        var act = async () => await CreateService(factory).ProvisionAsync(tenantId);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "an existing role with no stored envelope is unrecoverable"))
            .WithMessage("*DROP ROLE*",
                "the error must carry the operator runbook guidance (DROP ROLE + retry)");
    }

    private static async Task<byte[]> ReadEnvelopeAsync(string cpDbName, Guid tenantId)
    {
        await using var cp = CreateCpContext(cpDbName);
        var tenant = await cp.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        return (byte[]?)cp.Entry(tenant).Property("EncryptedConnectionString").CurrentValue
            ?? throw new InvalidOperationException("envelope not persisted");
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
