# Story 41-25: User & API Documentation Workflow

Status: drafted

## User Story

As a **tech writer** (or eligible role-holder), I want a merge-triggered workflow that drafts or updates
user-facing docs and API reference for a shipped feature, as audience-tagged prose on the lifecycle, so
that documentation tracks the code instead of lagging it.

## Priority

P2 / Wave 2 — merge-triggered, recurring; keeps docs in sync with delivery.

## Scope

Triggered when a feature merges (or on demand) → thin binding over `document-lifecycle`. `consumes:
[merged diff, Plan, AcceptanceCriteria?, existing docs]` / `produces: prose (user-docs, audience=user)`
and/or `prose (api-docs, audience=developer)`. Produce cells `(tech_writer, write-user-docs)` and
`(tech_writer, write-api-docs)`; review via `(tech_writer, review-docs)`.

## Produced documents

Audience-tagged prose docs, `issueId`/`repository`-lineaged. Review stage is a `Review` over the text
(accuracy-against-diff is the key check).

## Events

`USER_DOCS.STARTED`/`.DRAFTED`/`.ACCEPTED`; `API_DOCS.UPDATED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accepted docs route per autonomy; publishing to the docs surface (e.g. wiki.tamma.dev) is the post-accept
action. Missing/contradicting existing docs surface as review concerns, not silent overwrites.

## Autonomy behavior

- **70–84:** agent drafts; tech writer accepts.
- **85–100:** agent drafts and self-accepts; contract-affecting API-doc changes can be always-escalate.

## Acceptance Criteria

1. Thin lifecycle binding; prose reviewed by a `Review` that checks accuracy against the merged diff.
2. Idempotent per feature; updates existing docs rather than duplicating.
3. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (prose handling, lifecycle, review, store).
- **Related:** consumes 41-2 AcceptanceCriteria when present.

## Estimated Effort

3–4 days
