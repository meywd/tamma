# Template example-conformance gate — baseline contents and Epic 41 ownership

**Date**: 2026-07-27
**Context**: `tests/Tamma.Activities.Tests/Workflows/TemplateExampleConformanceTests.cs`

## The gap this gate closes

`ContractBindingTests` pins that a bound cell's template literally *contains* the JSON field
tokens its validator slices — but never checked whether the template's own worked example
actually **validates** against the document type the cell is bound to. A template could carry
every token while instructing the wrong document shape, making every produce through the cell a
guaranteed runtime validation failure with all tests green.

The new gate: for every DocumentType-bound cell in `ContractBindingTests.Bindings` (derived via
the new internal `DocumentTypeValidatedCells` accessor — same map, no second list to drift), it
extracts the template's fenced ```json example (last fence, then the exact
`DocumentLifecycleWorkflow.ExtractJsonObject` carve: first `{` … last `}`), normalizes
closed-set placeholder strings (`"low|medium|high"` → `"low"`, matching the RenderContract
idiom so 39-16 regeneration stays compatible), and runs the result through the bound type's
real `Validate()` via `DocumentTypeRegistry`.

## Bound cells fixed in this change (all were live defects of the same class)

All seven shipped templates below instructed shapes their own bound validator rejects.
Fixed by rewriting the worked example (front matter untouched; all `ContractBindingTests`
pinned tokens preserved):

| Cell | Was | Now |
|---|---|---|
| `(architect, plan-system-design)` | `files: [{path, action}]` + `dependencies` → MALFORMED_PAYLOAD | valid `Plan`: `files: string[]`, `dependsOn`, per-task `testing` |
| `(senior_developer, create-tasks)` | same legacy plan wire → MALFORMED_PAYLOAD | valid `Plan` (same rewrite) |
| `(senior_developer, decompose-issue)` | single subtask with dangling `dependsOn: ["ST-0"]` | two subtasks, `ST-2 dependsOn ["ST-1"]` |
| `(architect, propose-design)` | no alternative `id`s / no `recommendedAlternativeId` → RECOMMENDATION_UNKNOWN_ALTERNATIVE | ids `A1`/`A2` + `recommendedAlternativeId: "A1"` |
| `(product_owner, clarify-requirements)` | bare JSON array — un-ingestible (the lifecycle carve needs an object) | `{"phase": "questions", "questions": [...]}` |
| `(product_owner, incorporate-answers)` | no `phase` → UNKNOWN_PHASE | added `"phase": "resolution"` |
| `(tester, write-tests)` | bare array of `{description, type, file, testCode}` cases | `{"testCases": [...]}` with required `taskId`/`behavior` per case (extra fields kept) |

## The ratchet baseline (`KnownNonConformingTemplates`) — 11 entries, count-pinned

Entries may only ever be REMOVED. Each is an **unbound** cell (a bound cell may never be
baselined — the gate's test 3 enforces it); the owning Epic 41 story rewrites the template
when it binds the cell and deletes the entry in the same change.

| Cell | Intended type | Owner | Why non-conforming today |
|---|---|---|---|
| `(architect, plan-migration-strategy)` | `plan` | 41-12 | legacy plan wire (`{path, action}` files + `dependencies`) |
| `(tester, plan-test-strategy)` | `test-plan` (41-1b, future) | 41-13 | legacy plan wire |
| `(tester, exploratory-test)` | `findings` | 41-14 | no JSON example — instructs file-format test output |
| `(tester, write-regression-test)` | `test-spec` | 41-16 | no JSON example — instructs file-format test output |
| `(tester, verify-acceptance)` | `review` | 41-15 | `{issues, summary:{decision}}` shape; Review needs root `subject`/`decision`/string `summary` |
| `(product_owner, define-acceptance-criteria)` | `acceptance-criteria` (41-1b, future) | 41-2 | legacy plan wire |
| `(product_owner, plan-roadmap)` | `prose` (41-1c, future) | 41-4 | legacy plan wire |
| `(product_owner, prioritize-backlog)` | `backlog-ordering` (41-1b, future) | 41-3 | retired P0-P3 / `ownerRole` triage vocabulary |
| `(devops, plan-incident-response)` | `plan` | 41-22 | legacy plan wire |
| `(devops, write-postmortem)` | `prose` (41-1c, future) | 41-22 | no JSON example — markdown issue-comment format |
| `(tech_writer, update-changelog)` | `prose` (41-1c, future) | 41-24 | no JSON example — markdown issue-comment format |

Staleness mechanics: entries whose intended type is registered today are re-validated on every
run — one that starts conforming fails the build until deleted. Entries naming a future type
must name one of the planned 41-1b/41-1c keys (`test-plan`, `acceptance-criteria`,
`backlog-ordering`, `prose`); the moment such a key is registered in `DocumentTypeRegistry`,
its entries automatically start being staleness-checked against the real validator.

## Notes / residual

- The four non-DocumentType bound cells (assessment questions/analysis, deploy/rollback) are
  outside this gate by construction — their authority is an inline parser, not a document type
  (`ContractBindingTests.NonDocumentTypeResidual`).
- `TestSpec`'s cross-document rule (`CASE_UNKNOWN_TASK_ID`) needs a consumed plan and is not
  checkable against a static example; the gate uses the context-free `Validate`.
- `ClarificationDocumentType.RenderContract` still says the questions phase is a bare JSON
  array — that contradicts both its own `Validate` (requires `phase`) and the lifecycle ingest
  (object-only carve). The *template* is fixed; the renderer text is src-side and left as-is
  (out of scope for this change; worth a follow-up when 39-16 regeneration lands).
