using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Secrets;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;
// Tamma.Api.Services.Provisioning also declares a (legacy Cranl)
// ITenantConnectionResolver — alias the pool-cache abstraction the move
// engine actually evicts.
using IPoolResolver = Tamma.Data.Abstractions.ITenantConnectionResolver;

namespace Tamma.Api.Tests.Tenancy;

/// <summary>
/// Unified-tenancy Phase 4 Task 3 — orchestration unit suite for
/// <see cref="TenantMoveService"/>. Mirrors the recording-fake style of
/// <c>BackupTenantDatabaseActivityTests</c> / <c>DropTenantSchemaActivityTests</c>:
/// every observable side effect (pool DDL, pg tool spawns, evictions,
/// provisioning steps, tenant-db verify) lands on one shared timeline so
/// the tests can pin the 10-step order, the same- vs cross-cluster
/// branch, the password-never-in-argv discipline, the validation
/// rejections, and the history-mismatch abort.
/// </summary>
[TestFixture]
public class TenantMoveServiceTests
{
    private static readonly Guid SourceDbId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid TargetDbId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    private const string SourceHost = "src.internal";
    private const int SourcePort = 6432;

    private string _cpDbName = null!;
    private InMemoryCpFactory _factory = null!;
    private List<string> _timeline = null!;
    private RecordingPool _pool = null!;
    private RecordingProvisioning _provisioning = null!;
    private RecordingRunner _runner = null!;
    private RecordingResolver _resolver = null!;
    private FakeTenantDbFactory _tenantDbFactory = null!;

    [SetUp]
    public void SetUp()
    {
        _cpDbName = $"move-cp-{Guid.NewGuid():N}";
        _factory = new InMemoryCpFactory(_cpDbName);
        _timeline = new List<string>();
        _pool = new RecordingPool(_timeline)
        {
            Infos =
            {
                [SourceDbId] = new TenantAdminConnectionInfo(
                    SourceHost, SourcePort, "tamma_provisioner", "admin-src-pw", "srcdb"),
                [TargetDbId] = new TenantAdminConnectionInfo(
                    SourceHost, SourcePort, "tamma_provisioner", "admin-tgt-pw", "tgtdb"),
            },
            Names = { [SourceDbId] = "source", [TargetDbId] = "target" },
        };
        _provisioning = new RecordingProvisioning(_timeline);
        _runner = new RecordingRunner(_timeline);
        _resolver = new RecordingResolver(_timeline);
        _tenantDbFactory = new FakeTenantDbFactory(_timeline);
    }

    private TenantMoveService CreateService() => new(
        _factory,
        _pool,
        _provisioning,
        _runner,
        new Utf8Decryptor(),
        new Utf8Protector(),
        _resolver,
        _tenantDbFactory,
        Options.Create(new TenantMoveOptions()),
        NullLogger<TenantMoveService>.Instance);

