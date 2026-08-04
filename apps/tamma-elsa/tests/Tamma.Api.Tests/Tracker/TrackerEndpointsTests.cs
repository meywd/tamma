using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Dtos.Tracker;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Access;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Tracker;
using Tamma.Api.Tests.Documents;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Tracker;

/// <summary>
/// Story 44-2 — the tracker HTTP surface driven through its real handler
/// delegates against a REAL tenant schema (the <c>WorkItemRepositoryTests</c>
/// fixture shape: Testcontainers Postgres + <c>EfTenantDbMigrator</c> +
/// <c>SchemaRoutingFactory</c>/<c>FakeTenantContext</c>).
///
/// <para>The PATCH bodies are DESERIALIZED FROM JSON rather than constructed,
/// because the whole point of AC3's tri-state is what the model binder does
/// with an absent key versus an explicit null — a hand-built DTO would test the
/// assertion and skip the mechanism.</para>
///
/// <para>REQUIRES DOCKER.</para>
/// </summary>
[TestFixture]
[Category("Docker")]
public class TrackerEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;
    private Guid _tenantId;
    private string _schema = null!;

    private DocumentTestData.SchemaRoutingFactory _factory = null!;
    private DocumentTestData.FakeTenantContext _tenantContext = null!;
    private ProjectRepository _projects = null!;
    private WorkItemRepository _workItems = null!;
    private TrackerPreferenceRepository _preferences = null!;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("tracker_api_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();

        _tenantId = Guid.NewGuid();
        _schema = TenantNaming.SchemaName(_tenantId);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(_schema));

        _factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString).Map(_tenantId, _schema);
        _tenantContext = new DocumentTestData.FakeTenantContext(_tenantId);
        _projects = new ProjectRepository(_factory, _tenantContext);
        _workItems = new WorkItemRepository(_factory, _tenantContext);
        _preferences = new TrackerPreferenceRepository(_factory, _tenantContext);
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
                           {_schema}.iterations, {_schema}.projects,
                           {_schema}.tracker_preferences CASCADE;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Fixture plumbing ───────────────────────────────────────────────────

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    private TrackerService Service(
        ITaskAudienceResolver? resolver = null, TammaMode mode = TammaMode.SingleUser) =>
        new(_projects, _workItems, _preferences,
            resolver ?? new InitiatorOnlyTaskAudienceResolver(),
            new StubModeProvider(mode), _tenantContext);

    private sealed class StubModeProvider(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    private static ClaimsPrincipal Principal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));

    private static DefaultHttpContext Context(string? ifMatch = null)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        if (ifMatch is not null)
            ctx.Request.Headers["If-Match"] = ifMatch;
        return ctx;
    }

    private static async Task<(int Status, JsonElement Body, string? ETag)> Exec(
        IResult result, DefaultHttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var body = string.IsNullOrWhiteSpace(raw)
            ? default
            : JsonDocument.Parse(raw).RootElement.Clone();
        var etag = ctx.Response.Headers.ETag.ToString();
        return (ctx.Response.StatusCode, body, string.IsNullOrEmpty(etag) ? null : etag);
    }

    private async Task<ProjectEntity> NewProjectAsync(
        string key = "TAM", string scale = "fibonacci") =>
        await _projects.CreateAsync(new ProjectEntity
        {
            Key = key,
            Name = $"{key} project",
            EstimateScale = scale,
        });

    private async Task<WorkItemEntity> NewItemAsync(Guid projectId, string title = "item") =>
        await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = projectId,
            Kind = "task",
            Status = "backlog",
            Title = title,
        });

    // ══════════════════ AC3 — the 43-0 regression guard ════════════════════

    [Test]
    public async Task Patch_touches_only_the_sent_field()
    {
        var project = await NewProjectAsync();
        var created = await Service().CreateWorkItemAsync(
            JsonSerializer.Deserialize<CreateWorkItemRequest>($$"""
            {
              "projectId": "{{project.Id}}",
              "title": "original",
              "kind": "story",
              "status": "in_progress",
              "priority": "high",
              "issueType": "feature",
              "description": "the original description",
              "estimate": 5,
              "externalRef": { "platformKind": "github", "number": 7 }
            }
            """, Json)!, Guid.NewGuid());

        var before = await _workItems.GetAsync(created.Id);

        var http = Context();
        var (status, _, _) = await Exec(
            await TrackerEndpoints.PatchWorkItem(
                created.Id,
                JsonSerializer.Deserialize<PatchWorkItemRequest>("""{"title":"renamed"}""", Json)!,
                Service(), http),
            http);
        status.Should().Be(StatusCodes.Status200OK);

        var after = await _workItems.GetAsync(created.Id);
        after!.Title.Should().Be("renamed");
        // EVERY other column byte-unchanged. This is the acceptance-rules
        // acceptorRequirement bug (epic-43 README:380-383) expressed as a test:
        // a defaulted full-body PUT resets exactly these.
        after.Description.Should().Be(before!.Description);
        after.Kind.Should().Be(before.Kind);
        after.Status.Should().Be(before.Status);
        after.Priority.Should().Be(before.Priority);
        after.IssueType.Should().Be(before.IssueType);
        after.Estimate.Should().Be(before.Estimate);
        after.ExternalRefJson.Should().Be(before.ExternalRefJson);
        after.AssigneeUserId.Should().Be(before.AssigneeUserId);
        after.IterationId.Should().Be(before.IterationId);
        after.Rank.Should().Be(before.Rank);
        after.SiblingRank.Should().Be(before.SiblingRank);
        after.Key.Should().Be(before.Key);
        after.Number.Should().Be(before.Number);
        after.ClosedAt.Should().Be(before.ClosedAt);
        after.Version.Should().Be(before.Version + 1, "one write, one version bump");
    }

    [Test]
    public async Task Patch_null_clears_and_absent_preserves()
    {
        var project = await NewProjectAsync();
        var created = await Service().CreateWorkItemAsync(
            JsonSerializer.Deserialize<CreateWorkItemRequest>($$"""
            {
              "projectId": "{{project.Id}}", "title": "t", "kind": "task",
              "priority": "high", "issueType": "bug", "description": "d", "estimate": 3
            }
            """, Json)!, null);

        // Explicit nulls CLEAR; the untouched fields survive.
        var http = Context();
        await Exec(
            await TrackerEndpoints.PatchWorkItem(
                created.Id,
                JsonSerializer.Deserialize<PatchWorkItemRequest>(
                    """{"priority":null,"description":null}""", Json)!,
                Service(), http),
            http);

        var cleared = await _workItems.GetAsync(created.Id);
        cleared!.Priority.Should().BeNull("an explicit null is 'clear this field'");
        cleared.Description.Should().BeNull();
        cleared.IssueType.Should().Be("bug", "an ABSENT field is 'leave it alone'");
        cleared.Estimate.Should().Be(3m);

        // And the mirror: an empty body changes nothing at all.
        var noop = Context();
        await Exec(
            await TrackerEndpoints.PatchWorkItem(
                created.Id, JsonSerializer.Deserialize<PatchWorkItemRequest>("{}", Json)!,
                Service(), noop),
            noop);
        var untouched = await _workItems.GetAsync(created.Id);
        untouched!.IssueType.Should().Be("bug");
        untouched.Estimate.Should().Be(3m);
        untouched.Title.Should().Be("t");
    }

    [Test]
    public async Task Patch_project_touches_only_the_sent_field()
    {
        var project = await _projects.CreateAsync(new ProjectEntity
        {
            Key = "TAM",
            Name = "original",
            Description = "keep me",
            EstimateScale = "fibonacci",
            RepositoryId = Guid.NewGuid(),
        });

        var http = Context();
        var (status, _, _) = await Exec(
            await TrackerEndpoints.PatchProject(
                project.Id,
                JsonSerializer.Deserialize<PatchProjectRequest>("""{"name":"renamed"}""", Json)!,
                Service(), http),
            http);
        status.Should().Be(StatusCodes.Status200OK);

        var after = await _projects.GetAsync(project.Id);
        after!.Name.Should().Be("renamed");
        after.Description.Should().Be("keep me");
        after.EstimateScale.Should().Be("fibonacci", "an omitted scale must not fall back to not_used");
        after.RepositoryId.Should().Be(project.RepositoryId);
        after.Key.Should().Be("TAM");
    }

    // ══════════════════ AC9 — optimistic concurrency ════════════════════════

    [Test]
    public async Task Lost_update_is_409()
    {
        var project = await NewProjectAsync();
        var item = await NewItemAsync(project.Id);
        item.Version.Should().Be(1);

        var first = Context(ifMatch: "\"1\"");
        var (firstStatus, _, firstETag) = await Exec(
            await TrackerEndpoints.PatchWorkItem(
                item.Id, JsonSerializer.Deserialize<PatchWorkItemRequest>(
                    """{"title":"writer one"}""", Json)!,
                Service(), first),
            first);
        firstStatus.Should().Be(StatusCodes.Status200OK);
        firstETag.Should().Be("\"2\"", "every single-resource response carries the new ETag");

        // The second writer read the same version and never saw writer one.
        var second = Context(ifMatch: "\"1\"");
        var (secondStatus, secondBody, _) = await Exec(
            await TrackerEndpoints.PatchWorkItem(
                item.Id, JsonSerializer.Deserialize<PatchWorkItemRequest>(
                    """{"title":"writer two"}""", Json)!,
                Service(), second),
            second);
        secondStatus.Should().Be(StatusCodes.Status409Conflict,
            "a stale If-Match is a refusal, never a silent overwrite");
        secondBody.GetProperty("code").GetString().Should().Be("TRACKER.CONCURRENCY_CONFLICT");
        secondBody.GetProperty("retryable").GetBoolean().Should().BeTrue(
            "44-1 types the conflict retryable and the wire must say so");

        (await _workItems.GetAsync(item.Id))!.Title.Should().Be("writer one",
            "the loser's write must not have landed");
    }

    [Test]
    public async Task If_match_star_and_absent_both_pass_and_junk_is_400()
    {
        var project = await NewProjectAsync();
        var item = await NewItemAsync(project.Id);

        var star = Context(ifMatch: "*");
        (await Exec(await TrackerEndpoints.PatchWorkItem(
            item.Id, JsonSerializer.Deserialize<PatchWorkItemRequest>("""{"title":"a"}""", Json)!,
            Service(), star), star)).Status.Should().Be(StatusCodes.Status200OK);

        var none = Context();
        (await Exec(await TrackerEndpoints.PatchWorkItem(
            item.Id, JsonSerializer.Deserialize<PatchWorkItemRequest>("""{"title":"b"}""", Json)!,
            Service(), none), none)).Status.Should().Be(StatusCodes.Status200OK);

        var junk = Context(ifMatch: "\"not-a-version\"");
        var (status, body, _) = await Exec(await TrackerEndpoints.PatchWorkItem(
            item.Id, JsonSerializer.Deserialize<PatchWorkItemRequest>("""{"title":"c"}""", Json)!,
            Service(), junk), junk);
        status.Should().Be(StatusCodes.Status400BadRequest,
            "an unparseable precondition must not be silently ignored — ignoring it IS the lost update");
        body.GetProperty("code").GetString().Should().Be("TRACKER.INVALID_IF_MATCH");
    }

    // ══════════════════ AC11 — validation, loud and ordinal ═════════════════

    [Test]
    public async Task Bad_vocabulary_is_400_naming_the_set()
    {
        var project = await NewProjectAsync();
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.CreateWorkItem(
                new CreateWorkItemRequest(project.Id, "t", "Epic", null, null, null, null, null, null, null, null, null),
                Service(), Principal(Guid.NewGuid()), http),
            http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.GetProperty("code").GetString().Should().Be("TRACKER.UNKNOWN_KIND");
        body.GetProperty("error").GetString().Should()
            .Contain("epic").And.Contain("story").And.Contain("task").And.Contain("spike",
                "the 400 names the accepted set — parsing is ordinal, 'Epic' is not coerced to 'epic'");
    }

    [Test]
    public async Task Kind_bug_is_rejected_pointing_at_the_issueType_field()
    {
        var project = await NewProjectAsync();
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.CreateWorkItem(
                new CreateWorkItemRequest(project.Id, "t", "bug", null, null, null, null, null, null, null, null, null),
                Service(), Principal(Guid.NewGuid()), http),
            http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.GetProperty("code").GetString().Should().Be("TRACKER.UNKNOWN_KIND");
        body.GetProperty("error").GetString().Should().Contain("issueType",
            "bug/chore are TriageIssueType members, not kinds (44-0 AC1) — the 400 says where to send it");
    }

    [Test]
    public async Task Priority_aliases_are_accepted_and_absent_priority_stays_null()
    {
        var project = await NewProjectAsync();
        var service = Service();

        var critical = await service.CreateWorkItemAsync(
            new CreateWorkItemRequest(project.Id, "a", "task", null, "critical", null, null, null, null, null, null, null), null);
        critical.Priority.Should().Be("urgent", "critical is a documented alias (TriageVocabulary)");

        var medium = await service.CreateWorkItemAsync(
            new CreateWorkItemRequest(project.Id, "b", "task", null, "medium", null, null, null, null, null, null, null), null);
        medium.Priority.Should().Be("normal");

        var unset = await service.CreateWorkItemAsync(
            new CreateWorkItemRequest(project.Id, "c", "task", null, null, null, null, null, null, null, null, null), null);
        unset.Priority.Should().BeNull(
            "absent priority stores null — 'nobody prioritised this' is a different fact from 'normal' (44-0 AC11)");
    }

    [Test]
    public async Task Estimate_under_a_not_used_scale_is_400()
    {
        var project = await NewProjectAsync("NOE", scale: "not_used");
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.CreateWorkItem(
                new CreateWorkItemRequest(project.Id, "t", "task", null, null, null, null, null, null, null, 5m, null),
                Service(), Principal(Guid.NewGuid()), http),
            http);

        status.Should().Be(StatusCodes.Status400BadRequest,
            "EstimateScale.AllowsEstimate is the shipped rule (44-0); this story calls it at the boundary");
        body.GetProperty("code").GetString().Should().Be("TRACKER.ESTIMATE_NOT_ALLOWED");

        // …and the same rule on the PATCH path.
        var ok = await Service().CreateWorkItemAsync(
            new CreateWorkItemRequest(project.Id, "t2", "task", null, null, null, null, null, null, null, null, null), null);
        var patch = Context();
        var (patchStatus, _, _) = await Exec(
            await TrackerEndpoints.PatchWorkItem(
                ok.Id, JsonSerializer.Deserialize<PatchWorkItemRequest>("""{"estimate":8}""", Json)!,
                Service(), patch),
            patch);
        patchStatus.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Invalid_project_key_is_400_and_is_never_normalized()
    {
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.CreateProject(
                new CreateProjectRequest("tam", "lower case", null, null, null),
                Service(), Principal(Guid.NewGuid()), http),
            http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.GetProperty("code").GetString().Should().Be("TRACKER.INVALID_WORK_ITEM_KEY");
        (await _projects.GetByKeyAsync("TAM")).Should().BeNull("a bad key is rejected, never upper-cased into a good one");
    }

    [Test]
    public async Task Assign_without_the_field_is_400_and_explicit_null_unassigns()
    {
        var project = await NewProjectAsync();
        var assignee = Guid.NewGuid();
        var item = await Service().CreateWorkItemAsync(
            new CreateWorkItemRequest(project.Id, "t", "task", null, null, null, null, null, null, assignee, null, null), null);

        var missing = Context();
        var (missingStatus, missingBody, _) = await Exec(
            await TrackerEndpoints.AssignWorkItem(
                item.Id, JsonSerializer.Deserialize<AssignRequest>("{}", Json)!, Service(), missing),
            missing);
        missingStatus.Should().Be(StatusCodes.Status400BadRequest);
        missingBody.GetProperty("code").GetString().Should().Be("TRACKER.MISSING_FIELD");
        (await _workItems.GetAsync(item.Id))!.AssigneeUserId.Should().Be(assignee,
            "a body missing the field must never silently unassign");

        var explicitNull = Context();
        (await Exec(
            await TrackerEndpoints.AssignWorkItem(
                item.Id, JsonSerializer.Deserialize<AssignRequest>("""{"assigneeUserId":null}""", Json)!,
                Service(), explicitNull),
            explicitNull)).Status.Should().Be(StatusCodes.Status200OK);
        (await _workItems.GetAsync(item.Id))!.AssigneeUserId.Should().BeNull();
    }

    // ══════════════════ Delete guards (plan D11) ════════════════════════════

    [Test]
    public async Task Delete_with_children_is_409_listing_them()
    {
        var project = await NewProjectAsync();
        var parent = await NewItemAsync(project.Id, "parent");
        var child = await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id, Kind = "task", Status = "backlog",
            Title = "child", ParentId = parent.Id,
        });

        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.DeleteWorkItem(parent.Id, Service(), http), http);

        status.Should().Be(StatusCodes.Status409Conflict);
        body.GetProperty("code").GetString().Should().Be("TRACKER.HAS_CHILDREN");
        body.GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("key").GetString())
            .Should().Contain(child.Key, "the 409 NAMES the blockers so the UI can offer a real choice");
        (await _workItems.GetAsync(parent.Id)).Should().NotBeNull("nothing cascaded");
    }

    [Test]
    public async Task Delete_of_a_non_empty_project_is_409_listing_its_work_items()
    {
        var project = await NewProjectAsync();
        var item = await NewItemAsync(project.Id);

        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.DeleteProject(project.Id, Service(), http), http);

        status.Should().Be(StatusCodes.Status409Conflict);
        body.GetProperty("code").GetString().Should().Be("TRACKER.PROJECT_NOT_EMPTY");
        body.GetProperty("workItems").EnumerateArray()
            .Select(w => w.GetProperty("key").GetString()).Should().Contain(item.Key);
        (await _projects.GetAsync(project.Id)).Should().NotBeNull();
    }

    [Test]
    public async Task Delete_of_a_leaf_work_item_succeeds()
    {
        var project = await NewProjectAsync();
        var item = await NewItemAsync(project.Id);

        var http = Context();
        (await Exec(await TrackerEndpoints.DeleteWorkItem(item.Id, Service(), http), http))
            .Status.Should().Be(StatusCodes.Status204NoContent);
        (await _workItems.GetAsync(item.Id)).Should().BeNull();
    }

    // ══════════════════ Listing: keyset paging + filters ════════════════════

    [Test]
    public async Task Keyset_paging_is_stable_under_concurrent_insertion()
    {
        var project = await NewProjectAsync();
        var originals = new List<WorkItemEntity>();
        for (var i = 0; i < 5; i++)
            originals.Add(await NewItemAsync(project.Id, $"item {i}"));

        var service = Service();
        var seen = new List<string>();

        var first = await service.ListWorkItemsAsync(new WorkItemListQuery { Limit = 2 }, null);
        seen.AddRange(first.Items.Select(i => i.Key));
        first.NextCursor.Should().NotBeNull();

        // A new item lands at the FRONT of the ordering between pages — the
        // exact mutation that makes OFFSET paging duplicate a row (plan D7).
        var intruder = await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id, Kind = "task", Status = "backlog",
            Title = "intruder", Rank = Tamma.Core.Tracking.Rank.Prepend(originals[0].Rank),
        });

        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await service.ListWorkItemsAsync(
                new WorkItemListQuery { Limit = 2, Cursor = cursor }, null);
            seen.AddRange(page.Items.Select(i => i.Key));
            cursor = page.Items.Count == 0 ? null : page.NextCursor;
        }

        seen.Should().OnlyHaveUniqueItems("keyset paging never returns a row twice");
        seen.Should().Contain(originals.Select(o => o.Key),
            "and never skips a row that did not move");
        seen.Should().NotContain(intruder.Key,
            "a row inserted BEFORE the cursor is legitimately not in this iteration — "
            + "the guarantee is no-dup/no-skip for rows that did not move");
    }

    /// <summary>
    /// Plan test 16 as SPECIFIED — "page, re-rank mid-set, page again — no dup,
    /// no skip". Shipped originally as
    /// <see cref="Keyset_paging_is_stable_under_concurrent_insertion"/>, an
    /// INSERTION test, which leaves the RE-RANK half unexercised; amendment A1
    /// is itself about a row MOVING across an already-served page boundary, so
    /// the substituted test was the one that would have exercised A1's own
    /// stated failure mode (44-2 conformance round, 2026-07-29).
    ///
    /// <para>Re-ranking is driven at the repository seam
    /// (<c>IWorkItemRepository.SetRanksAsync</c>) because 44-2 ships no ranking
    /// ROUTE — <c>PatchWorkItemRequest</c> has no <c>rank</c> field and 44-3
    /// owns the endpoints. That is the same level the insertion test already
    /// drives its intruder at, and the paging under test is unaffected.</para>
    ///
    /// <para>The guarantee asserted is the honest one, matching the insertion
    /// test: rows that did NOT move are never duplicated and never skipped. A
    /// row that moves ACROSS the cursor is legitimately re-delivered or missed —
    /// that is inherent to keyset paging over a mutating ordered set, and it is
    /// still strictly better than offset paging, which corrupts the pages of
    /// rows that did not move at all.</para>
    /// </summary>
    [Test]
    public async Task Keyset_paging_is_stable_under_reorder()
    {
        var project = await NewProjectAsync();
        var originals = new List<WorkItemEntity>();
        for (var i = 0; i < 6; i++)
            originals.Add(await NewItemAsync(project.Id, $"item {i}"));

        var service = Service();
        var seen = new List<string>();

        var first = await service.ListWorkItemsAsync(new WorkItemListQuery { Limit = 2 }, null);
        seen.AddRange(first.Items.Select(i => i.Key));
        seen.Should().BeEquivalentTo([originals[0].Key, originals[1].Key]);
        first.NextCursor.Should().NotBeNull();

        // (a) A still-unseen row is dragged to the FRONT, behind the cursor the
        //     caller already holds. This is the mutation the plan named.
        var movedBack = await _workItems.SetRanksAsync(
            originals[4].Id, Tamma.Core.Tracking.Rank.Prepend(originals[0].Rank), null);
        movedBack.Should().NotBeNull();

        // (b) A still-unseen row is dragged WITHIN the unseen region — it must
        //     be delivered exactly once regardless of where it lands.
        var movedWithin = await _workItems.SetRanksAsync(
            originals[2].Id,
            Tamma.Core.Tracking.Rank.Between(originals[3].Rank, originals[5].Rank),
            null);
        movedWithin.Should().NotBeNull();

        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await service.ListWorkItemsAsync(
                new WorkItemListQuery { Limit = 2, Cursor = cursor }, null);
            seen.AddRange(page.Items.Select(i => i.Key));
            cursor = page.Items.Count == 0 ? null : page.NextCursor;
        }

        seen.Should().OnlyHaveUniqueItems(
            "a re-rank must never cause keyset paging to return a row twice");

        var unmoved = new[] { originals[0], originals[1], originals[3], originals[5] }
            .Select(o => o.Key);
        seen.Should().Contain(unmoved,
            "a row that did not move is never skipped, whatever its neighbours did");
        seen.Should().Contain(originals[2].Key,
            "a row re-ranked WITHIN the unseen region is still delivered exactly once");
        seen.Should().NotContain(originals[4].Key,
            "a row dragged BEHIND the cursor is legitimately not in this iteration — "
            + "the guarantee is no-dup/no-skip for rows that did not move");
    }

    /// <summary>
    /// Amendment A1's PREMISE, made executable (44-2 conformance round): the
    /// <c>(Rank, Key)</c> cursor is a total order only while ranks are unique
    /// within a project, and <b>nothing enforces that</b> —
    /// <c>SetRanksAsync</c> validates rank FORMAT and nothing else. This test
    /// pins today's permissiveness so 44-3 has something to flip when it either
    /// enforces uniqueness or moves the tie-break to an immutable column.
    /// It is deliberately NOT a demonstration of the resulting paging defect:
    /// that defect needs a rekey on top of the duplicate, and the rekey route
    /// is 44-3's.
    /// </summary>
    [Test]
    public async Task Duplicate_ranks_within_a_project_are_accepted_today()
    {
        var project = await NewProjectAsync();
        var a = await NewItemAsync(project.Id, "a");
        var b = await NewItemAsync(project.Id, "b");

        var collided = await _workItems.SetRanksAsync(b.Id, a.Rank, null);
        collided.Should().NotBeNull();
        collided!.Rank.Should().Be(a.Rank,
            "rank uniqueness within a project is an UNENFORCED invariant — the "
            + "(Rank, Key) keyset tie-break rests on it (amendment A1). 44-3 must "
            + "either enforce it or move the cursor to an immutable tie-break; "
            + "when it does, this test is the one to flip.");

        // The tie-break still yields a deterministic order today, because Key is
        // unique and the SQL ORDER BY carries it.
        var page = await Service().ListWorkItemsAsync(new WorkItemListQuery { Limit = 10 }, null);
        page.Items.Select(i => i.Key).Should().BeEquivalentTo(
            [a.Key, b.Key], o => o.WithStrictOrdering());
    }

    [Test]
    public async Task A_forged_cursor_is_400_rather_than_a_silent_restart()
    {
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.ListWorkItems(
                null, null, null, null, null, null, null, null, null, "!!!not-base64!!!", null,
                Service(), Principal(Guid.NewGuid())),
            http);

        status.Should().Be(StatusCodes.Status400BadRequest,
            "silently restarting at page 1 would re-deliver every row the caller already saw");
        body.GetProperty("code").GetString().Should().Be("TRACKER.INVALID_CURSOR");
    }

    [Test]
    public async Task List_filters_by_status_kind_and_external_link()
    {
        var project = await NewProjectAsync();
        await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id, Kind = "story", Status = "in_progress", Title = "native story",
        });
        await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id, Kind = "task", Status = "backlog", Title = "imported task",
            ExternalRefJson = """{"platformKind":"github","number":9}""",
        });

        var service = Service();
        (await service.ListWorkItemsAsync(new WorkItemListQuery { Statuses = ["in_progress"] }, null))
            .Items.Should().ContainSingle().Which.Title.Should().Be("native story");
        (await service.ListWorkItemsAsync(new WorkItemListQuery { Kinds = ["task"] }, null))
            .Items.Should().ContainSingle().Which.Title.Should().Be("imported task");
        (await service.ListWorkItemsAsync(new WorkItemListQuery { ExternalLinked = true }, null))
            .Items.Should().ContainSingle().Which.Title.Should().Be("imported task");
        (await service.ListWorkItemsAsync(new WorkItemListQuery { ExternalLinked = false }, null))
            .Items.Should().ContainSingle().Which.Title.Should().Be("native story");
        (await service.ListWorkItemsAsync(new WorkItemListQuery { TitleContains = "IMPORT" }, null))
            .Items.Should().ContainSingle().Which.Title.Should().Be("imported task");
    }

    [Test]
    public async Task A_typo_in_a_status_filter_is_400_not_an_empty_board()
    {
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.ListWorkItems(
                null, "in-progress", null, null, null, null, null, null, null, null, null,
                Service(), Principal(Guid.NewGuid())),
            http);
        status.Should().Be(StatusCodes.Status400BadRequest,
            "silently returning zero rows for a typo'd filter reads as data loss");
        body.GetProperty("code").GetString().Should().Be("TRACKER.UNKNOWN_STATUS");
    }

    [Test]
    public async Task Get_by_key_resolves_a_previous_key()
    {
        var project = await NewProjectAsync();
        var item = await NewItemAsync(project.Id);
        var original = item.Key;
        await _workItems.RekeyAsync(item.Id, "TAMMA-1");

        var http = Context();
        var (status, body, etag) = await Exec(
            await TrackerEndpoints.GetWorkItemByKey(original, Service(), http), http);

        status.Should().Be(StatusCodes.Status200OK,
            "already-written DocumentInstance.IssueId / DCB tags must keep resolving (44-0 AC8)");
        body.GetProperty("key").GetString().Should().Be("TAMMA-1");
        body.GetProperty("previousKeys").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(original);
        etag.Should().NotBeNull();
    }

    [Test]
    public async Task Work_item_response_carries_the_derived_status_category_and_parsed_external_ref()
    {
        var project = await NewProjectAsync();
        var item = await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id, Kind = "task", Status = "in_review", Title = "t",
            ExternalRefJson = """{"platformKind":"github","number":11}""",
        });

        var http = Context();
        var (_, body, _) = await Exec(await TrackerEndpoints.GetWorkItem(item.Id, Service(), http), http);

        body.GetProperty("statusCategory").GetString().Should().Be("started",
            "grouping is derived by WorkItemStatusCategoryExtensions.Category, never a set literal");
        body.GetProperty("externalRef").GetProperty("number").GetInt32().Should().Be(11,
            "the jsonb column rides the wire as JSON, not as an escaped string");
    }

    // ══════════════════ AC6 — the assignee picker ═══════════════════════════

    [Test]
    public async Task Empty_resolver_falls_back_to_membership()
    {
        var tenant = Guid.NewGuid();
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        var resolver = new TrackerAssigneeResolver(
            // The REAL shipped stub — not a fake. This test fails the day
            // someone removes the fallback and trusts EligibleAudienceAsync.
            new InitiatorOnlyTaskAudienceResolver(),
            new StubModeProvider(TammaMode.SaaS),
            new FakeMemberships([(memberA, "owner"), (memberB, "member")]),
            new FakeSoleUser(Guid.NewGuid()));

        var result = await resolver.ResolveAsync(tenant, Guid.NewGuid());

        result.Source.Should().Be("tenant-membership");
        result.Members.Should().NotBeEmpty("an empty picker reads as a bug and generates a support ticket");
        result.Members.Select(m => m.UserId).Should().BeEquivalentTo([memberA, memberB]);
    }

    [Test]
    public async Task Real_resolver_wins()
    {
        var tenant = Guid.NewGuid();
        var eligible = Guid.NewGuid();
        var resolver = new TrackerAssigneeResolver(
            new FakeAudienceResolver(audience: [new AudienceMember(eligible, "member")]),
            new StubModeProvider(TammaMode.SaaS),
            new FakeMemberships([(Guid.NewGuid(), "owner")]),
            new FakeSoleUser(Guid.NewGuid()));

        var result = await resolver.ResolveAsync(tenant, Guid.NewGuid());

        result.Source.Should().Be("audience-resolver",
            "when 39-20's real resolver answers, its answer wins with no code change here");
        result.Members.Should().ContainSingle().Which.UserId.Should().Be(eligible);
    }

    [Test]
    public async Task Single_user_mode_returns_the_sole_user()
    {
        var sole = Guid.NewGuid();
        var resolver = new TrackerAssigneeResolver(
            new InitiatorOnlyTaskAudienceResolver(),
            new StubModeProvider(TammaMode.SingleUser),
            new FakeMemberships([]),
            new FakeSoleUser(sole));

        var result = await resolver.ResolveAsync(tenantId: null, callerUserId: null);

        result.Source.Should().Be("single-user");
        result.Members.Should().ContainSingle().Which.UserId.Should().Be(sole);
    }

    // ══════════════════ AC7 — visibility, both branches ═════════════════════

    [Test]
    public async Task Stub_resolver_yields_tenant_scope()
    {
        var project = await NewProjectAsync();
        await NewItemAsync(project.Id, "someone else's item");

        // SaaS + the REAL shipped stub. Applying CanSeeAsync here would empty
        // the list, because the stub keys entirely on InitiatorUserId.
        var page = await Service(new InitiatorOnlyTaskAudienceResolver(), TammaMode.SaaS)
            .ListWorkItemsAsync(new WorkItemListQuery(), Guid.NewGuid());

        page.VisibilityMode.Should().Be("tenant");
        page.Items.Should().NotBeEmpty("an empty backlog reads as data loss — honest degradation instead");
    }

    [Test]
    public async Task Real_resolver_filters_per_user()
    {
        var project = await NewProjectAsync();
        var viewer = Guid.NewGuid();
        var mine = await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id, Kind = "task", Status = "backlog",
            Title = "mine", CreatedByUserId = viewer,
        });
        await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id, Kind = "task", Status = "backlog",
            Title = "theirs", CreatedByUserId = Guid.NewGuid(),
        });

        // A fake "real" resolver: the initiator-matches rule, but a DIFFERENT
        // type, so the stub check does not fire. This is the branch that lights
        // up the day 39-20 swaps the DI registration.
        var page = await Service(new FakeAudienceResolver(audience: []), TammaMode.SaaS)
            .ListWorkItemsAsync(new WorkItemListQuery(), viewer);

        page.VisibilityMode.Should().Be("per-user");
        page.Items.Should().ContainSingle().Which.Id.Should().Be(mine.Id);
    }

    // ══════════════════ AC8 — preferences, both modes ═══════════════════════

    [Test]
    public async Task SingleUser_preferences_key_on_user_id()
    {
        var userId = Guid.NewGuid();
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.PutPreferences(
                new UpsertTrackerPreferencesRequest(null, "story", "priority"),
                Service(), Principal(userId), _tenantContext,
                new StubModeProvider(TammaMode.SingleUser), http),
            http);

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("source").GetString().Should().Be("principal-override");

        var stored = await _preferences.GetAsync(userId);
        stored.Should().NotBeNull();
        stored!.UserId.Should().Be(userId);
        stored.TenantId.Should().BeNull("the strong XOR permits exactly one principal key");
        stored.DefaultKind.Should().Be("story");
    }

    [Test]
    public async Task SaaS_preferences_key_on_tenant_id()
    {
        var http = Context();
        await Exec(
            await TrackerEndpoints.PutPreferences(
                new UpsertTrackerPreferencesRequest(null, "spike", "assignee"),
                Service(), Principal(Guid.NewGuid()), _tenantContext,
                new StubModeProvider(TammaMode.SaaS), http),
            http);

        var stored = await _preferences.GetByTenantAsync(_tenantId);
        stored.Should().NotBeNull();
        stored!.TenantId.Should().Be(_tenantId);
        stored.UserId.Should().BeNull();
        stored.DefaultKind.Should().Be("spike");
    }

    [Test]
    public async Task Preference_planes_never_join()
    {
        var userId = Guid.NewGuid();
        var http = Context();
        await Exec(
            await TrackerEndpoints.PutPreferences(
                new UpsertTrackerPreferencesRequest(null, "story", "priority"),
                Service(), Principal(userId), _tenantContext,
                new StubModeProvider(TammaMode.SingleUser), http),
            http);

        // The SaaS surface must not see the user-plane row, and vice versa.
        (await _preferences.GetByTenantAsync(_tenantId)).Should().BeNull();

        var saasRead = Context();
        var (_, saasBody, _) = await Exec(
            await TrackerEndpoints.GetPreferences(
                Service(), Principal(userId), _tenantContext,
                new StubModeProvider(TammaMode.SaaS), saasRead),
            saasRead);
        saasBody.GetProperty("source").GetString().Should().Be("system-default",
            "a user-plane row is invisible to the tenant surface — the planes are parallel, never joined");
    }

    [Test]
    public async Task Get_preferences_resolves_defaults_and_delete_falls_back_to_them()
    {
        var userId = Guid.NewGuid();
        var mode = new StubModeProvider(TammaMode.SingleUser);

        var initial = Context();
        var (_, defaults, _) = await Exec(
            await TrackerEndpoints.GetPreferences(Service(), Principal(userId), _tenantContext, mode, initial),
            initial);
        defaults.GetProperty("source").GetString().Should().Be("system-default");
        defaults.GetProperty("defaultKind").GetString().Should().Be(TrackerPreferenceDefaults.DefaultKind);
        defaults.GetProperty("boardGroupBy").GetString().Should().Be(TrackerPreferenceDefaults.BoardGroupBy);

        var put = Context();
        await Exec(
            await TrackerEndpoints.PutPreferences(
                new UpsertTrackerPreferencesRequest(null, "epic", "kind"),
                Service(), Principal(userId), _tenantContext, mode, put),
            put);

        var del = Context();
        var (deleteStatus, _, _) = await Exec(
            await TrackerEndpoints.DeletePreferences(Service(), Principal(userId), _tenantContext, mode, del),
            del);
        deleteStatus.Should().Be(StatusCodes.Status200OK);

        var after = Context();
        var (_, resolvedAgain, _) = await Exec(
            await TrackerEndpoints.GetPreferences(Service(), Principal(userId), _tenantContext, mode, after),
            after);
        resolvedAgain.GetProperty("source").GetString().Should().Be("system-default",
            "DELETE removes the row so the shipped defaults take over (the AcceptanceRulesService posture)");
    }

    /// <summary>
    /// AC9 on the ONE route that used to be exempt (44-2 conformance round,
    /// 2026-07-29): <c>DELETE /api/tracker/preferences</c> never read
    /// <c>If-Match</c>, so a reset racing a concurrent save silently discarded
    /// that save. All four established semantics are asserted here, because a
    /// route that honours the header only partly is the harder bug to find.
    /// </summary>
    [Test]
    public async Task Delete_preferences_honours_if_match()
    {
        var userId = Guid.NewGuid();
        var mode = new StubModeProvider(TammaMode.SingleUser);

        var put = Context();
        var (_, _, etag) = await Exec(
            await TrackerEndpoints.PutPreferences(
                new UpsertTrackerPreferencesRequest(null, "epic", "kind"),
                Service(), Principal(userId), _tenantContext, mode, put),
            put);
        etag.Should().Be("\"1\"");

        // (a) junk precondition — 400, never silently ignored.
        var junk = Context(ifMatch: "\"not-a-version\"");
        var (junkStatus, junkBody, _) = await Exec(
            await TrackerEndpoints.DeletePreferences(
                Service(), Principal(userId), _tenantContext, mode, junk),
            junk);
        junkStatus.Should().Be(StatusCodes.Status400BadRequest);
        junkBody.GetProperty("code").GetString().Should().Be("TRACKER.INVALID_IF_MATCH");

        // (b) stale precondition — 409 retryable, and the row survives.
        //     A concurrent editor bumps the row to v2 while this caller still
        //     holds the v1 ETag; the reset must lose, not erase their save.
        await Service().UpsertPreferencesAsync(
            userId, new UpsertTrackerPreferencesRequest(null, "story", "status"), userId);

        var stale = Context(ifMatch: "\"1\"");
        var (staleStatus, staleBody, _) = await Exec(
            await TrackerEndpoints.DeletePreferences(
                Service(), Principal(userId), _tenantContext, mode, stale),
            stale);
        staleStatus.Should().Be(StatusCodes.Status409Conflict,
            "deleting the override discards the concurrent edit; a stale If-Match must refuse");
        staleBody.GetProperty("code").GetString().Should().Be("TRACKER.CONCURRENCY_CONFLICT");
        staleBody.GetProperty("retryable").GetBoolean().Should().BeTrue();
        (await _preferences.GetAsync(userId)).Should().NotBeNull(
            "the loser's delete must not have landed");

        // (c) current precondition — passes.
        var current = Context(ifMatch: "\"2\"");
        (await Exec(
            await TrackerEndpoints.DeletePreferences(
                Service(), Principal(userId), _tenantContext, mode, current),
            current)).Status.Should().Be(StatusCodes.Status200OK);
        (await _preferences.GetAsync(userId)).Should().BeNull();
    }

    /// <summary>
    /// The opt-out half of AC9 on the same route: an absent <c>If-Match</c>
    /// still means "no precondition" (and <c>*</c> passes) — tightening the
    /// route must not turn every unconditional reset into a 409/428.
    /// </summary>
    [Test]
    public async Task Delete_preferences_without_if_match_still_opts_out()
    {
        var userId = Guid.NewGuid();
        var mode = new StubModeProvider(TammaMode.SingleUser);

        // Drive the row to v3 so an accidental "expected 1" default would fail.
        var svc = Service();
        foreach (var kind in new[] { "epic", "story", "task" })
        {
            await svc.UpsertPreferencesAsync(
                userId, new UpsertTrackerPreferencesRequest(null, kind, "status"), userId);
        }
        (await _preferences.GetAsync(userId))!.Version.Should().Be(3);

        var none = Context();
        (await Exec(
            await TrackerEndpoints.DeletePreferences(
                Service(), Principal(userId), _tenantContext, mode, none),
            none)).Status.Should().Be(StatusCodes.Status200OK,
            "an absent If-Match opts OUT of the precondition — that is the established semantics");
        (await _preferences.GetAsync(userId)).Should().BeNull();

        // `*` is "any current version", and there must be one: a second delete
        // has nothing left to remove, so it is a 404, not a 409.
        await svc.UpsertPreferencesAsync(
            userId, new UpsertTrackerPreferencesRequest(null, "spike", "status"), userId);
        var star = Context(ifMatch: "*");
        (await Exec(
            await TrackerEndpoints.DeletePreferences(
                Service(), Principal(userId), _tenantContext, mode, star),
            star)).Status.Should().Be(StatusCodes.Status200OK);

        var gone = Context(ifMatch: "*");
        (await Exec(
            await TrackerEndpoints.DeletePreferences(
                Service(), Principal(userId), _tenantContext, mode, gone),
            gone)).Status.Should().Be(StatusCodes.Status404NotFound,
            "no override to delete is a 404, unchanged by the precondition");
    }

    [Test]
    public async Task Preferences_reject_an_unknown_default_kind()
    {
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.PutPreferences(
                new UpsertTrackerPreferencesRequest(null, "Epic", null),
                Service(), Principal(Guid.NewGuid()), _tenantContext,
                new StubModeProvider(TammaMode.SingleUser), http),
            http);

        status.Should().Be(StatusCodes.Status400BadRequest);
        body.GetProperty("code").GetString().Should().Be("TRACKER.UNKNOWN_KIND");
    }


    // ══════════════ AC9 — concurrency, PROVED CONCURRENTLY ══════════════════
    // Review MAJOR-2 / MODERATE-3 / MINOR-7 (2026-07-29). `Lost_update_is_409`
    // above is SEQUENTIAL: writer one completes before writer two starts, so it
    // would pass against a pure check-then-write with no atomic guard at all —
    // which is exactly what projects and preferences had. The tests below are
    // the ones that discriminate. Two shapes, deliberately:
    //
    //   (a) DETERMINISTIC, at the repository seam — reproduces the precise
    //       interleaving W2.read(v1) → W1 completes(v2) → W2.repo-read(v2) →
    //       W2 writes v3. The service check cannot see it (the service already
    //       read v1) and the EF token alone cannot see it (the repository
    //       re-read v2), so ONLY the plumbed-through precondition catches it.
    //
    //   (b) GENUINELY CONCURRENT, at the handler seam — two writers whose reads
    //       both precede either write, run under Task.WhenAll. This is the
    //       reviewer's own reproduction: before the fix BOTH returned 200 for
    //       projects and preferences, and the first writer's rename was
    //       silently reverted.

    [Test]
    public async Task Work_item_stale_precondition_loses_even_when_the_repository_reread_is_fresh()
    {
        var project = await NewProjectAsync();
        var item = await NewItemAsync(project.Id);
        item.Version.Should().Be(1);

        // W2 reads v1 and holds the snapshot.
        var writerTwoSnapshot = await _workItems.GetAsync(item.Id);
        writerTwoSnapshot!.Version.Should().Be(1);
        writerTwoSnapshot.Title = "writer two";

        // W1 completes: the row is now v2.
        var afterOne = await _workItems.UpdateAsync(
            new WorkItemEntity
            {
                Id = item.Id, ProjectId = item.ProjectId, Kind = item.Kind, Status = item.Status,
                Title = "writer one", Key = item.Key, Number = item.Number,
                Rank = item.Rank, SiblingRank = item.SiblingRank,
            },
            expectedVersion: 1);
        afterOne!.Version.Should().Be(2);

        // W2 now writes. Its repository re-read WILL see v2 — the EF token on
        // its own would be satisfied. The precondition it asserted (v1) is what
        // must refuse it.
        var writerTwo = async () => await _workItems.UpdateAsync(writerTwoSnapshot, expectedVersion: 1);

        (await writerTwo.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("TRACKER.CONCURRENCY_CONFLICT");

        (await _workItems.GetAsync(item.Id))!.Title.Should().Be("writer one",
            "the loser's write must not have landed");
    }

    [Test]
    public async Task Project_stale_precondition_loses_even_when_the_repository_reread_is_fresh()
    {
        var project = await NewProjectAsync();
        project.Version.Should().Be(1);

        var writerTwoSnapshot = await _projects.GetAsync(project.Id);
        writerTwoSnapshot!.Name = "writer two";

        var afterOne = await _projects.UpdateAsync(
            new ProjectEntity
            {
                Id = project.Id, Key = project.Key, Name = "writer one",
                EstimateScale = project.EstimateScale,
            },
            expectedVersion: 1);
        afterOne!.Version.Should().Be(2);

        var writerTwo = async () => await _projects.UpdateAsync(writerTwoSnapshot, expectedVersion: 1);

        (await writerTwo.Should().ThrowAsync<TammaError>())
            .Which.Code.Should().Be("TRACKER.CONCURRENCY_CONFLICT");

        (await _projects.GetAsync(project.Id))!.Name.Should().Be("writer one",
            "before the fix ProjectEntity.Version was not an EF concurrency token and "
            + "ProjectRepository.UpdateAsync copied the stale snapshot's columns wholesale, "
            + "so writer one's rename was silently reverted");
    }

    [Test]
    public async Task Concurrent_project_patches_with_the_same_if_match_produce_exactly_one_winner()
    {
        var project = await NewProjectAsync();
        project.Version.Should().Be(1);

        // Both writers read v1 BEFORE either writes — that is what the shared
        // `If-Match: "1"` encodes — and both requests are in flight together.
        async Task<(int Status, JsonElement Body, string? ETag)> Patch(string name)
        {
            var http = Context(ifMatch: "\"1\"");
            return await Exec(
                await TrackerEndpoints.PatchProject(
                    project.Id,
                    JsonSerializer.Deserialize<PatchProjectRequest>($$"""{"name":"{{name}}"}""", Json)!,
                    Service(), http),
                http);
        }

        var results = await Task.WhenAll(Patch("writer one"), Patch("writer two"));

        results.Count(r => r.Status == StatusCodes.Status200OK).Should().Be(1,
            "exactly one writer may win — the reviewer proved BOTH returned 200 before the fix");
        var loser = results.Single(r => r.Status != StatusCodes.Status200OK);
        loser.Status.Should().Be(StatusCodes.Status409Conflict);
        loser.Body.GetProperty("code").GetString().Should().Be("TRACKER.CONCURRENCY_CONFLICT");
        loser.Body.GetProperty("retryable").GetBoolean().Should().BeTrue();

        var winnerName = results.Single(r => r.Status == StatusCodes.Status200OK)
            .Body.GetProperty("name").GetString();
        var stored = await _projects.GetAsync(project.Id);
        stored!.Name.Should().Be(winnerName, "the loser's rename must not have landed on top");
        stored.Version.Should().Be(2, "one winner, one version bump");
    }

    [Test]
    public async Task Concurrent_preference_puts_with_the_same_if_match_produce_exactly_one_winner()
    {
        var userId = Guid.NewGuid();

        // Seed the row so both writers have a v1 to assert against.
        var seeded = await Service().UpsertPreferencesAsync(
            userId, new UpsertTrackerPreferencesRequest(null, "task", "status"), userId);
        seeded.Version.Should().Be(1);

        async Task<(int Status, JsonElement Body, string? ETag)> Put(string groupBy)
        {
            var http = Context(ifMatch: "\"1\"");
            return await Exec(
                await TrackerEndpoints.PutPreferences(
                    new UpsertTrackerPreferencesRequest(null, "task", groupBy),
                    Service(), Principal(userId), _tenantContext,
                    new StubModeProvider(TammaMode.SingleUser), http),
                http);
        }

        var results = await Task.WhenAll(Put("assignee"), Put("kind"));

        results.Count(r => r.Status == StatusCodes.Status200OK).Should().Be(1,
            "tracker_preferences.Version was not an EF concurrency token either — the same "
            + "AC9 claim was false on this surface too");
        results.Single(r => r.Status != StatusCodes.Status200OK)
            .Body.GetProperty("code").GetString().Should().Be("TRACKER.CONCURRENCY_CONFLICT");

        var stored = await _preferences.GetAsync(userId);
        stored!.Version.Should().Be(2, "one winner, one version bump");
        stored.BoardGroupBy.Should().Be(
            results.Single(r => r.Status == StatusCodes.Status200OK)
                .Body.GetProperty("boardGroupBy").GetString());
    }

    [Test]
    public async Task Concurrent_status_and_assign_with_the_same_if_match_produce_exactly_one_winner()
    {
        // The other two read-check-then-separate-write shapes (MODERATE-3):
        // SetWorkItemStatusAsync and AssignWorkItemAsync.
        var project = await NewProjectAsync();
        var item = await NewItemAsync(project.Id);

        async Task<(int Status, JsonElement Body, string? ETag)> Move(string status)
        {
            var http = Context(ifMatch: "\"1\"");
            return await Exec(
                await TrackerEndpoints.SetWorkItemStatus(
                    item.Id, new SetStatusRequest(status), Service(), http),
                http);
        }

        var moves = await Task.WhenAll(Move("in_progress"), Move("blocked"));
        moves.Count(r => r.Status == StatusCodes.Status200OK).Should().Be(1);
        moves.Single(r => r.Status != StatusCodes.Status200OK)
            .Body.GetProperty("code").GetString().Should().Be("TRACKER.CONCURRENCY_CONFLICT");
        (await _workItems.GetAsync(item.Id))!.Version.Should().Be(2);

        var second = await NewItemAsync(project.Id, "second");
        async Task<(int Status, JsonElement Body, string? ETag)> Assign(Guid assignee)
        {
            var http = Context(ifMatch: "\"1\"");
            return await Exec(
                await TrackerEndpoints.AssignWorkItem(
                    second.Id,
                    JsonSerializer.Deserialize<AssignRequest>(
                        $$"""{"assigneeUserId":"{{assignee}}"}""", Json)!,
                    Service(), http),
                http);
        }

        var assigns = await Task.WhenAll(Assign(Guid.NewGuid()), Assign(Guid.NewGuid()));
        assigns.Count(r => r.Status == StatusCodes.Status200OK).Should().Be(1);
        assigns.Single(r => r.Status != StatusCodes.Status200OK)
            .Body.GetProperty("code").GetString().Should().Be("TRACKER.CONCURRENCY_CONFLICT");
        (await _workItems.GetAsync(second.Id))!.Version.Should().Be(2);
    }

    // ══════════════ MODERATE-4 — the racy delete pre-checks ═════════════════

    [Test]
    public async Task Deleting_a_project_that_gained_a_work_item_after_the_pre_check_is_409_not_500()
    {
        var project = await NewProjectAsync();
        await NewItemAsync(project.Id);

        // The seam: a service whose emptiness pre-check reports "empty" while
        // the row is not. That is precisely the state a work item created in the
        // gap between the pre-check and the DELETE leaves behind. Everything
        // else delegates to the real service, so the FK RESTRICT is tripped for
        // real by the real repository.
        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.DeleteProject(
                project.Id, new BlindPreCheckTrackerService(Service()), http),
            http);

        status.Should().Be(StatusCodes.Status409Conflict,
            "ProjectRepository.DeleteAsync's own comment promised the caller maps the constraint "
            + "violation to a 409; before this fix only TammaError was caught and PostgresException "
            + "23503 escaped as an unhandled 500");
        body.GetProperty("code").GetString().Should().Be("TRACKER.PROJECT_NOT_EMPTY");

        (await _projects.GetAsync(project.Id)).Should().NotBeNull("the refusal must not delete");
    }

    [Test]
    public async Task Deleting_a_work_item_that_gained_a_child_after_the_pre_check_is_409_not_500()
    {
        var project = await NewProjectAsync();
        var parent = await NewItemAsync(project.Id, "parent");
        await _workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = project.Id,
            Kind = "task",
            Status = "backlog",
            Title = "child",
            ParentId = parent.Id,
        });

        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.DeleteWorkItem(
                parent.Id, new BlindPreCheckTrackerService(Service()), http),
            http);

        status.Should().Be(StatusCodes.Status409Conflict);
        body.GetProperty("code").GetString().Should().Be("TRACKER.HAS_CHILDREN");

        (await _workItems.GetAsync(parent.Id)).Should().NotBeNull();
    }

    // ══════════════ MINOR-9 — coherence on the PROJECT write ════════════════

    [Test]
    public async Task Setting_a_project_to_not_used_while_it_holds_estimates_is_refused()
    {
        var project = await NewProjectAsync(scale: "fibonacci");
        await Service().CreateWorkItemAsync(
            JsonSerializer.Deserialize<CreateWorkItemRequest>($$"""
            {"projectId":"{{project.Id}}","title":"estimated","kind":"task","estimate":5}
            """, Json)!, null);

        var http = Context();
        var (status, body, _) = await Exec(
            await TrackerEndpoints.PatchProject(
                project.Id,
                JsonSerializer.Deserialize<PatchProjectRequest>(
                    """{"estimateScale":"not_used"}""", Json)!,
                Service(), http),
            http);

        status.Should().Be(StatusCodes.Status400BadRequest,
            "coherence was enforced on the work-item write only, so the incoherent state "
            + "(not_used scale + stored estimates) was reachable through the project write");
        body.GetProperty("code").GetString().Should().Be("TRACKER.ESTIMATE_NOT_ALLOWED");

        (await _projects.GetAsync(project.Id))!.EstimateScale.Should().Be("fibonacci",
            "the refusal must not have written the scale");
    }

    [Test]
    public async Task Setting_a_project_to_not_used_is_allowed_once_no_estimates_remain()
    {
        var project = await NewProjectAsync(scale: "fibonacci");
        await NewItemAsync(project.Id); // no estimate

        var http = Context();
        var (status, _, _) = await Exec(
            await TrackerEndpoints.PatchProject(
                project.Id,
                JsonSerializer.Deserialize<PatchProjectRequest>(
                    """{"estimateScale":"not_used"}""", Json)!,
                Service(), http),
            http);

        status.Should().Be(StatusCodes.Status200OK, "the guard blocks the incoherent case only");
        (await _projects.GetAsync(project.Id))!.EstimateScale.Should().Be("not_used");
    }

    // ══════════════ MINOR-8 — visibility keys on the CREATOR ════════════════

    [Test]
    public async Task Visibility_is_keyed_on_the_creator_not_the_assignee()
    {
        // PINS TODAY'S BEHAVIOUR, WHICH IS A KNOWN GAP — not an endorsement.
        // TrackerService builds TaskRef(tenantId, item.CreatedByUserId, …), and
        // TaskRef carries exactly ONE principal axis (Story 39-20 owns that
        // shape), so an item ASSIGNED TO the viewer but created by someone else
        // is filtered OUT of the viewer's own list the day a real resolver
        // replaces the stub. This test exists so that day is not a surprise: it
        // will FAIL when 39-20 widens the axis, and its failure is the reminder
        // to fix the keying at the same time.
        var project = await NewProjectAsync();
        var viewer = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();

        var minecreated = await Service().CreateWorkItemAsync(
            JsonSerializer.Deserialize<CreateWorkItemRequest>($$"""
            {"projectId":"{{project.Id}}","title":"I filed this","kind":"task"}
            """, Json)!, viewer);

        var assignedToMe = await Service().CreateWorkItemAsync(
            JsonSerializer.Deserialize<CreateWorkItemRequest>($$"""
            {"projectId":"{{project.Id}}","title":"assigned to me","kind":"task",
             "assigneeUserId":"{{viewer}}"}
            """, Json)!, someoneElse);

        var page = await Service(new FakeAudienceResolver([]), TammaMode.SaaS)
            .ListWorkItemsAsync(new WorkItemListQuery { ProjectId = project.Id }, viewer);

        page.VisibilityMode.Should().Be(TrackerService.VisibilityPerUser,
            "a non-stub resolver is registered, so the per-user branch is live");
        page.Items.Select(i => i.Id).Should().Contain(minecreated.Id);
        page.Items.Select(i => i.Id).Should().NotContain(assignedToMe.Id,
            "KNOWN GAP (review MINOR-8): the TaskRef is keyed on CreatedByUserId, so an item "
            + "assigned to the viewer but created by another user is filtered out of the "
            + "viewer's own list. Story 39-20 must widen TaskRef's principal axis when it "
            + "swaps the resolver — if this assertion starts failing, that is what happened.");
    }

    /// <summary>
    /// A <see cref="ITrackerService"/> decorator whose DELETE PRE-CHECKS report
    /// "nothing blocks this" while the database says otherwise — the exact state
    /// a row created between the pre-check and the DELETE produces. Everything
    /// else delegates, so the FK RESTRICT is tripped by the real repository and
    /// the endpoint's 23503 mapping is exercised end to end (review MODERATE-4).
    /// </summary>
    private sealed class BlindPreCheckTrackerService(ITrackerService inner) : ITrackerService
    {
        public Task<IReadOnlyList<WorkItemEntity>> ProjectWorkItemsAsync(Guid projectId, int limit) =>
            Task.FromResult<IReadOnlyList<WorkItemEntity>>([]);

        public Task<IReadOnlyList<WorkItemEntity>> ChildrenAsync(Guid id, int limit) =>
            Task.FromResult<IReadOnlyList<WorkItemEntity>>([]);

        public Task<IReadOnlyList<ProjectEntity>> ListProjectsAsync(bool includeArchived) => inner.ListProjectsAsync(includeArchived);
        public Task<ProjectEntity?> GetProjectAsync(Guid projectId) => inner.GetProjectAsync(projectId);
        public Task<ProjectEntity> CreateProjectAsync(CreateProjectRequest request, Guid? createdByUserId) => inner.CreateProjectAsync(request, createdByUserId);
        public Task<ProjectEntity?> PatchProjectAsync(Guid projectId, PatchProjectRequest request, int? ifMatchVersion) => inner.PatchProjectAsync(projectId, request, ifMatchVersion);
        public Task<bool> DeleteProjectAsync(Guid projectId, int? ifMatchVersion) => inner.DeleteProjectAsync(projectId, ifMatchVersion);
        public Task<WorkItemEntity?> GetWorkItemAsync(Guid id) => inner.GetWorkItemAsync(id);
        public Task<WorkItemEntity?> GetWorkItemByKeyAsync(string key) => inner.GetWorkItemByKeyAsync(key);
        public Task<WorkItemPage> ListWorkItemsAsync(WorkItemListQuery query, Guid? viewerUserId) => inner.ListWorkItemsAsync(query, viewerUserId);
        public Task<WorkItemEntity> CreateWorkItemAsync(CreateWorkItemRequest request, Guid? createdByUserId) => inner.CreateWorkItemAsync(request, createdByUserId);
        public Task<WorkItemEntity?> PatchWorkItemAsync(Guid id, PatchWorkItemRequest request, int? ifMatchVersion) => inner.PatchWorkItemAsync(id, request, ifMatchVersion);
        public Task<WorkItemEntity?> SetWorkItemStatusAsync(Guid id, string statusWire, int? ifMatchVersion) => inner.SetWorkItemStatusAsync(id, statusWire, ifMatchVersion);
        public Task<WorkItemEntity?> AssignWorkItemAsync(Guid id, Guid? assigneeUserId, int? ifMatchVersion) => inner.AssignWorkItemAsync(id, assigneeUserId, ifMatchVersion);
        public Task<bool> DeleteWorkItemAsync(Guid id, int? ifMatchVersion) => inner.DeleteWorkItemAsync(id, ifMatchVersion);
        public Task<ResolvedTrackerPreferences> GetPreferencesAsync(Guid? userId) => inner.GetPreferencesAsync(userId);
        public Task<ResolvedTrackerPreferences> GetPreferencesForTenantAsync(Guid tenantId) => inner.GetPreferencesForTenantAsync(tenantId);
        public Task<ResolvedTrackerPreferences> UpsertPreferencesAsync(Guid? userId, UpsertTrackerPreferencesRequest request, Guid? actingUserId, int? ifMatchVersion = null) => inner.UpsertPreferencesAsync(userId, request, actingUserId, ifMatchVersion);
        public Task<ResolvedTrackerPreferences> UpsertPreferencesForTenantAsync(Guid tenantId, UpsertTrackerPreferencesRequest request, Guid? actingUserId, int? ifMatchVersion = null) => inner.UpsertPreferencesForTenantAsync(tenantId, request, actingUserId, ifMatchVersion);
        public Task<bool> DeletePreferencesAsync(Guid? userId, int? ifMatchVersion = null) => inner.DeletePreferencesAsync(userId, ifMatchVersion);
        public Task<bool> DeletePreferencesForTenantAsync(Guid tenantId, int? ifMatchVersion = null) => inner.DeletePreferencesForTenantAsync(tenantId, ifMatchVersion);
    }

    // ── Fakes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A DIFFERENT TYPE from <see cref="InitiatorOnlyTaskAudienceResolver"/>, so
    /// the stub check does not fire — this is what "39-20 has landed" looks
    /// like to every branch under test.
    /// </summary>
    private sealed class FakeAudienceResolver(IReadOnlyList<AudienceMember> audience) : ITaskAudienceResolver
    {
        public Task<bool> CanSeeAsync(Guid userId, TaskRef task) =>
            Task.FromResult(task.InitiatorUserId == userId);

        public Task<IReadOnlyList<AudienceMember>> EligibleAudienceAsync(TaskRef task, string roleWire) =>
            Task.FromResult(audience);
    }

    private sealed class FakeMemberships(IReadOnlyList<(Guid UserId, string Role)> rows)
        : ITenantMembershipRepository
    {
        public Task<List<TenantMembership>> ListAllByTenantAsync(Guid tenantId) =>
            Task.FromResult(rows
                .Select(r => new TenantMembership { TenantId = tenantId, UserId = r.UserId, Role = r.Role })
                .ToList());

        public Task<TenantMembership> AddAsync(Guid tenantId, Guid userId, string role) => throw new NotSupportedException();
        public Task RemoveAsync(Guid tenantId, Guid userId) => throw new NotSupportedException();
        public Task<string?> GetRoleAsync(Guid tenantId, Guid userId) => throw new NotSupportedException();
        public Task<(List<TenantMembership> Members, int Total)> ListByTenantAsync(Guid tenantId, int limit, int offset) => throw new NotSupportedException();
        public Task<List<TenantMembership>> GetUserTenantsAsync(Guid userId) => throw new NotSupportedException();
        public Task UpdateRoleAsync(Guid tenantId, Guid userId, string role) => throw new NotSupportedException();
        public Task<int> CountOwnersAsync(Guid tenantId) => throw new NotSupportedException();
        public Task<List<SoleOwnedTenant>> ListSoleOwnedTenantsAsync(Guid userId) => throw new NotSupportedException();
        public Task RemoveAllForUserAsync(Guid userId) => throw new NotSupportedException();
    }

    private sealed class FakeSoleUser(Guid id) : ISoleUserProvider
    {
        public Task<Guid> GetSoleUserIdAsync(CancellationToken ct = default) => Task.FromResult(id);
    }
}
