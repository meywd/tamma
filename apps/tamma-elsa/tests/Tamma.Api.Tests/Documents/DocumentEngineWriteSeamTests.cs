using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-11 (AC7/AC8, D6 — Api half) — the engine→API persist/status seam.
/// <see cref="DocumentEndpoints.PersistFromEngine"/> maps the wire envelope onto
/// <c>InsertAsync</c> and surfaces a <see cref="TammaError"/> as a 400 with its
/// code; <see cref="DocumentEndpoints.SetStatusFromEngine"/> parses + transitions
/// and maps registry/store errors to 400. (The Activities-side fail-loud client is
/// covered by <c>Tamma.Activities.Tests.Documents.DocumentEngineWriteSeamTests</c>.)
/// </summary>
[TestFixture]
public class DocumentEngineWriteSeamTests
{
    private readonly Guid _tenant = Guid.NewGuid();

    [Test]
    public async Task PersistFromEngine_NullTenant_Returns400()
    {
        var repo = new RecordingWriteRepo();
        var env = DocumentJson.Serialize(DocumentTestData.DecompositionEnvelope("i"));

        var result = await DocumentEndpoints.PersistFromEngine(
            new PersistDocumentRequest(env, null), repo,
            new DocumentTestData.FakeTenantContext(null), CancellationToken.None);

        StatusOf(result).Should().Be(400);
        repo.InsertCalled.Should().BeFalse();
    }

    [Test]
    public async Task PersistFromEngine_MapsEnvelopeOntoInsert_Returns201()
    {
        var eventId = Guid.NewGuid();
        var envelope = DocumentTestData.DecompositionEnvelope("issue-7");
        var repo = new RecordingWriteRepo
        {
            InsertResult = DocumentTestData.Row(envelope.Id, "issue-7", DocumentTestData.DecompositionType,
                "draft", 1, DocumentTestData.ValidDecompositionBody, _tenant),
        };

        var result = await DocumentEndpoints.PersistFromEngine(
            new PersistDocumentRequest(DocumentJson.Serialize(envelope), eventId), repo,
            new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(201);
        repo.InsertCalled.Should().BeTrue();
        repo.RequestedTenant.Should().Be(_tenant);
        repo.RequestedEnvelope!.Id.Should().Be(envelope.Id);
        repo.RequestedEnvelope.IssueId.Should().Be("issue-7");
        repo.RequestedCorrelatingEventId.Should().Be(eventId);
    }

    [Test]
    public async Task PersistFromEngine_RepoTammaError_MapsToCoded400()
    {
        var repo = new RecordingWriteRepo
        {
            ThrowOnInsert = new TammaError("DOCUMENT.STORE.INVALID_BODY", "bad body"),
        };
        var env = DocumentJson.Serialize(DocumentTestData.DecompositionEnvelope("i"));

        var result = await DocumentEndpoints.PersistFromEngine(
            new PersistDocumentRequest(env, null), repo,
            new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("DOCUMENT.STORE.INVALID_BODY");
    }

    [Test]
    public async Task PersistFromEngine_InvalidEnvelopeJson_Returns400()
    {
        var repo = new RecordingWriteRepo();
        var result = await DocumentEndpoints.PersistFromEngine(
            new PersistDocumentRequest("{ not valid", null), repo,
            new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("invalid_envelope");
        repo.InsertCalled.Should().BeFalse();
    }

    [Test]
    public async Task SetStatusFromEngine_UnknownStatus_MapsToCoded400()
    {
        var repo = new RecordingWriteRepo();
        var result = await DocumentEndpoints.SetStatusFromEngine(
            Guid.NewGuid(), new SetDocumentStatusRequest("bogus", null), repo,
            new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("DOCUMENT.STORE.UNKNOWN_STATUS");
        repo.SetStatusCalled.Should().BeFalse();
    }

    [Test]
    public async Task SetStatusFromEngine_Success_Returns200()
    {
        var docId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var repo = new RecordingWriteRepo
        {
            SetStatusResult = DocumentTestData.Row(docId, "i", DocumentTestData.DecompositionType,
                "accepted", 1, DocumentTestData.ValidDecompositionBody, _tenant),
        };

        var result = await DocumentEndpoints.SetStatusFromEngine(
            docId, new SetDocumentStatusRequest("accepted", eventId), repo,
            new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(200);
        repo.SetStatusCalled.Should().BeTrue();
        repo.RequestedStatus.Should().Be(DocumentInstanceStatus.Accepted);
        repo.RequestedCorrelatingEventId.Should().Be(eventId);
    }

    [Test]
    public async Task SetStatusFromEngine_RepoTammaError_MapsToCoded400()
    {
        var repo = new RecordingWriteRepo
        {
            ThrowOnSetStatus = new TammaError("DOCUMENT.STORE.NOT_FOUND", "missing"),
        };

        var result = await DocumentEndpoints.SetStatusFromEngine(
            Guid.NewGuid(), new SetDocumentStatusRequest("accepted", null), repo,
            new DocumentTestData.FakeTenantContext(_tenant), CancellationToken.None);

        StatusOf(result).Should().Be(400);
        ErrorOf(result).Should().Be("DOCUMENT.STORE.NOT_FOUND");
    }

    private static int? StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    private static string? ErrorOf(IResult result)
    {
        var value = (result as IValueHttpResult)?.Value;
        return value?.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private sealed class RecordingWriteRepo : IDocumentInstanceRepository
    {
        public bool InsertCalled { get; private set; }
        public bool SetStatusCalled { get; private set; }
        public Guid? RequestedTenant { get; private set; }
        public DocumentEnvelope? RequestedEnvelope { get; private set; }
        public Guid? RequestedCorrelatingEventId { get; private set; }
        public DocumentInstanceStatus? RequestedStatus { get; private set; }
        public DocumentInstance? InsertResult { get; set; }
        public DocumentInstance? SetStatusResult { get; set; }
        public TammaError? ThrowOnInsert { get; set; }
        public TammaError? ThrowOnSetStatus { get; set; }

        public Task<DocumentInstance> InsertAsync(Guid tenantId, DocumentEnvelope envelope, Guid? correlatingEventId, CancellationToken ct)
        {
            InsertCalled = true;
            RequestedTenant = tenantId;
            RequestedEnvelope = envelope;
            RequestedCorrelatingEventId = correlatingEventId;
            if (ThrowOnInsert is not null) throw ThrowOnInsert;
            return Task.FromResult(InsertResult!);
        }

        public Task<DocumentInstance> SetStatusAsync(Guid tenantId, Guid documentId, DocumentInstanceStatus status, Guid? correlatingEventId, CancellationToken ct)
        {
            SetStatusCalled = true;
            RequestedTenant = tenantId;
            RequestedStatus = status;
            RequestedCorrelatingEventId = correlatingEventId;
            if (ThrowOnSetStatus is not null) throw ThrowOnSetStatus;
            return Task.FromResult(SetStatusResult!);
        }

        public Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(Guid tenantId, string issueId, string? audience, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(Guid tenantId, string issueId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
