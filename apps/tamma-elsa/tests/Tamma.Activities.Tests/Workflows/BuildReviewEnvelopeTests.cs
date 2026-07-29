using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Resume;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-1c follow-ups (adversarial review 2026-07-29) — EXECUTING proof of the
/// Review → subject parent linkage (the 39-11 D8 parent-first rule) at every mint
/// site, since the full-runtime execution fixtures do not currently run anywhere
/// (see <see cref="ProseLifecycleExecutionTests"/>' [Explicit] reason):
///
/// <list type="bullet">
/// <item><see cref="DocumentLifecycleWorkflow.BuildReviewEnvelope"/> (the lifecycle's
/// internal mint site, via InternalsVisibleTo) — driven with a state built through
/// the REAL helper chain (Init → MintDraft → AppendDraft), for a prose draft AND a
/// legacy (decomposition) draft: the minted Review's ParentDocumentId is the current
/// draft's id, type-agnostically. Not tautological: the test never passes a
/// parentDocumentId — the production code derives it from the lifecycle state.</item>
/// <item><see cref="ReviewProducerHelper.MintReviewEnvelope"/> — the 39-7 producers'
/// shared mint (single-reviewer <c>BuildEnvelope</c> and panel
/// <c>BuildAggregateEnvelope</c> both route through it): a document subject's id
/// becomes the parent; a diff subject yields none.</item>
/// <item><see cref="DocumentLifecycleHelper.ApplyReEntry"/> — the Accept re-entry's
/// synthesized recovered review is parent-linked to the recovered draft.</item>
/// </list>
/// </summary>
[TestFixture]
public class BuildReviewEnvelopeTests
{
    private const string ProsePayloadJson =
        """{"kind":"adr","audience":"engineering","title":"ADR-001: Prose rides the lifecycle","body":"## Decision\nwords"}""";

    private const string DecompositionPayloadJson =
        """{"summary":"s","subtasks":[]}""";

    private static string ApprovingReviewJson(Guid subjectId, string subjectType) =>
        $$"""{"subject":{"kind":"document","documentId":"{{subjectId}}","documentType":"{{subjectType}}"},"decision":"approve","summary":"ok","issues":[]}""";

    // ── DocumentLifecycleWorkflow.BuildReviewEnvelope (F1) ────────────────────

    [Test]
    public void BuildReviewEnvelope_ProseState_ParentIsTheCurrentDraft()
    {
        var state = StateWithDraft("devops", "write-postmortem", "prose", ProsePayloadJson);
        var draftId = state.Current!.Id;

        var review = DocumentLifecycleWorkflow.BuildReviewEnvelope(
            state, ApprovingReviewJson(draftId, "prose"), "document-review");

        review.Type.Should().Be("review");
        review.ParentDocumentId.Should().Be(draftId,
            "the lifecycle-minted Review is OVER the current draft — parent-first linkage (39-11 D8 / 41-1c AC5)");
        review.ProducedBy.Role.Should().Be(AgentRole.TechWriter.ToWire(),
            "a bare prose dispatch resolves the 41-1c D6 tech_writer reviewer default");
    }

    [Test]
    public void BuildReviewEnvelope_LegacyDecompositionState_ParentIsTheCurrentDraft_TypeAgnostic()
    {
        var state = StateWithDraft("senior_developer", "decompose-issue", "decomposition", DecompositionPayloadJson);
        var draftId = state.Current!.Id;

        var review = DocumentLifecycleWorkflow.BuildReviewEnvelope(
            state, ApprovingReviewJson(draftId, "decomposition"), "document-review");

        review.Type.Should().Be("review");
        review.ParentDocumentId.Should().Be(draftId,
            "the parent linkage is type-agnostic — no prose branch, every type's review links the same way");
    }

    [Test]
    public void BuildReviewEnvelope_SecondDraft_ParentTracksTheLatestDraft()
    {
        // The parent must follow the CURRENT (latest) draft, not the first one.
        var state = StateWithDraft("devops", "write-postmortem", "prose", ProsePayloadJson);
        var firstDraftId = state.Current!.Id;
        state = AppendDraft(state, "devops", "write-postmortem", ProsePayloadJson, supersedes: firstDraftId);
        state.Current!.Id.Should().NotBe(firstDraftId);

        var review = DocumentLifecycleWorkflow.BuildReviewEnvelope(
            state, ApprovingReviewJson(state.Current!.Id, "prose"), "document-review");

        review.ParentDocumentId.Should().Be(state.Current!.Id);
        review.ParentDocumentId.Should().NotBe(firstDraftId);
    }

    // ── ReviewProducerHelper.MintReviewEnvelope (F2 — 39-7 producers) ─────────

