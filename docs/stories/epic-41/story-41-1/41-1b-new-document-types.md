# Story 41-1b: New Document Types — AcceptanceCriteria, BacklogOrdering, SprintPlan, TestPlan, ThreatModel, UxSpec

Status: drafted

*Split from 41-1 — see [the enabler-set umbrella](./41-1-team-role-and-document-type-extensions.md).
Batch shape and effort follow the landed precedent: 39-3 registered four types, 39-4 registered six.*

## User Story

As the **Epic 41 program**, I want the six document types the epic's planning/QA/security/UX activities
produce registered in the Epic 39 vocabulary with executable domain rules, so that those activities can
persist and review a typed document at all — today none of the six can be written to the store by a human
*or* an agent.

## Priority

P0 — hard gate for 41-2, 41-3, 41-6, 41-13, 41-19, 41-27 on both execution paths. `DocumentTypeKey` is a
closed compile-time vocabulary; an unregistered type is unparsable (`DocumentTypeKey.cs:49-59`,
`DOCUMENT.TYPE.UNKNOWN`) and unresolvable (`DocumentTypeRegistry.cs:85-91`,
`DOCUMENT.TYPE.NOT_REGISTERED`).

## Scope

Six `DocumentTypeKey` members + six `IDocumentType` implementations appended to
`DocumentTypeRegistry.s_registrations`, each with schema, executable domain rules, prompt-contract
renderer, examples and drift tests — the 39-3/39-4 pattern, no new machinery. Domain rules per the epic
README's new-types table:

| Type | Domain rules beyond schema | Producing cell |
|---|---|---|
| `AcceptanceCriteria` | each criterion independently verifiable; Given/When/Then or checklist form; bound to an `issueId`; no criterion references unimplemented scope | `(product_owner, …)` — named by 41-2 |
| `BacklogOrdering` | total order over the referenced item set; every item has a rationale + value/effort estimate; no ties | `(product_owner, prioritize-backlog)` |
| `SprintPlan` | committed set ≤ stated capacity; every committed item has an owner-role + estimate; carry-over flagged | `(scrum_master, plan-sprint)` — needs 41-1a |
| `TestPlan` | risk areas ranked; each strategy line maps to a coverage target; entry/exit criteria stated | `(tester, plan-test-strategy)` |
| `ThreatModel` | STRIDE (or configured) categorisation; each threat has asset + mitigation + residual-risk; unmitigated high-risk ⇒ escalation | `(security, threat-model)` |
| `UxSpec` | every flow has entry + success + error states; each screen/step lists a11y requirements; maps to acceptance criteria | `(ux_designer, author-ui-spec)` — needs 41-1a |

## Design decisions to record

- **D1 — acceptance posture per type is chosen, not inherited.** `AcceptanceDefaults.For`
  (`AcceptanceDefaults.cs:128-133`) ends in a `_ => Rules` catch-all, so a newly registered type
  **compiles and runs** while silently taking the single-`architect` unanimous base row. That is a real
  default for `AcceptanceCriteria`/`SprintPlan`/`ThreatModel`/`UxSpec` — plausibly wrong for at least
  `SprintPlan` (a scrum_master/product_owner acceptor) and `ThreatModel` (a security acceptor). Each of
  the six gets an explicit answer.
- **D2 — no workflow edges in this story.** `DocumentTypeRegistry.BuildSeed` declares
  `WorkflowDocumentInterface` rows keyed by Elsa `DefinitionId`, not by document type. Registering a type
  with no producing workflow is legal (`WorkflowInterfaceGraphTests.Every_declared_produces_key_is_registered_or_pending`
  only constrains the *edge* direction). Edges land with the workflows.

## Acceptance Criteria

1. `DocumentTypeKeyExtensions.Parse` round-trips all six new wire strings (`acceptance-criteria`,
   `backlog-ordering`, `sprint-plan`, `test-plan`, `threat-model`, `ux-spec`) — today each throws
   `DOCUMENT.TYPE.UNKNOWN`; `DocumentTypeRegistry.Resolve` returns an `IDocumentType` for each — today
   each throws `DOCUMENT.TYPE.NOT_REGISTERED`.
2. Each type's `Validate` **rejects a named counter-example and accepts a named positive example** per
   its row above — e.g. a `BacklogOrdering` with two items at the same rank is rejected with a named
   violation code; a `SprintPlan` whose committed estimates exceed the stated capacity is rejected; a
   `ThreatModel` with an unmitigated high-risk threat and no escalation is rejected. One rejecting and
   one accepting fixture per rule, each asserting the violation code, not just "invalid".
