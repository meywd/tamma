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
        (await ledger.DecideAsync(tid, null, first.Id, granted: false, Guid.NewGuid(), "no")).Should().NotBeNull();
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
        await ledger.DecideAsync(tid, null, request.Id, granted: true, decider, null);

        var consumed = await ledger.TryConsumeAsync(
            tid, null, "wf-1", "effect:deploy.promote-prod");
        consumed.Should().NotBeNull();
        consumed!.ConsumedAtUtc.Should().NotBeNull();

        (await ledger.TryConsumeAsync(
                tid, null, "wf-1", "effect:deploy.promote-prod"))
            .Should().BeNull("a consumed grant does not cover a second call");

        // Group-scoped grant covers every member of the group (membership
        // resolved from ActionCatalog inside the ledger — F2).
        var groupRequest = await ledger.RequestAsync(
            tid, null, "wf-2", "group", "deploy-control", null, 70);
        await ledger.DecideAsync(tid, null, groupRequest.Id, granted: true, decider, null);
        (await ledger.TryConsumeAsync(
                tid, null, "wf-2", "effect:deploy.rollback"))
            .Should().NotBeNull("a group grant covers every member of that group");

        // An expired grant does not cover.
        var expired = await ledger.RequestAsync(
            tid, null, "wf-3", "action", "effect:deploy.promote-prod", null, 70,
            ttl: TimeSpan.FromMilliseconds(-1));
        await ledger.DecideAsync(tid, null, expired.Id, granted: true, decider, null);
        // (DecideAsync refuses expired pending rows → returns null; verify.)
        var decidedExpired = await ledger.TryConsumeAsync(
            tid, null, "wf-3", "effect:deploy.promote-prod");
        decidedExpired.Should().BeNull("an expired grant never covers");

        // A pending (undecided) grant does not cover.
        await ledger.RequestAsync(tid, null, "wf-4", "action", "effect:deploy.promote-prod", null, 70);
        (await ledger.TryConsumeAsync(
                tid, null, "wf-4", "effect:deploy.promote-prod"))
            .Should().BeNull("only a granted row covers");
    }

    // ── Adversarial review F2 — a group grant only covers its own members ──

    [Test]
    public async Task GroupGrant_CannotBeConsumedForAnActionOutsideTheGroup()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        // A deploy-control group grant…
        var request = await ledger.RequestAsync(
            tid, null, "wf-f2", "group", "deploy-control", null, 70);
        await ledger.DecideAsync(tid, null, request.Id, granted: true, Guid.NewGuid(), null);

        // …must NOT cover tool:shell_execute (command-execution group), no
        // matter what group the caller claims: membership is resolved from
        // ActionCatalog inside the ledger, never from caller input.
        (await ledger.TryConsumeAsync(tid, null, "wf-f2", "tool:shell_execute"))
            .Should().BeNull(
                "a group grant covers only catalog members of that group (review F2)");

        // The grant is still live and still covers a genuine member.
        (await ledger.TryConsumeAsync(tid, null, "wf-f2", "effect:deploy.promote-prod"))
            .Should().NotBeNull("the failed non-member consume must not burn the grant");
    }

    // ── Adversarial review F6 (2026-08-01) — DECIDING is principal-scoped ──

    [Test]
    public async Task Decide_RefusesAForeignPrincipalsRow()
    {
        // `ListAuthorizations` is principal-scoped with a comment explaining that
        // merely ENUMERATING another principal's rows is a capability disclosure —
        // but the ability to DECIDE one was unscoped, and the id is handed out in
        // the Seam C 409 body and the Seam E response. In SaaS that let any tenant
        // admin holding a guid GRANT tenant A's blocked effect.
        var ledger = new EfActionAuthorizationLedger(_factory);
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        var row = await ledger.RequestAsync(
            owner, null, "wf-f6", "action", "effect:deploy.promote-prod", null, 70);

        (await ledger.DecideAsync(attacker, null, row.Id, granted: true, Guid.NewGuid(), "mine now"))
            .Should().BeNull("a foreign principal may not decide another principal's authorization");

        // Deciding is scoped by PRINCIPAL, not by decider: a single-user row is
        // not reachable from a tenant principal either.
        (await ledger.DecideAsync(null, attacker, row.Id, granted: true, Guid.NewGuid(), null))
            .Should().BeNull("the user plane may not decide a tenant-plane row");

        // The row is untouched and the real owner can still decide it.
        await using (var db = _factory.CreateDbContext())
        {
            db.ActionAuthorizations.Single(a => a.Id == row.Id).State.Should().Be("pending");
        }
        (await ledger.DecideAsync(owner, null, row.Id, granted: true, Guid.NewGuid(), "ok"))
            .Should().NotBeNull("the owning principal still decides its own row");
    }

    // ── Adversarial review F1 — CAS: exactly one winner under concurrency ──

    [Test]
    public async Task ConcurrentConsume_OfOneGrant_HasExactlyOneWinner()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        var request = await ledger.RequestAsync(
            tid, null, "wf-race", "action", "effect:deploy.promote-prod", null, 70);
        await ledger.DecideAsync(tid, null, request.Id, granted: true, Guid.NewGuid(), null);

        // The reviewer's probe shape: multiple contexts race the same grant.
        // Each TryConsumeAsync call creates its OWN DbContext (factory-made),
        // so every contender reads the candidate before any single row-level
        // CAS has stamped it; the conditional UPDATE (WHERE granted AND
        // unconsumed) is what must arbitrate — never last-write-wins.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await barrier.Task;
                return await ledger.TryConsumeAsync(
                    tid, null, "wf-race", "effect:deploy.promote-prod");
            })
            .ToArray();
        barrier.SetResult();
        var results = await Task.WhenAll(contenders);

        results.Count(r => r is not null).Should().Be(1,
            "one human decision covers ONE run — a double-consume is the F1 bug");

        await using var db = _factory.CreateDbContext();
        db.ActionAuthorizations.Single(a => a.Id == request.Id)
            .ConsumedAtUtc.Should().NotBeNull();
    }

    [Test]
    public async Task ConcurrentGrantAndDeny_ExactlyOneWins_AndTheRowMatchesTheWinner()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        var request = await ledger.RequestAsync(
            tid, null, "wf-race-2", "action", "effect:deploy.promote-prod", null, 70);

        var granter = Guid.NewGuid();
        var denier = Guid.NewGuid();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grantTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await ledger.DecideAsync(tid, null, request.Id, granted: true, granter, "yes");
        });
        var denyTask = Task.Run(async () =>
        {
            await barrier.Task;
            return await ledger.DecideAsync(tid, null, request.Id, granted: false, denier, "no");
        });
        barrier.SetResult();
        var outcomes = await Task.WhenAll(grantTask, denyTask);

        outcomes.Count(o => o is not null).Should().Be(1,
            "DecideAsync must CAS on state='pending' — concurrent grant and deny "
            + "both returning non-null (last write wins) is the F1 bug");

        var winnerGranted = outcomes.Single(o => o is not null)!.State == "granted";
        await using var db = _factory.CreateDbContext();
        var row = db.ActionAuthorizations.Single(a => a.Id == request.Id);
        row.State.Should().Be(winnerGranted ? "granted" : "denied",
            "the persisted state must match the single winner's verdict");
        row.DecidedByUserId.Should().Be(winnerGranted ? granter : denier);
    }

    // ── Adversarial review F3 — time-expired rows never deadlock the key ───

    [Test]
    public async Task ExpiredPendingRow_DoesNotDeadlockTheKey_AFreshRequestSucceeds()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        var stale = await ledger.RequestAsync(
            tid, null, "wf-f3", "action", "effect:deploy.promote-prod", null, 70,
            ttl: TimeSpan.FromMilliseconds(-1));
        stale.State.Should().Be("pending");

        // Before the fix: the open-row check idempotently returned the stale
        // row (which DecideAsync refuses), and the partial unique index
        // blocked a fresh insert — the key was dead forever.
        var fresh = await ledger.RequestAsync(
            tid, null, "wf-f3", "action", "effect:deploy.promote-prod", "retry", 70);

        fresh.Id.Should().NotBe(stale.Id, "a time-expired open row is closed, not returned");
        fresh.State.Should().Be("pending");
        fresh.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow, "the fresh row carries a live TTL");

        await using var db = _factory.CreateDbContext();
        db.ActionAuthorizations.Single(a => a.Id == stale.Id).State.Should().Be("expired",
            "the stale row is transitioned out of the partial unique index");

        // And the fresh row is decidable — the whole point of unblocking.
        (await ledger.DecideAsync(tid, null, fresh.Id, granted: true, Guid.NewGuid(), null))
            .Should().NotBeNull();
    }

    [Test]
    public async Task TimeExpiredGrant_IsNotConsumable()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        // Grant while live, then push the expiry into the past — the shape a
        // grant reaches 24h after the decision.
        var request = await ledger.RequestAsync(
            tid, null, "wf-f3b", "action", "effect:deploy.promote-prod", null, 70);
        (await ledger.DecideAsync(tid, null, request.Id, granted: true, Guid.NewGuid(), null))
            .Should().NotBeNull();
        await ExecAsync(
            """
            UPDATE action_authorizations
            SET "ExpiresAtUtc" = now() - interval '1 minute'
            WHERE "Id" = @id;
            """, ("id", request.Id));

        (await ledger.TryConsumeAsync(tid, null, "wf-f3b", "effect:deploy.promote-prod"))
            .Should().BeNull("the consume predicate excludes expired-by-time grants");

        await using var db = _factory.CreateDbContext();
        db.ActionAuthorizations.Single(a => a.Id == request.Id)
            .ConsumedAtUtc.Should().BeNull("a refused consume must not stamp the row");
    }

    [Test]
    public async Task Decide_RejectsAlreadyDecidedAndExpiredRows()
    {
        var ledger = new EfActionAuthorizationLedger(_factory);
        var tid = Guid.NewGuid();

        var request = await ledger.RequestAsync(
            tid, null, "wf-9", "action", "effect:deploy.promote-prod", null, 70);
        (await ledger.DecideAsync(tid, null, request.Id, true, Guid.NewGuid(), null)).Should().NotBeNull();
        (await ledger.DecideAsync(tid, null, request.Id, false, Guid.NewGuid(), null))
            .Should().BeNull("a decided row cannot be re-decided (the caller 409s)");

        var expiring = await ledger.RequestAsync(
            tid, null, "wf-10", "action", "effect:deploy.promote-prod", null, 70,
            ttl: TimeSpan.FromMilliseconds(-1));
        (await ledger.DecideAsync(tid, null, expiring.Id, true, Guid.NewGuid(), null))
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
