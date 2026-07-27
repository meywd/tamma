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

The cell exists in the taxonomy today but **nothing dispatches it** — this is a greenfield binding, not a
migration, and there is no 41-1a work in it. What IS in scope is a **template rewrite**: the shipped
`Prompts/product_owner/define-acceptance-criteria.md` instructs a task breakdown (the `Plan` wire, with
criteria smuggled into each task's `testing` string), not acceptance criteria. Bound unchanged to the
`AcceptanceCriteria` validator it would fail every produce, so the body is rewritten to the
`AcceptanceCriteria` contract (39-15 D7 precedent; front matter unchanged).

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

1. Built as a greenfield thin lifecycle binding (nothing dispatches the cell today); no bespoke
   parse/terminal. Includes the `define-acceptance-criteria` template rewrite to the `AcceptanceCriteria`
   contract (see Scope).
2. Output validated by the `AcceptanceCriteria` type; validation failure flows the repair/review/escalation
   rings, never a dead end.
3. Accepted document persisted with lineage: Issue → Clarification? → AcceptanceCriteria → Reviews.
   `DocumentInstance` carries a single `ParentDocumentId`, so the parent is the accepted Clarification when
   one exists (else the Findings, else null); the other consumed document ids ride the
   `ACCEPTANCE_CRITERIA.DRAFTED` event payload.
4. 41-15 can read the latest accepted `AcceptanceCriteria` for an issue via the 39-11 store.
5. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched `document-lifecycle` child); passes the 39-10 structural test with no allowlist
   entry.

## Dependencies

- **Blocking:** **41-1b** (`AcceptanceCriteria` type — an unregistered type is unpersistable on the
  human path too), Epic 39 lifecycle/store/accept.
- **Unblocks:** 41-15, merge-gate consumption.

## Estimated Effort

3–4 days
