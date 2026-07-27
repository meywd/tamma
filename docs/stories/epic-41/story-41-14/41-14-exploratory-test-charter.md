# Story 41-14: Exploratory Test Charter Workflow

Status: drafted

## User Story

As a **tester** (or eligible role-holder), I want a workflow that produces an exploratory-testing charter
and captures the session's observations as a typed `Findings` document on the lifecycle, so that
unscripted testing yields tracked, evidence-cited findings instead of ephemeral notes.

## Priority

P3 / Wave 3 — complements scripted testing; consumes 41-13.

## Scope

Thin binding over `document-lifecycle`. `consumes: [TestPlan (41-13)?, feature under test, AcceptanceCriteria?]`
/ `produces: Findings` (ONE document: the charter mission is the `topic`/`summary`, the session
observations are its `findings[]` — a charter-then-session two-document split is not expressible in one
binding and re-entry would short-circuit the second run). Produce cell `(tester, exploratory-test)`.

The cell exists; what IS in scope is a **template rewrite**: the shipped
`Prompts/tester/exploratory-test.md` instructs the model to write an exploratory test FILE (code), which
`FindingsDocumentType.Validate` would reject as `MALFORMED_PAYLOAD` on every produce. It is rewritten to
the `Findings` contract.

## Produced document

`Findings`: each observation cites what was exercised as evidence; severity + reproduction where a defect
is found; ranked. `issueId` lineage.

## Events

`EXPLORATORY.CHARTER.STARTED` → `.FINDINGS` / `.FAILED` alongside `DOCUMENT.*`. (No `.SESSION` member —
nothing in the binding observes "a session happened" separately from the produce step; and every landed
family carries a failure member for the `rejected`/`escalated` exits.)

## Orchestrator / user interaction

Accept gate routes per autonomy; defect findings can seed `triage-defect`/41-17; the charter is
human-or-agent (a human tester runs the session at low autonomy, an agent explores at high autonomy).

## Autonomy behavior

- **70–84:** the produce step is assigned to a human tester, who fills the one `Findings` document
  (charter mission as `topic`/`summary`, session observations as `findings[]`) in the course of running
  the session.
- **85–100:** agent charters, explores (tool-enabled), and self-accepts; confirmed defects always route to
  triage.

> **Epic 42 caveat — "tool-enabled" means the six coding tools, nothing more.** Exploration today
> degrades to `FileRead`/`SearchCode`/`ShellExecute`/`RunTests`
> (`Tamma.Api/Program.cs:753-764`); there is no governed exploration tooling. The charter half is
> agent-reachable now; genuinely tool-enabled exploration waits on **Epic 42**.

## Acceptance Criteria

1. Thin lifecycle binding; `Findings` cite concrete evidence. A session that found nothing emits **one
   finding** whose `title` is the charter mission and whose `summary` records "no anomalies observed",
   citing what was exercised — evidence still required. (An empty `findings[]` is deliberately invalid:
   `FindingsDocumentType` fires `EMPTY_FINDINGS`, so "valid empty findings" is not a thing the type
   permits.)
2. Defect findings are **readable** by triage/PR-triage: the accepted `Findings` is retrievable for the
   issue through the same `FetchLatestAcceptedDocumentActivity` seam `triage-po-decision` uses, and the
   seed row declares the produced edge. Actually routing them into triage is a `TriageItemCycleWorkflow` /
   41-17 edit, filed forward — not claimed here.
3. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Findings`, lifecycle, store, routing).
- **Related:** consumes 41-13; feeds 41-17/triage.

## Estimated Effort

3 days
