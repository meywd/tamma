# Finding: Assessment-family migration (39-13) — policy gaps & behavior changes

**Date**: 2026-07-23
**Author**: Claude (agent)
**Type**: 🚨 Known Issue
**Category**: Architecture

## 📋 Summary

Migrating the assessment family (Research / Ambiguity / Clarify / Design) onto the document
lifecycle (Story 39-13) surfaced four items that were **filed back**, not patched in the family
bindings: an operator-visible ambiguity-threshold default change, a missing per-type
human-assignment default for Design, a delivery-workflow double-emit on crash re-entry, and an
inert cross-run supersedes input on the Clarify binding.

**All four are now resolved** (2026-07-24). Items 1 and 4 are decisions ("keep it" / "don't build
it"); items 2 and 3 shipped code. One follow-up remains — see Action Items.

## 🔍 Context

Discovered while implementing **Story 39-13**. Each binding follows the 39-12 recipe; these are the
places where the generic policy/lifecycle layer doesn't yet provide what the family needs, so the
plan's "fix in the generic layer, file back" rule applies.

### Related Components
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs` / `AcceptanceDefaults.cs`
- `apps/tamma-elsa/src/Tamma.Api/Dtos/AcceptanceRules/AcceptanceRulesDtos.cs` (admin PUT surface)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` (delivery hook)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/{AmbiguityScoringWorkflow,ClarifyingQuestionsWorkflow,DesignProposalWorkflow,DesignDeliveryWorkflow}.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/DocumentInstanceRepository.cs` (the supersession chain invariant)
- Epic 39, Stories 39-13 / 39-5 / 39-6

## 💡 The Findings

### 1. Ambiguity escalation threshold default changed 0.5 → 0.7 (operator-visible) — ✅ RESOLVED
The retired `AmbiguityThresholds.DefaultClarify` was **0.5**. The effective threshold now comes from
`AcceptanceRules.AmbiguityEscalationThreshold`, whose default is **0.7**. An assessment scoring in
[0.5, 0.7) that previously triggered clarification now passes below-threshold under default rules.

**Resolved — keep 0.7 (user decision).** Confirmed explicitly by the user over legacy parity at 0.5:
0.7 is the intended shipped default and no code change is wanted. The threshold stays admin-tunable
per 39-5, so a deployment that wants legacy parity sets 0.5 in its rules row. Pinned by
`AcceptanceDefaultsDriftTests.Shared_knobs_are_pinned`.

### 2. Design has no per-type "pinned to human by default" rules field (D4 gap) — ✅ RESOLVED (model + default)
39-13 D4 called for shipping a `design` per-type default that pins acceptance to human assignment.
39-5's `AcceptanceRules` exposed **no** per-type autonomy-floor / human-assignment field — the only
per-type mechanism (`AcceptanceDefaults.For`) selects single-reviewer vs panel, not who decides.

**Resolved (model half).** `AcceptanceRules` now carries an OPTIONAL, additive
`AcceptorRequirement` (wire `acceptorRequirement`), a `[Wire]` enum with two members:

| Member | Wire | Meaning |
|---|---|---|
| `Any` (default) | `any` | The autonomy dial alone decides who accepts — exactly the pre-39-13 behavior. |
| `Human` | `human` | The decision is pinned to a person regardless of how high the autonomy dial is set. |

`Any` is deliberately the FIRST member (so it is the CLR default): a rules row written before the
field existed, and any construction that omits it, loads as today's behavior rather than silently
tightening a policy. `Validate()` rejects an undefined value with `ACCEPTANCE_RULES.INVALID`, per the
file's existing convention. `AcceptanceDefaults.For(design)` ships `Human`; every other type stays
`Any`. Reviewer selection for `design` is UNCHANGED (single architect, unanimous) — only *who answers
the accept decision* is pinned. The field is expressible over the admin surface
(`AcceptanceRulesUpsertRequest`, trailing + defaulted so pre-existing bodies still bind).

**Remaining — the gate does not yet honor it (deliberate).** Nothing reads `AcceptorRequirement`
today, so shipping it is 100% behavior-preserving. The natural consumer is 39-17's orchestrator
routing (`AcceptanceRouting.DecideSelf` vs `AssignToRole`), which has no production consumer yet.
Wiring it into `AcceptanceGuardrails.Clamp` instead was rejected as NOT a small change: the clamp can
only convert a decision to `Escalate(reason)`, and there is no fitting member in
`AcceptanceEscalationReason` — that vocabulary is deliberately closed and count-pinned at exactly six
(39-5 D10), is mapped by `ToLifecycleOutcome`, and is on the dashboard wire. Expanding it is a
coordinated 39-5/39-17 contract change, not a follow-up patch.

### 3. Delivery workflow can double-emit on crash re-entry at ACCEPT — ✅ RESOLVED
The D5 delivery hook dispatched `deliveryWorkflowDefinitionId` (`design-proposal-delivery`) on each
entry to ACCEPT, and that child emits `DESIGN.PROPOSAL.GENERATED` / `DELIVERED`. The ACCEPT region has
TWO inbound edges — `RouteAccept` (review approved) and `ReEntryAcceptGate` (39-10 crash re-entry) —
and the dispatch was gated only by `HasDeliveryGate` ("is a delivery workflow configured?"), never by
re-entry position the way the bindings' own legacy emits are (D8/D10). A crash + re-entry that
resumed at ACCEPT therefore re-delivered and re-emitted both events.

**Resolved.** `DocumentLifecycleWorkflow` now puts a second `FlowDecision`, `DeliveryReEntryGate`
("First Entry To Accept?", condition `reEntryStage != "accept"`), between `HasDeliveryGate` "True"
and `DispatchDelivery`; its "False" edge goes straight to `PublishAcceptanceRequest`. A run that
resumed at ACCEPT skips delivery entirely, so delivery — and its legacy emits — happen exactly once
per delivered revision.

A revise round that loops back through REVIEW into ACCEPT *does* re-deliver: that is a NEW revision
the decider has not seen, not a duplicate of an already-delivered one. Only the crash-resume edge is
suppressed.

Pinned by `DocumentLifecycleWorkflowStructureTests.Workflow_DeliveryDispatch_SitsBehindTheReEntryGate`
(the dispatch's ONLY inbound edge is `DeliveryReEntryGate`/`True`; that gate's only inbound is
`HasDeliveryGate`/`True`; the "False" edge reaches publish; ACCEPT is still entered only via
`BuildAcceptanceRequest → HasDeliveryGate`, so no path routes around the gate).

### 4. `Clarification` Run B's `supersedesDocumentId` was inert — ✅ RESOLVED (removed, by design)
The Clarify binding passed `supersedesDocumentId` = Run A's document id in its Run B dispatch input,
but `DocumentLifecycleWorkflow` exposes no such input, so the key was silently dropped.

**Resolved by REMOVING the inert input, not by adding the lifecycle edge.** Threading it into the
produced envelope's `DocumentEnvelope.SupersedesDocumentId` conflicts with how supersession already
works, in three concrete ways:

1. **The lifecycle owns the field end-to-end.** `IngestDraft` derives the edge solely from
   `isRevise`: a revise supersedes the reviewed draft, everything else supersedes nothing. But
   `isRevise: false` does NOT mean "first draft" — a validation *repair* also takes that branch, and
   repairs happen inside revise rounds too. A cross-run edge applied on `!isRevise` would re-apply
   itself to a repair after a revise, re-targeting an already-superseded prior.
2. **The store's chain is strictly linear and write-once per prior.** `DocumentInstanceRepository`
   flips the prior row to `superseded` inside the insert, and a unique filtered index on
   `supersedes_document_id` means two rows cannot supersede the same prior (`23505`). Combined with
   (1) that is a fail-loud fault of the run, not a lineage improvement.
3. **It would change crash-resume behavior.** Superseding Run A's ACCEPTED clarification changes what
   `GetLatestAcceptedAsync` returns for the `clarification` type, which is exactly what
   `ComputeReEntryPosition` folds — so the edge silently retargets Clarify's re-entry position. That
   is 39-6/39-10 work, not an additive lineage tweak.

Cross-run lineage for the two Clarification runs rides the shared `issueId`/`correlationId`, which is
what the 39-11 lineage query groups on — so the lineage is not lost, only the redundant explicit edge.
There is house precedent for this reading: `DebugDiagnosisWorkflow` accepts a cross-run
`supersedesDocumentId` and folds it into the PRODUCER CONTEXT (`FoldRecentChanges`), never into the
envelope. The binding's Run B dispatch now carries a comment recording the decision, and the dead
`RunADocId` variable is gone.

## ✅ Action Items
- [x] Confirm/adjust the ambiguity threshold default (0.7 vs legacy 0.5) — **keep 0.7 (user decision)**.
- [x] Add a per-type human-assignment/autonomy-floor field to `AcceptanceRules` (39-5) and ship the
      `design` default — `AcceptorRequirement { any | human }`, `design → human`.
- [x] Gate the D5 delivery dispatch on re-entry position (39-6) to guarantee exactly-once
      `DESIGN.PROPOSAL.GENERATED/DELIVERED`.
- [x] `Clarification` Run B's `supersedesDocumentId` — removed as inert; cross-run lineage stays on
      shared `issueId`/`correlationId` (rationale above).
- [ ] **Remaining:** make an acceptor honor `AcceptanceRules.AcceptorRequirement`. Belongs to 39-17's
      orchestrator routing (`Human` ⇒ never `DecideSelf`). If it is ever wanted in the deterministic
      clamp instead, that needs a coordinated 39-5 decision to open the six-member
      `AcceptanceEscalationReason` vocabulary.
- [ ] **Remaining:** wire the crash-at-accept execution scenario so the exactly-once delivery
      assertion runs against a live host (the structure pin covers the topology; the `[Explicit]`
      crash scenario is still not wired).
- [ ] **Unrelated latent bug spotted while analysing #4** (NOT introduced or fixed here): a repair
      that runs *after* a revise mints a draft with `SupersedesDocumentId = null`, breaking the
      supersession chain for that round. `IngestDraft` should inherit
      `state.Current?.SupersedesDocumentId` on the repair branch. Belongs to 39-6/39-11.

## 🔗 Related
- `.dev/findings/document-lifecycle-persist-not-wired.md` (AC7 live-store gap — same "file back" class)
- `.dev/findings/planning-family-review-surface-changes.md` (39-14 sibling behavior changes)
- `docs/stories/epic-39/story-39-13/implementation-plan.md` (D4, D5, D7, Risks)

## 📊 Impact Assessment
**Severity**: 🟢 Low (was 🟡 Medium) — the double-emit is fixed and pinned; the threshold default is a
confirmed decision; the acceptor floor is declared and defaulted. What remains is consumer wiring in
a story that has not started (39-17) plus one execution-test scenario.

---

**Status**: ✅ Resolved (two follow-ups tracked above)
**Last Updated**: 2026-07-24
