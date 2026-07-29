using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Data;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-5 (AC1–AC4, AC6) — the governed-action storage against a REAL
/// Postgres (EF-InMemory enforces neither CHECK constraints nor
/// NULLS NOT DISTINCT): the three-scope principal CHECK, the mode-row CHECK,
/// the target-kind CHECK, NULLS-NOT-DISTINCT uniqueness (including the
/// all-null platform principal), per-field nullability (a threshold-only
/// write leaves the other columns NULL), the DELIBERATE absence of a numeric
/// bound on MinAutonomy at the DB layer, plane isolation on the repository
/// reads, the ledger's open-row partial unique index and TryConsume
/// semantics, and the wipe-survival property (idempotent re-migrate).
///
/// <para>Mirrors <c>ProviderSettingsMigrationTests</c> (the Testcontainers
/// migration-test convention) on its own container.</para>
/// </summary>
[TestFixture]
public class ActionAssignmentStorageTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private SimpleFactory _factory = null!;

    private sealed class SimpleFactory(DbContextOptions<ControlPlaneDbContext> options)
        : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() => new(options);
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("action_governance_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(_connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ControlPlaneMigrationsHistory"));
        ControlPlaneDbContext.ConfigureControlPlaneWarnings(options);
        await using (var db = new ControlPlaneDbContext(options.Options))
        {
            await db.Database.MigrateAsync();
        }
        _factory = new SimpleFactory(options.Options);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    [SetUp]
    public async Task ClearTables()
    {
        await ExecAsync("TRUNCATE TABLE action_assignments; TRUNCATE TABLE action_authorizations;");
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Insert(
        string tenantExpr, string userExpr, string kind, string key, string minExpr) =>
        $"""
        INSERT INTO action_assignments ("TenantId", "UserId", "TargetKind", "TargetKey", "MinAutonomy")
        VALUES ({tenantExpr}, {userExpr}, '{kind}', '{key}', {minExpr});
        """;

    // ── AC1 — the three admissible scopes ───────────────────────────────────

    [Test]
    public async Task AllThreeScopes_AreAccepted_IncludingThePlatformCeiling()
    {
        await ExecAsync(Insert("NULL", "NULL", "action", "tool:shell_execute", "101"));
        await ExecAsync(Insert("@tid", "NULL", "action", "tool:shell_execute", "90"),
            ("tid", Guid.NewGuid()));
        await ExecAsync(Insert("NULL", "@uid", "action", "tool:shell_execute", "80"),
            ("uid", Guid.NewGuid()));
    }

    [Test]
    public async Task BothPrincipalsSet_IsRejected_ByThePrincipalScopeCheck()
    {
        var act = () => ExecAsync(Insert("@tid", "@uid", "action", "tool:file_write", "90"),
            ("tid", Guid.NewGuid()), ("uid", Guid.NewGuid()));

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514",
                "ck_action_assignments_principal_scope admits tenant-only, user-only and "
                + "NEITHER (the ceiling) — never both");
    }

    // ── The mode-row and target-kind CHECKs ─────────────────────────────────

    [Test]
    public async Task ModeRow_WithAThreshold_IsRejected_AndActionRow_WithoutOne_IsRejected()
    {
        var modeWithThreshold = () => ExecAsync(Insert("NULL", "NULL", "mode", "saas", "90"));
        (await modeWithThreshold.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");

        var actionWithoutThreshold = () =>
            ExecAsync(Insert("NULL", "NULL", "action", "tool:file_write", "NULL"));
        (await actionWithoutThreshold.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");

        // The legal shapes of both kinds.
        await ExecAsync(Insert("NULL", "NULL", "mode", "saas", "NULL"));
        await ExecAsync(Insert("NULL", "NULL", "action", "tool:file_write", "90"));
    }

    [Test]
    public async Task UnknownTargetKind_IsRejected()
    {
        var act = () => ExecAsync(Insert("NULL", "NULL", "namespace", "tool", "90"));
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23514");
    }

    // ── AC3 — deliberately NO numeric bound at the DB layer ─────────────────

    [Test]
    public async Task MinAutonomy_AcceptsValuesOutsideTheDialRange_AtTheDbLayer()
    {
        // 5 is far outside [70,100] ∪ {101}: the DB must accept it, because a
        // CHECK frozen into a migration would be a second permanent hardcoding
        // of the AutonomyDial bound (validation is domain-side, 43-5 AC3/D5).
        await ExecAsync(Insert("NULL", "NULL", "action", "tool:file_write", "5"));
    }

    // ── NULLS NOT DISTINCT uniqueness ───────────────────────────────────────

    [Test]
    public async Task DuplicatePlatformRow_IsRejected_AndPlanesStayDisjoint()
    {
        await ExecAsync(Insert("NULL", "NULL", "action", "tool:file_write", "90"));

        var dup = () => ExecAsync(Insert("NULL", "NULL", "action", "tool:file_write", "95"));
        (await dup.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23505",
                "NULLS NOT DISTINCT collapses the all-null platform principal to one row per target");

        // The same target under a tenant and a user principal is disjoint.
        await ExecAsync(Insert("@tid", "NULL", "action", "tool:file_write", "90"),
            ("tid", Guid.NewGuid()));
        await ExecAsync(Insert("NULL", "@uid", "action", "tool:file_write", "90"),
            ("uid", Guid.NewGuid()));
    }

    // ── AC2 — per-field nullability through the repository ─────────────────

    [Test]
    public async Task ThresholdOnlyUpsert_LeavesTheOtherPolicyColumnsNull()
    {
        var repo = new EfActionAssignmentRepository(_factory);
        var tid = Guid.NewGuid();

        var (row, created) = await repo.UpsertAsync(
            tid, null, "action", "tool:file_write",
            minAutonomy: 95, enforce: null, enabled: null, allowedRoles: null,
            note: null, actingUserId: null);

        created.Should().BeTrue();
        row.MinAutonomy.Should().Be(95);
        row.Enforce.Should().BeNull("a threshold-only write says NOTHING about enforce");
        row.Enabled.Should().BeNull("a threshold-only write must not re-enable anything");
        row.AllowedRoles.Should().BeNull();

        // A later enforce-only write leaves the threshold alone.
        var (updated, wasCreated) = await repo.UpsertAsync(
            tid, null, "action", "tool:file_write",
            minAutonomy: null, enforce: false, enabled: null, allowedRoles: null,
            note: null, actingUserId: null);
        wasCreated.Should().BeFalse();
        updated.MinAutonomy.Should().Be(95, "an unset parameter never resets a stored field");
        updated.Enforce.Should().BeFalse();
        updated.Version.Should().Be(2);
    }

    // ── AC6 — plane isolation on the repository reads ───────────────────────

    [Test]
    public async Task Platform_rows_are_never_returned_by_a_principal_query()
    {
        var repo = new EfActionAssignmentRepository(_factory);
        var tid = Guid.NewGuid();
        await repo.UpsertAsync(null, null, "action", "tool:shell_execute", 101,
            null, null, null, null, null);
        await repo.UpsertAsync(tid, null, "action", "tool:file_write", 90,
            null, null, null, null, null);

        var principalRows = await repo.ListForPrincipalAsync(tid, null);

        principalRows.Should().ContainSingle()
            .Which.TargetKey.Should().Be("tool:file_write");
        principalRows.Should().NotContain(r => r.TenantId == null && r.UserId == null,
            "the ceiling is applied by the evaluator via max(), never by union (43-5 D2)");

        (await repo.ListPlatformAsync()).Should().ContainSingle()
            .Which.TargetKey.Should().Be("tool:shell_execute");
    }

    [Test]
    public void Reads_DoNotUseTenantDbContextFactory()
    {
        // Structural half of AC6: the repository's only constructor takes the
        // CONTROL-PLANE factory; the tenant-residency idiom cannot even be
        // expressed through its surface.
        var ctorParams = typeof(EfActionAssignmentRepository).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        ctorParams.Should().ContainSingle()
            .Which.Should().Be(typeof(IDbContextFactory<ControlPlaneDbContext>));
        ctorParams.Should().NotContain(typeof(Tamma.Data.Abstractions.ITenantDbContextFactory));

        // And the CODE carries none of the tenant idiom (doc comments may NAME
        // the forbidden identifiers to explain their absence — strip them).
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tamma.sln")))
            dir = Path.GetDirectoryName(dir);
        var code = string.Join('\n', File.ReadAllLines(Path.Combine(
                dir!, "src", "Tamma.Data", "Repositories", "ActionAssignmentRepository.cs"))
            .Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        code.Should().NotContain("ITenantDbContextFactory");
        code.Should().NotContain("IgnoreQueryFilters");
        code.Should().NotContain("ApplyTenantFilter");
    }

    [Test]
    public async Task PrincipalQuery_RejectsTheBothNullShape()
    {
        var repo = new EfActionAssignmentRepository(_factory);

        var act = () => repo.ListForPrincipalAsync(null, null);

        await act.Should().ThrowAsync<ArgumentException>(
            "the platform plane is read via ListPlatformAsync, never a principal query");
    }

    // ── AC4 — the authorization ledger ──────────────────────────────────────

    [Test]
    public async Task Ledger_AllowsOneOpenRow_ThenAFreshOne_AfterDenial()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        var first = await ledger.RequestAsync(
            tid, null, "wf-1", "action", "effect:deploy.promote-prod", "please", 70);
        first.State.Should().Be("pending");
        first.RequestedAtUtc.Should().NotBe(default, "RequestedAtUtc is NOT NULL from day one");
        first.ExpiresAtUtc.Should().NotBeNull("the default +24h TTL applies");

        // A second request while one is open returns the SAME row.
        var second = await ledger.RequestAsync(
            tid, null, "wf-1", "action", "effect:deploy.promote-prod", null, null);
        second.Id.Should().Be(first.Id);

        // The partial unique index rejects a raw second open row…
        var raw = () => ExecAsync(
            """
            INSERT INTO action_authorizations ("TenantId", "UserId", "CorrelationId", "TargetKind", "TargetKey")
            VALUES (@tid, NULL, 'wf-1', 'action', 'effect:deploy.promote-prod');
            """, ("tid", tid));
        (await raw.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23505");

        // …but after a denial a fresh request is legal.
        (await ledger.DecideAsync(first.Id, granted: false, Guid.NewGuid(), "no")).Should().NotBeNull();
        var third = await ledger.RequestAsync(
            tid, null, "wf-1", "action", "effect:deploy.promote-prod", null, null);
        third.Id.Should().NotBe(first.Id);
    }

    [Test]
    public async Task TryConsume_ActionGrantCoversItself_GroupGrantCoversMembers_ExpiredAndConsumedDoNot()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();
        var decider = Guid.NewGuid();

        // Action-scoped grant covers itself, once.
        var request = await ledger.RequestAsync(
            tid, null, "wf-1", "action", "effect:deploy.promote-prod", null, 70);
        await ledger.DecideAsync(request.Id, granted: true, decider, null);

        var consumed = await ledger.TryConsumeAsync(
            tid, null, "wf-1", "effect:deploy.promote-prod", "deploy-control");
        consumed.Should().NotBeNull();
        consumed!.ConsumedAtUtc.Should().NotBeNull();

        (await ledger.TryConsumeAsync(
                tid, null, "wf-1", "effect:deploy.promote-prod", "deploy-control"))
            .Should().BeNull("a consumed grant does not cover a second call");

        // Group-scoped grant covers every member of the group.
        var groupRequest = await ledger.RequestAsync(
            tid, null, "wf-2", "group", "deploy-control", null, 70);
        await ledger.DecideAsync(groupRequest.Id, granted: true, decider, null);
        (await ledger.TryConsumeAsync(
                tid, null, "wf-2", "effect:deploy.rollback", "deploy-control"))
            .Should().NotBeNull("a group grant covers every member of that group");

        // An expired grant does not cover.
        var expired = await ledger.RequestAsync(
            tid, null, "wf-3", "action", "effect:deploy.promote-prod", null, 70,
            ttl: TimeSpan.FromMilliseconds(-1));
        await ledger.DecideAsync(expired.Id, granted: true, decider, null);
        // (DecideAsync refuses expired pending rows → returns null; verify.)
        var decidedExpired = await ledger.TryConsumeAsync(
            tid, null, "wf-3", "effect:deploy.promote-prod", "deploy-control");
        decidedExpired.Should().BeNull("an expired grant never covers");

        // A pending (undecided) grant does not cover.
        await ledger.RequestAsync(tid, null, "wf-4", "action", "effect:deploy.promote-prod", null, 70);
        (await ledger.TryConsumeAsync(
                tid, null, "wf-4", "effect:deploy.promote-prod", "deploy-control"))
            .Should().BeNull("only a granted row covers");
    }

    [Test]
    public async Task Decide_RejectsAlreadyDecidedAndExpiredRows()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        var request = await ledger.RequestAsync(
            tid, null, "wf-9", "action", "effect:deploy.promote-prod", null, 70);
        (await ledger.DecideAsync(request.Id, true, Guid.NewGuid(), null)).Should().NotBeNull();
        (await ledger.DecideAsync(request.Id, false, Guid.NewGuid(), null))
            .Should().BeNull("a decided row cannot be re-decided (the caller 409s)");

        var expiring = await ledger.RequestAsync(
            tid, null, "wf-10", "action", "effect:deploy.promote-prod", null, 70,
            ttl: TimeSpan.FromMilliseconds(-1));
        (await ledger.DecideAsync(expiring.Id, true, Guid.NewGuid(), null))
            .Should().BeNull("an expired pending row cannot be granted");
    }

    // ── The wipe-survival property ──────────────────────────────────────────

    [Test]
    public async Task Epic19Wipe_ThenRemigrate_TablesSurviveWithRows()
    {
        var tid = Guid.NewGuid();
        await ExecAsync(Insert("@tid", "NULL", "action", "agent-action:deploy", "101"),
            ("tid", tid));

        // The wipe drops the migration history but deliberately NOT these
        // tables; the whole migration graph then re-runs. Simulate the part
        // that matters: this migration's DDL re-executes against the existing
        // tables — it must neither 42P07 nor touch the surviving rows.
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tamma.sln")))
            dir = Path.GetDirectoryName(dir);
        var migration = File.ReadAllText(Directory.GetFiles(
                Path.Combine(dir!, "src", "Tamma.Data", "Migrations", "ControlPlane"),
                "*_AddActionGovernance.cs")
            .Single(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)));
        var sql = System.Text.RegularExpressions.Regex
            .Match(migration, "migrationBuilder.Sql\\(\"\"\"(?<sql>[\\s\\S]*?)\"\"\"\\);")
            .Groups["sql"].Value;
        sql.Should().NotBeNullOrWhiteSpace();

        await ExecAsync(sql);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT "MinAutonomy" FROM action_assignments WHERE "TargetKey" = 'agent-action:deploy';""",
            conn);
        (await cmd.ExecuteScalarAsync()).Should().Be(101,
            "an admin tightening must survive the wipe-and-remigrate deploy cycle");
    }
}