    [Test]
    public void MintReviewEnvelope_DocumentSubject_ParentIsTheSubjectDocument()
    {
        var subjectId = UuidV7.NewGuid();
        var subject = new ReviewSubject
        {
            Kind = ReviewerSelectionHelper.DocumentSubjectKind,
            DocumentId = subjectId,
            DocumentType = "prose",
        };
        var producer = DocumentProducer.Create(
            AgentRole.TechWriter.ToWire(),
            RolePhaseMap.GetPanelActionForRole(AgentRole.TechWriter, "prose").ToWire(),
            "review-single-reviewer");
        using var payload = JsonDocument.Parse(ApprovingReviewJson(subjectId, "prose"));

        var envelope = ReviewProducerHelper.MintReviewEnvelope(
            subject, producer, "issue-1", "corr-1", payload.RootElement.Clone(), DateTimeOffset.UtcNow);

        envelope.Type.Should().Be("review");
        envelope.ParentDocumentId.Should().Be(subjectId,
            "the 39-7 producers mint parent-linked reviews so lineage never needs the body probe");
        envelope.State.Should().Be(DocumentState.Validated);
    }

    [Test]
    public void MintReviewEnvelope_DiffSubject_HasNoParentDocument()
    {
        var subject = new ReviewSubject
        {
            Kind = ReviewerSelectionHelper.DiffSubjectKind,
            Repository = "owner/repo",
            PrNumber = 7,
        };
        var producer = DocumentProducer.Create(
            AgentRole.TechWriter.ToWire(),
            RolePhaseMap.GetPanelActionForRole(AgentRole.TechWriter, "prose").ToWire(),
            "review-single-reviewer");
        using var payload = JsonDocument.Parse(
            """{"subject":{"kind":"diff","repository":"owner/repo","prNumber":7},"decision":"approve","summary":"ok","issues":[]}""");

        var envelope = ReviewProducerHelper.MintReviewEnvelope(
            subject, producer, "issue-1", "corr-1", payload.RootElement.Clone(), DateTimeOffset.UtcNow);

        envelope.ParentDocumentId.Should().BeNull("code is not a document type — a diff review has no parent document");
    }

    [Test]
    public void ParentDocumentIdFor_IsDrivenBySubjectKind()
    {
        var id = UuidV7.NewGuid();
        ReviewProducerHelper.ParentDocumentIdFor(
                new ReviewSubject { Kind = ReviewerSelectionHelper.DocumentSubjectKind, DocumentId = id })
            .Should().Be(id);
        ReviewProducerHelper.ParentDocumentIdFor(
                new ReviewSubject { Kind = ReviewerSelectionHelper.DiffSubjectKind, Repository = "o/r", PrNumber = 1 })
            .Should().BeNull();
    }

    // ── DocumentLifecycleHelper.ApplyReEntry (F2 — re-entry synthesis) ────────

    [Test]
    public void ApplyReEntry_AcceptPosition_SynthesizedReview_ParentIsTheRecoveredDraft()
    {
        var state = NewState("devops", "write-postmortem", "prose");
        var existing = MintDraft(state, "devops", "write-postmortem", ProsePayloadJson, supersedes: null);
        var position = new LifecycleResumePosition
        {
            DocumentTypeKey = "prose",
            ResumeAt = LifecycleResumeStage.Accept,
            ExistingDocumentId = existing.Id,
            ExistingRevision = 1,
            Basis = "approval requested, undecided at revision 1",
        };

        var reentered = DocumentLifecycleHelper.ApplyReEntry(state, position, existing);

        reentered.Current!.Id.Should().Be(existing.Id, "the recovered draft becomes the current draft");
        reentered.Reviews.Should().ContainSingle()
            .Which.ParentDocumentId.Should().Be(existing.Id,
                "the synthesized recovered review is OVER the recovered draft — parent-first linkage");
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static DocumentLifecycleHelper.LifecycleState NewState(string role, string action, string typeKey)
    {
        var resolved = DocumentLifecycleHelper.ResolveRules(null, typeKey, DateTimeOffset.UtcNow);
        return DocumentLifecycleHelper.Init(
            role, action, "{}", typeKey, "issue-1", "corr-1", UuidV7.NewGuid(), "revisionFeedback", null, resolved);
    }

    private static DocumentEnvelope MintDraft(
        DocumentLifecycleHelper.LifecycleState state, string role, string action, string payloadJson, Guid? supersedes)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var producer = DocumentProducer.Create(role, action, "document-lifecycle");
        return DocumentLifecycleHelper.MintDraft(
            state, doc.RootElement.Clone(), producer, supersedes, DateTimeOffset.UtcNow);
    }

    private static DocumentLifecycleHelper.LifecycleState AppendDraft(
        DocumentLifecycleHelper.LifecycleState state, string role, string action, string payloadJson, Guid? supersedes)
        => DocumentLifecycleHelper.AppendDraft(state, MintDraft(state, role, action, payloadJson, supersedes));

    private static DocumentLifecycleHelper.LifecycleState StateWithDraft(
        string role, string action, string typeKey, string payloadJson)
        => AppendDraft(NewState(role, action, typeKey), role, action, payloadJson, supersedes: null);
}
