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

**Two phases — one document per binding, because one lifecycle dispatch produces exactly one document and
one cell maps to exactly one contract:**

- **Phase A (`Findings`):** triggered at sprint close → thin binding over `document-lifecycle`.
  `consumes: [standup digests (41-7 `Findings`), DCB events for the sprint, blocker/escalation events]` /
  `produces: Findings` (retro items with evidence). Produce cell `(scrum_master, facilitate-retro)`
  (41-1a). The `SprintPlan (41-6)` consumed edge is deliberately deferred (it would put Phase A on
  41-1b's critical path for no benefit); it is an additive follow-up. An empty sprint short-circuits to
  `RETRO.SKIPPED` before dispatch (the 41-7 pattern — `EMPTY_FINDINGS` makes a "nothing happened" retro
  invalid).
- **Phase B (prose narrative):** a **second** thin binding on a **second** cell,
  `(scrum_master, write-retro-narrative)` — a cell 41-1a does not currently mint; its addition is a
  recorded lockstep amendment against 41-1a. Produces `prose` (audience=team) and needs 41-1c.

## Produced document

Phase A — `Findings`: each retro item cites sprint evidence; action items ranked and role-owned.
Phase B — a separate prose narrative document, audience-tagged (team). `tenantId`/sprint lineage.

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

1. Thin lifecycle binding; `Findings` items cite concrete sprint evidence (enforced by the shipped
   `FindingsDocumentType`; the new work is making citations resolvable against the sprint's actual events,
   via 41-7's validation-context ring). An empty sprint short-circuits to `RETRO.SKIPPED` with no document.
2. Each accepted action item is emitted as a `RETRO.ACTION_ITEM` row carrying its owning role and
   evidence, and the accept gate publishes an `AcceptanceRequest`; **role-scoped Task View delivery is
   unreachable until 39-19/39-20 land** (the audience resolver is the fail-closed
   `InitiatorOnlyTaskAudienceResolver` stub).
3. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate suspends
   inside the dispatched child); 39-10 structural test green without allowlist.

## Dependencies

- **Blocking (Phase A):** **41-1a** (`scrum_master` role + `facilitate-retro` cell) only, plus Epic 39
  (`Findings`, lifecycle, store, routing, 4-7 query API). The `SprintPlan` edge is deferred, so Phase A is
  NOT on 41-1b's critical path.
- **Blocking (Phase B):** **41-1c** (the `prose` type + `Audience` tag) AND the 41-1a amendment minting
  `(scrum_master, write-retro-narrative)` — a cell 41-1a's current list does not carry (*added: 41-8 was
  absent from the epic's prose-blocked list*).
- **Related:** 41-6, 41-7.

## Estimated Effort

3–4 days
