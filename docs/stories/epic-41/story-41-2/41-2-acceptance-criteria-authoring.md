# Story 41-2: Acceptance-Criteria Authoring Workflow

Status: drafted

## User Story

As a **product owner** (or an eligible role-holder at lower autonomy), I want a workflow that turns an
issue + its clarified requirements into a typed, testable **AcceptanceCriteria** document on the standard
lifecycle, so that "done" is defined once, reviewed, accepted, and then consumed by acceptance
verification (41-15) and the merge gate — instead of being implicit in a plan or a reviewer's head.

## Priority

P0 / Wave 1 — highest single-story leverage. It is the upstream anchor for 41-15 and gives the merge/accept
gates an explicit definition-of-done to check against.

## Scope

Thin binding over `document-lifecycle`. `consumes: [issue, Clarification?, Findings?]` /
`produces: AcceptanceCriteria`. Produce cell `(product_owner, define-acceptance-criteria)`.

## Produced document

`AcceptanceCriteria` (41-1): independently verifiable criteria in Given/When/Then or checklist form,
bound to `issueId`, no criterion referencing out-of-scope work. Reviewed via the unified `Review`
(single reviewer default: a second PO or tester lens; panel by policy).

## Events

`ACCEPTANCE_CRITERIA.STARTED` → `.DRAFTED` → `.ACCEPTED` / typed-escalation, alongside generic
`DOCUMENT.*`. All tagged `issueId`/`repository`/`tenantId`.

## Orchestrator / user interaction

Accept gate publishes `AcceptanceRequest`; orchestrator routes per rules + autonomy. A holder of the PO
role (or the initiator) can accept in the Task View or by asking the orchestrator in chat.

## Autonomy behavior

- **70–84:** produce is assigned to a human PO; accept is a human decision.
- **85–94:** agent drafts, human accepts.
- **95–100:** agent drafts and self-accepts unless an always-escalate class (e.g. contract-affecting
  criteria) is configured.

## Acceptance Criteria

1. Rebuilt as a thin lifecycle binding; no bespoke parse/terminal.
2. Output validated by the `AcceptanceCriteria` type; validation failure flows the repair/review/escalation
   rings, never a dead end.
3. Accepted document persisted with lineage: Issue → Clarification? → AcceptanceCriteria → Reviews.
4. 41-15 can read the latest accepted `AcceptanceCriteria` for an issue via the 39-11 store.
5. `[ResumeBehavior(Both)]`; passes the 39-10 structural test with no allowlist entry.

## Dependencies

- **Blocking:** **41-1b** (`AcceptanceCriteria` type — an unregistered type is unpersistable on the
  human path too), Epic 39 lifecycle/store/accept.
- **Unblocks:** 41-15, merge-gate consumption.

## Estimated Effort

3–4 days
