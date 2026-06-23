# Completeness Audit — `TriagePanelReviewWorkflow`

**Date:** 2026-06-22
**Workflow:** `triage-panel-review` (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs`)
**Maturity:** **thin** — a happy-path 4-call fan-out + aggregate skeleton. Mediation posture is correct, but it has **no failure signal, no persistence, no panel-lifecycle audit event, and no usability gate** — failures are silently aggregated as empty reviews.

---

## Purpose & Owner

**Purpose:** Run a 4-role LLM panel (security analyst, developer, devops, tester) over one triage item and aggregate their assessments into a single `panelResultJson` for the downstream PO decision. Each role triages through its own lens via a role-specific action (`security → assess-vulnerability`, `developer/tester → triage-defect`, `devops → diagnose-incident`).

**Owner:** Epic 26 — Project Management & Triage, **Story 26-1 Issue Triage Workflow** (`docs/stories/epic-26/story-26-1/26-1-issue-triage-workflow.md`). The 4-role panel and the per-role triage action are wired through **Story 27-19** dispatch-site migration / role-action taxonomy (`docs/stories/epic-27/27-19-dispatch-site-migration.md`, `RolePhaseMap.GetTriageActionForRole`). It is a leaf sub-workflow of `TriageItemCycleWorkflow` (between `TriageContextGatheringWorkflow` and `TriagePODecisionWorkflow`).

**Call graph:** `IssueTriageWorkflow` → (fire&forget, singleton) `TriageItemCycleWorkflow` → `triage-context-gathering` → **`triage-panel-review`** → `triage-po-decision` → `ApplyTriageResultActivity`.

---

## Current Capabilities (what it actually does today)

Flow: `Init → SecReview(llm-call) → ExtractSec → DevReview → ExtractDev → DevOpsReview → ExtractDevOps → TesterReview → ExtractTester → Aggregate → Output panelResultJson → Finish` — a single linear chain, no branches.

- **Inputs:** `repository`, `itemJson`, `contextJson`. **Output:** `panelResultJson`.
- **`Init`** copies the three inputs into workflow variables (`contextJson` defaults to `"{}"`).
- **4 sequential role dispatches** via the `RoleTriageDispatch` helper, each a `DispatchWorkflow(WorkflowDefinitionId="llm-call", WaitForCompletion=true, enableTools=true)` carrying `role`, the role-specific `action` from `RolePhaseMap.GetTriageActionForRole`, and `variables{itemJson, contextJson, repository}`. **Mediation is correct** — no engine-held provider key; every LLM call goes through the central `llm-call` workflow (the 32-5 seam).
- **Per-role `ExtractTriageReview`** pulls `llmResponse` from the shared `llmResult`, slices the first `{`…last `}` and validates it parses; otherwise wraps the raw text as `{"rawAssessment": ...}`. When `llmResult` is null/has no `llmResponse`, it returns the literal `"{}"`.
- **`Aggregate`** builds `{ reviews: [{role, assessment}], reviewCount }` over a fixed `ReviewRoles` list; an empty (`"{}"`) review is recorded as a participating role with `assessment="{}"`.
- **DCB events today:** none from this workflow itself. The only audit events are the nested `llm-call` events. `SetVariable`/`Finish` carry no `EventType`; there is no `TRIAGE.PANEL.*` lifecycle record.

**Contrast — the sibling that shows the intended bar.** `PlanReviewWorkflow` (`plan-review`, same cluster) is a 3-phase multi-agent debate over 7 roles that: parses each role's **verdict** (`ReviewAggregationHelper.ParseRoleVerdict`), **persists every role's finding immediately** via `StoreRoleFindingActivity` (so partial results survive), runs an anonymized **rebuttal round**, supports **early-termination on unanimous approve**, a bounded **round loop**, and explicit **escalate-to-human**. The triage panel is the degenerate "fan-out + concatenate" version of the same shape, with none of the verdict-parsing, persistence, failure-surfacing, or audit structure.

---

## Intended Full Scope (with citations)

What a complete triage panel must do, grounded in spec + the cluster's own conventions:

- **Produce a structured, decision-usable panel result, not a string blob.** The panel exists to feed `TriagePODecisionWorkflow`. Story 26-1 has the PO classify type/priority/complexity/automation; the panel should hand the PO **per-role structured findings** (e.g. `verdict`/`severity`/`assessment`/`suggestedLabels`) plus a **success/failure roster**, the way `PlanReviewWorkflow` aggregates `{role, verdict, comments, suggestedChanges}` via `ReviewAggregationHelper` (`PlanReviewWorkflow.cs:224-273`).
- **Fail-closed, no soft-fail masking.** Per `CLAUDE.md` + `feedback_resolution_no_empty_fallback` + the pivot spec rule set (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §rules: "never silent-failure / no false-success"), a role whose `llm-call` returns nothing/garbage **must be recorded as failed**, not as a `{}` participant. The panel must expose `failedRoles`/`succeededCount` so the PO and the audit trail can see a degraded panel. The prior triage audit flags exactly this: "failed/empty role reviews are aggregated as `{}` with no failure signal (soft-fail masking)" (`docs/superpowers/audits/2026-06-22/workflow-audit-triage.md`, TriagePanelReviewWorkflow §, P1).
- **Persist each role's finding immediately.** Cluster convention (`PlanReviewWorkflow` → `StoreRoleFindingActivity`, "allows partial results to persist even if later scans fail", `StoreRoleFindingActivity.cs:14-18`). Triage findings should be stored to context/vector DB so they survive a later-role failure and are retrievable for re-triage and learning.
- **Emit cycle/panel-scoped DCB audit events.** Story 26-1 AC9 specifies `TRIAGE.ISSUE.STARTED/COMPLETED`; by the `AGGREGATE.ACTION.STATUS` convention (`CLAUDE.md`) the panel should emit `TRIAGE.PANEL.STARTED` / `.COMPLETED` / `.PARTIAL` / `.FAILED` carrying role count + success count. The triage cluster audit calls this out as a P1 gap across the whole cluster (no per-stage `TRIAGE.*` lifecycle events).
- **Preserve the mediation boundary (already satisfied).** Pivot rule 1 — a step must never call an external provider directly; LLM goes via `call-LLM`/`llm-call`. The workflow already complies and must keep doing so (`do not re-point the dispatch at a provider`).
- **Be tenant/persona-correct by relying on `llm-call`.** No `tenantId`/persona/provider is passed from the workflow; resolution happens inside `llm-call`/tamma-api (`ITenantContext`). Correct layering — keep it.

Domain best-practice for a multi-reviewer panel: explicit per-reviewer status, a quorum/usability rule (don't pass a panel to the decider if too few roles produced usable output), deterministic aggregation independent of LLM prose, and a full audit record of who reviewed and what they found.

---

## Missing Capabilities (gap to "complete")

| # | Capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Fail-closed per role — no `{}`-as-participant.** A role whose `llm-call` yields no/garbage `llmResponse` is recorded as a normal review with `assessment="{}"`; `reviewCount` is always 4. A panel where 1–4 roles failed is indistinguishable from a full panel. Track per-role success; emit failure. | **P0** | none (32-5 only sharpens it via typed error) |
| 2 | **Panel usability / quorum gate.** No check that "enough roles produced a usable assessment". A panel that wholly failed still emits a `panelResultJson` of four `{}` reviews and the cycle proceeds to PO + applies labels. Must surface `TRIAGE.PANEL.FAILED` and let the cycle route to a non-applying terminal. | **P0** | `TriageItemCycleWorkflow` failure edge (cross-workflow) |
| 3 | **Cycle/panel-scoped DCB events.** No `TRIAGE.PANEL.STARTED/COMPLETED/PARTIAL/FAILED`; only nested `llm-call` events exist. Story 26-1 AC9 + `AGGREGATE.ACTION.STATUS` convention. | **P1** | none |
| 4 | **Per-role finding persistence.** No `StoreRoleFindingActivity` (or equivalent) per role; a later-role failure loses earlier roles' work, and findings aren't retrievable for re-triage/learning. The sibling `plan-review` persists every role. | **P1** | none (activity already exists) |
| 5 | **Structured aggregation (verdict/severity parsing).** `assessment` is stored as an opaque JSON/raw string; the PO gets prose, not parsed `verdict`/`severity`/`suggestedLabels`. No `ReviewAggregationHelper`-style typed parse. | **P1** | none |
| 6 | **`succeededCount` / `failedRoles` in `panelResultJson`.** The output contract gives the PO no signal of panel health, so the PO can't down-weight a degraded panel. | **P1** | none |
| 7 | **No tests.** Zero tests for dispatch wiring, JSON-slice extraction, the failure→`{}` path, or aggregation. | **P1** | none |
| 8 | **DRY: role list duplicated.** `ReviewRoles[]` and the 4 explicit dispatch calls encode the panel roster twice; drift risk if a role is added/removed. | **P2** | none |
| 9 | **Context-empty awareness.** If `contextJson` arrived empty (degraded context-gathering), the panel runs blind with no marker; no `panelInputDegraded` flag passed through. | **P2** | none |

> P0 #2 is technically discharged in `TriageItemCycleWorkflow` (the parent owns the failure edge), but the panel must *expose* the failure signal (#1, #6) for that gate to exist. Listed here because the panel is currently incapable of signaling it.

---

## Ordered Build-out Spec (to reach complete + robust)

Safety/contract first, then audit/persistence, then scope/polish. Mediation boundary is preserved throughout — every LLM call stays on `llm-call`; no provider key enters the engine.

1. **P0 — Track per-role success; stop recording failures as participants.** In `ExtractTriageReview`, when `llmResult` is null / lacks `llmResponse` / yields no parseable JSON, set the role's review to a sentinel that is distinguishable from a real assessment (e.g. write the raw text as today **and** set a parallel per-role boolean `*ReviewOk` variable to `false`; on the empty/no-response path set `false`). Do **not** emit the literal `"{}"` as if the role participated. Add four `*ReviewOk` variables (security/developer/devops/tester), defaulting `false`.

2. **P0 — Compute panel health in `Aggregate` and put it in the contract.** Extend the `Aggregate` output to `{ reviews:[{role, status, assessment}], reviewCount, succeededCount, failedRoles:[...] }` where `status ∈ {"ok","failed"}` is driven by the `*ReviewOk` flags. Add a `FlowDecision("Panel Usable?")` after `Aggregate` (e.g. `succeededCount >= quorum`, quorum default 1 or 2) → on **False** route to a new `Set Outputs(panelStatus="failed")` + `EmitTriageEventActivity(TRIAGE.PANEL.FAILED)` terminal; on **True** continue to the normal output. Surface `panelStatus` (`ok`/`partial`/`failed`) as a second workflow output so `TriageItemCycleWorkflow` can branch on it (this is the lever for the parent's fail-closed edge — cluster P0).

3. **P1 — Emit panel-lifecycle DCB events.** Add a small `EmitTriageEventActivity` (or reuse the cluster's event-emitting base activity) emitting `TRIAGE.PANEL.STARTED` right after `Init` (tags `{repository, itemId}`), and `TRIAGE.PANEL.COMPLETED` (status=`ok`) / `TRIAGE.PANEL.PARTIAL` (some `failedRoles`) / `TRIAGE.PANEL.FAILED` (below quorum) at the matching exits, carrying `{ roleCount, succeededCount, failedRoles }`. Satisfies Story 26-1 AC9 intent + `AGGREGATE.ACTION.STATUS`.

4. **P1 — Persist each role's finding immediately.** After each `Extract*Review`, insert a `StoreRoleFindingActivity` (already in the codebase, `Tamma.Activities/Context`) with `Role="triage-{role}"`, `FindingsJson=<that role's review var>`, keyed by repository + item/issue number — mirroring `PlanReviewWorkflow.StoreReviewRole`. This keeps partial panel results when a later role fails and makes findings retrievable for re-triage/learning. Thread an `issueNumber`/`itemId` input (parse from `itemJson`) so the store key is meaningful.

5. **P1 — Structured aggregation.** Parse each role's review into a typed shape before aggregating (verdict/severity/suggestedLabels/notes), the way `ReviewAggregationHelper.ParseRoleVerdict` is used in `plan-review`; extend or add a `TriageAggregationHelper` so the PO receives parsed fields rather than opaque blobs. Keep raw `assessment` alongside for audit. This makes the PO decision deterministic over structured input and lets the panel pre-suggest labels for label-vocabulary validation downstream.

6. **P1 — DRY the role roster + tests.** Derive the four dispatch calls from a single `(role)` source (loop or a typed array of `AgentRole`) so `ReviewRoles[]` and the dispatch list can't drift. Add C# (xUnit) tests: dispatch input shape (`role`/`action` via `GetTriageActionForRole`, `enableTools=true`); `ExtractTriageReview` JSON-slice happy path + raw-text wrap + **no-response → failed (not `{}`)**; `Aggregate` producing correct `succeededCount`/`failedRoles`; `Panel Usable?` routing on a fully-failed panel.

7. **P2 — Propagate context-degradation marker.** Accept a `contextStatus` input (paired with the `TRIAGE.CONTEXT.EMPTY` signal recommended for `triage-context-gathering`); if context was empty, include `panelInputDegraded=true` in `panelResultJson` and a low-severity `TRIAGE.PANEL.DEGRADED_INPUT` event, so the PO/audit know the panel reviewed with thin context.

---

## Verdict

**thin.** The workflow is more than a placeholder — it has a real 4-role panel with correct role/action taxonomy and correct LLM mediation (no rule-1 violation, no engine-held keys). But it is the happy-path skeleton the user flagged: it has **no failure signal** (a failed role is aggregated as a `{}` participant, `reviewCount` always 4 — a P0 soft-fail-masking / false-success defect), **no panel-lifecycle DCB event**, **no per-role persistence**, **no structured aggregation**, and **no usability gate**, all of which its sibling `PlanReviewWorkflow` already demonstrates as the intended bar. Reaching "complete" is **M** — bounded to this workflow plus one new emit activity, reuse of the existing `StoreRoleFindingActivity`, a small aggregation helper, and a panel-status output the parent cycle can branch on (the parent's fail-closed edge is separate cluster work tracked under `IssueTriage`/`TriageItemCycle`).
