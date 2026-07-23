using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-10 (AC6) — the idempotent step guards (Elsa-free): re-entering an
/// ALREADY-ACCEPTED lineage yields zero produce decisions, zero review decisions, and
/// an event plan containing NO second <c>DOCUMENT.ACCEPTED</c> — exactly
/// <c>DOCUMENT.REENTERED</c>. Driven over the same lineage the calculator reconstructs
/// (the "drive the lifecycle twice over the same accepted lineage" contract).
/// </summary>
[TestFixture]
public class LifecycleReEntryGuardTests
{
    private const string Type = "decomposition";
    private static readonly Guid Doc = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001");
    private static readonly Guid Session = Guid.Parse("0192a8b0-2222-7abc-8def-000000000002");

    private static ResumeEventRow Row(string type, int seq, Guid? docId = null, int? rev = null, Guid? session = null)
        => new(type, new DateTime(2026, 7, 23, 0, 0, seq, DateTimeKind.Utc), docId, Type, session, rev);

    private static readonly ResumeEventRow[] AcceptedLineage =
    {
        Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
        Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
        Row("DOCUMENT.REVIEWED", 3, Doc, 0),
        Row("APPROVAL.REQUESTED", 4, Doc, 0, Session),
        Row("APPROVAL.PROVIDED", 5, Doc, 0, Session),
        Row("DOCUMENT.ACCEPTED", 6, Doc, 0),
    };

    [Test]
    public void SecondPassOverAcceptedLineage_SkipsProduceAndReview()
    {
        var accepted = new AcceptedDocumentRef(Doc, Type, 1);
        var position = LifecycleResumeCalculator.Reconstruct(Type, accepted, AcceptedLineage);

        position.ResumeAt.Should().Be(LifecycleResumeStage.Complete);
        DocumentLifecycleHelper.ShouldSkipProduce(position).Should().BeTrue("an accepted document is never re-produced");
        DocumentLifecycleHelper.ShouldSkipReview(position).Should().BeTrue("an accepted document is never re-reviewed");
        DocumentLifecycleHelper.ShouldShortCircuitAccepted(position).Should().BeTrue();
    }

    [Test]
    public void CompleteReEntry_EmitsReentered_NotASecondAccepted()
    {
        var accepted = new AcceptedDocumentRef(Doc, Type, 1);
        var position = LifecycleResumeCalculator.Reconstruct(Type, accepted, AcceptedLineage);

        var evt = ComputeReEntryPositionActivity.BuildReenteredEvent(position, "issue-1", Type, "corr-1", null);
        evt.EventType.Should().Be(DocumentEvents.Reentered);
        evt.EventType.Should().NotBe(DocumentEvents.Accepted, "the short-circuit must not re-emit acceptance");
        evt.Status.Should().Be("success");

        ComputeReEntryPositionActivity.SkippedStages(LifecycleResumeStage.Complete)
            .Should().BeEquivalentTo(new[] { "produce", "validate", "review", "accept" });
    }

    [Test]
    public void ReviewReEntry_SkipsProduceButNotReview()
    {
        var lineage = new[]
        {
            Row("DOCUMENT.PRODUCED.SUCCESS", 1, Doc, 0),
            Row("DOCUMENT.VALIDATED.SUCCESS", 2, Doc, 0),
        };
        var position = LifecycleResumeCalculator.Reconstruct(Type, null, lineage);

        position.ResumeAt.Should().Be(LifecycleResumeStage.Review);
        DocumentLifecycleHelper.ShouldSkipProduce(position).Should().BeTrue();
        DocumentLifecycleHelper.ShouldSkipReview(position).Should().BeFalse("Review re-entry still reviews the existing revision");
        DocumentLifecycleHelper.ShouldShortCircuitAccepted(position).Should().BeFalse();
    }

    [Test]
    public void FreshProduce_EmitsNoReentered()
    {
        var position = LifecycleResumeCalculator.Reconstruct(Type, null, Array.Empty<ResumeEventRow>());
        position.ResumeAt.Should().Be(LifecycleResumeStage.Produce);
        DocumentLifecycleHelper.ShouldSkipProduce(position).Should().BeFalse();
        ComputeReEntryPositionActivity.SkippedStages(LifecycleResumeStage.Produce).Should().BeEmpty();
    }
}
