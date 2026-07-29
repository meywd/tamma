using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-1c AC1/AC5/AC6 — the FAST (default-gate) proof that prose rides the
/// generic lifecycle machinery: <see cref="DocumentLifecycleHelper"/> accepts a
/// prose producer spec, resolves the D6 tech_writer rules, mints a draft whose
/// envelope carries the payload's audience (D2), selects an EXECUTABLE reviewer
/// lens for (tech_writer, prose) — the arm 41-1a landed — and routes an approved
/// review to Accept. The runtime wiring half lives in the CI-only
/// <see cref="ProseLifecycleExecutionTests"/>; the no-bespoke-branch half in
/// <see cref="ProseLifecycleStructureTests"/>.
/// </summary>
[TestFixture]
public class ProseLifecycleHelperTests
{
    private const string ProsePayloadJson =
        """{"kind":"adr","audience":"engineering","title":"ADR-001: Prose rides the lifecycle","body":"## Decision\nRegister a prose type; body stays free markdown."}""";

    [Test]
    public void ValidateProducerSpec_AcceptsAProseProducingPair()
    {
        var act = () => DocumentLifecycleHelper.ValidateProducerSpec("devops", "write-postmortem", "prose");
        act.Should().NotThrow("(devops, write-postmortem) is an eligible taxonomy pair and 'prose' is a registered type");
    }

    [Test]
    public void ResolveRules_ForProse_ResolvesTheTechWriterRow()
    {
        var resolved = DocumentLifecycleHelper.ResolveRules(null, "prose", DateTimeOffset.UtcNow);
        resolved.Rules.ReviewerSelection.Mode.Should().Be(ReviewerMode.SingleReviewer);
        resolved.Rules.ReviewerSelection.ReviewerRole.Should().Be(AgentRole.TechWriter.ToWire(),
            "a bare prose dispatch takes the 41-1c D6 default, not the architect catch-all");
    }

    [Test]
    public void TechWriterReviewerLens_IsExecutableForProse()
    {
        // 41-1c's Related note: the D6 row could not execute until 41-1a landed
        // the TechWriter selector arm. It has — pin the whole reviewer producer
        // triple the workflow's BuildReviewEnvelope constructs.
        var action = RolePhaseMap.GetPanelActionForRole(AgentRole.TechWriter, "prose");
        action.Should().Be(AgentAction.ReviewDocs);
        var producer = DocumentProducer.Create(
            AgentRole.TechWriter.ToWire(), action.ToWire(), "document-review");
        producer.Role.Should().Be("tech_writer");
    }

    [Test]
    public void FullProseCycle_DraftValidateReviewAccept_UsingOnlyGenericHelpers()
    {
        // AC1's mechanics with ONLY the generic helper surface — the same calls
        // DocumentLifecycleWorkflow makes for every other type; nothing prose-shaped.
        var state = NewProseState();

        // PRODUCE — mint the draft; the envelope carries the payload audience (D2).
        using var doc = JsonDocument.Parse(ProsePayloadJson);
        var producer = DocumentProducer.Create("devops", "write-postmortem", "document-lifecycle");
        var draft = DocumentLifecycleHelper.MintDraft(
            state, doc.RootElement.Clone(), producer, supersedes: null, DateTimeOffset.UtcNow);
        draft.Type.Should().Be("prose");
        draft.Audience.Should().Be("engineering", "the draft-mint path copies payload → envelope");
        state = DocumentLifecycleHelper.AppendDraft(state, draft);

        // VALIDATE — through the registry, like the workflow's VALIDATE stage.
        DocumentTypeRegistry.Resolve(state.TypeKey).Validate(state.Current!.Payload)
            .IsValid.Should().BeTrue("a well-tagged markdown body validates (AC2)");

        // REVIEW — the reviewer's Review envelope references the prose draft
        // (AC5's ParentDocumentId linkage, exactly as BuildReviewEnvelope mints it).
        var reviewJson =
            $$"""{"subject":{"kind":"document","documentId":"{{draft.Id}}","documentType":"prose"},"decision":"approve","summary":"clear and correctly tagged","issues":[]}""";
        using var reviewDoc = JsonDocument.Parse(reviewJson);
        var reviewer = DocumentProducer.Create(
            AgentRole.TechWriter.ToWire(),
            RolePhaseMap.GetPanelActionForRole(AgentRole.TechWriter, state.TypeKey).ToWire(),
            "document-review");
        var review = DocumentEnvelope.CreateDraft(
            DocumentTypeKey.Review, 1, state.IssueId, state.CorrelationId, reviewer,
            reviewDoc.RootElement.Clone(), parentDocumentId: state.Current!.Id);
        review.ParentDocumentId.Should().Be(draft.Id, "the Review is OVER the prose document (AC5)");
        review.Audience.Should().BeNull("a review payload carries no audience — only prose is tagged");
        state = DocumentLifecycleHelper.AppendReview(state, review);

        // ROUTE — an approving review routes to ACCEPT; the terminal result is Accepted.
        DocumentLifecycleHelper.ComputeReviewRoute(state, reviewJson)
            .Should().Be(DocumentLifecycleHelper.ReviewRoute.Accept);
        var result = DocumentLifecycleHelper.BuildAccepted(state, state.Current!.Id);
        result.Status.Should().Be(DocumentLifecycleResult.StatusAccepted);
        result.DocumentId.Should().Be(draft.Id);
    }

    private static DocumentLifecycleHelper.LifecycleState NewProseState()
    {
        var resolved = DocumentLifecycleHelper.ResolveRules(null, "prose", DateTimeOffset.UtcNow);
        return DocumentLifecycleHelper.Init(
            "devops", "write-postmortem", "{}", "prose",
            "issue-prose-1", "corr-prose-1", Guid.NewGuid(), "revisionFeedback", null, resolved);
    }
}
