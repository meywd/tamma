# Story 41-9: ADR Authoring Workflow

Status: drafted

## User Story

As an **architect** (or eligible role-holder), I want a workflow that captures a significant technical
decision as an **Architecture Decision Record** — a prose document with an audience tag — on the standard
lifecycle, so that decisions are drafted, reviewed, accepted, and stored with issue lineage instead of
living only in chat or a reviewer's memory.

## Priority

P1 / Wave 1 — cheap, high-value, and the reference implementation of the **prose-on-lifecycle** path that
the whole tech-writer / devops / PM prose family (41-4, 41-5, 41-22, 41-24, 41-25, 41-26, 41-8) reuses.

## Scope

Thin binding over `document-lifecycle`. `consumes: [issue, Design?, Findings?]` / `produces: prose (ADR,
audience=engineering)`. Produce cell `(architect, write-adr)`. *Prose stays prose* (Epic 39): markdown +
audience tag, no forced schema; the review stage is a `Review` over the prose.

## Produced document

Prose ADR (context / decision / consequences / alternatives-considered, but structure is convention, not
validated schema), audience-tagged, `issueId`-lineaged.

## Events

`ADR.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; a decision affecting a public contract can be a configured always-escalate
class. Accepted ADRs are queryable per issue and per repo.

## Autonomy behavior

- **70–84:** agent drafts, architect accepts.
- **85–100:** agent drafts and self-accepts unless the decision touches an always-escalate class.

## Acceptance Criteria

1. Thin lifecycle binding; prose rides the lifecycle with an audience tag; review stage produces a `Review`
   over the ADR text.
2. No bespoke parse/terminal; non-success exits are typed escalations with lineage.
3. Accepted ADR persisted with lineage and retrievable via the 39-11 store.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1c** (the `prose` type + `Audience` field — 41-9 is the designated *reference
  implementation* of the prose path, so it cannot precede the story that builds it; *corrected: was
  "Epic 39 (prose-document handling)", which 39-1:58 records as out of Epic 39's scope*), Epic 39
  (lifecycle, review, store).
- **Related:** consumes 41-10 System Design output when present.

## Estimated Effort

2–3 days
