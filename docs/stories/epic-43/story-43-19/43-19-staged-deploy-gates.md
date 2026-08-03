# Story 43-19: Live QA/UAT Deploy Gates (the stage-scoped wait)

Status: drafted

## User Story

As a platform operator, I want the `deploy.qa` and `deploy.uat` catalog
actions to be LIVE workflow gates — not just catalogued levels — so an
autonomous deploy to QA or UAT pauses for approval at the dial the operator
set, the same way `deploy.prod` already does via Seam E.

## Why this exists (2026-08-03)

Story 43-12 minted `deploy.{dev,qa,uat,staging,prod}` at 70/75/80/85/90 and
retired the coarse `deploy.promote-prod`. But 43-12 deliberately deferred the
second half of its AC4: **`deploy.qa` and `deploy.uat` are catalogued at
their levels and NOT yet live workflow gates.** Only `deploy.prod` is a live
Seam-E gate today (`DeploymentPipelineWorkflow` prod gate, `CheckActionGateActivity`).

The deferral was correct — the plumbing crosses this lane's fence — but it
leaves a gap: an operator who sets the dial to 78 expects a QA deploy (75) to
auto-run and a UAT deploy (80) to wait; today neither QA nor UAT consults the
gate at all, so the level is inert for those two stages.

## Scope

1. A stage-entry gate before the QA stage and before the UAT stage in
   `DeploymentPipelineWorkflow`, each calling `CheckActionGateActivity` on
   `deploy.qa` / `deploy.uat` respectively (mirroring the prod gate).
2. `WaitForDeploymentApprovalActivity` gains a **Stage** input so its bookmark
   is stage-scoped — reusing it unmodified across three stages collides
   (43-12's plan flagged this as the risk-carrying change). The bookmark key
   must include the stage segment.
3. The resume path (`AdlEndpoints` / `IElsaWorkflowService` /
   `ElsaWorkflowService`, `DeployApprovalDecisionRequest`) carries the stage so
   a QA approval resumes the QA wait, not UAT's.
4. `deploy.dev` and `deploy.staging` stay RESERVED (the pipeline ships
   QA->UAT->Prod only; no dev/staging stage exists to gate).

## Out of scope

The prod gate (live), the catalog keys (43-12 shipped them), the dial UI.

## Acceptance criteria

1. A QA-stage deploy resolves `deploy.qa` at the current dial; below 75 it
   suspends on a QA-scoped bookmark, above it proceeds. Same for UAT / 80.
2. A QA approval and a UAT approval in the same run resume their OWN waits —
   proved by a two-stage suspend/resume test where approving QA does not
   release UAT.
3. `deploy.dev` / `deploy.staging` remain reserved (no performer); a test
   asserts no pipeline stage references them.
4. The prod gate is unchanged (regression pin).
5. Structural: every deploy stage that performs an effect has a gate before
   it; a stage with no gate fails the test.

## Dependencies

43-12 (the keys — landed). Sequence after Wave C; it is the natural completion
of 43-12's AC4.

## Effort

2-3 days — the stage-scoped bookmark and its resume path are the whole risk;
the two gate nodes mirror the prod one.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-08-03 | 1.0.0   | Initial story creation — completes 43-12 AC4 | Claude |
