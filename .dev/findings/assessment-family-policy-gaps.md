# Finding: Assessment-family migration (39-13) — policy gaps & behavior changes

**Date**: 2026-07-23
**Author**: Claude (agent)
**Type**: 🚨 Known Issue
**Category**: Architecture

## 📋 Summary

Migrating the assessment family (Research / Ambiguity / Clarify / Design) onto the document
lifecycle (Story 39-13) surfaced three items that are **filed back**, not patched in the family
bindings: an operator-visible ambiguity-threshold default change, a missing per-type
human-assignment default for Design, and a delivery-workflow double-emit on crash re-entry.

## 🔍 Context

Discovered while implementing **Story 39-13**. Each binding follows the 39-12 recipe; these are the
places where the generic policy/lifecycle layer doesn't yet provide what the family needs, so the
plan's "fix in the generic layer, file back" rule applies.

### Related Components
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs` / `AcceptanceDefaults.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` (delivery hook)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/{AmbiguityScoringWorkflow,DesignProposalWorkflow,DesignDeliveryWorkflow}.cs`
- Epic 39, Stories 39-13 / 39-5 / 39-6

## 💡 The Findings

### 1. Ambiguity escalation threshold default changed 0.5 → 0.7 (operator-visible)
The retired `AmbiguityThresholds.DefaultClarify` was **0.5**. The effective threshold now comes from
`AcceptanceRules.AmbiguityEscalationThreshold`, whose default is **0.7**. An assessment scoring in
[0.5, 0.7) that previously triggered clarification now passes below-threshold under default rules.
This is admin-tunable per 39-5, so parity is a config choice — but the default shipped is different
from legacy. **Action:** confirm 0.7 is intended with the 39-5 owner; if legacy parity is required,
set the per-type/system default back to 0.5.

### 2. Design has no per-type "pinned to human by default" rules field (D4 gap)
39-13 D4 called for shipping a `design` per-type default that pins acceptance to human assignment.
39-5's `AcceptanceRules` exposes **no** per-type autonomy-floor / human-assignment field — the only
per-type mechanism (`AcceptanceDefaults.For`) selects single-reviewer vs panel, not who decides.
So no such default was shipped; Design uses the base single-architect/unanimous rules and the
orchestrator's autonomy dial decides who accepts, same as every other type. **Action:** file to 39-5
to add a per-type autonomy-floor / human-required field, then ship the `design` default.

### 3. Delivery workflow can double-emit on crash re-entry at ACCEPT
The D5 delivery hook dispatches `deliveryWorkflowDefinitionId` (`design-proposal-delivery`) on each
entry to ACCEPT, and that child emits `DESIGN.PROPOSAL.GENERATED` / `DELIVERED`. On a crash + re-entry
that re-enters the ACCEPT region, the delivery is re-dispatched and those events re-emit — the
delivery dispatch is **not** gated by the lifecycle's re-entry position the way the binding's own
legacy emits are (D8/D10). **Action:** gate the delivery dispatch on re-entry position inside
`DocumentLifecycleWorkflow` (a 39-6 change), then add the exactly-once assertion to the `[Explicit]`
crash scenario. Not reachable in the wired execution scenarios (a)–(d); the crash-at-accept scenario
is not yet wired.

## ✅ Action Items
- [ ] Confirm/adjust the ambiguity threshold default (0.7 vs legacy 0.5) with the 39-5 owner.
- [ ] Add a per-type human-assignment/autonomy-floor field to `AcceptanceRules` (39-5) and ship the
      `design` default.
- [ ] Gate the D5 delivery dispatch on re-entry position (39-6) to guarantee exactly-once
      `DESIGN.PROPOSAL.GENERATED/DELIVERED`; wire the crash-at-accept execution scenario.
- [ ] (Related) `Clarification` Run B's `supersedesDocumentId` is passed but inert — 39-6 exposes no
      cross-run supersedes edge; cross-run lineage currently rides shared `issueId`/`correlationId`.
      Add the supersedes edge to the lifecycle if explicit document-chain lineage is required.

## 🔗 Related
- `.dev/findings/document-lifecycle-persist-not-wired.md` (AC7 live-store gap — same "file back" class)
- `docs/stories/epic-39/story-39-13/implementation-plan.md` (D4, D5, D7, Risks)

## 📊 Impact Assessment
**Severity**: 🟡 Medium — none block 39-13's own gates (structure/resume/events/drift all pass);
all are generic-layer follow-ups. Item 1 is the most operator-visible.

---

**Status**: 🔍 Needs Review
**Last Updated**: 2026-07-23
