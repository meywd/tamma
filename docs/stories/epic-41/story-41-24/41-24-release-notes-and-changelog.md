# Story 41-24: Release Notes & Changelog Workflow

Status: drafted

## User Story

As a **tech writer** (or eligible role-holder), I want a release-triggered workflow that composes release
notes (audience: users) and updates the changelog (audience: developers) from the merged changes in a
release window, as prose documents on the lifecycle, so that every release ships accurate, reviewed notes
without hand-assembly.

## Priority

P1 / Wave 2 — release-triggered, recurring, user-facing; a clear prose-on-lifecycle win.

## Scope

Triggered by a release/tag event → thin binding over `document-lifecycle`. `consumes: [merged PRs +
Decompositions/Plans in the release window, DCB MERGE.* events]` / `produces: prose (release-notes,
audience=user)` and `prose (changelog, audience=developer)`. Produce cells `(tech_writer,
write-release-notes)` and `(tech_writer, update-changelog)`; review via `(tech_writer, review-docs)`.

## Produced documents

Two audience-tagged prose documents, `repository`/release-lineaged. *Prose stays prose* — markdown, no
forced schema; review stage is a `Review` over the text.

## Events

`RELEASE_NOTES.STARTED`/`.DRAFTED`/`.ACCEPTED`; `CHANGELOG.UPDATED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accepted notes route per autonomy; publishing to the release/GitHub surface is the post-accept action. A
customer-facing release can be a configured always-escalate class (human sign-off before publish).

## Autonomy behavior

- **70–84:** agent drafts; tech writer/PO accepts before publish.
- **85–100:** agent drafts and self-accepts internal changelog; customer-facing notes publish per policy
  (always-escalate optional).

## Acceptance Criteria

1. Thin lifecycle binding; both outputs ride the lifecycle as audience-tagged prose reviewed by a `Review`.
2. Window derivation is deterministic (the release just cut); re-run is idempotent.
3. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (prose handling, lifecycle, review, store), 4-7 query API for the window.

## Estimated Effort

3–4 days
