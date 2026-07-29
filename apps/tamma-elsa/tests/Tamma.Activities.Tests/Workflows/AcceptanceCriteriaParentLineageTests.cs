using System;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-2 AC3, conformance follow-up F9 (2026-07-29) — the PERSISTED lineage edge.
///
/// <para><b>What was broken.</b> AC3 claims the accepted document is "persisted with
/// lineage: Issue → Clarification? → AcceptanceCriteria", carried by
/// <c>DocumentInstance.ParentDocumentId</c>. As first landed,
/// <c>AcceptanceCriteriaAuthoringWorkflow</c> computed that parent AFTER the lifecycle
/// returned and exposed it only as a workflow OUTPUT. The persisted row's parent was
/// never set: <c>DocumentLifecycleHelper.MintDraft</c> called
/// <see cref="DocumentEnvelope.CreateDraft"/> without a <c>parentDocumentId</c>, so it
/// defaulted to null — and that is the ONLY draft-minting call site in <c>src/</c>. The
/// edge existed in the event stream and nowhere in <c>document_instances</c>.
///
/// <para><b>What landed instead.</b> An optional <c>parentDocumentId</c> is threaded
/// through the lifecycle dispatch (<c>DocumentLifecycleWorkflow</c>'s Init) into
/// <see cref="DocumentLifecycleHelper.LifecycleState.ParentDocumentId"/> and out through
/// <c>MintDraft</c>. It is optional and TRAILING, defaulting to null, so every other
/// producer that dispatches <c>document-lifecycle</c> is byte-identical — which this
/// fixture pins in both directions:</para>
/// <list type="number">
/// <item>the 41-2 shape (an accepted Clarification exists) ⇒ the minted envelope's
/// <c>ParentDocumentId</c> IS that Clarification's id, on the produce draft and on
/// every repair/revise draft of the same run;</item>
/// <item>an existing producer (the 39-6 decomposition pilot's exact Init call, which
/// passes nothing) ⇒ the minted envelope's <c>ParentDocumentId</c> is still null.</item>
/// </list>
///
/// <para>The envelope is the unit the store writes verbatim
/// (<c>DocumentInstanceRepository.InsertAsync</c>:
/// <c>ParentDocumentId = envelope.ParentDocumentId</c>), and the persisted-row half is
/// asserted end-to-end against real Postgres by
/// <c>NewDocumentTypeStoreRoundTripTests.AcceptanceCriteria_PersistsAndReadsBackItsParentDocumentEdge</c>.</para>
/// </summary>
[TestFixture]
public class AcceptanceCriteriaParentLineageTests
{
    private const string AcceptanceCriteriaType = "acceptance-criteria";

    // ── (a) the 41-2 shape: an accepted Clarification is the persisted parent ──

    [Test]
    public void AcceptedClarification_IsTheMintedDraftsParentDocumentId()
    {
        var clarificationId = UuidV7.NewGuid();
        var findingsId = UuidV7.NewGuid();

        // The binding's own chooser, then the lifecycle's own input parser — the real
        // chain, not a hand-picked Guid: clarification wins over findings (D4).
        var chosen = AcceptanceCriteriaBindingHelper.ChooseParentDocumentId(
            clarificationId.ToString(), findingsId.ToString());
        chosen.Should().Be(clarificationId.ToString());

        var state = AcceptanceCriteriaState(DocumentLifecycleHelper.ParseParentDocumentId(chosen));
        var draft = Mint(state, supersedes: null);

        draft.ParentDocumentId.Should().Be(clarificationId,
            "AC3 — the PERSISTED acceptance-criteria row must carry the Issue → Clarification → "
            + "AcceptanceCriteria edge, not just the workflow output and the DRAFTED payload");
        draft.SupersedesDocumentId.Should().BeNull("a first draft supersedes nothing (39-2 D4)");
    }

    [Test]
    public void Findings_IsTheParent_WhenNoClarificationWasAccepted()
    {
        var findingsId = UuidV7.NewGuid();

        var chosen = AcceptanceCriteriaBindingHelper.ChooseParentDocumentId("", findingsId.ToString());
        var state = AcceptanceCriteriaState(DocumentLifecycleHelper.ParseParentDocumentId(chosen));

        Mint(state, supersedes: null).ParentDocumentId.Should().Be(findingsId,
            "D4 — Clarification first, else the Findings");
    }

    [Test]
    public void NeitherUpstreamDocument_LeavesTheParentSlotNull()
    {
        var chosen = AcceptanceCriteriaBindingHelper.ChooseParentDocumentId(null, null);
        var state = AcceptanceCriteriaState(DocumentLifecycleHelper.ParseParentDocumentId(chosen));

        Mint(state, supersedes: null).ParentDocumentId.Should().BeNull(
            "acceptance criteria are authorable from the issue alone — no upstream, no parent edge");
    }

    [Test]
    public void EveryDraftOfTheRun_InheritsTheParent_ProduceRepairAndRevise()
    {
        var clarificationId = UuidV7.NewGuid();
        var state = AcceptanceCriteriaState(clarificationId);

        var produce = Mint(state, DocumentLifecycleHelper.ResolveSupersedes(
            state, DocumentLifecycleHelper.DraftOrigin.Produce));
        state = DocumentLifecycleHelper.AppendDraft(state, produce);

        var repair = Mint(state, DocumentLifecycleHelper.ResolveSupersedes(
            state, DocumentLifecycleHelper.DraftOrigin.Repair));
        state = DocumentLifecycleHelper.AppendDraft(state, repair);

        var revise = Mint(state, DocumentLifecycleHelper.ResolveSupersedes(
            state, DocumentLifecycleHelper.DraftOrigin.Revise));

        new[] { produce, repair, revise }.Should().OnlyContain(e => e.ParentDocumentId == clarificationId,
            "the upstream document the RUN descends from does not change between rounds — only the "
            + "supersession chain does");
        revise.SupersedesDocumentId.Should().Be(repair.Id,
            "the parent edge is orthogonal to the supersession edge — threading one must not disturb the other");
    }

