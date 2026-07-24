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
/ `produces: Findings` (charter mission + session observations). Produce cell `(tester, exploratory-test)`.

## Produced document

`Findings`: each observation cites what was exercised as evidence; severity + reproduction where a defect
is found; ranked. `issueId` lineage.

## Events

`EXPLORATORY.CHARTER.STARTED` → `.SESSION` → `.FINDINGS` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; defect findings can seed `triage-defect`/41-17; the charter is
human-or-agent (a human tester runs the session at low autonomy, an agent explores at high autonomy).

## Autonomy behavior

- **70–84:** agent drafts the charter; a human runs the session and records findings.
- **85–100:** agent charters, explores (tool-enabled), and self-accepts; confirmed defects always route to
  triage.

> **Epic 42 caveat — "tool-enabled" means the six coding tools, nothing more.** Exploration today
> degrades to `FileRead`/`SearchCode`/`ShellExecute`/`RunTests`
> (`Tamma.Api/Program.cs:753-764`); there is no governed exploration tooling. The charter half is
> agent-reachable now; genuinely tool-enabled exploration waits on **Epic 42**.

## Acceptance Criteria

1. Thin lifecycle binding; `Findings` cite concrete evidence; empty session ⇒ valid empty findings.
2. Defect findings integrate with triage/PR-triage.
3. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Findings`, lifecycle, store, routing).
- **Related:** consumes 41-13; feeds 41-17/triage.

## Estimated Effort

3 days