    /// <summary>
    /// Seeds plans (real <see cref="PlansSeeder"/> rows so the eligibility
    /// predicate runs against production placement policies), the two pool
    /// rows, and one placed free-tier tenant on the source row.
    /// </summary>
    private async Task<Guid> SeedAsync(
        string tenantStatus = "active",
        string targetHost = SourceHost,
        int targetPort = SourcePort,
        string targetStatus = "active",
        string[]? targetTiers = null,
        bool placed = true,
        Guid? tenantDatabaseId = null)
    {
        var tenantId = Guid.NewGuid();
        await using var cp = await _factory.CreateDbContextAsync();
        await PlansSeeder.SeedAsync(cp);

        cp.TenantDatabases.Add(new TenantDatabase
        {
            Id = SourceDbId,
            Label = "src",
            Host = SourceHost,
            Port = SourcePort,
            AdminConnectionStringEncrypted = new byte[] { 1 },
            PlacementClass = "shared",
            TierEligibility = ["free", "team"],
            TenantCount = 1,
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        cp.TenantDatabases.Add(new TenantDatabase
        {
            Id = TargetDbId,
            Label = "tgt",
            Host = targetHost,
            Port = targetPort,
            AdminConnectionStringEncrypted = new byte[] { 2 },
            PlacementClass = "shared",
            TierEligibility = targetTiers ?? ["free", "team"],
            TenantCount = 0,
            Status = targetStatus,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var schema = TenantNaming.SchemaName(tenantId);
        var role = TenantNaming.RoleName(tenantId);
        var tenant = new Tenant
        {
            Id = tenantId,
            Name = "Move Test Tenant",
            Slug = $"move-{tenantId:N}"[..20],
            Type = "personal",
            Plan = "free",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var entry = cp.Tenants.Add(tenant);
        entry.Property("Status").CurrentValue = tenantStatus;
        entry.Property("EncryptedConnectionString").CurrentValue = Encoding.UTF8.GetBytes(
            $"Host={SourceHost};Port={SourcePort};Database=srcdb;Username={role};"
            + $"Password=tenant-pw;Search Path={schema}");
        entry.Property("KekVersion").CurrentValue = (short)1;
        if (placed)
        {
            entry.Property<string?>("SchemaName").CurrentValue = schema;
            entry.Property<Guid?>("DatabaseId").CurrentValue = tenantDatabaseId ?? SourceDbId;
        }
        await cp.SaveChangesAsync();
        return tenantId;
    }

    private async Task<(string? Status, Guid? DatabaseId, byte[]? Envelope)> ReadTenantAsync(
        Guid tenantId)
    {
        await using var cp = await _factory.CreateDbContextAsync();
        var tenant = await cp.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        var entry = cp.Entry(tenant);
        return (
            entry.Property<string?>("Status").CurrentValue,
            entry.Property<Guid?>("DatabaseId").CurrentValue,
            (byte[]?)entry.Property("EncryptedConnectionString").CurrentValue);
    }

    private static void AssertOrdered(List<string> timeline, params string[] substrings)
    {
        var cursor = -1;
        foreach (var fragment in substrings)
        {
            var index = timeline.FindIndex(cursor + 1, e => e.Contains(fragment));
            index.Should().BeGreaterThan(cursor,
                $"timeline must contain '{fragment}' after position {cursor} — actual: "
                + string.Join(" | ", timeline));
            cursor = index;
        }
    }

    // ── step order + branch behaviour ─────────────────────────────────

    [Test]
    public async Task Move_SameCluster_RunsStepsInOrder_SwapsDatabaseOnly()
    {
        var tenantId = await SeedAsync();
        var schema = TenantNaming.SchemaName(tenantId);
        var role = TenantNaming.RoleName(tenantId);

        await CreateService().MoveAsync(tenantId, TargetDbId);

        // The 10-step order, as observable side effects: drain-evict →
        // dump → (defensive target drop + schema create) → restore →
        // history verify (source, target) → evict + tenant-db verify →
        // drop source schema.
        AssertOrdered(_timeline,
            "evict",
            "run:pg_dump",
            "sql:target:DROP SCHEMA",
            "provision:schema",
            "run:pg_restore",
            "scalar:source",
            "scalar:target",
            "evict",
            "tenantdb:verify",
            "sql:source:DROP SCHEMA");

        // Same cluster — the role step must be skipped (roles are
        // cluster-wide) and no role DDL may hit either row.
        _timeline.Should().NotContain(e => e.Contains("provision:role"));
        _pool.Executed.Should().NotContain(c =>
            c.Sql.Contains("DROP ROLE") || c.Sql.Contains("DROP OWNED BY"));

        // pg_dump targets the SOURCE row's database, scoped to the schema.
        var dump = _runner.Requests[0];
        dump.FileName.Should().Be("pg_dump");
        dump.Arguments.Should().ContainInOrder("--host", SourceHost);
        dump.Arguments.Should().ContainInOrder("--dbname", "srcdb");
        dump.Arguments.Should().ContainInOrder("--schema", schema);

        // pg_restore targets the TARGET row's database, owned by the role.
        var restore = _runner.Requests[1];
        restore.FileName.Should().Be("pg_restore");
        restore.Arguments.Should().ContainInOrder("--dbname", "tgtdb");
        restore.Arguments.Should().Contain("--no-owner");
        restore.Arguments.Should().ContainInOrder("--role", role);

        // Both tools consumed the SAME tmp dump file, and it is gone.
        var dumpArgs = dump.Arguments.ToList();
        var dumpFile = dumpArgs[dumpArgs.IndexOf("--file") + 1];
        restore.Arguments.Should().Contain(dumpFile);
        File.Exists(dumpFile).Should().BeFalse("the tmp dump is deleted in a finally");

        // Re-point: same credentials + Search Path, ONLY the Database
        // swapped to the target row's.
        var (status, databaseId, envelope) = await ReadTenantAsync(tenantId);
        status.Should().Be("active");
        databaseId.Should().Be(TargetDbId);
        var minted = new NpgsqlConnectionStringBuilder(Encoding.UTF8.GetString(envelope!));
        minted.Database.Should().Be("tgtdb");
        minted.Username.Should().Be(role, "same-cluster keeps the role");
        minted.Password.Should().Be("tenant-pw", "same-cluster keeps the password");
        minted.SearchPath.Should().Be(schema, "Search Path must survive the swap");

        // Bookkeeping: TenantCount shifted source→target.
        await using var cp = await _factory.CreateDbContextAsync();
        (await cp.TenantDatabases.SingleAsync(d => d.Id == SourceDbId)).TenantCount.Should().Be(0);
        (await cp.TenantDatabases.SingleAsync(d => d.Id == TargetDbId)).TenantCount.Should().Be(1);
    }

    [Test]
    public async Task Move_CrossCluster_CreatesRole_MintsFreshEnvelope_DropsSourceRole()
    {
        var tenantId = await SeedAsync(targetHost: "other.internal", targetPort: 7432);
        var schema = TenantNaming.SchemaName(tenantId);
        var role = TenantNaming.RoleName(tenantId);
        _pool.RoleExists = true; // the role exists on the source cluster

        await CreateService().MoveAsync(tenantId, TargetDbId);

        // Cross-cluster: role created on the target BEFORE the schema,
        // and the source cluster loses the role after the schema drop.
        AssertOrdered(_timeline,
            "run:pg_dump",
            "provision:role",
            "provision:schema",
            "run:pg_restore",
            "sql:source:DROP SCHEMA",
            "sql:source:DROP OWNED BY",
            "sql:source:DROP ROLE");

        // Fresh envelope minted via the provisioning seam with the fresh
        // password (the old password belongs to the source cluster only).
        var (status, databaseId, envelope) = await ReadTenantAsync(tenantId);
        status.Should().Be("active");
        databaseId.Should().Be(TargetDbId);
        var minted = new NpgsqlConnectionStringBuilder(Encoding.UTF8.GetString(envelope!));
        minted.Host.Should().Be("other.internal");
        minted.Password.Should().Be("fresh-role-pw");
        minted.Username.Should().Be(role);
        minted.SearchPath.Should().Be(schema);
    }

    [Test]
    public async Task Move_PasswordsNeverInArgv_TravelViaPgPasswordOnly()
    {
        var tenantId = await SeedAsync();

        await CreateService().MoveAsync(tenantId, TargetDbId);

        _runner.Requests.Should().HaveCount(2);
        foreach (var request in _runner.Requests)
        {
            // The critical assertion: no password — tenant or admin —
            // may ever appear in argv (world-readable via /proc).
            foreach (var secret in new[] { "admin-src-pw", "admin-tgt-pw", "tenant-pw" })
            {
                request.Arguments.Should().NotContain(a => a.Contains(secret),
                    $"argv of {request.FileName} must never carry '{secret}'");
            }
            request.Arguments.Should().Contain("--no-password");
            request.EnvironmentOverrides.Should().NotBeNull();
            request.EnvironmentOverrides!.Should().ContainKey("PGPASSWORD");
        }
        _runner.Requests[0].EnvironmentOverrides!["PGPASSWORD"].Should().Be("admin-src-pw");
        _runner.Requests[1].EnvironmentOverrides!["PGPASSWORD"].Should().Be("admin-tgt-pw");
    }

    // ── validation rejections ──────────────────────────────────────────

    [Test]
    public async Task Move_Rejects_TenantInNonMovableStatus_NamingTheState()
    {
        var tenantId = await SeedAsync(tenantStatus: "suspended");

        var act = async () => await CreateService().MoveAsync(tenantId, TargetDbId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*suspended*");
        _runner.Requests.Should().BeEmpty("validation failures must precede any side effect");
        (await ReadTenantAsync(tenantId)).Status.Should().Be("suspended");
    }

    [Test]
    public async Task Move_Rejects_TargetSameAsSource()
    {
        var tenantId = await SeedAsync();

        var act = async () => await CreateService().MoveAsync(tenantId, SourceDbId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*must differ*");
        _runner.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Move_Rejects_IneligibleTarget()
    {
        // Target only accepts the enterprise tier — a free tenant must
        // bounce off the SAME predicate placement uses.
        var tenantId = await SeedAsync(targetTiers: ["enterprise"]);

        var act = async () => await CreateService().MoveAsync(tenantId, TargetDbId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not eligible*free*");
        _runner.Requests.Should().BeEmpty();
        (await ReadTenantAsync(tenantId)).Status.Should().Be("active",
            "validation failures must not open the read-only window");
    }

    [Test]
    public async Task Move_Rejects_TargetNotActive()
    {
        var tenantId = await SeedAsync(targetStatus: "retired");

        var act = async () => await CreateService().MoveAsync(tenantId, TargetDbId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*retired*");
        _runner.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Move_Rejects_TenantWithoutPlacement()
    {
        var tenantId = await SeedAsync(placed: false);

        var act = async () => await CreateService().MoveAsync(tenantId, TargetDbId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no placement*");
        _runner.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task Move_Rejects_UnknownTenant_AndUnknownTarget()
    {
        var tenantId = await SeedAsync();

        var unknownTenant = async () =>
            await CreateService().MoveAsync(Guid.NewGuid(), TargetDbId);
        (await unknownTenant.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*not found*");

        var unknownTarget = async () =>
            await CreateService().MoveAsync(tenantId, Guid.NewGuid());
        (await unknownTarget.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not exist*");
    }

    // ── failure policy ─────────────────────────────────────────────────

    [Test]
    public async Task Move_HistoryMismatch_Aborts_SourceIntact_TenantStaysDraining()
    {
        var tenantId = await SeedAsync();
        var (_, _, envelopeBefore) = await ReadTenantAsync(tenantId);
        _pool.ScalarResult = (dbId, _) => dbId == SourceDbId ? 5L : 3L;

        var act = async () => await CreateService().MoveAsync(tenantId, TargetDbId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*__TenantMigrationsHistory*");

        // Source intact: no DROP SCHEMA ever reached the source row; the
        // envelope and placement still point at the source; the tenant is
        // left 'draining' for the operator to retry or reset.
        _pool.Executed.Should().NotContain(c =>
            c.DatabaseId == SourceDbId && c.Sql.Contains("DROP SCHEMA"));
        var (status, databaseId, envelopeAfter) = await ReadTenantAsync(tenantId);
        status.Should().Be("draining");
        databaseId.Should().Be(SourceDbId);
        envelopeAfter.Should().Equal(envelopeBefore, "the re-point must not have committed");
    }

    [Test]
    public async Task Move_CrossCluster_RolePreexistsWithoutPassword_ThrowsRunbook()
    {
        var tenantId = await SeedAsync(targetHost: "other.internal", targetPort: 7432);
        _provisioning.RolePassword = null; // idempotent-skip: password unrecoverable

        var act = async () => await CreateService().MoveAsync(tenantId, TargetDbId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*DROP OWNED BY*");
        (await ReadTenantAsync(tenantId)).Status.Should().Be("draining",
            "steps 2-6 failures leave the tenant in the read-only window per the failure policy");
    }

    [Test]
    public async Task Move_Resume_AfterCommittedRepoint_SweepsStaleSchema_AndActivates()
    {
        // Failure-after-step-7 shape: the tenant already points at the
        // TARGET and is still draining; the stale schema copy survives on
        // the old source row. Re-running the SAME move must complete the
        // tail (sweep + activate) without re-dumping anything.
        var tenantId = await SeedAsync(
            tenantStatus: "draining", tenantDatabaseId: TargetDbId);
        _pool.SchemaExists = (dbId, _) => dbId == SourceDbId;

        await CreateService().MoveAsync(tenantId, TargetDbId);

        _runner.Requests.Should().BeEmpty("the resume tail must not re-dump or re-restore");
        _pool.Executed.Should().Contain(c =>
            c.DatabaseId == SourceDbId && c.Sql.Contains("DROP SCHEMA"));
        (await ReadTenantAsync(tenantId)).Status.Should().Be("active");
    }

    // ── fakes ──────────────────────────────────────────────────────────

    private sealed class InMemoryCpFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName;
        public InMemoryCpFactory(string dbName) => _dbName = dbName;

        public ControlPlaneDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            return new ControlPlaneDbContext(options);
        }

        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class RecordingPool : ITenantDatabasePool
    {
        private readonly List<string> _timeline;
        public RecordingPool(List<string> timeline) => _timeline = timeline;

        public Dictionary<Guid, TenantAdminConnectionInfo> Infos { get; } = new();
        public Dictionary<Guid, string> Names { get; } = new();
        public List<(Guid DatabaseId, string Sql)> Executed { get; } = new();
        public Func<Guid, string, object?> ScalarResult { get; set; } = (_, _) => 5L;
        public Func<Guid, string, bool> SchemaExists { get; set; } = (_, _) => false;
        public bool RoleExists { get; set; }

        private string NameOf(Guid id) => Names.TryGetValue(id, out var n) ? n : id.ToString();

        public Task<string> GetAdminConnectionStringAsync(Guid databaseId, CancellationToken ct = default)
            => Task.FromResult($"Host={Infos[databaseId].Host};Database={Infos[databaseId].Database}");

        public Task<int> ExecuteOnAsync(Guid databaseId, string commandText, CancellationToken ct = default)
        {
            Executed.Add((databaseId, commandText));
            _timeline.Add($"sql:{NameOf(databaseId)}:{commandText}");
            return Task.FromResult(0);
        }

        public Task<object?> ExecuteScalarOnAsync(Guid databaseId, string commandText, CancellationToken ct = default)
        {
            _timeline.Add($"scalar:{NameOf(databaseId)}");
            return Task.FromResult(ScalarResult(databaseId, commandText));
        }

        public Task<bool> RoleExistsOnAsync(Guid databaseId, string roleName, CancellationToken ct = default)
            => Task.FromResult(RoleExists);

        public Task<bool> SchemaExistsOnAsync(Guid databaseId, string schemaName, CancellationToken ct = default)
            => Task.FromResult(SchemaExists(databaseId, schemaName));

        public Task<TenantAdminConnectionInfo> GetConnectionInfoAsync(Guid databaseId, CancellationToken ct = default)
            => Task.FromResult(Infos[databaseId]);

        public Task<string> GetDatabaseNameAsync(Guid databaseId, CancellationToken ct = default)
            => Task.FromResult(Infos[databaseId].Database);

        public Task<string> BuildTenantConnectionStringAsync(
            Guid databaseId, string roleName, string password, string schemaName,
            CancellationToken ct = default)
            => Task.FromResult(
                $"Host={Infos[databaseId].Host};Database={Infos[databaseId].Database};"
                + $"Username={roleName};Password={password};Search Path={schemaName}");

        public void EvictAdminConnection(Guid databaseId) { }
    }

    private sealed class RecordingProvisioning : ITenantProvisioningService
    {
        private readonly List<string> _timeline;
        public RecordingProvisioning(List<string> timeline) => _timeline = timeline;

        /// <summary>Null simulates idempotent-skip (password unrecoverable).</summary>
        public string? RolePassword { get; set; } = "fresh-role-pw";

        public Task<TenantPlacement> AssignPlacementAsync(Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException("the move never re-assigns placement");

        public Task<string?> CreateRoleAsync(Guid tenantId, TenantPlacement placement, CancellationToken ct = default)
        {
            _timeline.Add($"provision:role:{placement.DatabaseId}");
            return Task.FromResult(RolePassword);
        }

        public Task CreateSchemaAsync(Guid tenantId, TenantPlacement placement, CancellationToken ct = default)
        {
            _timeline.Add($"provision:schema:{placement.DatabaseId}");
            return Task.CompletedTask;
        }

        public Task<string> BuildConnectionStringAsync(
            Guid tenantId, TenantPlacement placement, string password, CancellationToken ct = default)
            => Task.FromResult(
                $"Host=other.internal;Port=7432;Database=tgtdb;"
                + $"Username={TenantNaming.RoleName(tenantId)};Password={password};"
                + $"Search Path={placement.SchemaName}");

        public Task ProvisionAsync(Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException("the move never provisions from scratch");
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly List<string> _timeline;
        public RecordingRunner(List<string> timeline) => _timeline = timeline;

        public List<ProcessRunRequest> Requests { get; } = new();
        public Func<ProcessRunRequest, ProcessRunResult> Handler { get; set; } =
            _ => new ProcessRunResult(0, "", "", false, 0);

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            _timeline.Add($"run:{Path.GetFileName(request.FileName)}");
            return Task.FromResult(Handler(request));
        }
    }

    private sealed class RecordingResolver : IPoolResolver
    {
        private readonly List<string> _timeline;
        public RecordingResolver(List<string> timeline) => _timeline = timeline;

        public ValueTask<NpgsqlDataSource> GetDataSourceAsync(Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<NpgsqlDataSource> GetElsaDataSourceAsync(Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<ITenantConnectionLease> LeaseAsync(Guid tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask EvictAsync(Guid tenantId, CancellationToken ct = default)
        {
            _timeline.Add("evict");
            return ValueTask.CompletedTask;
        }

        public TenantConnectionPoolStats GetStats() => new(0, 0, 0);
    }

    private sealed class FakeTenantDbFactory : ITenantDbContextFactory
    {
        private readonly List<string> _timeline;
        private readonly string _dbName = $"move-tenantdb-{Guid.NewGuid():N}";
        public FakeTenantDbFactory(List<string> timeline) => _timeline = timeline;

        public ValueTask<TenantDbContext> CreateAsync(Guid tenantId, CancellationToken ct = default)
        {
            _timeline.Add("tenantdb:verify");
            var options = new DbContextOptionsBuilder<TenantDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            return ValueTask.FromResult(new TenantDbContext(options, tenantId));
        }
    }

    private sealed class Utf8Decryptor : IConnectionStringDecryptor
    {
        public string Decrypt(byte[] envelope, int? kekVersion) => Encoding.UTF8.GetString(envelope);
    }

    private sealed class Utf8Protector : ITenantConnectionStringProtector
    {
        public byte[] Encrypt(string plaintext) => Encoding.UTF8.GetBytes(plaintext);
        public int CurrentKekVersion => 1;
    }
}

/// <summary>
/// Unified-tenancy Phase 4 Task 3 — env-gated end-to-end proof of
/// <see cref="TenantMoveService"/> against real <c>pg_dump</c>/<c>pg_restore</c>:
/// two physical databases inside ONE Postgres container act as two pool
/// rows (same cluster). A tenant is provisioned on A through the real
/// Phase 2 pipeline, a marker row is written through the real resolver
/// chain, and after <c>MoveAsync</c> the schema must be GONE on A and
/// PRESENT on B with the marker + migrations history, the envelope must
/// decrypt to B's database, the resolver round-trip must work, the pool
/// TenantCounts must have shifted, and the tenant must be 'active' again.
///
/// <para>Skipped (<see cref="Assert.Ignore(string)"/>) when
/// <c>pg_dump</c>/<c>pg_restore</c> are not on PATH — mirroring the
/// env-gated pattern used elsewhere in the suite.</para>
/// </summary>
[TestFixture]
public class TenantMoveServiceEndToEndTests
{
    private static readonly byte[] Kek = BuildKek(seed: 77);
    private static readonly Guid TargetRowId =
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private const string TargetDatabaseName = "move_b";

    private PostgreSqlContainer _postgres = null!;
    private string _adminA = null!;
    private string _adminB = null!;
    private IConnectionStringDecryptor _decryptor = null!;
    private ITenantConnectionStringProtector _protector = null!;

    private static byte[] BuildKek(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static bool ToolOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator)
        .Any(dir => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, tool)));

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!ToolOnPath("pg_dump") || !ToolOnPath("pg_restore"))
        {
            Assert.Ignore(
                "pg_dump/pg_restore not found on PATH — the move e2e shells out to the "
                + "real Postgres client tools and cannot run on this host.");
        }

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("move_a")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _adminA = _postgres.GetConnectionString();

        // Second physical database in the SAME container = the second pool
        // row (same cluster, so the move exercises the swap-Database-only
        // branch with the real tools).
        await ExecAsync(_adminA, $"CREATE DATABASE {TargetDatabaseName}");
        _adminB = new NpgsqlConnectionStringBuilder(_adminA)
        {
            Database = TargetDatabaseName,
        }.ConnectionString;

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
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    [Test]
    public async Task Move_BetweenTwoPoolRows_EndToEnd()
    {
        // ── arrange: control plane with two pool rows + a tenant on A ──
        var cpDbName = nameof(Move_BetweenTwoPoolRows_EndToEnd);
        var factory = new InMemoryCpFactory(cpDbName);
        Guid tenantId;
        await using (var cp = await factory.CreateDbContextAsync())
        {
            await PlansSeeder.SeedAsync(cp);
            await TenantDatabasesSeeder.SeedAsync(cp, _adminA, _protector);

            var central = await cp.TenantDatabases.SingleAsync(
                d => d.Id == TenantDatabasesSeeder.CentralDatabaseId);
            cp.TenantDatabases.Add(new TenantDatabase
            {
                Id = TargetRowId,
                Label = "move-target",
                Host = central.Host,
                Port = central.Port,
                AdminConnectionStringEncrypted = _protector.Encrypt(_adminB),
                PlacementClass = "shared",
                TierEligibility = central.TierEligibility,
                TenantCapacity = null,
                TenantCount = 0,
                Status = "active",
                KekVersion = (short)_protector.CurrentKekVersion,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            tenantId = Guid.NewGuid();
            cp.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Move E2E Tenant",
                Slug = $"move-{tenantId:N}"[..20],
                Type = "personal",
                Plan = "free",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await cp.SaveChangesAsync();
        }

        var pool = new TenantDatabasePool(factory, _decryptor, NullLogger<TenantDatabasePool>.Instance);
        var provisioning = new TenantProvisioningService(
            new TenantPlacementService(factory, NullLogger<TenantPlacementService>.Instance),
            pool,
            factory,
            new EfTenantDbMigrator(),
            _protector,
            NullLogger<TenantProvisioningService>.Instance);
        await provisioning.ProvisionAsync(tenantId);

        var schema = TenantNaming.SchemaName(tenantId);

        using var metrics = new TenantConnectionPoolMetrics();
        await using var resolver = new LruPooledTenantConnectionResolver(
            factory,
            _decryptor,
            metrics,
            Options.Create(new TenantConnectionPoolOptions()),
            NullLogger<LruPooledTenantConnectionResolver>.Instance);
        var tenantFactory = new TenantDbContextFactory(resolver);

        // Marker row through the REAL resolver chain — this is the data
        // the move must carry over byte-for-byte.
        var markerId = Guid.NewGuid();
        await using (var ctx = await tenantFactory.CreateAsync(tenantId))
        {
            ctx.AgentConfigs.Add(new AgentConfig
            {
                Id = markerId,
                TenantId = tenantId,
                Config = """{"marker":"phase4-move"}""",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var service = new TenantMoveService(
            factory,
            pool,
            provisioning,
            new DefaultProcessRunner(),
            _decryptor,
            _protector,
            resolver,
            tenantFactory,
            Options.Create(new TenantMoveOptions()),
            NullLogger<TenantMoveService>.Instance);

        // ── act ────────────────────────────────────────────────────────
        await service.MoveAsync(tenantId, TargetRowId);

        // ── assert: physical layout ────────────────────────────────────
        (await ScalarAsync(_adminA,
            $"SELECT count(*) FROM information_schema.schemata WHERE schema_name = '{schema}'"))
            .Should().Be(0L, "the source schema on A must be dropped after the move");
        (await ScalarAsync(_adminB,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{schema}' "
            + "AND table_name IN ('agent_configs', '__TenantMigrationsHistory')"))
            .Should().Be(2L, "the restored schema on B must carry the data tables + history");
        (await ScalarAsync(_adminB,
            $"SELECT count(*) FROM {TenantNaming.Quote(schema)}.agent_configs"))
            .Should().Be(1L, "the marker row must survive the move");
        (await ScalarAsync(_adminB,
            $"SELECT count(*) FROM {TenantNaming.Quote(schema)}.\"__TenantMigrationsHistory\""))
            .Should().BeOfType(typeof(long)).And.NotBe(0L,
                "the migrations history must be restored row-for-row");

        // ── assert: control plane ──────────────────────────────────────
        await using (var cp = await factory.CreateDbContextAsync())
        {
            var tenant = await cp.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            var entry = cp.Entry(tenant);
            entry.Property<string?>("Status").CurrentValue.Should().Be("active");
            entry.Property<Guid?>("DatabaseId").CurrentValue.Should().Be(TargetRowId);

            var envelope = (byte[]?)entry.Property("EncryptedConnectionString").CurrentValue;
            var minted = new NpgsqlConnectionStringBuilder(
                new TenantSecretProtector(Kek).Decrypt(envelope!));
            minted.Database.Should().Be(TargetDatabaseName,
                "the envelope must decrypt to B's database after the re-point");
            minted.Username.Should().Be(TenantNaming.RoleName(tenantId),
                "same-cluster move keeps the tenant role");
            minted.SearchPath.Should().Be(schema);

            (await cp.TenantDatabases.SingleAsync(
                    d => d.Id == TenantDatabasesSeeder.CentralDatabaseId))
                .TenantCount.Should().Be(0, "the source row releases the slot");
            (await cp.TenantDatabases.SingleAsync(d => d.Id == TargetRowId))
                .TenantCount.Should().Be(1, "the target row claims the slot");
        }

        // ── assert: resolver round-trip lands on B with the marker ─────
        await using (var ctx = await tenantFactory.CreateAsync(tenantId))
        {
            ctx.Database.GetDbConnection().Database.Should().Be(TargetDatabaseName);
            var marker = await ctx.AgentConfigs.AsNoTracking().SingleAsync();
            marker.Id.Should().Be(markerId);
            marker.Config.Should().Contain("phase4-move");
        }
    }

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

    private sealed class InMemoryCpFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly string _dbName;
        public InMemoryCpFactory(string dbName) => _dbName = dbName;

        public ControlPlaneDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase(_dbName)
                .Options;
            return new ControlPlaneDbContext(options);
        }

        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
