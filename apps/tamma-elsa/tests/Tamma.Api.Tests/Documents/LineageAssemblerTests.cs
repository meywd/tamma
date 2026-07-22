using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Documents;
using Tamma.Core;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Documents;

/// <summary>
/// Story 39-11 (AC3/AC8) — pure tests for <see cref="LineageAssembler"/>: grouping
/// + ordering, review linkage (parent-first + body-probe fallback), unresolvable
/// reviews surfaced in <c>unlinkedReviews</c>, the outcome matrix, and the corrupt-
/// body tripwire (D5).
/// </summary>
[TestFixture]
public class LineageAssemblerTests
{
    private readonly Guid _tenant = Guid.NewGuid();

    [Test]
    public void Assemble_groups_types_in_first_produced_order_revisions_ascending()
    {
        var t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var findings = Guid.NewGuid();
        var decompR1 = Guid.NewGuid();
        var decompR2 = Guid.NewGuid();

        // findings produced first, then two decomposition revisions.
        var rows = new List<DocumentInstance>
        {
            DocumentTestData.Row(findings, "issue-1", DocumentTestData.DecompositionType, "accepted", 1,
                DocumentTestData.ValidDecompositionBody, _tenant, createdAt: t0),
            DocumentTestData.Row(decompR2, "issue-1", "review", "accepted", 2,
                DocumentTestData.ValidReviewBody(decompR1), _tenant, createdAt: t0.AddMinutes(3)),
            DocumentTestData.Row(decompR1, "issue-1", DocumentTestData.DecompositionType, "superseded", 1,
                DocumentTestData.ValidDecompositionBody, _tenant, createdAt: t0.AddMinutes(1)),
        };

        var lineage = LineageAssembler.Assemble("issue-1", rows);

        lineage.IssueId.Should().Be("issue-1");
        // Types in first-produced order: findings' decomposition row at t0 is the
        // first decomposition; there is exactly one non-review type here.
        lineage.Types.Should().ContainSingle();
        lineage.Types[0].DocumentType.Should().Be(DocumentTestData.DecompositionType);
        lineage.Types[0].Revisions.Select(r => r.Revision).Should().ContainInOrder(1, 1);
    }

    [Test]
    public void Assemble_attaches_review_to_subject_via_parent_document_id()
    {
        var subjectId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var rows = new List<DocumentInstance>
        {
            DocumentTestData.Row(subjectId, "i", DocumentTestData.DecompositionType, "accepted", 1,
                DocumentTestData.ValidDecompositionBody, _tenant),
            // No body subject.documentId match — parent_document_id carries it.
            DocumentTestData.Row(reviewId, "i", "review", "accepted", 1,
                DocumentTestData.ValidReviewBody(Guid.NewGuid()), _tenant, parentDocumentId: subjectId),
        };

        var lineage = LineageAssembler.Assemble("i", rows);

        lineage.UnlinkedReviews.Should().BeEmpty();
        var subject = lineage.Types.Single().Revisions.Single();
        subject.Reviews.Should().ContainSingle().Which.Id.Should().Be(reviewId);
    }

    [Test]
    public void Assemble_falls_back_to_body_probe_when_no_parent()
    {
        var subjectId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();
        var rows = new List<DocumentInstance>
        {
            DocumentTestData.Row(subjectId, "i", DocumentTestData.DecompositionType, "accepted", 1,
                DocumentTestData.ValidDecompositionBody, _tenant),
            // parent null → resolves via body subject.documentId.
            DocumentTestData.Row(reviewId, "i", "review", "accepted", 1,
                DocumentTestData.ValidReviewBody(subjectId), _tenant),
        };

        var lineage = LineageAssembler.Assemble("i", rows);

        lineage.UnlinkedReviews.Should().BeEmpty();
        lineage.Types.Single().Revisions.Single().Reviews.Single().Id.Should().Be(reviewId);
    }

    [Test]
    public void Assemble_surfaces_unresolvable_review_in_unlinkedReviews_never_dropped()
    {
        var reviewId = Guid.NewGuid();
        var rows = new List<DocumentInstance>
        {
            // A review whose subject (both parent + body) points at a doc NOT in
            // this response.
            DocumentTestData.Row(reviewId, "i", "review", "accepted", 1,
                DocumentTestData.ValidReviewBody(Guid.NewGuid()), _tenant),
        };

        var lineage = LineageAssembler.Assemble("i", rows);

        lineage.Types.Should().BeEmpty();
        lineage.UnlinkedReviews.Should().ContainSingle().Which.Id.Should().Be(reviewId);
    }

    [Test]
    public void Outcome_is_accepted_when_every_latest_per_type_is_accepted()
    {
        var rows = new List<DocumentInstance>
        {
            DocumentTestData.Row(Guid.NewGuid(), "i", DocumentTestData.DecompositionType, "accepted", 1,
                DocumentTestData.ValidDecompositionBody, _tenant),
        };

        LineageAssembler.Assemble("i", rows).Outcome.Should().Be("accepted");
    }

    [Test]
    public void Outcome_is_escalated_when_any_non_superseded_row_is_escalated()
    {
        var rows = new List<DocumentInstance>
        {
            DocumentTestData.Row(Guid.NewGuid(), "i", DocumentTestData.DecompositionType, "escalated", 1,
                DocumentTestData.ValidDecompositionBody, _tenant),
        };

        LineageAssembler.Assemble("i", rows).Outcome.Should().Be("escalated");
    }

    [Test]
    public void Outcome_is_in_progress_when_a_type_latest_is_not_accepted()
    {
        var rows = new List<DocumentInstance>
        {
            DocumentTestData.Row(Guid.NewGuid(), "i", DocumentTestData.DecompositionType, "draft", 1,
                DocumentTestData.ValidDecompositionBody, _tenant),
        };

        LineageAssembler.Assemble("i", rows).Outcome.Should().Be("in-progress");
    }

    [Test]
    public void Empty_issue_yields_empty_types_and_in_progress()
    {
        var lineage = LineageAssembler.Assemble("i", new List<DocumentInstance>());
        lineage.Types.Should().BeEmpty();
        lineage.Outcome.Should().Be("in-progress");
    }

    [Test]
    public void Assemble_throws_corrupt_body_on_invalid_stored_body()
    {
        var rows = new List<DocumentInstance>
        {
            // Registered type, but the stored body fails validation (should be
            // unreachable — write validates — hence the tripwire).
            DocumentTestData.Row(Guid.NewGuid(), "i", DocumentTestData.DecompositionType, "accepted", 1,
                "{}", _tenant),
        };

        var act = () => LineageAssembler.Assemble("i", rows);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.STORE.CORRUPT_BODY");
    }

    [Test]
    public void AssembleDocument_projects_a_single_row()
    {
        var id = Guid.NewGuid();
        var row = DocumentTestData.Row(id, "i", DocumentTestData.DecompositionType, "accepted", 3,
            DocumentTestData.ValidDecompositionBody, _tenant);

        var entry = LineageAssembler.AssembleDocument(row);

        entry.Id.Should().Be(id);
        entry.Revision.Should().Be(3);
        entry.Status.Should().Be("accepted");
        entry.Reviews.Should().BeEmpty();
    }
}
