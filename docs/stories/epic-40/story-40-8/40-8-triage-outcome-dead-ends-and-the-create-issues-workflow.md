# Story 40-8: Triage Outcome Dead Ends — Build `create-issues` (or Route the Branches Somewhere Real)

Status: drafted

Fixes: `.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md`. Cross-references: Story 43-11's Missing-actions hunt (which re-surfaced the dangling dispatch), Story 39-24 (`:494-500`, the audit that found it), Story 31-13 (the `git.issue.create` catalog key).

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **reviewer whose plan review concludes "defer these items" or "split this issue"**,
I want those outcomes to actually create the deferred/sub-issues and finish the cycle,
So that defer and split are usable decisions instead of a permanent silent hang.

## Priority

P1 — two of the five plan-review outcomes (`Defer`, `Split` — `SingleIssueCycleWorkflow.cs:261-274`) dead-end today. This epic owns `SingleIssueCycleWorkflow` (40-2/40-4/40-5/40-7 all work in it), so the fix lands here.

## Architectural Context (READ FIRST)

- **The defect** (full record in the bug entry): `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs:283` and `:300` dispatch `WorkflowDefinitionId = new("create-issues")` with `WaitForCompletion = true`; no workflow declares that id (full-tree grep, re-verified 2026-08-02); the branches suspend forever.
- **The decision: BUILD the workflow.** Rerouting to an existing path was considered and rejected: the defer/split payloads (`subResult["deferred"]` / `subResult["split"]`, JSON arrays of issue drafts) have no existing consumer — no current workflow creates platform issues from a list — so any reroute either drops the items (silently losing the reviewer's decision) or lands on the `NeedsHuman` terminal (turning two automated outcomes into manual work forever). Building it is small because the seam already exists: `POST /api/engine/create-issue` (`EngineEndpoints.CreateIssue` → the platform driver) is live today — it is the very route 43-11's hunt flagged as "live and ungoverned" and Story 31-13 catalogs as `effect:git.issue.create` (`docs/stories/epic-31/31-13-full-pr-operations.md`, Scope 4). If the product owner overrules and prefers rerouting, the choice and reason are recorded here and the ACs collapse to AC4–AC6.
- **The shape**: a `CreateIssuesWorkflow` (`DefinitionId = "create-issues"` — matching the existing dispatch sites, so `SingleIssueCycleWorkflow` needs no edit for the happy path), inputs `repository` + `issuesJson` (the shape both call sites already send, `:284-288`, `:301-305`), iterating the array through the mediated issue-create call via `TammaApiClient`, tolerant of an empty/malformed array (completes with a count of 0 rather than faulting — the call sites default to `"[]"`).
- **Resumable by design** (this epic's standard): the workflow declares its `[ResumeBehavior]` per `resumable-workflow-standard.md`; partial completion (3 of 5 issues created, then crash) must not double-create on resume — idempotency via the created-issue record in the workflow state or a dedupe key per item.
- **Governance**: each create rides `effect:git.issue.create` once 31-13 lands its key; until then the route's existing (ungoverned) state is unchanged by this story — this story does not mint catalog keys (31-13 owns that; the drift sweep there proves coverage).
- **The class of defect gets a guard.** The Epic 41 README (`:592-597`) names the contributing cause: ~105 magic-string dispatch sites, no definition-id constants, and a second live instance of the same bug (`MentorshipController.cs:79`, `"tamma-autonomous-mentorship"` vs `mentorship`). A structural test closes the class for `Tamma.ElsaServer`: enumerate every `WorkflowDefinitionId` literal dispatched in `Workflows/` and assert each matches a declared `DefinitionId`.

## Acceptance Criteria

1. **`create-issues` exists and completes**: a workflow with `DefinitionId = "create-issues"` accepts `repository` + `issuesJson`, creates one platform issue per array item through the mediated engine route, and completes with a result carrying the created issue numbers. Empty and malformed `issuesJson` complete with zero creations and a recorded warning — never a fault, never a hang.
2. **Defer and split finish end to end**: a `SingleIssueCycleWorkflow` run driven to the `Defer` outcome creates the deferred issues and reaches the cycle's terminal; same for `Split` with sub-issues. Both pinned as workflow tests (the bug's reproduction, inverted).
3. **Resume does not double-create**: kill/resume between items; the created set on the platform is exactly the input list once. `[ResumeBehavior]` declared per the epic standard.
4. **The dangling-dispatch class is pinned**: a structural test asserts every `WorkflowDefinitionId` dispatched from `Tamma.ElsaServer/Workflows/` matches a declared workflow `DefinitionId`. It fails today for `create-issues` (until AC1) and would have caught this bug at introduction. The `MentorshipController.cs:79` mismatch is fixed in passing or explicitly allowlisted with the reason (it is out-of-directory; do not silently widen scope).
5. **Audit trail**: issue creation emits the existing engine-side events for the create route, tenant-tagged, one per item — no new event vocabulary.
6. **`dotnet test` green.**

## Dependencies

- **Story 31-13** — catalogs `effect:git.issue.create` for the route this workflow calls, and 43-9-style enforcement on it. Not blocking (the route is live today); land in either order, but the 31-13 drift sweep must see this workflow's route usage. Cross-linked both ways.
- **Story 40-5** — the `[ResumeBehavior]` standard and allowlist this workflow declares under. Landed/drafted in this epic.
- **Bug record**: `.dev/bugs/2026-08-02-single-issue-cycle-dead-create-issues-dispatch.md`.
- **Verified in tree**: `SingleIssueCycleWorkflow.cs:261-274` (outcome switch), `:279-291` (`CreateDeferredIssues`), `:296-308` (`CreateSplitIssues`); `EngineEndpoints.CreateIssue`; `docs/stories/epic-41/README.md:592-597`; `docs/stories/epic-39/story-39-24/39-24-acceptance-step-coverage.md:494-500`.

## Out of Scope

- Minting catalog keys (31-13) or gating the route (43-9/31-13 AC2).
- A global definition-id constants file for all ~105 sites — AC4 guards the `Workflows/` directory; the full sweep is the 41-30/41-32 allowlist argument, owned there.
- Changing the plan-review outcome vocabulary or the review workflow itself.
- Sub-issue linking/hierarchy on the platform (parent-child relations) — created flat; hierarchy is a 44-x native-tracking concern.

## Estimated Effort

2 days — 1 for the workflow + idempotent resume, 0.5 for the end-to-end defer/split pins, 0.5 for the structural definition-id test and the mentorship fix/allowlist.

## Change Log

| Date       | Version | Changes                                                                  | Author |
| ---------- | ------- | ------------------------------------------------------------------------ | ------ |
| 2026-08-02 | 1.0.0   | Initial story — build create-issues, fix defer/split dead ends, pin the dangling-dispatch class | Claude |