    [Test]
    public void TheParent_SurvivesTheStateSerializationRoundTrip()
    {
        // The lifecycle holds its state in ONE workflow variable across suspend/resume
        // (the accept gate suspends). A parent that does not survive Serialize/Deserialize
        // would silently vanish on any run that revises after a human decision.
        var clarificationId = UuidV7.NewGuid();
        var state = AcceptanceCriteriaState(clarificationId);

        var rehydrated = DocumentLifecycleHelper.Deserialize(DocumentLifecycleHelper.Serialize(state));

        rehydrated.ParentDocumentId.Should().Be(clarificationId);
        Mint(rehydrated, supersedes: null).ParentDocumentId.Should().Be(clarificationId);
    }

    // ── (b) every other producer is unchanged ────────────────────────────

    [Test]
    public void AnExistingProducerThatPassesNothing_StillMintsANullParent()
    {
        // The 39-6 decomposition pilot's Init call, argument-for-argument — it supplies no
        // parentDocumentId, and the new parameter is optional + trailing, so this is the
        // pre-41-2 call site verbatim.
        var state = DocumentLifecycleHelper.Init(
            "senior_developer", "decompose-issue", "{}", "decomposition",
            "issue-1", "corr-1", UuidV7.NewGuid(), "revisionNotes", null,
            Resolved("decomposition"));

        state.ParentDocumentId.Should().BeNull("the parameter defaults to null for every existing caller");

        var producer = DocumentProducer.Create("senior_developer", "decompose-issue", "llm-call");
        using var doc = JsonDocument.Parse("{\"summary\":\"s\"}");
        var draft = DocumentLifecycleHelper.MintDraft(
            state, doc.RootElement.Clone(), producer, supersedes: null, DateTimeOffset.UtcNow);

        draft.ParentDocumentId.Should().BeNull(
            "no producer's persisted row may gain a parent edge it did not have before 41-2's threading");
    }

    [Test]
    public void ADeserializedPre41_2State_HasNoParent()
    {
        // A lifecycle that suspended BEFORE this change resumes from state json with no
        // "parentDocumentId" member. It must rehydrate to null, not throw.
        var state = DocumentLifecycleHelper.Init(
            "senior_developer", "decompose-issue", "{}", "decomposition",
            "issue-1", "corr-1", UuidV7.NewGuid(), "revisionNotes", null, Resolved("decomposition"));

        var json = DocumentLifecycleHelper.Serialize(state);
        var stripped = json.Replace("\"parentDocumentId\":null,", "", StringComparison.Ordinal)
                           .Replace(",\"parentDocumentId\":null", "", StringComparison.Ordinal);

        DocumentLifecycleHelper.Deserialize(stripped).ParentDocumentId.Should().BeNull();
    }

    // ── the input parser: a lineage hint may never fail a produce ────────

    [Test]
    public void ParseParentDocumentId_TreatsAbsentBlankGarbageAndEmptyAsNoParent()
    {
        DocumentLifecycleHelper.ParseParentDocumentId(null).Should().BeNull();
        DocumentLifecycleHelper.ParseParentDocumentId("").Should().BeNull();
        DocumentLifecycleHelper.ParseParentDocumentId("   ").Should().BeNull();
        DocumentLifecycleHelper.ParseParentDocumentId("not-a-guid").Should().BeNull(
            "a malformed lineage hint degrades to 'no parent' — it must never throw a produce away");
        DocumentLifecycleHelper.ParseParentDocumentId(Guid.Empty.ToString()).Should().BeNull(
            "the all-zero guid is 'unset', not a document id");

        var id = UuidV7.NewGuid();
        DocumentLifecycleHelper.ParseParentDocumentId($"  {id}  ").Should().Be(id);
    }

    // ── fixtures ─────────────────────────────────────────────────────────

    private static ResolvedAcceptanceRules Resolved(string typeKey) =>
        new(AcceptanceDefaults.Rules, AcceptanceRulesSource.SystemDefault, 1, typeKey, DateTimeOffset.UtcNow);

    private static DocumentLifecycleHelper.LifecycleState AcceptanceCriteriaState(Guid? parentDocumentId) =>
        DocumentLifecycleHelper.Init(
            "product_owner", "define-acceptance-criteria", "{}", AcceptanceCriteriaType,
            "issue-41-2", "issue-41-2", UuidV7.NewGuid(), "contextFindings", null,
            Resolved(AcceptanceCriteriaType),
            parentDocumentId: parentDocumentId);

    private static DocumentEnvelope Mint(DocumentLifecycleHelper.LifecycleState state, Guid? supersedes)
    {
        using var doc = JsonDocument.Parse("{\"criteria\":[]}");
        var producer = DocumentProducer.Create("product_owner", "define-acceptance-criteria", "llm-call");
        return DocumentLifecycleHelper.MintDraft(
            state, doc.RootElement.Clone(), producer, supersedes, DateTimeOffset.UtcNow);
    }
}
