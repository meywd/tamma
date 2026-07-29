using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Core.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-11 (AC5/AC6/AC8) — guards on the document read endpoints
/// (<see cref="DocumentEndpoints"/>). Handlers are called directly with a recording
/// fake repository + a fake tenant context (the
/// <see cref="Tamma.Api.Tests.Dashboard.ReposRunsEndpointsGuardTests"/> style).
///
/// <para>Coverage: null AND <see cref="Guid.Empty"/> tenant → <c>404
/// no_active_tenant</c> BEFORE any repository call (fail-closed); a bare-id fetch
/// whose row belongs to another tenant → <c>404 document_not_found</c>; happy-path
/// projection pins.</para>
/// </summary>
[TestFixture]
public class DocumentEndpointsGuardTests
{
    private readonly Guid _tenant = Guid.NewGuid();

    // ── Fail-closed: null / empty tenant on all three reads ────────────────────

    [Test]
    public async Task GetIssueLineage_NullTenant_FailsClosed_WithoutCallingRepo()
    {
        var repo = new RecordingRepo();
        var result = await DocumentEndpoints.GetIssueLineage(
            "i", repo, new DocumentTestData.FakeTenantContext(null), CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.ListByIssueCalled.Should().BeFalse();
    }

    [Test]
    public async Task GetIssueLineage_EmptyTenant_FailsClosed_WithoutCallingRepo()
    {
        var repo = new RecordingRepo();
        var result = await DocumentEndpoints.GetIssueLineage(
            "i", repo, new DocumentTestData.FakeTenantContext(Guid.Empty), CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.ListByIssueCalled.Should().BeFalse();
    }

    [Test]
    public async Task GetLatestAccepted_NullTenant_FailsClosed_WithoutCallingRepo()
    {
        var repo = new RecordingRepo();
        var result = await DocumentEndpoints.GetLatestAccepted(
            "i", repo, new DocumentTestData.FakeTenantContext(null), CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.GetLatestAcceptedCalled.Should().BeFalse();
    }

    [Test]
    public async Task GetDocument_EmptyTenant_FailsClosed_WithoutCallingRepo()
    {
        var repo = new RecordingRepo();
        var result = await DocumentEndpoints.GetDocument(
            Guid.NewGuid(), repo, new DocumentTestData.FakeTenantContext(Guid.Empty), CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("no_active_tenant");
        repo.GetByIdCalled.Should().BeFalse();
    }

    // ── Entity-level re-check on the bare-id fetch ─────────────────────────────

    [Test]
    public async Task GetDocument_ForeignTenantRow_Returns404_DocumentNotFound()
    {
        var foreign = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var repo = new RecordingRepo
        {
            // The repo is asked with the caller's tenant but a stale/foreign row
            // surfaces — the entity-level re-check must reject it.
            RowById = DocumentTestData.Row(docId, "i", DocumentTestData.DecompositionType, "accepted", 1,
                DocumentTestData.ValidDecompositionBody, foreign),
        };

        var result = await DocumentEndpoints.GetDocument(
            docId, repo, new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("document_not_found");
    }

    [Test]
    public async Task GetDocument_UnknownRow_Returns404()
    {
        var repo = new RecordingRepo { RowById = null };
        var result = await DocumentEndpoints.GetDocument(
            Guid.NewGuid(), repo, new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(404);
        ErrorOf(result).Should().Be("document_not_found");
    }

    // ── Story 41-1c AC3/AC4 — the audience query filter ────────────────────────

    [Test]
    public async Task GetIssueLineage_UnknownAudience_Returns400_WithoutCallingRepo()
    {
        // An out-of-vocabulary audience is a 400 (unknown_audience), never an
        // empty 200 — silence would read as "no documents" when the truth is
        // "no such audience".
        var repo = new RecordingRepo();
        var result = await DocumentEndpoints.GetIssueLineage(
            "i", repo, new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None,
            audience: "marketing");

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("unknown_audience");
        repo.ListByIssueCalled.Should().BeFalse();
    }

    [Test]
    public async Task GetIssueLineage_VocabularyAudience_IsThreadedToTheRepository()
    {
        var repo = new RecordingRepo();
        var result = await DocumentEndpoints.GetIssueLineage(
            "i", repo, new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None,
            audience: "stakeholder");

        StatusOf(result).Should().Be(200);
        repo.ListByIssueCalled.Should().BeTrue();
        repo.RequestedAudience.Should().Be("stakeholder");
    }

    [Test]
    public async Task GetIssueLineage_NoAudience_PassesNullFilter()
    {
        var repo = new RecordingRepo();
        await DocumentEndpoints.GetIssueLineage(
            "i", repo, new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        repo.ListByIssueCalled.Should().BeTrue();
        repo.RequestedAudience.Should().BeNull("no filter means the pre-41-1c unfiltered read");
    }

    // ── Happy-path projections ─────────────────────────────────────────────────

    [Test]
    public async Task GetIssueLineage_ConcreteTenant_ProjectsTrail()
    {
        var repo = new RecordingRepo
        {
            IssueRows =
            {
                DocumentTestData.Row(Guid.NewGuid(), "issue-9", DocumentTestData.DecompositionType,
                    "accepted", 1, DocumentTestData.ValidDecompositionBody, _tenant),
            },
        };

        var result = await DocumentEndpoints.GetIssueLineage(
            "issue-9", repo, new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(200);
        repo.RequestedTenant.Should().Be(_tenant);
        var root = await CaptureJson(result);
        root.GetProperty("issueId").GetString().Should().Be("issue-9");
        root.GetProperty("outcome").GetString().Should().Be("accepted");
        root.GetProperty("types").GetArrayLength().Should().Be(1);
        root.GetProperty("types")[0].GetProperty("documentType").GetString()
            .Should().Be(DocumentTestData.DecompositionType);
    }

    [Test]
    public async Task GetDocument_OwnedRow_ProjectsEntry()
    {
        var docId = Guid.NewGuid();
        var repo = new RecordingRepo
        {
            RowById = DocumentTestData.Row(docId, "i", DocumentTestData.DecompositionType, "accepted", 2,
                DocumentTestData.ValidDecompositionBody, _tenant),
        };

        var result = await DocumentEndpoints.GetDocument(
            docId, repo, new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(200);
        var root = await CaptureJson(result);
        root.GetProperty("id").GetGuid().Should().Be(docId);
        root.GetProperty("revision").GetInt32().Should().Be(2);
        root.GetProperty("status").GetString().Should().Be("accepted");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static int? StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static string? ErrorOf(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
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

    private sealed class RecordingRepo : IDocumentInstanceRepository
    {
        public bool ListByIssueCalled { get; private set; }
        public bool GetLatestAcceptedCalled { get; private set; }
        public bool GetByIdCalled { get; private set; }
        public Guid? RequestedTenant { get; private set; }
        public string? RequestedAudience { get; private set; }
        public List<DocumentInstance> IssueRows { get; } = new();
        public List<DocumentInstance> LatestRows { get; } = new();
        public DocumentInstance? RowById { get; set; }

        public Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(Guid tenantId, string issueId, string? audience, CancellationToken ct)
        {
            ListByIssueCalled = true;
            RequestedTenant = tenantId;
            RequestedAudience = audience;
            return Task.FromResult<IReadOnlyList<DocumentInstance>>(IssueRows);
        }

        public Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(Guid tenantId, string issueId, CancellationToken ct)
        {
            GetLatestAcceptedCalled = true;
            RequestedTenant = tenantId;
            return Task.FromResult<IReadOnlyList<DocumentInstance>>(LatestRows);
        }

        public Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct)
        {
            GetByIdCalled = true;
            RequestedTenant = tenantId;
            return Task.FromResult(RowById);
        }

        public Task<DocumentInstance> InsertAsync(Guid tenantId, DocumentEnvelope envelope, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DocumentInstance> SetStatusAsync(Guid tenantId, Guid documentId, DocumentInstanceStatus status, Guid? correlatingEventId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
