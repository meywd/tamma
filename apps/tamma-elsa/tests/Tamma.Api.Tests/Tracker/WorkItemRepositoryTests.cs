using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Tests.Documents;
using Tamma.Core;
using Tamma.Core.Tracking;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-1 AC5/AC1 (repository) — the key mint, the frozen-key +
/// <c>PreviousKeys</c> history, keyset paging over the <c>COLLATE "C"</c>
/// order, the RESTRICT parent FK, and the canonical relation writer (the
/// shipped <c>WorkItemRelationKind.Canonicalize</c>, exercised through
/// <see cref="WorkItemRepository.AddRelationAsync"/>). The concurrency test is
/// the AC5 proof: parallel creates serialize on the project row lock and mint
/// distinct, contiguous numbers.
///
/// <para>REQUIRES DOCKER to run (CI-verified). Single tenant schema via
/// <see cref="EfTenantDbMigrator"/> + the <c>SchemaRoutingFactory</c> /
/// <c>FakeTenantContext</c> doubles (the <c>DocumentTestData</c> shapes).</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class WorkItemRepositoryTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;
    private Guid _tenantId;
    private string _schema = null!;

    private WorkItemRepository _repository = null!;
    private ProjectRepository _projects = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tracker_repo_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();

        _tenantId = Guid.NewGuid();
        _schema = TenantNaming.SchemaName(_tenantId);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(_schema));

        var factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString)
            .Map(_tenantId, _schema);
        var tenantContext = new DocumentTestData.FakeTenantContext(_tenantId);
        _repository = new WorkItemRepository(factory, tenantContext);
        _projects = new ProjectRepository(factory, tenantContext);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    [SetUp]
    public async Task ClearTables()
    {
        await using var conn = new NpgsqlConnection(CsFor(_schema));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"""
            TRUNCATE TABLE {_schema}.work_item_relations, {_schema}.work_items,
                           {_schema}.iterations, {_schema}.projects CASCADE;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }
            .ConnectionString;

    private Task<ProjectEntity> NewProjectAsync(string key = "TAM") =>
        _projects.CreateAsync(new ProjectEntity { Key = key, Name = $"{key} project" });

    private static WorkItemEntity NewItem(Guid projectId, string title = "item") => new()
    {
        ProjectId = projectId,
        Kind = "task",
        Status = "backlog",
        Title = title,
    };

    // ───────────────────────── The mint (AC5) ─────────────────────────

    [Test]
    public async Task Concurrent_creates_mint_distinct_contiguous_keys()
    {
        var project = await NewProjectAsync();

        const int count = 50;
        var created = await Task.WhenAll(Enumerable.Range(0, count)
            .Select(i => _repository.CreateAsync(NewItem(project.Id, $"item {i}"))));

        created.Select(w => w.Number).Should().OnlyHaveUniqueItems(
            "two concurrent creates must never mint the same number (the FOR UPDATE row lock)");
        created.Select(w => w.Number).OrderBy(n => n).Should().Equal(
            Enumerable.Range(1, count),
            "numbering is gap-free and contiguous — TAM-1, TAM-2, TAM-4 looks like data loss");
        created.Select(w => w.Key).Should().OnlyHaveUniqueItems();
        created.Should().OnlyContain(w => w.Key == $"TAM-{w.Number}");

        var next = await _repository.CreateAsync(NewItem(project.Id, "one more"));
        next.Number.Should().Be(count + 1);
    }

    [Test]
    public async Task Create_appends_ranks_at_the_end_of_both_axes()
    {
        var project = await NewProjectAsync();
        var first = await _repository.CreateAsync(NewItem(project.Id, "first"));
        var second = await _repository.CreateAsync(NewItem(project.Id, "second"));

        Rank.IsValid(first.Rank).Should().BeTrue();
        Rank.IsValid(second.SiblingRank).Should().BeTrue();
        string.CompareOrdinal(first.Rank, second.Rank).Should().BeNegative(
            "each create appends strictly after the current maximum");
        string.CompareOrdinal(first.SiblingRank, second.SiblingRank).Should().BeNegative();
    }

    // ────────────── Frozen key + PreviousKeys history (44-0 AC8) ──────────────

    [Test]
    public async Task GetByKey_resolves_the_previous_key_after_a_rekey()
    {
        var project = await NewProjectAsync();
        var item = await _repository.CreateAsync(NewItem(project.Id));
        item.Key.Should().Be("TAM-1");

        var rekeyed = await _repository.RekeyAsync(item.Id, "TAMMA-1");
        rekeyed!.Key.Should().Be("TAMMA-1");
        // NOTE: Equal(params T[]) has NO because overload — a reason string
        // would be treated as an expected element. Use the IEnumerable overload.
        rekeyed.PreviousKeys.Should().Equal(
            new[] { "TAM-1" }, "the outgoing key is recorded, oldest first");

        (await _repository.GetByKeyAsync("TAMMA-1"))!.Id.Should().Be(item.Id);
        (await _repository.GetByKeyAsync("TAM-1"))!.Id.Should().Be(
            item.Id,
            "every already-written DocumentInstance.IssueId and DCB tag must still find its item");

        // Idempotence of the history rule (WorkItemKeyHistory.Record): a
        // second hop records the intermediate key once, no duplicates.
        var again = await _repository.RekeyAsync(item.Id, "TAM-1");
        again!.PreviousKeys.Should().Equal("TAM-1", "TAMMA-1");
        var back = await _repository.RekeyAsync(item.Id, "TAMMA-1");
        back!.PreviousKeys.Should().Equal(
            new[] { "TAM-1", "TAMMA-1" },
            "re-recording an already-recorded key must not duplicate it");
    }

    [Test]
    public async Task Rekey_rejects_a_malformed_key_without_touching_the_row()
    {
        var project = await NewProjectAsync();
        var item = await _repository.CreateAsync(NewItem(project.Id));

        var act = () => _repository.RekeyAsync(item.Id, "tam-1");
        (await act.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("TRACKER.INVALID_WORK_ITEM_KEY");

        (await _repository.GetAsync(item.Id))!.Key.Should().Be("TAM-1");
    }

    // ─────────────────── Keyset paging over the C-collated order ───────────────────

    [Test]
    public async Task Keyset_paging_is_stable_under_mid_range_insertion()
    {
        var project = await NewProjectAsync();
        var seeded = new List<WorkItemEntity>();
        for (var i = 0; i < 9; i++)
            seeded.Add(await _repository.CreateAsync(NewItem(project.Id, $"item {i}")));

        var page1 = await _repository.ListAsync(new WorkItemQuery
        {
            ProjectId = project.Id,
            Limit = 4,
        });
        page1.Should().HaveCount(4);

        // A drag lands a NEW item between page 1 and page 2 — one UPDATE-shaped
        // insert. Keyset paging must neither duplicate nor skip the ORIGINAL rows.
        var cursorRank = page1[^1].Rank;
        var nextRank = seeded.Select(w => w.Rank)
            .Where(r => string.CompareOrdinal(r, cursorRank) > 0)
            .OrderBy(r => r, StringComparer.Ordinal)
            .First();
        var wedged = NewItem(project.Id, "wedged mid-range");
        wedged.Rank = Rank.Between(cursorRank, nextRank);
        await _repository.CreateAsync(wedged);

        var page2 = await _repository.ListAsync(new WorkItemQuery
        {
            ProjectId = project.Id,
            Limit = 100,
            AfterRank = page1[^1].Rank,
            AfterKey = page1[^1].Key,
        });

        var paged = page1.Concat(page2).Select(w => w.Id).ToList();
        paged.Should().OnlyHaveUniqueItems("keyset paging must never duplicate a row");
        paged.Should().Contain(seeded.Select(w => w.Id),
            "no pre-existing row may be skipped by an insertion between pages");
    }

    // ─────────────────────── Hierarchy storage seams ───────────────────────

    [Test]
    public async Task Parent_delete_is_restricted_while_children_exist()
    {
        var project = await NewProjectAsync();
        var parent = await _repository.CreateAsync(NewItem(project.Id, "epic"));
        var child = NewItem(project.Id, "child");
        child.ParentId = parent.Id;
        await _repository.CreateAsync(child);

        var act = () => _repository.DeleteAsync(parent.Id);
        await act.Should().ThrowAsync<DbUpdateException>(
            "ParentId is ON DELETE RESTRICT — silently deleting a subtree is unrecoverable");

        (await _repository.GetAsync(parent.Id)).Should().NotBeNull();
    }

    [Test]
    public async Task SetStatus_stamps_and_clears_ClosedAt_via_the_derived_terminal_rule()
    {
        var project = await NewProjectAsync();
        var item = await _repository.CreateAsync(NewItem(project.Id));
        item.ClosedAt.Should().BeNull();

        var done = await _repository.SetStatusAsync(item.Id, "done");
        done!.ClosedAt.Should().NotBeNull();

        var reopened = await _repository.SetStatusAsync(item.Id, "in_progress");
        reopened!.ClosedAt.Should().BeNull("reopening clears the terminal stamp");

        var junk = () => _repository.SetStatusAsync(item.Id, "sprinting");
        (await junk.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("TRACKER.UNKNOWN_STATUS");
    }

    // ──────────── Relations — the canonical writer (44-0 AC14 / D8) ────────────

    [Test]
    public async Task Mirror_symmetric_relation_maps_onto_the_same_stored_row()
    {
        var project = await NewProjectAsync();
        var a = await _repository.CreateAsync(NewItem(project.Id, "a"));
        var b = await _repository.CreateAsync(NewItem(project.Id, "b"));

        var first = await _repository.AddRelationAsync(a.Id, b.Id, WorkItemRelationKind.Related);
        var mirror = await _repository.AddRelationAsync(b.Id, a.Id, WorkItemRelationKind.Related);

        mirror.Id.Should().Be(first.Id,
            "Canonicalize stores symmetric kinds lower-id-first, so the mirror IS the original");
        (await _repository.ListRelationsAsync(a.Id)).Should().HaveCount(1);

        var (lower, higher) = a.Id.CompareTo(b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);
        first.SourceId.Should().Be(lower);
        first.TargetId.Should().Be(higher);
    }

    [Test]
    public async Task Blocks_stays_directed_and_self_relation_is_rejected()
    {
        var project = await NewProjectAsync();
        var a = await _repository.CreateAsync(NewItem(project.Id, "a"));
        var b = await _repository.CreateAsync(NewItem(project.Id, "b"));

        var forward = await _repository.AddRelationAsync(a.Id, b.Id, WorkItemRelationKind.Blocks);
        forward.SourceId.Should().Be(a.Id, "blocks is directed — source→target is meaning");
        forward.TargetId.Should().Be(b.Id);

        var reverse = await _repository.AddRelationAsync(b.Id, a.Id, WorkItemRelationKind.Blocks);
        reverse.Id.Should().NotBe(forward.Id,
            "A-blocks-B and B-blocks-A are different facts (a cycle to SHOW, not to merge)");

        var self = () => _repository.AddRelationAsync(a.Id, a.Id, WorkItemRelationKind.Duplicate);
        (await self.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("TRACKER.SELF_RELATION");
    }

    [Test]
    public async Task Relation_edges_cascade_away_with_the_item()
    {
        var project = await NewProjectAsync();
        var a = await _repository.CreateAsync(NewItem(project.Id, "a"));
        var b = await _repository.CreateAsync(NewItem(project.Id, "b"));
        await _repository.AddRelationAsync(a.Id, b.Id, WorkItemRelationKind.Duplicate);

        (await _repository.DeleteAsync(b.Id)).Should().BeTrue();
        (await _repository.ListRelationsAsync(a.Id)).Should().BeEmpty();
    }

    // ─────────────────────── Vocabulary write boundary ───────────────────────

    [Test]
    public async Task Create_rejects_out_of_vocabulary_wires_before_the_database_sees_them()
    {
        var project = await NewProjectAsync();

        var badKind = NewItem(project.Id);
        badKind.Kind = "bug"; // TriageIssueType's axis, not WorkItemKind's (44-0 AC1)
        var act = () => _repository.CreateAsync(badKind);
        (await act.Should().ThrowExactlyAsync<TammaError>())
            .Which.Code.Should().Be("TRACKER.UNKNOWN_KIND");

        var aliased = NewItem(project.Id);
        aliased.Priority = "critical"; // documented alias → urgent
        var created = await _repository.CreateAsync(aliased);
        created.Priority.Should().Be("urgent",
            "aliases fold through TriageVocabulary; the CHECK only ever sees canonical wires");

        var unset = NewItem(project.Id);
        var createdUnset = await _repository.CreateAsync(unset);
        createdUnset.Priority.Should().BeNull("null priority is 'nobody prioritised' — a real fact");
    }
}
