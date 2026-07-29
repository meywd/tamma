using System.Text.RegularExpressions;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Tamma.Core.Tracking;
using Tamma.Data.Pooling;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1 AC2/AC3/AC4 (schema) — pin the <c>AddTrackerCore</c> tenant
/// migration against a real Postgres 17: the five tables land; the CHECK
/// constraints mirror 44-0's wire vocabularies MEMBER-FOR-MEMBER (asserted by
/// reflection over the Core enums, so a member added there without a schema
/// amendment fails here); BOTH rank columns are <c>COLLATE "C"</c> and real
/// <c>Rank</c> output sorts identically in SQL and under
/// <see cref="StringComparer.Ordinal"/>; the <c>tracker_preferences</c> strong
/// XOR + NULLS NOT DISTINCT dedupe hold; and the <c>work_item_relations</c>
/// unique index rejects a duplicate canonical triple while the no-self CHECK
/// rejects a self-edge. EF-InMemory enforces none of this — a Postgres
/// testcontainer is the only proof.
///
/// <para>REQUIRES DOCKER to run (CI-verified). The fixture runs the full
/// tenant migration graph via <see cref="EfTenantDbMigrator"/> into the
/// default (public) schema, exactly like
/// <c>AcceptanceRulesOverridesMigrationTests</c>.</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class TrackerMigrationTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tracker_core_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(_connectionString);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    [SetUp]
    public async Task ClearTables()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE work_item_relations, work_items, iterations, projects, tracker_preferences CASCADE;",
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    // ─────────────────────────── Tables land (AC2) ───────────────────────────

    [Test]
    public async Task All_five_tracker_tables_land_in_the_tenant_schema()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.tables
            WHERE table_name IN
              ('projects','work_items','work_item_relations','iterations','tracker_preferences');
            """, conn);
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(5);
    }

    // ──────────────────── CHECKs mirror the Core enums (AC3) ────────────────────

    [Test]
    public async Task Status_check_constraint_matches_WorkItemStatus_member_for_member()
    {
        var dbSet = await ReadCheckLiteralsAsync("ck_work_items_status");
        var enumSet = Enum.GetValues<WorkItemStatus>().Select(s => s.ToWire());
        dbSet.Should().BeEquivalentTo(enumSet,
            "ck_work_items_status must enumerate exactly the 8 WorkItemStatus wire strings");
        dbSet.Should().HaveCount(8).And.Contain("triage");
    }

    [Test]
    public async Task Kind_check_constraint_matches_WorkItemKind_member_for_member()
    {
        var dbSet = await ReadCheckLiteralsAsync("ck_work_items_kind");
        var enumSet = Enum.GetValues<WorkItemKind>().Select(k => k.ToWire());
        dbSet.Should().BeEquivalentTo(enumSet,
            "ck_work_items_kind must enumerate exactly the 4 WorkItemKind wire strings");
        dbSet.Should().HaveCount(4).And.NotContain("bug").And.NotContain("chore");
    }

    [Test]
    public async Task Relation_kind_check_matches_WorkItemRelationKind_and_estimate_scale_matches_EstimateScale()
    {
        var relationSet = await ReadCheckLiteralsAsync("ck_work_item_relations_kind");
        relationSet.Should().BeEquivalentTo(
            Enum.GetValues<WorkItemRelationKind>().Select(k => k.ToWire()));

        var scaleSet = await ReadCheckLiteralsAsync("ck_projects_estimate_scale");
        scaleSet.Should().BeEquivalentTo(
            Enum.GetValues<EstimateScale>().Select(s => s.ToWire()));
    }

    [Test]
    public async Task Junk_status_is_rejected_by_the_check_constraint()
    {
        var projectId = await SeedProjectAsync("TAM");
        await using var conn = await OpenAsync();
        var ex = await FluentActions
            .Awaiting(() => InsertWorkItemAsync(conn, projectId, 1, "TAM-1", "task", "sprinting", "a", "a"))
            .Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514");
        ex.Which.ConstraintName.Should().Be("ck_work_items_status");
    }

    // ─────────────── The collation obligation (AC4 — Rank.cs's contract) ───────────────

    [Test]
    public async Task Both_rank_columns_are_created_with_the_C_collation()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT column_name, collation_name FROM information_schema.columns
            WHERE table_name = 'work_items' AND column_name IN ('Rank','SiblingRank')
            ORDER BY column_name;
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var seen = new Dictionary<string, string?>();
        while (await reader.ReadAsync())
            seen[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);

        seen.Should().HaveCount(2);
        seen["Rank"].Should().Be("C", "the Rank algebra's ordinal contract holds only under COLLATE \"C\"");
        seen["SiblingRank"].Should().Be("C", "one algebra, two columns (44-0 AC10)");
    }

    [Test]
    public async Task Rank_order_by_matches_ordinal_comparison_for_real_rank_algebra_output()
    {
        // Generate ranks the way production will: an append chain, a prepend
        // chain, and midpoint bisection between random neighbours — the mixed
        // history RankTests pins in-memory, now proven against real SQL order.
        var ranks = new List<string> { Rank.First() };
        for (var i = 0; i < 120; i++)
            ranks.Add(Rank.Append(ranks[^1]));
        var min = ranks.Min(StringComparer.Ordinal)!;
        for (var i = 0; i < 60; i++)
        {
            min = Rank.Prepend(min);
            ranks.Add(min);
        }
        var random = new Random(44_1);
        for (var i = 0; i < 120; i++)
        {
            var sorted = ranks.OrderBy(r => r, StringComparer.Ordinal).ToList();
            var at = random.Next(sorted.Count - 1);
            ranks.Add(Rank.Between(sorted[at], sorted[at + 1]));
        }
        ranks = ranks.Distinct(StringComparer.Ordinal).ToList();

        var projectId = await SeedProjectAsync("TAM");
        await using var conn = await OpenAsync();
        var shuffled = ranks.OrderBy(_ => random.Next()).ToList();
        var number = 1;
        foreach (var rank in shuffled)
        {
            await InsertWorkItemAsync(
                conn, projectId, number, $"TAM-{number}", "task", "backlog", rank, rank);
            number++;
        }

        var fromSql = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT \"Rank\" FROM work_items ORDER BY \"Rank\";", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                fromSql.Add(reader.GetString(0));
        }

        fromSql.Should().Equal(
            ranks.OrderBy(r => r, StringComparer.Ordinal),
            "ORDER BY \"Rank\" must agree with StringComparer.Ordinal — the board order IS the API order");

        // And the second axis rides the same collation.
        var siblingFromSql = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT \"SiblingRank\" FROM work_items ORDER BY \"SiblingRank\";", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                siblingFromSql.Add(reader.GetString(0));
        }
        siblingFromSql.Should().Equal(ranks.OrderBy(r => r, StringComparer.Ordinal));
    }

    // ──────────────── tracker_preferences: strong XOR + dedupe ────────────────

    [Test]
    public async Task Preferences_xor_rejects_both_set_and_both_null()
    {
        await using var conn = await OpenAsync();

        await using (var bothSet = new NpgsqlCommand(
            """
            INSERT INTO tracker_preferences ("UserId", "TenantId")
            VALUES (@uid, @tid);
            """, conn))
        {
            bothSet.Parameters.AddWithValue("uid", Guid.NewGuid());
            bothSet.Parameters.AddWithValue("tid", Guid.NewGuid());
            var ex = await FluentActions.Awaiting(() => bothSet.ExecuteNonQueryAsync())
                .Should().ThrowAsync<PostgresException>();
            ex.Which.SqlState.Should().Be("23514");
            ex.Which.ConstraintName.Should().Be("ck_tracker_preferences_principal_xor");
        }

        await using var bothNull = new NpgsqlCommand(
            "INSERT INTO tracker_preferences (\"UserId\", \"TenantId\") VALUES (NULL, NULL);", conn);
        var ex2 = await FluentActions.Awaiting(() => bothNull.ExecuteNonQueryAsync())
            .Should().ThrowAsync<PostgresException>();
        ex2.Which.SqlState.Should().Be("23514",
            "the STRONG XOR form rejects both-null — the weak audit_records form must not be copied");
    }

    [Test]
    public async Task Preferences_unique_index_dedupes_the_null_half()
    {
        var userId = Guid.NewGuid();
        await using var conn = await OpenAsync();

        await using (var first = new NpgsqlCommand(
            "INSERT INTO tracker_preferences (\"UserId\", \"TenantId\") VALUES (@uid, NULL);", conn))
        {
            first.Parameters.AddWithValue("uid", userId);
            await first.ExecuteNonQueryAsync();
        }

        await using var second = new NpgsqlCommand(
            "INSERT INTO tracker_preferences (\"UserId\", \"TenantId\") VALUES (@uid, NULL);", conn);
        second.Parameters.AddWithValue("uid", userId);
        var ex = await FluentActions.Awaiting(() => second.ExecuteNonQueryAsync())
            .Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23505",
            "NULLS NOT DISTINCT makes two (uid, NULL) rows collide — without it the dedupe silently does nothing");
    }

    // ─────────────── work_item_relations: canonical unique + no-self ───────────────

    [Test]
    public async Task Relations_unique_index_rejects_a_duplicate_canonical_triple_and_no_self_edge()
    {
        var projectId = await SeedProjectAsync("TAM");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await InsertWorkItemAsync(conn, projectId, 1, "TAM-1", "task", "backlog", "V", "V", a);
        await InsertWorkItemAsync(conn, projectId, 2, "TAM-2", "task", "backlog", "h", "h", b);

        async Task InsertRelation(Guid source, Guid target, string kind)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO work_item_relations ("SourceId", "TargetId", "Kind")
                VALUES (@s, @t, @k);
                """, conn);
            cmd.Parameters.AddWithValue("s", source);
            cmd.Parameters.AddWithValue("t", target);
            cmd.Parameters.AddWithValue("k", kind);
            await cmd.ExecuteNonQueryAsync();
        }

        await InsertRelation(a, b, "related");
        var dup = await FluentActions.Awaiting(() => InsertRelation(a, b, "related"))
            .Should().ThrowAsync<PostgresException>();
        dup.Which.SqlState.Should().Be("23505",
            "the unique index assumes rows arrive Canonicalize'd — a duplicate canonical triple collides");

        var self = await FluentActions.Awaiting(() => InsertRelation(a, a, "blocks"))
            .Should().ThrowAsync<PostgresException>();
        self.Which.SqlState.Should().Be("23514");
        self.Which.ConstraintName.Should().Be("ck_work_item_relations_no_self");
    }

    // ─────────────── (ProjectId, Number) + Key uniqueness (AC5's schema half) ───────────────

    [Test]
    public async Task Project_number_and_key_are_unique()
    {
        var projectId = await SeedProjectAsync("TAM");
        await using var conn = await OpenAsync();
        await InsertWorkItemAsync(conn, projectId, 1, "TAM-1", "task", "backlog", "V", "V");

        var sameNumber = await FluentActions
            .Awaiting(() => InsertWorkItemAsync(conn, projectId, 1, "TAM-99", "task", "backlog", "h", "h"))
            .Should().ThrowAsync<PostgresException>();
        sameNumber.Which.SqlState.Should().Be("23505");

        var sameKey = await FluentActions
            .Awaiting(() => InsertWorkItemAsync(conn, projectId, 2, "TAM-1", "task", "backlog", "h", "h"))
            .Should().ThrowAsync<PostgresException>();
        sameKey.Which.SqlState.Should().Be("23505");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>
    /// The wire-string literals of a CHECK ... IN (...) constraint, read from
    /// <c>pg_get_constraintdef</c> (e.g. <c>'triage'::text</c> → <c>triage</c>).
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadCheckLiteralsAsync(string constraintName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = @name;", conn);
        cmd.Parameters.AddWithValue("name", constraintName);
        var definition = (string?)await cmd.ExecuteScalarAsync();
        definition.Should().NotBeNull($"constraint {constraintName} must exist");
        return Regex.Matches(definition!, "'([a-z_]+)'").Select(m => m.Groups[1].Value).ToList();
    }

    private async Task<Guid> SeedProjectAsync(string key)
    {
        var id = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO projects (\"Id\", \"Key\", \"Name\") VALUES (@id, @key, @name);", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("name", $"{key} project");
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task InsertWorkItemAsync(
        NpgsqlConnection conn, Guid projectId, int number, string key,
        string kind, string status, string rank, string siblingRank, Guid? id = null)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO work_items
              ("Id", "ProjectId", "Number", "Key", "Kind", "Status", "Title", "Rank", "SiblingRank")
            VALUES (@id, @pid, @num, @key, @kind, @status, @title, @rank, @sibling);
            """, conn);
        cmd.Parameters.AddWithValue("id", id ?? Guid.NewGuid());
        cmd.Parameters.AddWithValue("pid", projectId);
        cmd.Parameters.AddWithValue("num", number);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("title", key);
        cmd.Parameters.AddWithValue("rank", rank);
        cmd.Parameters.AddWithValue("sibling", siblingRank);
        await cmd.ExecuteNonQueryAsync();
    }
}
