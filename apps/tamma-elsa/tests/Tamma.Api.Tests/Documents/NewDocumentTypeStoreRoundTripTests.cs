using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using System.Text.Json;
using Tamma.Api.Endpoints;
using Tamma.Core.Documents;
using Tamma.Data;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 41-1b AC3 — the six new document types must ROUND-TRIP through the store:
/// <c>DocumentEnvelope</c> → <see cref="DocumentInstanceRepository.InsertAsync"/> →
/// read back through <c>ListByIssueAsync</c> and the 39-11 lineage path, payload
/// intact and type resolving correctly. Postgres 17 Testcontainer (the same fixture
/// shape as <c>DocumentInstanceRepositoryTests</c> / <c>ProseStoreAndLineageTests</c>)
/// — EF InMemory models neither <c>jsonb</c> re-serialization nor the real column
/// widths, so only a real Postgres proves a body survives the round trip.
///
/// <para><b>Registry-wide sweep, not six hand-written copies.</b> The cases are
/// generated from <see cref="DocumentTypeRegistry.All"/>, so the SEVENTEENTH type
/// (and the eighteenth) is covered the moment it registers — and the pre-existing
/// 39-3/39-4 types plus 41-1c prose ride along for free. Each case's payload is the
/// type's own shipped valid <see cref="DocumentExample"/> (the same source the Core
/// registry example loop validates), so no sample body is invented here and none can
/// drift from its validator. <see cref="Sweep_covers_every_one_of_the_six_41_1b_types"/>
/// pins AC3's explicit six against the generated set, so a registry regression that
/// drops a type fails loudly instead of silently shrinking this suite.</para>
///
/// <para><b>Producer provenance is deliberately generic.</b> The store never couples
/// producer to document type (that binding is the agent taxonomy's job, covered by
/// <c>DocumentProducer</c>/<c>RolePhaseMap</c> tests); one taxonomy-valid cell keeps
/// the sweep type-agnostic.</para>
/// </summary>
[TestFixture]
public class NewDocumentTypeStoreRoundTripTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _baseConnectionString = null!;
    private DocumentInstanceRepository _repo = null!;
    private DocumentTestData.FakeTenantContext _tenantContext = null!;
    private Guid _tenant;

    /// <summary>
    /// AC3's explicit six (41-1b). Held as <see cref="DocumentTypeKey"/> members so
    /// the list cannot drift from the vocabulary at compile time.
    /// </summary>
    private static readonly DocumentTypeKey[] Epic41bKeys =
    {
        DocumentTypeKey.AcceptanceCriteria,
        DocumentTypeKey.BacklogOrdering,
        DocumentTypeKey.SprintPlan,
        DocumentTypeKey.TestPlan,
        DocumentTypeKey.ThreatModel,
        DocumentTypeKey.UxSpec,
    };

    /// <summary>Every registered type's wire key — the sweep's case source.</summary>
    public static IEnumerable<string> RegisteredTypeKeys =>
        DocumentTypeRegistry.All.Select(t => t.Key);

    // ── fixture (copied from DocumentInstanceRepositoryTests / ProseStoreAndLineageTests) ──

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("new_document_type_roundtrip_test")
            .WithUsername("tamma").WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _baseConnectionString = _postgres.GetConnectionString();

        // One migrated tenant schema for the whole sweep: every case writes to its
        // OWN issue id, so the reads stay isolated without paying a tenant migration
        // per case (the sweep is ~2 cases per registered type and grows with the
        // registry).
        _tenant = Guid.NewGuid();
        var schema = TenantNaming.SchemaName(_tenant);
        await new EfTenantDbMigrator().MigrateTenantAppAsync(CsFor(schema));
        var factory = new DocumentTestData.SchemaRoutingFactory(_baseConnectionString).Map(_tenant, schema);
        _tenantContext = new DocumentTestData.FakeTenantContext(_tenant);
        _repo = new DocumentInstanceRepository(factory, _tenantContext);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _postgres.DisposeAsync();

    private string CsFor(string schema) =>
        new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = schema }.ConnectionString;

    // ── AC3 — the sweep: envelope → store → ListByIssueAsync + lineage ────────

    [TestCaseSource(nameof(RegisteredTypeKeys))]
    public async Task EveryRegisteredType_RoundTripsThroughStoreAndLineage(string typeKey)
    {
        var type = DocumentTypeRegistry.Resolve(typeKey);
        var payloadJson = ValidExampleJson(type);
        var issueId = $"issue-rt-{typeKey}";
        var envelope = EnvelopeFor(typeKey, issueId, DocumentState.Accepted);

        var written = await _repo.InsertAsync(_tenant, envelope, null, CancellationToken.None);

        written.DocumentType.Should().Be(typeKey, "the envelope's type is persisted verbatim");
        written.SchemaVersion.Should().Be(type.SchemaVersion);
        written.Revision.Should().Be(1);
        written.Status.Should().Be("accepted");

        // ── read-back #1: ListByIssueAsync (unfiltered) ───────────────────────
        var listed = await _repo.ListByIssueAsync(_tenant, issueId, null, CancellationToken.None);
        var row = listed.Should().ContainSingle().Which;
        row.Id.Should().Be(envelope.Id);
        row.DocumentType.Should().Be(typeKey);
        DocumentTestData.SameJson(row.BodyJson, payloadJson).Should().BeTrue(
            $"the '{typeKey}' payload must survive the jsonb round trip intact");
        row.Audience.Should().Be(DocumentEnvelope.ReadPayloadAudience(envelope.Payload),
            "the audience column mirrors the payload (null for every type that carries no tag)");

        // The stored row resolves back to the SAME registered implementation, and the
        // stored body still passes that implementation's validator.
        var resolved = DocumentTypeRegistry.Resolve(row.DocumentType);
        resolved.Key.Should().Be(type.Key);
        resolved.PayloadClrType.Should().Be(type.PayloadClrType);
        using (var storedDoc = JsonDocument.Parse(row.BodyJson))
        {
            resolved.Validate(storedDoc.RootElement).IsValid.Should().BeTrue(
                $"the stored '{typeKey}' body must still validate on read (the D5 corruption tripwire)");
        }

        // ── read-back #2: the 39-11 lineage path ──────────────────────────────
        var result = await DocumentEndpoints.GetIssueLineage(
            issueId, _repo, _tenantContext, CancellationToken.None);
        (result as IStatusCodeHttpResult)!.StatusCode.Should().Be(200);

        var lineage = await CaptureJson(result);
        var entry = FindEntry(lineage, envelope.Id);
        entry.Should().NotBeNull($"the '{typeKey}' document must be reachable through the lineage read");
        entry!.Value.GetProperty("documentType").GetString().Should().Be(typeKey);
        entry.Value.GetProperty("revision").GetInt32().Should().Be(1);
        entry.Value.GetProperty("status").GetString().Should().Be("accepted");
        DocumentTestData.SameJson(entry.Value.GetProperty("body").GetRawText(), payloadJson)
            .Should().BeTrue($"the lineage read hands back the '{typeKey}' payload intact");
    }

    // ── AC3 — the sweep, revision half: a superseding revision of each type ───

    [TestCaseSource(nameof(RegisteredTypeKeys))]
    public async Task EveryRegisteredType_RevisionChain_ReadsBackThroughLineageAndLatestAccepted(string typeKey)
    {
        var payloadJson = ValidExampleJson(DocumentTypeRegistry.Resolve(typeKey));
        var issueId = $"issue-rev-{typeKey}";

        var r1 = await _repo.InsertAsync(
            _tenant, EnvelopeFor(typeKey, issueId, DocumentState.Accepted), null, CancellationToken.None);
        var r2 = await _repo.InsertAsync(
            _tenant, EnvelopeFor(typeKey, issueId, DocumentState.Accepted, supersedes: r1.Id),
            null, CancellationToken.None);

        r2.Revision.Should().Be(2);

        var listed = await _repo.ListByIssueAsync(_tenant, issueId, null, CancellationToken.None);
        listed.Should().HaveCount(2);
        listed.Single(r => r.Id == r1.Id).Status.Should().Be("superseded");
        listed.Single(r => r.Id == r2.Id).Status.Should().Be("accepted");
        listed.Should().OnlyContain(r => r.DocumentType == typeKey);

        // Latest-accepted keeps ≤1 row per type — the superseded revision never appears.
        var latest = await _repo.GetLatestAcceptedAsync(_tenant, issueId, CancellationToken.None);
        latest.Should().ContainSingle().Which.Id.Should().Be(r2.Id);

        // Both revisions are reachable through the lineage read, bodies intact.
        var lineage = await CaptureJson(await DocumentEndpoints.GetIssueLineage(
            issueId, _repo, _tenantContext, CancellationToken.None));
        foreach (var (id, revision) in new[] { (r1.Id, 1), (r2.Id, 2) })
        {
            var entry = FindEntry(lineage, id);
            entry.Should().NotBeNull($"revision {revision} of '{typeKey}' must appear in the lineage");
            entry!.Value.GetProperty("revision").GetInt32().Should().Be(revision);
            entry.Value.GetProperty("documentType").GetString().Should().Be(typeKey);
            DocumentTestData.SameJson(entry.Value.GetProperty("body").GetRawText(), payloadJson)
                .Should().BeTrue($"revision {revision} of '{typeKey}' keeps its payload");
        }
    }

    // ── AC3 — the generated set really does contain the explicit six ──────────

    [Test]
    public void Sweep_covers_every_one_of_the_six_41_1b_types()
    {
        var swept = RegisteredTypeKeys.ToList();
        foreach (var key in Epic41bKeys)
            swept.Should().Contain(key.ToWire(),
                $"AC3 names '{key.ToWire()}' explicitly — a registry regression that drops it must fail " +
                "here, not silently shrink the sweep");

        swept.Should().OnlyHaveUniqueItems();
    }

    // ── AC3 — the six coexist in ONE issue, each keeping its own type + payload ─

    [Test]
    public async Task AllSixNewTypes_InOneIssue_EachTypeTrailKeepsItsOwnPayload()
    {
        const string issueId = "issue-41-1b-all-six";
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in Epic41bKeys)
        {
            var wire = key.ToWire();
            expected[wire] = ValidExampleJson(DocumentTypeRegistry.Resolve(wire));
            await _repo.InsertAsync(
                _tenant, EnvelopeFor(wire, issueId, DocumentState.Accepted), null, CancellationToken.None);
        }

        var listed = await _repo.ListByIssueAsync(_tenant, issueId, null, CancellationToken.None);
        listed.Select(r => r.DocumentType).Should().BeEquivalentTo(expected.Keys);

        var lineage = await CaptureJson(await DocumentEndpoints.GetIssueLineage(
            issueId, _repo, _tenantContext, CancellationToken.None));

        var trails = lineage.GetProperty("types").EnumerateArray().ToList();
        trails.Select(t => t.GetProperty("documentType").GetString()).Should().BeEquivalentTo(expected.Keys,
            "each of the six is its own type trail — none is swallowed by another's grouping");

        foreach (var trail in trails)
        {
            var wire = trail.GetProperty("documentType").GetString()!;
            var revisions = trail.GetProperty("revisions").EnumerateArray().ToList();
            revisions.Should().ContainSingle();
            DocumentTestData.SameJson(revisions[0].GetProperty("body").GetRawText(), expected[wire])
                .Should().BeTrue($"the '{wire}' trail hands back the '{wire}' payload, not a neighbour's");
        }

        lineage.GetProperty("unlinkedReviews").GetArrayLength().Should().Be(0);
        lineage.GetProperty("outcome").GetString().Should().Be("accepted",
            "every type's latest revision is accepted");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The type's OWN shipped valid example (the registry example loop's source) —
    /// no sample body is invented here, so a validator change that invalidates an
    /// example is caught once, in Core, not silently duplicated in this suite.
    /// </summary>
    private static string ValidExampleJson(IDocumentType type) =>
        type.Examples.First(e => e.IsValid).PayloadJson;

    private static DocumentEnvelope EnvelopeFor(
        string typeKey, string issueId, DocumentState state, Guid? supersedes = null)
    {
        var type = DocumentTypeRegistry.Resolve(typeKey);
        var draft = DocumentEnvelope.CreateDraft(
            DocumentTypeKeyExtensions.Parse(typeKey),
            type.SchemaVersion,
            issueId,
            "corr-1",
            // Type-agnostic on purpose (see the fixture note): the store validates the
            // BODY against the type and the producer against the taxonomy, never one
            // against the other.
            DocumentProducer.Create("senior_developer", "decompose-issue", "issue-decomposition"),
            DocumentTestData.Payload(ValidExampleJson(type)),
            supersedesDocumentId: supersedes);
        return state == DocumentState.Draft ? draft : draft with { State = state };
    }

    /// <summary>
    /// Find a document's lineage entry wherever the assembler placed it: a type
    /// trail's revision, a review attached to a subject, or <c>unlinkedReviews</c>
    /// (where a <c>review</c> whose subject is not in-response lands, D8). Keeping
    /// the lookup placement-agnostic is what lets ONE sweep cover every registered
    /// type, review included.
    /// </summary>
    private static JsonElement? FindEntry(JsonElement lineage, Guid id)
    {
        foreach (var trail in lineage.GetProperty("types").EnumerateArray())
        {
            foreach (var revision in trail.GetProperty("revisions").EnumerateArray())
            {
                if (revision.GetProperty("id").GetGuid() == id) return revision;
                foreach (var review in revision.GetProperty("reviews").EnumerateArray())
                    if (review.GetProperty("id").GetGuid() == id) return review;
            }
        }

        foreach (var review in lineage.GetProperty("unlinkedReviews").EnumerateArray())
            if (review.GetProperty("id").GetGuid() == id) return review;

        return null;
    }

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
