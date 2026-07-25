# Story 41-13: Test-Plan / Strategy Authoring Workflow

Status: drafted

## User Story

As a **tester** (or eligible role-holder), I want a workflow that authors a risk-based **TestPlan** for an
issue or release — scope, coverage targets, environments, entry/exit criteria — on the standard lifecycle,
so that testing strategy is explicit and accepted above the executable cases, instead of implicit in
`test-case-creation`.

## Priority

P3 / Wave 3 — the strategy layer above `TestSpec`; consumes 41-2.

## Scope

Thin binding over `document-lifecycle`. `consumes: [issue, AcceptanceCriteria (41-2), Plan, Findings]` /
`produces: TestPlan`. Produce cell `(tester, plan-test-strategy)`.

## Produced document

`TestPlan` (41-1): risk areas ranked; each strategy line maps to a coverage target; entry/exit criteria
stated. `issueId` lineage. Reviewed via `(tester, review-testability)` / architect lens.

## Events

`TEST_PLAN.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; accepted `TestPlan` drives `test-case-creation` (which produces the bound
`TestSpec` cases) and 41-14 charter scoping.

## Autonomy behavior

- **70–84:** agent drafts; tester accepts.
- **85–100:** agent drafts and self-accepts; a plan for a safety-critical area can be always-escalate.

## Acceptance Criteria

1. Thin lifecycle binding; `TestPlan` validated (risk ranking, coverage mapping, entry/exit).
2. Consumes `AcceptanceCriteria` when present; strategy lines trace to criteria.
3. Consumable by `test-case-creation` / 41-14 via 39-11.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** **41-1b** (`TestPlan` type), Epic 39 (lifecycle, review, store); 41-2.

## Estimated Effort

3–4 days
