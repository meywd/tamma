# Story 41-26: Runbook & Ops-Docs Authoring Workflow

Status: drafted

## User Story

As a **devops** engineer / tech writer (or eligible role-holder), I want a workflow that authors an
operational runbook for a service or a recurring operational task, as audience-tagged prose on the
lifecycle, so that on-call procedures are captured, reviewed, and kept current instead of tribal knowledge.

## Priority

P3 / Wave 3 — proactive ops hygiene; naturally seeded by 41-22 postmortems.

## Scope

Triggered on demand or by a postmortem action item → thin binding over `document-lifecycle`. `consumes:
[service/infra context (context-scan), incident Diagnosis/postmortem?, deployment config]` / `produces:
prose (runbook, audience=ops)`. Produce cell **`(tech_writer, write-runbook)`** — `WriteRunbook` is in
`tech_writer`'s eligible set only, so a `(devops, write-runbook)` cell is not legal (it would fail the
taxonomy build gates and the prompt loader at startup; the only template on disk is
`Prompts/tech_writer/write-runbook.md`). Review via `(devops, review-operability)` — the ops-peer lens,
eligible and selector-reachable today — with `(tech_writer, review-docs)` as the upgrade once 41-24's
rewrite of that cell lands. The produce template is rewritten in scope: the shipped `write-runbook.md` is
a generic Summary/Key-Findings markdown skeleton, not the symptoms → checks → remediation → escalation
runbook shape, and its raw markdown must move inside 41-1c's `{kind, audience, title, body}` envelope.

## Produced document

Audience-tagged prose runbook (symptoms → checks → remediation → escalation), `repository`-lineaged.
Review stage is a `Review` over the text.

## Events

`RUNBOOK.STARTED`/`.DRAFTED`/`.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accepted runbook routes per autonomy and is published to the ops docs surface; a 41-22 postmortem
"add/update runbook" action item can dispatch this workflow directly.

## Autonomy behavior

- **70–84:** agent drafts; devops accepts.
- **85–100:** agent drafts and self-accepts; a runbook covering a regulated/critical path can be
  always-escalate.

> **Epic 42 caveat — "publish" has no tool.** Pushing the runbook to the ops-docs host needs a
> publish capability (**42-9**); none of the six registered `IToolExecutor`s
> (`Tamma.Api/Program.cs:753-764`) provides one. Drafting is agent-reachable; publication is
> **human-assigned** (rule 4) until Epic 42 lands.

## Acceptance Criteria

1. Thin lifecycle binding; prose reviewed by a `Review` (default reviewer `(devops,
   review-operability)`; `(tech_writer, review-docs)` as the upgrade).
2. Can be dispatched as a postmortem follow-up with the incident `Diagnosis` as input — passed as an
   explicit `incidentDiagnosisScope` input using 41-22's producer-scoped id (a bare issue-id read would
   find the wrong `diagnosis` or none).
3. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1c** (the `prose` type + `Audience` field; *corrected: was "Epic 39 (prose
  handling)" — out of Epic 39's scope per 39-1:58*), Epic 39 (lifecycle, review, store),
  `context-gathering`.
- **Related:** dispatched by 41-22. **41-1a** is an *upgrade*, not a gate: the default reviewer is
  `(devops, review-operability)` (eligible and selector-reachable today), so the `(tech_writer,
  review-docs)` review stage — which needs 41-1a's selector arm plus 41-24's cell rewrite — is the
  follow-on, not a blocker.

## Estimated Effort

2–3 days
