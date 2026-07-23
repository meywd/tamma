using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents.Resume;

namespace Tamma.Core.Tests.Documents.Resume;

/// <summary>
/// Story 39-10 (AC5) — the pure re-entry position matrix for
/// <see cref="LifecycleResumeCalculator"/>. Proves the fold reconstructs the coarse
/// stage from durable truth deterministically, and that store/stream disagreement is
/// a fail-loud <c>DOCUMENT.REENTRY.INCONSISTENT_STATE</c> (it never guesses).
/// </summary>
[TestFixture]
public class LifecycleResumeCalculatorTests
{
    private const string Type = "decomposition";
    private static readonly Guid Doc = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001");
    private static readonly Guid Session = Guid.Parse("0192a8b0-2222-7abc-8def-000000000002");

    private static ResumeEventRow Row(string type, int seq, Guid? docId = null, int? revision = null, Guid? session = null, string? typeKey = Type)
        => new(type, new DateTime(2026, 7, 23, 0, 0, seq, DateTimeKind.Utc), docId, typeKey, session, revision);

    // ── Produce (no usable prior work) ─────────────────────────────────

    [Test]
    public void NoRowsNoEvents_YieldsProduce()
    {
        var p = LifecycleResumeCalculator.Reconstruct(Type, null, Array.Empty<ResumeEventRow>());
        p.ResumeAt.Should().Be(LifecycleResumeStage.Produce);
        p.ExistingDocumentId.Should().BeNull();
    }

    [Test]
    public void ProducedButValidationFailed_YieldsProduce()
    {
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.FAILED", 2, Doc, 0),
        };
        LifecycleResumeCalculator.Reconstruct(Type, null, events).ResumeAt.Should().Be(LifecycleResumeStage.Produce);
    }

    [Test]
    public void RevisionInFlight_RevisionStartedLast_YieldsProduce()
    {
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
            Row("DOCUMENT.REVIEWED", 3, Doc, 0),
            Row("DOCUMENT.REVISION_STARTED", 4, Doc, 1),
        };
        LifecycleResumeCalculator.Reconstruct(Type, null, events).ResumeAt.Should().Be(LifecycleResumeStage.Produce);
    }

    // ── Review (produced + validated, unreviewed) ──────────────────────

    [Test]
    public void ProducedAndValidated_Unreviewed_YieldsReviewAtThatRevision()
    {
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
        };
        var p = LifecycleResumeCalculator.Reconstruct(Type, null, events);
        p.ResumeAt.Should().Be(LifecycleResumeStage.Review);
        p.ExistingDocumentId.Should().Be(Doc);
        p.ExistingRevision.Should().Be(0);
    }

    [Test]
    public void ReviseThenValidated_Unreviewed_YieldsReviewAtNewRevision()
    {
        var rev = Guid.Parse("0192a8b0-3333-7abc-8def-000000000003");
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
            Row("DOCUMENT.REVIEWED", 3, Doc, 0),
            Row("DOCUMENT.REVISION_STARTED", 4, rev, 1),
            Row("DOCUMENT.VALIDATED.SUCCESS", 5, rev, 1),
        };
        var p = LifecycleResumeCalculator.Reconstruct(Type, null, events);
        p.ResumeAt.Should().Be(LifecycleResumeStage.Review);
        p.ExistingDocumentId.Should().Be(rev);
        p.ExistingRevision.Should().Be(1);
    }

    // ── Accept (unanswered approval) ───────────────────────────────────

    [Test]
    public void ApprovalRequested_Unanswered_YieldsAcceptWithSession()
    {
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
            Row("DOCUMENT.REVIEWED", 3, Doc, 0),
            Row("APPROVAL.REQUESTED", 4, Doc, 0, Session),
        };
        var p = LifecycleResumeCalculator.Reconstruct(Type, null, events);
        p.ResumeAt.Should().Be(LifecycleResumeStage.Accept);
        p.PendingDecisionSessionId.Should().Be(Session);
    }

    [Test]
    public void ApprovalProvided_ClearsPending_NotAccept()
    {
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
            Row("DOCUMENT.REVIEWED", 3, Doc, 0),
            Row("APPROVAL.REQUESTED", 4, Doc, 0, Session),
            Row("APPROVAL.PROVIDED", 5, Doc, 0, Session),
        };
        LifecycleResumeCalculator.Reconstruct(Type, null, events).ResumeAt.Should().Be(LifecycleResumeStage.Produce);
    }

    // ── Complete (accepted) ────────────────────────────────────────────

    [Test]
    public void Accepted_StoreAndStreamAgree_YieldsComplete()
    {
        var accepted = new AcceptedDocumentRef(Doc, Type, 1);
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
            Row("DOCUMENT.REVIEWED", 3, Doc, 0),
            Row("APPROVAL.REQUESTED", 4, Doc, 0, Session),
            Row("APPROVAL.PROVIDED", 5, Doc, 0, Session),
            Row("DOCUMENT.ACCEPTED", 6, Doc, 0),
        };
        var p = LifecycleResumeCalculator.Reconstruct(Type, accepted, events);
        p.ResumeAt.Should().Be(LifecycleResumeStage.Complete);
        p.ExistingDocumentId.Should().Be(Doc);
        p.ExistingRevision.Should().Be(1);
    }

    // ── Inconsistency (fail-loud, never guesses) ───────────────────────

    [Test]
    public void AcceptedRow_NoAcceptedEvent_ThrowsInconsistentState()
    {
        var accepted = new AcceptedDocumentRef(Doc, Type, 1);
        var events = new[] { Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0) };

        var act = () => LifecycleResumeCalculator.Reconstruct(Type, accepted, events);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.REENTRY.INCONSISTENT_STATE");
    }

    [Test]
    public void AcceptedEvent_NoAcceptedRow_ThrowsInconsistentState()
    {
        var events = new[] { Row("DOCUMENT.ACCEPTED", 1, Doc, 0) };

        var act = () => LifecycleResumeCalculator.Reconstruct(Type, null, events);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.REENTRY.INCONSISTENT_STATE");
    }

    // ── Type isolation + determinism ───────────────────────────────────

    [Test]
    public void ForeignTypeEvents_AreIgnored()
    {
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0, typeKey: "plan"),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0, typeKey: "plan"),
        };
        LifecycleResumeCalculator.Reconstruct(Type, null, events).ResumeAt.Should().Be(LifecycleResumeStage.Produce);
    }

    [Test]
    public void Reconstruct_IsDeterministic()
    {
        var events = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
        };
        var a = LifecycleResumeCalculator.Reconstruct(Type, null, events);
        var b = LifecycleResumeCalculator.Reconstruct(Type, null, events);
        a.Should().Be(b);
    }
}
