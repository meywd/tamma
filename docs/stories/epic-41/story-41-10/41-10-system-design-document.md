# Story 41-10: System Design Document Workflow

Status: drafted

## User Story

As an **architect** (or eligible role-holder), I want a workflow that produces a typed `Design` document
for a larger feature — covering API contract, data model, and integration points with weighed
alternatives — on the standard lifecycle, so that non-trivial designs are proposed, reviewed, and accepted
before implementation planning, instead of being improvised in the plan step.

## Priority

P2 / Wave 3 — the depth counterpart to `design-proposal` for multi-surface features.

## Scope

Thin binding over `document-lifecycle`, `DefinitionId = "system-design"` (free today — no workflow claims
it). `consumes: [issue, Findings, AcceptanceCriteria?, context-scan]` /
`produces: Design`. Produce cell **`(architect, design-system)`** — a new cell, minted by **41-1a**.
`design-api-contract` / `design-data-model` / `design-integration` stay **unbound**, reserved for a future
facet-scoped story; their three concerns are **sections of the one `Design` document** this workflow
produces, not three produce steps.

> **Corrected — the produce cell was `(architect, plan-system-design)`, which is already taken.**
> `PlanGenerationWorkflow` (39-14) binds that cell as the produce step of its `document-lifecycle`
> binding (`PlanGenerationWorkflow.cs:186-188`; `DocumentTypeRegistry.cs:151` `plan-generation` →
> `DocumentTypeKey.Plan`, non-provisional), and CI pins its parser authority to
> `PlanDocumentType.Validate` with the `"tasks"|"steps"` + `"fileMap"|"files"|"filesToModify"` token
> groups (`ContractBindingTests.cs:160-164`). The shipped `Prompts/architect/plan-system-design.md`
> instructs a tasks/files/dependencies plan and has no `Design` fields at all. Binding 41-10 there would
> force one cell to declare two `produces` types — which 39-16's per-cell regeneration forbids
> (`Plan.cs:139-142`) — or break the contract test. **`plan-system-design` is RESERVED as
> plan-generation's `Plan` producer and this story must not edit its `Bindings` entry.**
>
> **Decision — mint a new cell rather than reuse a facet cell.** The three unbound architect cells are
> genuinely facet-scoped in their shipped templates ("designing an API contract" /
> "designing a data model" / "designing an integration between systems or services",
> `Prompts/architect/design-*.md`), so binding one of them as the produce step for a whole-system design
> would misname the cell. The only existing whole-system lens, `(architect, propose-design)`, is already
> the landed `Design` producer for `design-proposal` (`ContractBindingTests.cs:147`;
> `DocumentTypeRegistry.cs` `design-proposal` → `Design`, non-provisional) — that stays the
> **single-surface** path. 41-10 is the **multi-surface** path and takes its own cell.

## Produced document

`Design` (39-4): ≥1 alternative with trade-offs; recommendation references an alternative; the API/data
/integration facets are sections of the one document. `issueId` lineage. Reviewed via panel (architect +
senior-dev + security + relevant lenses).

## Events

`SYSTEM_DESIGN.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; a design touching a public contract or cross-service boundary can be an
always-escalate class. Accepted `Design` is consumed by `plan-generation` and can seed 41-9 ADRs.

## Autonomy behavior

- **70–84:** agent drafts; architect accepts.
- **85–100:** agent drafts and self-accepts; contract/boundary-affecting designs always escalate per policy.

## Acceptance Criteria

1. Thin lifecycle binding on `(architect, design-system)`; the story adds ONE new `ContractBindingTests`
   `Bindings` entry for that cell with authority `DesignDocumentType.Validate`, and the
   `(architect, plan-system-design)` entry (`ContractBindingTests.cs:160`) is byte-unchanged and still
   asserts `PlanDocumentType.Validate`.
2. `Design` validation is exercised by fixtures that each fail on exactly one rule: no alternatives ⇒
   `NO_ALTERNATIVES`; an alternative without trade-offs ⇒ `ALTERNATIVE_MISSING_TRADEOFFS`; a
   recommendation naming an alternative not in the list ⇒ `RECOMMENDATION_UNKNOWN_ALTERNATIVE`; empty
   summary ⇒ `MISSING_SUMMARY` (`Design.cs:48-57`).
3. A facet (API contract / data model / integration) is either present as a section or explicitly marked
   not-applicable with a reason; a body with a facet that is neither is rejected by a story-local rule
   (`DESIGN_FACET_MISSING`). No separate facet workflows and no new `Bindings` entries for
   `design-api-contract` / `design-data-model` / `design-integration`.
4. An accepted `Design` is retrievable by `issueId` through 39-11 and is read by a `plan-generation` run
   in an integration test (and by 41-9 for ADR seeding).
5. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate
   suspends inside the dispatched `document-lifecycle` child); 39-10 structural test green without an
   allowlist entry. A new
   `WorkflowDocumentInterface` row (`system-design` → `Design`, non-provisional) is declared and
   `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is bumped in the same change.

> Depth, alternative quality and "is this the right design" are **not** acceptance criteria — no
> deterministic check exists for them. They are the review panel's job (39-7) and the accept gate's.

## Dependencies

- **Blocking:** **41-1a** — must mint the `(architect, design-system)` action cell and put it in
  `AgentRole.Architect`'s eligible set (`RolePhaseMap.cs:65-77`) plus a `Prompts/architect/design-system.md`
  template; Epic 39 (`Design`, lifecycle, review-panel, store).
- **Related:** feeds `plan-generation`, 41-9. Sibling to the landed `design-proposal` (single-surface).

## Estimated Effort

4–5 days
