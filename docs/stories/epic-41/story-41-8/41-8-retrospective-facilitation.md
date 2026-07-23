# Story 41-8: Retrospective Facilitation Workflow

Status: drafted

## User Story

As a **scrum master** (or eligible role-holder), I want a workflow that assembles a retrospective from a
sprint's DCB history — what went well, what didn't, action items — as a `Findings` document (with prose
narrative) on the lifecycle, so that retros produce durable, tracked action items instead of evaporating
after the meeting.

## Priority

P3 / Wave 3 — per-sprint cadence; consumes 41-6/41-7 outputs.

## Scope

Triggered at sprint close (or scheduled) → thin binding over `document-lifecycle`. `consumes: [SprintPlan
(41-6), standup digests (41-7), DCB events for the sprint, blocker/escalation events]` / `produces:
Findings` (retro items with evidence) plus a prose narrative summary. Produce cell
`(scrum_master, facilitate-retro)` (41-1).

## Produced document

`Findings`: each retro item cites sprint evidence; action items ranked and role-owned. Accompanying prose
narrative is audience-tagged (team). `tenantId`/sprint lineage.

## Events

`RETRO.STARTED` → `.SYNTHESIZED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accepted action items route to owning roles' Task Views (and can seed 41-3/41-11 backlog candidates). The
retro is deliberately an **artifact around** the human conversation, not a replacement for it (README
out-of-scope note).

## Autonomy behavior

- **70–84:** agent drafts the retro; scrum master reviews/accepts before broadcast.
- **85–100:** agent synthesizes and self-accepts; action items auto-assigned within the eligible set.

## Acceptance Criteria

1. Thin lifecycle binding; `Findings` items cite concrete sprint evidence.
2. Action items produce role-scoped Task View entries via 39-20.
3. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** 41-1 (`scrum_master` role), Epic 39 (`Findings`, lifecycle, store, routing, 4-7 query API).
- **Related:** 41-6, 41-7.

## Estimated Effort

3–4 days
