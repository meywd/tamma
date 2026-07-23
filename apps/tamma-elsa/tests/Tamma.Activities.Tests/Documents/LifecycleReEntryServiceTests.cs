using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-10 (AC5 I/O half, D1/D7) — the re-entry SERVICE reads the latest-accepted
/// instance via 39-11's repository method (never HTTP), maps the DCB event slice to the
/// neutral fold DTO, and delegates to the pure calculator. The Null seam always yields
/// Produce (D7).
/// </summary>
[TestFixture]
public class LifecycleReEntryServiceTests
{
    private const string Type = "decomposition";
    private const string Issue = "issue-1";
    private static readonly Guid Tenant = Guid.Parse("0192a8b0-9999-7abc-8def-000000000009");
    private static readonly Guid Doc = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001");
    private static readonly Guid Session = Guid.Parse("0192a8b0-2222-7abc-8def-000000000002");

    private static Mock<ITenantContext> TenantCtx()
    {
        var m = new Mock<ITenantContext>();
        m.SetupGet(t => t.TenantId).Returns(Tenant);
        return m;
    }

    private static DomainEvent Ev(string type, long seq, string? documentType = Type,
        Guid? documentId = null, Guid? session = null, int? round = null)
    {
        var tags = new Dictionary<string, object?> { ["issueId"] = Issue };
        if (documentType is not null) tags["documentType"] = documentType;
        if (documentId is Guid d) tags["documentId"] = d.ToString();
        if (session is Guid s) tags["sessionId"] = s.ToString();
        if (round is int r) tags["round"] = r;
        return new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = Tenant,
            Tags = System.Text.Json.JsonSerializer.Serialize(tags),
            CreatedAt = new DateTime(2026, 7, 23, 0, 0, (int)seq, DateTimeKind.Utc),
            SequenceNumber = seq,
        };
    }

    private static Mock<IEventRepository> Events(IReadOnlyList<DomainEvent> document, IReadOnlyList<DomainEvent> approval)
    {
        var m = new Mock<IEventRepository>();
        m.Setup(r => r.QueryEventsAsync(Tenant, "DOCUMENT.", true, null, null, null, null, null, It.IsAny<int>(), false))
            .ReturnsAsync((document, (int?)document.Count));
        m.Setup(r => r.QueryEventsAsync(Tenant, "APPROVAL.", true, null, null, null, null, null, It.IsAny<int>(), false))
            .ReturnsAsync((approval, (int?)approval.Count));
        return m;
    }

    [Test]
    public async Task Reconstruct_ProducedAndValidated_ReadsLatestAccepted_AndYieldsReview()
    {
        var docs = new Mock<IDocumentInstanceRepository>();
        docs.Setup(d => d.GetLatestAcceptedAsync(Tenant, Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentInstance>());

        var events = Events(
            new[] { Ev("DOCUMENT.PRODUCED.SUCCESS", 1, documentId: Doc, round: 0),
                    Ev("DOCUMENT.VALIDATED.SUCCESS", 2, documentId: Doc, round: 0) },
            Array.Empty<DomainEvent>());

        var svc = new LifecycleReEntryService(docs.Object, events.Object, TenantCtx().Object);
        var p = await svc.ReconstructAsync(Tenant, Issue, Type, CancellationToken.None);

        p.ResumeAt.Should().Be(LifecycleResumeStage.Review);
        p.ExistingDocumentId.Should().Be(Doc);
        // The 39-11 repository method — not any HTTP call — is the latest-accepted source.
        docs.Verify(d => d.GetLatestAcceptedAsync(Tenant, Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Reconstruct_Accepted_YieldsComplete()
    {
        var accepted = new DocumentInstance
        {
            Id = Doc, DocumentType = Type, IssueId = Issue, Revision = 1,
            Status = "accepted", ProducedByRole = "senior_developer", ProducedByAction = "decompose-issue",
            SchemaVersion = 1, BodyJson = "{}", TenantId = Tenant,
        };
        var docs = new Mock<IDocumentInstanceRepository>();
        docs.Setup(d => d.GetLatestAcceptedAsync(Tenant, Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { accepted });

        var events = Events(
            new[] { Ev("DOCUMENT.PRODUCED.SUCCESS", 1, documentId: Doc, round: 0),
                    Ev("DOCUMENT.VALIDATED.SUCCESS", 2, documentId: Doc, round: 0),
                    Ev("DOCUMENT.REVIEWED", 3, documentId: Doc, round: 0),
                    Ev("DOCUMENT.ACCEPTED", 4, documentId: Doc, round: 0) },
            Array.Empty<DomainEvent>());

        var svc = new LifecycleReEntryService(docs.Object, events.Object, TenantCtx().Object);
        var p = await svc.ReconstructAsync(Tenant, Issue, Type, CancellationToken.None);

        p.ResumeAt.Should().Be(LifecycleResumeStage.Complete);
        p.ExistingDocumentId.Should().Be(Doc);
    }

    [Test]
    public async Task Reconstruct_UnansweredApproval_RecoversSession_Accept()
    {
        var docs = new Mock<IDocumentInstanceRepository>();
        docs.Setup(d => d.GetLatestAcceptedAsync(Tenant, Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentInstance>());

        var events = Events(
            new[] { Ev("DOCUMENT.PRODUCED.SUCCESS", 1, documentId: Doc, round: 0),
                    Ev("DOCUMENT.VALIDATED.SUCCESS", 2, documentId: Doc, round: 0),
                    Ev("DOCUMENT.REVIEWED", 3, documentId: Doc, round: 0) },
            new[] { Ev("APPROVAL.REQUESTED", 4, documentId: Doc, session: Session) });

        var svc = new LifecycleReEntryService(docs.Object, events.Object, TenantCtx().Object);
        var p = await svc.ReconstructAsync(Tenant, Issue, Type, CancellationToken.None);

        p.ResumeAt.Should().Be(LifecycleResumeStage.Accept);
        p.PendingDecisionSessionId.Should().Be(Session);
    }

    [Test]
    public async Task GetDocumentBody_ReconstructsEnvelopeFromStoreRow()
    {
        var row = new DocumentInstance
        {
            Id = Doc, DocumentType = Type, IssueId = Issue, Revision = 1, Status = "validated",
            ProducedByRole = "senior_developer", ProducedByAction = "decompose-issue",
            ProducedByWorkflow = "llm-call", SchemaVersion = 1, CorrelationId = "corr-1",
            BodyJson = "{\"summary\":\"x\"}", TenantId = Tenant,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var docs = new Mock<IDocumentInstanceRepository>();
        docs.Setup(d => d.GetByIdAsync(Tenant, Doc, It.IsAny<CancellationToken>())).ReturnsAsync(row);

        var svc = new LifecycleReEntryService(docs.Object, new Mock<IEventRepository>().Object, TenantCtx().Object);
        var env = await svc.GetDocumentBodyAsync(Tenant, Doc, CancellationToken.None);

        env.Should().NotBeNull();
        env!.Id.Should().Be(Doc);
        env.Type.Should().Be(Type);
        env.Payload.GetProperty("summary").GetString().Should().Be("x");
    }

    [Test]
    public async Task NullService_AlwaysYieldsProduce()
    {
        var svc = new NullLifecycleReEntryService();
        var p = await svc.ReconstructAsync(Tenant, Issue, Type, CancellationToken.None);
        p.ResumeAt.Should().Be(LifecycleResumeStage.Produce);
        (await svc.GetDocumentBodyAsync(Tenant, Doc, CancellationToken.None)).Should().BeNull();
    }
}
