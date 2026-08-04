# Story 41-1b: New Document Types — AcceptanceCriteria, BacklogOrdering, SprintPlan, TestPlan, ThreatModel, UxSpec

Status: done — conformance-reviewed 2026-07-29; all six types register, validate, and now round-trip through the store (`NewDocumentTypeStoreRoundTripTests`, 36 cases on a real Postgres 17); all six producing cells carry a contract entry (`ContractBindingTests.PendingProducerCells`) and the `(security, threat-model)` template — which instructed a shape its own validator rejected — was rewritten to the real ThreatModel wire; follow-up finding 4 (null array element ⇒ NRE) is resolved cross-type by the shared `DocumentPayloadGuard`. AC4's literal "16" reads **17** in the tree because 41-1c registered `prose` afterwards — see the dated AC4 amendment. Open: the three legacy-shape templates owned by 41-2/41-3/41-13, and the mis-attributed subject when a null element sits in a sibling *context* document

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
   > **Closed (2026-07-29 conformance round).** This AC had NO test when the story was first reviewed;
   > it now has `tests/Tamma.Api.Tests/Documents/NewDocumentTypeStoreRoundTripTests.cs` — a Postgres 17
   > Testcontainer fixture in the shape of `DocumentInstanceRepositoryTests` /
   > `ProseStoreAndLineageTests`. **36 cases, all green.** The cases are generated from
   > `DocumentTypeRegistry.All` rather than hand-written per type, so all **17** registered types are
   > swept (the six 41-1b types, the ten 39-3/39-4 incumbents and 41-1c's `prose`) and the eighteenth is
   > covered the day it registers. Each case takes the type's own shipped valid `DocumentExample` —
   > no body invented here, none able to drift from its validator — through envelope →
   > `DocumentInstanceRepository.InsertAsync` (which resolves the type and rejects a failing body with
   > `DOCUMENT.STORE.INVALID_BODY` *before* persisting, so the write door is a validated one) →
   > `ListByIssueAsync` and the production lineage handler `GET /api/documents/issues/{issueId}/lineage`,
   > with the jsonb body byte-identical and the type re-resolving on read-back.
   > `Sweep_covers_every_one_of_the_six_41_1b_types` pins this AC's explicit six against the generated
   > set, and `AllSixNewTypes_InOneIssue_EachTypeTrailKeepsItsOwnPayload` puts all six in ONE issue and
   > asserts each keeps its own type trail with zero unlinked reviews.
   > **Two limits worth stating rather than glossing:** (a) the per-type sweep's lineage half asserts
   > *reachability* — the document is found somewhere in the lineage response — not *placement*; a
   > regression that let a document slip out of its type trail into `unlinkedReviews` is caught only by
   > the all-six test, which does pin placement; (b) every case inserts an already-`Accepted` envelope
   > with `audience: null`, so the draft→accepted transition and the audience-filtered read stay covered
   > by `ProseStoreAndLineageTests`, not here.
4. **The two document-vocabulary count pins are bumped consciously, with the reason in the comment:**
   `DocumentTypeKeyTests.cs:20` `Be(10)` → `Be(16)` and `DocumentTypeRegistryTests.cs:37`
   `HaveCount(10)` → `HaveCount(16)`. Both currently fail the build the moment a member is appended, and
   neither was named anywhere in the pre-split story.
   > **Amendment (2026-07-29):** 41-1b moved both pins 10 → 16 as written; **41-1c then moved them
   > 16 → 17** by registering `prose`. The tree therefore reads `Be(17)` /
   > `HaveCount(17)` — each delta is attributed in the pin comments (39-3 +4, 39-4 +6, 41-1b +6,
   > 41-1c +1). A future reader checking this AC against the tree should expect 17, not 16.
5. **`AcceptanceDefaults.For` returns the documented row for each of the six** (D1): a test asserts the
   chosen `AcceptorRequirement` / `ReviewerSelection` per type, so a type that should fall through to the
   base row does so on purpose and one that should not gets its own arm in
   `AcceptanceDefaults.cs:128-133`.
6. Each type has a prompt-contract renderer (`IDocumentType.RenderContract`) and a classification for its
   producing cell in the template/contract gates; **no existing `Bindings` entry is edited**, and the
   build gate is green with no new `IntentionallyUnbound` or residual entry.
   > **Amendment (2026-07-29) — no `Bindings` entry landed for any of the six.** A `Bindings` entry is
   > permitted but not required (implementation-plan-41-1b C4: the coverage guard enumerates only
   > *dispatched* pairs), and three of the six producing cells still ship legacy-shape templates that
   > `EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` would reject — those templates belong
   > to 41-2/41-3/41-13. What landed instead: `(product_owner, define-acceptance-criteria)`,
   > `(product_owner, prioritize-backlog)` and `(tester, plan-test-strategy)` are baselined in
   > `TemplateExampleConformanceTests.KnownNonConformingTemplates` with their intended type and owning
   > story; `(scrum_master, plan-sprint)` and `(ux_designer, author-ui-spec)` are in
   > `ConformingUnboundCells` and validate against their real types today. **Gap:**
   > `(security, threat-model)` — `ThreatModel`'s declared producing cell (`ThreatModel.cs:227`) — is in
   > none of the three tables, and its shipped template instructs an `{issues, verdict}` review shape
   > that cannot validate as `threat-model`. It needs a baseline entry (owned by 41-19) before this AC
   > can be called satisfied.
   > **Closed (2026-07-29 conformance round) — all six producing cells now carry a contract entry, and
   > the `(security, threat-model)` gap named above is gone.** `ContractBindingTests` gained a fourth
   > classification, `PendingProducerCells` — "contract declared, dispatch pending" — with one entry per
   > producing cell of the six types: `(product_owner, define-acceptance-criteria)` → 41-2,
   > `(product_owner, prioritize-backlog)` → 41-3, `(tester, plan-test-strategy)` → 41-13,
   > `(security, threat-model)` → 41-19, `(scrum_master, plan-sprint)` → 41-6,
   > `(ux_designer, author-ui-spec)` → 41-27. Each pins the token contract its binding story will adopt
   > verbatim, names that story, and states why it is not a live binding today. This was the only
   > honest home for them: a `Bindings` entry asserts a dispatch that does not exist and an
   > `IntentionallyUnbound` entry claims the cell has no structured contract when it has a typed one —
   > both trip the existing stale-classification guard. **No `Bindings` entry was edited or added** (16
   > before, 16 after), so the AC's "no existing `Bindings` entry is edited" clause still holds.
   > Two new guards keep the table from becoming a place contracts go to stop being checked:
   > `EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse` fails the build the day a
   > compiled site emits the pair — forcing the entry to GRADUATE into `Bindings`, where the
   > template-token gate takes over — and
   > `EveryPendingProducerCell_IntendedContractIsCarriedByItsDocumentType` checks every pinned token
   > group against the type's real `RenderContract()`, and re-asserts D4 (one producing cell per type).
   > **Stated precisely:** that second guard checks the *contract*, not the *template*. All six token
   > sets are carried by `RenderContract()` today, but only three of the six shipped TEMPLATES carry
   > them (`threat-model` 9/9, `plan-sprint` 8/8, `author-ui-spec` 10/10);
   > `define-acceptance-criteria`, `prioritize-backlog` and `plan-test-strategy` still instruct legacy
   > wires and stay baselined in `TemplateExampleConformanceTests.KnownNonConformingTemplates` — their
   > rewrite is 41-2/41-3/41-13's work, exactly as the amendment above says. The "adopt verbatim when it
   > binds" promise therefore means *after* those three stories rewrite their template, not before it.
   > **The template fix:** `Prompts/security/threat-model.md` was rewritten (version 1 → 2) from the
   > `{issues, verdict}` review shape — measured against the real validator, that shipped example failed
   > with `NO_ASSETS` + `NO_THREATS`, i.e. the template instructed a shape its own registered validator
   > could never accept — to the real ThreatModel wire (`assets` /
   > `threats[assetRef, category, description, mitigation, residualRisk]` / `escalation`). Its worked
   > example now validates with zero violations and deliberately exercises the load-bearing
   > unmitigated-high-risk ⇒ escalation rule rather than dodging it, and the cell is classified in
   > `TemplateExampleConformanceTests.ConformingUnboundCells` until 41-19 binds it. Every rule the
   > template's "Rules:" list states was checked to be a rule the validator actually enforces.
   > **First graduation (2026-07-29, Story 41-2).** `PendingProducerCells` is **6 → 5**:
   > `(product_owner, define-acceptance-criteria)` graduated exactly as designed — 41-2's
   > `AcceptanceCriteriaAuthoringWorkflow` now dispatches the pair, so
   > `EveryPendingProducerCell_IsUndispatched_AndClassifiedNowhereElse` would have failed on a
   > surviving entry, and its `IntendedContract` (10 token groups) moved into `Bindings`
   > **verbatim** — the promise held, with no token renegotiated. The template rewrite the
   > amendment above assigned to 41-2 landed in the same change (version 1 → 2; its worked example
   > validates against `AcceptanceCriteriaDocumentType` with zero violations), so its
   > `KnownNonConformingTemplates` baseline is gone and the count pin moved **16 → 15** — the first
   > time that ratchet has turned the direction it was built to turn. `Bindings` is 16 → 17 (41-2)
   > → 18 (41-9's `(architect, write-adr)` → `prose`), which does not disturb this AC's "no existing
   > `Bindings` entry is edited" clause: both are additions. Still pending: `prioritize-backlog`
   > (41-3), `plan-test-strategy` (41-13), `threat-model` (41-19), `plan-sprint` (41-6),
   > `author-ui-spec` (41-27).
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

**Resolved (2026-07-29 conformance round) — finding 4 (null array element ⇒ NRE, not
`MALFORMED_PAYLOAD`).** Fixed as the shared cross-type hardening pass the finding asked for, NOT in the
41-1b lane alone: a new `Tamma.Core/Documents/Types/DocumentPayloadGuard.cs` that **all 17** registered
`IDocumentType.Validate` bodies — and the three `ValidateWithContext` overrides
(`AcceptanceCriteria`, `TestSpec`, `UxSpec`) — now delegate through. Two layers, in order: a structural
pre-scan that walks the raw `JsonElement` before the type's body runs and rejects any null element in
any array with that type's own `MALFORMED_PAYLOAD` code and a message naming the offending JSON path
(so 39-9's repair turn can tell the model which entry to fill in), then a widened catch mapping the
other structural exceptions to the same violation. A null PROPERTY value (`{"criteria":null}`) is
deliberately NOT caught — each type already degrades that to its own domain violation and that outcome
is preserved. `TammaError` is deliberately NOT in the caught set, so a genuine invariant breach still
fails loud. The proof is a registry-driven sweep,
`tests/Tamma.Core.Tests/Documents/Types/DocumentTypesNullElementSweepTests.cs`, which reflects over each
type's payload CLR type and mutates each shipped valid example rather than hand-writing per-type cases:
16 of the 17 types declare an array member (`prose` has none) and every one is probed.
`Tamma.Core.Tests` went 880 → 959 and 74 of the 79 new tests are red against the pre-fix tree — 35 of
those threw the reported `NullReferenceException`.
**Two things to state honestly.** (a) This is a deliberate *tightening*, not a pure no-op: a null
element inside a **string** array (e.g. `plan.tasks[].files`, `findings.citations`,
`ux-spec.errorStates`, `test-plan.entryCriteria`) was previously accepted as fully **valid** and now
returns `MALFORMED_PAYLOAD`. The accurate no-drift statement is that *no shipped example and no payload
without a null array element changes outcome* — which is what a 39-example, 272-null-property and
2,265-case mutation differential against the pre-fix tree actually showed (zero differences). (b) The
guard also catches a null element in a sibling **context** document, which previously threw an NRE —
it now fails closed, but reports `MALFORMED_PAYLOAD` *against the payload*, which is the wrong subject.
See the new open follow-up below.

**Open follow-up — finding 4b (the context-null subject is mis-attributed, and the widened catch is
broad):** when the null element sits in a sibling *context* document rather than in the payload, the
guard returns `MALFORMED_PAYLOAD` with "The payload is structurally malformed…", so the repair ring
would ask the model to fix a document that has nothing wrong with it; the cross-document readers
(`ReadDecompositionSubtaskIds` and its `TestSpec`/`UxSpec` equivalents) still dereference context
elements unguarded, and there is no test for that path. Relatedly, the widened catch swallows
`InvalidOperationException` / `ArgumentException` / `KeyNotFoundException` / `IndexOutOfRangeException`
— exactly what a `.Single()`, a dictionary miss or an off-by-one inside a FUTURE `ValidateCore` would
throw — so a validator logic bug would be reported to the model as a malformed payload instead of
surfacing. Neither is reachable by anything in the tree today (959/959 green); both are shape risks to
close when a story next touches the guard.

**The original finding 4, kept for the record (superseded by the resolution above):** a payload whose
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
piecemeal divergence from the pre-existing types. *(That is exactly the shape the 2026-07-29 fix took —
one shared guard and a registry-driven sweep, rather than per-type fixtures.)*
