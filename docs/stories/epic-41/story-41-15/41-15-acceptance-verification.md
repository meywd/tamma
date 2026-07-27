# Story 41-15: Acceptance Verification Workflow

Status: drafted

## User Story

As a **tester** (or eligible role-holder), I want a workflow that verifies an implemented change against
its accepted `AcceptanceCriteria` and emits a typed `Review` verdict on the lifecycle, so that the cycle
answers *"does this meet the requirement?"* — not just *"do the tests pass?"* — before merge/close.

## Priority

P0 / Wave 1 — closes the loop 41-2 opens. Without it, acceptance criteria are authored but never checked.
Cannot start before 41-2 (and therefore 41-1b, which registers the `AcceptanceCriteria` type).

## Scope

Thin binding over `document-lifecycle`. `consumes: [AcceptanceCriteria, diff/PR, TestSpec?, CI results]`
/ `produces: Review` (subject = the change; each criterion mapped pass/fail with evidence). Produce cell
`(tester, verify-acceptance)` — an existing, unbound cell (`AgentAction.cs`, shipped template).

The binding verifies **any** diff, human- or agent-authored; it does not require the autonomous coding
step. Wiring the verdict into `single-issue-cycle`/`merge-approval` (AC3) is the part that does.

## Produced document

Unified `Review` whose issues carry the failing criterion id + evidence; a blocking failure ⇒ not
approvable (39-4 invariant). Decision enum drives merge routing.

## Events

`ACCEPTANCE.VERIFY.STARTED` → `.VERDICT` (approved/changes/undecidable) alongside `DOCUMENT.*`, tagged
`issueId`/`prId`/`repository`.

## Orchestrator / user interaction

Accept gate routes the verdict per autonomy. A "changes-requested" verdict escalates with lineage
(criteria + failing evidence) so the orchestrator can loop back to `review-fix`/coding or assign a human.

## Autonomy behavior

- **70–84:** a human tester verifies; verdict acceptance is human.
- **85–94:** agent verifies; a human confirms an "approved" verdict on the merge path.
- **95–100:** agent verifies and self-accepts an unambiguous pass; any failing criterion always escalates.

## Acceptance Criteria

1. Reads the latest accepted `AcceptanceCriteria` (41-2) for the `issueId` via 39-11; when none exists the
   run fails loud with a distinct error code and emits no `Review` — an integration test asserts an issue
   with no criteria never yields an `approve` verdict.
2. Every criterion in the consumed document maps to exactly one pass/fail entry in the `Review`; a body
   that omits a criterion id, or cites one not in the source document, is rejected by a story-local rule
   (`CRITERION_UNMAPPED` / `CRITERION_UNKNOWN`). *Corrected: "each criterion mapped pass/fail with
   evidence" was Scope prose with no check behind it.*
3. `decision = approve` carrying any critical-severity issue is rejected with
   `APPROVE_WITH_BLOCKING_ISSUES` (`ReviewDocumentType.cs:35`, `:88-97`); an issue with no suggested fix ⇒
   `ISSUE_MISSING_FIX`; a `subject.kind = "diff"` without `repository` + `prNumber`|`commitSha` ⇒
   `SUBJECT_INCOMPLETE` (`ReviewDocumentType.cs:23-32`).
4. The verdict is a gate input to `single-issue-cycle`/`merge-approval`: an integration test shows a
   `request-changes` verdict blocking the merge-approval path and an `approve` verdict releasing it.
5. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate
   suspends inside the dispatched `document-lifecycle` child); 39-10 structural test green without an
   allowlist entry. A new
   `WorkflowDocumentInterface` row is declared and `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned`
   is bumped in the same change.

> Whether the verifier *judged* a criterion correctly is not an acceptance criterion — no deterministic
> check exists. Completeness of the mapping (AC2) and the approve/blocking invariant (AC3) are.

## Dependencies

- **Blocking:** 41-2 (and transitively 41-1b, which registers the `AcceptanceCriteria` document type —
  unregistered types are unpersistable on the human path too); Epic 39 (`Review`, lifecycle, store).
- **Blocking for AC4 only:** **Epic 40**. *Corrected: this previously read "Epic 40 (change under test)",
  which over-blocked the whole story. The binding verifies any diff — a human-authored PR is a valid
  subject, so AC1–AC3 and AC5 have no Epic 40 dependency. What does depend on Epic 40 is AC4's in-loop
  gating: `.github/workflows/tamma-agent.yml` does not exist in this repo, so the coding step's dispatch
  fails loud with `WorkflowNotFound` (`AgentDispatchMediationService.cs:109`) and there is no
  agent-authored change to gate until Epic 40 lands the runner substrate.*
- **Unblocks:** requirement-complete merge gating.

## Estimated Effort

4–5 days
