# Story 41-28: Design Review & Accessibility Audit Workflow

Status: drafted

## User Story

As a **UX / designer** (or eligible role-holder), I want a workflow that reviews a `UxSpec` or a shipped UI
against usability heuristics and accessibility standards, producing a typed `Review` on the lifecycle, so
that design and a11y quality are checked and routed — not left to chance.

## Priority

P3 / Wave 4 — the review counterpart to 41-27. Depends on 41-1's `ux_designer` role.

## Scope

Thin binding over `document-lifecycle`. `consumes: [UxSpec (41-27) or shipped UI diff, AcceptanceCriteria?]`
/ `produces: Review` (subject = the spec or UI; issues carry a11y standard + severity + fix). Produce cells
`(ux_designer, review-design)` and `(ux_designer, audit-accessibility)` (41-1) as lenses aggregating into
one `Review`.

## Produced document

Unified `Review`: each issue carries category (usability | a11y) + severity + WCAG (or configured)
reference + fix; blocking a11y issues ⇒ not approvable (39-4 invariant). `issueId` lineage.

## Events

`DESIGN_REVIEW.STARTED` → `.VERDICT` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Verdict routes through the accept gate; a blocking a11y failure escalates with lineage and can loop back to
41-27 or the coding step. Legal/compliance-relevant a11y failures can be an always-escalate class.

## Autonomy behavior

- **70–84:** agent drafts the review; a human designer signs off.
- **85–100:** agent review self-accepted for non-blocking verdicts; blocking a11y issues always escalate.

## Acceptance Criteria

1. Thin lifecycle binding; validated unified `Review`; blocking a11y issues cannot be laundered into approval.
2. a11y lens references a configured standard (WCAG default) per issue.
3. Verdict integrates as a gate input for UI-affecting merges.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** 41-1 (`ux_designer` role), Epic 39 (`Review`, lifecycle, review producers, store).
- **Related:** reviews 41-27 output.

## Estimated Effort

4 days