3. A draft of each new type round-trips envelope → `DocumentInstance` row → 39-11 store read-back with
   `issueId` lineage intact.
4. **The two document-vocabulary count pins are bumped consciously, with the reason in the comment:**
   `DocumentTypeKeyTests.cs:20` `Be(10)` → `Be(16)` and `DocumentTypeRegistryTests.cs:37`
   `HaveCount(10)` → `HaveCount(16)`. Both currently fail the build the moment a member is appended, and
   neither was named anywhere in the pre-split story.
5. **`AcceptanceDefaults.For` returns the documented row for each of the six** (D1): a test asserts the
   chosen `AcceptorRequirement` / `ReviewerSelection` per type, so a type that should fall through to the
   base row does so on purpose and one that should not gets its own arm in
   `AcceptanceDefaults.cs:128-133`.
6. Each type has a prompt-contract renderer (`IDocumentType.RenderContract`) and one `ContractBindingTests`
   entry per producing cell; **no existing `Bindings` entry is edited**, and the build gate is green with
   no new `IntentionallyUnbound` or residual entry.
   > **Note — the shared-contract hazard.** `RenderContract` is per *document type*, not per producing
   > cell (`IDocumentType.cs:47-50`; `Plan.cs:135` returns one `Contract` const for both `plan`
   > producers). Any new type with two producing cells inherits the same constraint, so each of the six
   > declares exactly one producing cell here.
7. **`WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)` is NOT touched by this story** (D2).
   > **Corrected — an earlier reading attributed that pin to the type-registration work.** It counts
   > `DocumentTypeRegistry.WorkflowInterfaces` rows keyed by Elsa `DefinitionId` (the comment at `:38-44`
   > records 39-15 moving it 15 → 16 by adding a *workflow* edge). It moves when 41-2/41-3/41-6/41-13/
   > 41-19/41-27 declare their bindings — each of those stories owns its own `+1`.

## Dependencies

- **Blocking:** Epic 39 (39-2 registry + envelope + drift tests, 39-3/39-4 type pattern, 39-11 store,
  39-16 contract generation). `SprintPlan` and `UxSpec` additionally need **41-1a** for their producing
  role.
- **Unblocks:** 41-2, 41-3, 41-6, 41-13, 41-19, 41-27.

## Estimated Effort

5–6 days (39-4 shipped six types in 5–6 days)

## Follow-ups from adversarial review (2026-07-29)

**Resolved in the review-fix pass (same date):** three duplicate-identifier gaps in the shipped
validators, each following the `CRITERION_ID_DUPLICATED` naming pattern —
`BacklogOrdering` accepted duplicate `itemId` entries (the same item at two ranks validated, breaking
"total order over the referenced item set") → new `ITEM_ID_DUPLICATED`; `TestPlan` accepted two risk
areas with the same name (making `riskAreaRef` ambiguous) → new `RISK_AREA_NAME_DUPLICATED`;
`ThreatModel` accepted duplicate asset ids and duplicate threat ids → new `ASSET_ID_DUPLICATED` and
`THREAT_ID_DUPLICATED`. One rejecting and one accepting fixture per rule landed alongside (AC2
discipline).

**Open follow-up — finding 4 (null array element ⇒ NRE, not `MALFORMED_PAYLOAD`):** a payload whose
array carries a JSON `null` element (e.g. `{"items":[null]}`) deserializes to a list containing a null
element; every per-item loop then dereferences it and throws `NullReferenceException` instead of
returning a `MALFORMED_PAYLOAD` violation. The `try/catch` in each `Validate` only catches
`JsonException`, so the NRE escapes and **faults `DocumentLifecycleWorkflow` (the `Validate` call site
at ~line 342) instead of routing the document to the repair ring** — a malformed agent reply becomes a
faulted workflow instance rather than a repairable validation failure. This is an **inherited pattern
across ALL registered types, including pre-existing ones (e.g. `Findings`)**, not a defect of the six
41-1b types alone — the fix belongs in a shared-validator hardening pass (null-element guard or a
shared deserialize helper that maps null elements to `MALFORMED_PAYLOAD`) across every
`IDocumentType.Validate`, with one null-element fixture per type. Not fixed in the 41-1b lane to avoid
piecemeal divergence from the pre-existing types.
