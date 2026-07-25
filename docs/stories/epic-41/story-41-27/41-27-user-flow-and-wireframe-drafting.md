# Story 41-27: User-Flow & Wireframe/UI-Spec Drafting Workflow

Status: drafted

## User Story

As a **UX / designer** (or eligible role-holder), I want a workflow that drafts the user flows and a
structured UI spec for a feature as a typed `UxSpec` on the lifecycle — screens, states, transitions,
accessibility requirements — so that interface design is proposed, reviewed, and accepted before
implementation, instead of being invented in code.

## Priority

P3 / Wave 4 — opens the entirely-missing UX/design surface. Depends on 41-1's `ux_designer` role +
`UxSpec` type.

## Scope

Thin binding over `document-lifecycle`. `consumes: [issue, AcceptanceCriteria (41-2)?, Findings]` /
`produces: UxSpec`. Produce cells `(ux_designer, draft-user-flow)` and `(ux_designer, author-ui-spec)`
(41-1), folded into one `UxSpec` document.

## Produced document

`UxSpec` (41-1): every flow has entry + success + error states; each screen/step lists a11y requirements;
maps to acceptance criteria. `issueId` lineage. Reviewed via 41-28 / product-owner lens.

## Events

`UX_SPEC.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; accepted `UxSpec` feeds `plan-generation` and 41-2/41-15 (its a11y +
state requirements become acceptance criteria). Pixel-level mockup rendering is out of scope (README) —
this produces the structured spec, not rendered artwork.

## Autonomy behavior

- **70–84:** agent drafts flows/spec; a human designer/PO accepts.
- **85–100:** agent drafts and self-accepts within policy; brand/UX-guideline-affecting specs can be
  always-escalate.

## Acceptance Criteria

1. Thin lifecycle binding; `UxSpec` validated (flow states, a11y per screen, criteria mapping).
2. Consumes `AcceptanceCriteria` when present; spec traces to criteria.
3. Consumable by `plan-generation`/41-28 via 39-11.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1a** (`ux_designer` role + `draft-user-flow`/`author-ui-spec` cells) **and
  41-1b** (`UxSpec` type), Epic 39 (lifecycle, review, store).
- **Related:** feeds 41-28; consumed by `plan-generation`.

## Estimated Effort

4–5 days
