# Story 27-19: Workflow Dispatch-Site Migration

## Story

As the Tamma workflows, I need the ~21 `llm-call` dispatch sites to emit
`AgentRole.X.ToWire()` / `AgentAction.X.ToWire()` instead of raw string
literals, so dispatched `(role, action)` pairs are compile-time safe and
guaranteed to be in the taxonomy.

Canonical design: SPEC §3.1, §5; verified audit set = 21 sites across 14 files.

## Priority

P0 (Critical).

## Dependencies

Story 27-15 (enums). Enables Story 27-17 (drift test).

## Acceptance Criteria

1. Every `["action"] = "<literal>"` and `["role"]/["agentRole"] = "<literal>"`
   at the 21 `llm-call` dispatch sites is replaced with
   `AgentAction.X.ToWire()` / `AgentRole.X.ToWire()`.
2. Legacy aliases at dispatch (`"implementer"`, `"analyst"`) are replaced with
   the canonical enum (`AgentRole.Developer`, `AgentRole.ProductOwner`); wire
   output is canonical (`"developer"`, `"product_owner"`).
3. Dynamic role-loop dispatch (`["role"] = role` in `ReviewRoles` arrays in
   `PlanReviewWorkflow`, `TaskReviewWorkflow`, `TriagePanelReviewWorkflow`,
   and the `ContextGatheringWorkflow` RoleScan param) iterates `AgentRole`
   values and emits `.ToWire()`.
4. Sites that currently emit a *specific* action keep emitting it; sites that
   only know the generic action emit the generic enum value (transitional,
   SPEC §3.5) — no behaviour change, only type-safety.
5. The constructed dynamic role `"po-decision-round-{n}"` in
   `PlanReviewWorkflow` is NOT a taxonomy role; document it as a session
   identifier passed via a different input, not the `(role, action)` key
   (no change required, just annotate to prevent false drift-test failures).
6. Wire output is byte-identical to today for every site (regression: existing
   suspended workflow instances unaffected; SPEC §7).

## Technical Context

- 14 files under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`:
  ContextGathering, TriageContextGathering, PlanGeneration, PlanReview,
  TaskCreation, TaskReview, TestCaseCreation, DeploymentPipeline,
  TriagePanelReview, TriagePODecision, ReviewFix, Debugging, plus the
  `ReviewRoles` arrays and RoleScan helper.
- `ReviewFix`/`Debugging` currently dispatch `agentRole="implementer"` with NO
  `["action"]` key — add the correct `AgentAction` per SPEC §4 mapping table
  (developer/`address-review-comments`, developer/`debug`).

## Estimate

10 hours.
