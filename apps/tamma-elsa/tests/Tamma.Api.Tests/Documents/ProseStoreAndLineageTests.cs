using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using System.Text.Json;
using Tamma.Api.Endpoints;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Data;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 41-1c AC1 (persistence half) / AC3 / AC7 — Postgres 17 Testcontainer
/// proof of the prose store path: the <c>audience</c> column round-trips
/// envelope → row → lineage read-back; the issue-scoped audience filter returns
/// exactly the tagged rows and <c>null</c> returns everything (the unchanged
/// pre-41-1c behaviour, provably); rows written BEFORE the migration (audience
/// NULL) still read back through both the repository and the lineage endpoint;
/// and a prose document without an audience cannot be WRITTEN (D8 — rejected at
/// the registry-validating write door, nothing persisted).
/// </summary>
[TestFixture]
public class ProseStoreAndLineageTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("prose_store_api_test")
            .WithUsername("tamma").WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    private async Task<(DocumentInstanceRepository Repo, Guid Tenant, string Schema)> NewRepoAsync()
    {
        var tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));
        var factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString).Map(tenant, schema);
        var repo = new DocumentInstanceRepository(factory, new DocumentTestData.FakeTenantContext(tenant));
        return (repo, tenant, schema);
    }

    private TenantDbContext NewContext(string schema) =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseNpgsql(CsFor(schema)).Options, Guid.NewGuid());

    // ── AC1 (persistence half) + AC3: audience round-trips as a column ────────

    [Test]
    public async Task InsertAsync_ProseEnvelope_PersistsTheAudienceColumn()
    {
        var (repo, tenant, schema) = await NewRepoAsync();
        var envelope = DocumentTestData.ProseEnvelope("issue-p1", audience: "engineering");
        envelope.Audience.Should().Be("engineering", "CreateDraft copies payload → envelope (D2)");

        var row = await repo.InsertAsync(tenant, envelope, null, CancellationToken.None);
        row.Audience.Should().Be("engineering");

        await using var ctx = NewContext(schema);
        var stored = await ctx.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == envelope.Id);
        stored.Audience.Should().Be("engineering", "the audience is a COLUMN, not a body-parse");
    }

    [Test]
    public async Task ListByIssueAsync_AudienceFilter_ReturnsOnlyTaggedRows_AndNullReturnsAll()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        const string issue = "issue-p2";

        var stakeholder = DocumentTestData.ProseEnvelope(issue, audience: "stakeholder", kind: "roadmap");
        var engineering = DocumentTestData.ProseEnvelope(issue, audience: "engineering", kind: "adr");
        var ops = DocumentTestData.ProseEnvelope(issue, audience: "ops", kind: "runbook");
        var nonProse = DocumentTestData.DecompositionEnvelope(issue);
        foreach (var e in new[] { stakeholder, engineering, ops, nonProse })
            await repo.InsertAsync(tenant, e, null, CancellationToken.None);

        // AC3 — the filter returns the stakeholder-tagged row and excludes the others.
        var filtered = await repo.ListByIssueAsync(tenant, issue, "stakeholder", CancellationToken.None);
        filtered.Should().ContainSingle().Which.Id.Should().Be(stakeholder.Id);

        // null ⇒ UNFILTERED — every existing caller's behaviour, provably unchanged.
        var all = await repo.ListByIssueAsync(tenant, issue, null, CancellationToken.None);
        all.Should().HaveCount(4);
        all.Single(r => r.Id == nonProse.Id).Audience.Should().BeNull(
            "a non-prose document carries no audience tag");
    }

    // ── AC3 — the 39-11 lineage endpoint filter ───────────────────────────────

    [Test]
    public async Task GetIssueLineage_AudienceQuery_FiltersAndCarriesAudienceOnEntries()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        const string issue = "issue-p3";
        var stakeholder = DocumentTestData.ProseEnvelope(issue, audience: "stakeholder", kind: "roadmap");
        var engineering = DocumentTestData.ProseEnvelope(issue, audience: "engineering", kind: "adr");
        await repo.InsertAsync(tenant, stakeholder, null, CancellationToken.None);
        await repo.InsertAsync(tenant, engineering, null, CancellationToken.None);

        var tc = new DocumentTestData.FakeTenantContext(tenant);
        var result = await DocumentEndpoints.GetIssueLineage(issue, repo, tc, CancellationToken.None, "stakeholder");
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(200);

        var root = await CaptureJson(result);
        var types = root.GetProperty("types");
        types.GetArrayLength().Should().Be(1);
        var entry = types[0].GetProperty("revisions")[0];
        entry.GetProperty("id").GetGuid().Should().Be(stakeholder.Id);
        entry.GetProperty("audience").GetString().Should().Be("stakeholder",
            "the lineage entry surfaces the audience tag (AC3)");

        // Unknown audience → 400, not an empty 200.
        var bad = await DocumentEndpoints.GetIssueLineage(issue, repo, tc, CancellationToken.None, "marketing");
        (bad as IStatusCodeHttpResult)!.StatusCode.Should().Be(400);
    }

    // ── AC7 — non-destructive migration: pre-41-1c rows read back with NULL ───

    [Test]
    public async Task PreMigrationRows_WithNullAudience_ReadBackThroughStoreAndLineage()
    {
        var (repo, tenant, schema) = await NewRepoAsync();
        const string issue = "issue-p4";

        // Seed a row DIRECTLY the way every pre-41-1c row exists: audience NULL.
        var legacyId = UuidV7.NewGuid();
        await using (var ctx = NewContext(schema))
        {
            ctx.Documents.Add(DocumentTestData.Row(
                legacyId, issue, DocumentTestData.DecompositionType, "accepted", 1,
                DocumentTestData.ValidDecompositionBody, tenant));
            await ctx.SaveChangesAsync();
        }

        var rows = await repo.ListByIssueAsync(tenant, issue, null, CancellationToken.None);
        rows.Should().ContainSingle().Which.Audience.Should().BeNull();

        var result = await DocumentEndpoints.GetIssueLineage(
            issue, repo, new DocumentTestData.FakeTenantContext(tenant), CancellationToken.None);
        var root = await CaptureJson(result);
        var entry = root.GetProperty("types")[0].GetProperty("revisions")[0];
        entry.GetProperty("id").GetGuid().Should().Be(legacyId);
        entry.GetProperty("audience").ValueKind.Should().Be(JsonValueKind.Null,
            "existing rows gain a NULL audience and still read back (AC7)");
    }

    // ── AC4/AC7 (D8) — a prose row without an audience cannot be written ──────

    [Test]
    public async Task InsertAsync_ProseWithoutAudience_IsRejected_NothingPersisted()
    {
        var (repo, tenant, schema) = await NewRepoAsync();
        var body = """{ "kind": "adr", "title": "Untagged", "body": "words" }""";
        var envelope = DocumentTestData.ProseEnvelope("issue-p5", body: body);
        envelope.Audience.Should().BeNull("the payload carries no audience to copy");

        var act = async () => await repo.InsertAsync(tenant, envelope, null, CancellationToken.None);
        var err = (await act.Should().ThrowAsync<TammaError>()).Which;
        err.Code.Should().Be("DOCUMENT.STORE.INVALID_BODY");
        err.Message.Should().Contain("PROSE_AUDIENCE_MISSING");

        await using var ctx = NewContext(schema);
        (await ctx.Documents.IgnoreQueryFilters().CountAsync(d => d.IssueId == "issue-p5")).Should().Be(0);
    }

    [Test]
    public async Task InsertAsync_EnvelopeAudienceDisagreesWithPayload_IsRejected()
    {
        var (repo, tenant, _) = await NewRepoAsync();
        var envelope = DocumentTestData.ProseEnvelope("issue-p6", audience: "engineering")
            with { Audience = "ops" };

        var act = async () => await repo.InsertAsync(tenant, envelope, null, CancellationToken.None);
        (await act.Should().ThrowAsync<TammaError>()).Which.Code
            .Should().Be("PROSE_AUDIENCE_ENVELOPE_MISMATCH");
    }

    // ── helper ────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> CaptureJson(IResult result)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        using var stream = new MemoryStream();
        ctx.Response.Body = stream;
        await result.ExecuteAsync(ctx);
        stream.Position = 0;
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }
}
