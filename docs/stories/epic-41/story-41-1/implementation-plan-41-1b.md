# Implementation Plan — Story 41-1b: New Document Types — AcceptanceCriteria, BacklogOrdering, SprintPlan, TestPlan, ThreatModel, UxSpec

## Scope & Deliverable

When this story is done, `DocumentTypeKey` has **16 members** instead of 10 and
`DocumentTypeRegistry.All` has **16 registrations** instead of 10;
`apps/tamma-elsa/src/Tamma.Core/Documents/Types/` contains six new payload records + `IDocumentType`
implementations (`Validate` / `RenderContract` / `Examples`), each with executable domain rules that
reject a named counter-example with a named violation code; `AcceptanceDefaults.For` returns a
*deliberately chosen* row for each of the six rather than silently falling through to the
single-`architect` catch-all; a draft of each round-trips envelope → `document_instances` row → 39-11
lineage read-back with `issueId` intact; and the two vocabulary count pins have moved 10 → 16 with the
reason in the comment.

**No workflow edges, no prompt files, no migration, no changes to existing types.** Diff surface:
`Tamma.Core/Documents/DocumentTypeKey.cs`, `DocumentTypeRegistry.cs`, `Documents/Types/**`,
`Documents/Policy/AcceptanceDefaults.cs`, `Tamma.Core.Tests/Documents/**`, plus (for the two 41-1a-gated
types) `Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`.

## Pre-Reading

- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/story-41-1/implementation-plan.md` — shared lockstep rules; this plan
  instantiates the "adding a `DocumentTypeKey`" half
- `docs/stories/epic-41/README.md:262-282` — the new-types table (why each type is not an existing one)
  and the reuse-first list
- **The pattern to copy, verbatim:**
  - `docs/stories/epic-39/story-39-4/implementation-plan.md` — six types in one story; read its
    Implementation Steps and Test Plan sections in full, this story is its structural twin
  - `docs/stories/epic-39/story-39-3/implementation-plan.md` — batch 1, and the `Tamma.Core.Tests`
    dependency posture (D7 there / D8 in 39-4: `Tamma.Core.Tests` gains no `ProjectReference` to
    `Tamma.ElsaServer`/`Tamma.Activities`)
  - `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TestSpec.cs` (254 lines) and
    `TriageDecision.cs` (263) — the closest analogues in size and shape; `Diagnosis.cs` (272) for the
    richest rule set; `Decomposition.cs` + `DependencyGraphCheck.cs` for the shared graph helper
- **The core contracts:**
  `apps/tamma-elsa/src/Tamma.Core/Documents/IDocumentType.cs` — `Key`/`SchemaVersion`/`PayloadClrType`/
  `Validate` (`:29`) / `ValidateWithContext` (`:47`, additive DIM) / `RenderContract` (`:50`) /
  `Examples` (`:56`);
  `DocumentTypeKey.cs:22-34` (the 10 members) and `:49-59` (`Parse` → `DOCUMENT.TYPE.UNKNOWN`);
  `DocumentTypeRegistry.cs:27-40` (`s_registrations`), `:79-92` (`Resolve` →
  `DOCUMENT.TYPE.NOT_REGISTERED`), `:103-126` (`BuildIndex`), `:134-174` (`BuildSeed`);
  `DocumentValidationResult.cs`, `DocumentExample.cs`, `DocumentJson.cs`;
  `apps/tamma-elsa/src/Tamma.Core/Agents/EnumWire.cs` (the `[Wire]` machinery for new closed enums)
- **Acceptance posture:** `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs` —
  `PanelRoster` `:60-69`, `Rules` `:75`, `s_panelRules` `:100-108`, `s_humanAcceptorRules` `:113-116`,
  the **static-ctor validation loop** `:119-121`, and `For` `:129-134` (the `_ => Rules` catch-all)
- **Store + lineage (AC3):** `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs`;
  `Tamma.Data/Repositories/IDocumentInstanceRepository.cs:25-26` (`InsertAsync` validates the body
  against the registry **before** persisting) and `DocumentInstanceRepository.cs:27+`;
  `Tamma.Data/TammaModelConfiguration.cs:1358-1415`;
  `Tamma.Core/Documents/Lineage/IssueDocumentLineage.cs`;
  `Tamma.Api/Endpoints/DocumentEndpoints.cs:32-44` (`GetIssueLineage`), `:52-65` (`GetLatestAccepted`),
  `:98-130` (`PersistFromEngine`)
- **The pins and gates:**
  `tests/Tamma.Core.Tests/Documents/DocumentTypeKeyTests.cs:17-20`;
  `DocumentTypeRegistryTests.cs:24-37` (count pin), `:44-100` (the per-registered-type contract loop:
  unique vocabulary key, deterministic non-empty contract, ≥1 valid + ≥1 invalid **self-checking**
  example emitting **exactly** its `ExpectedViolationCodes`), `:113+`
  (`Every_vocabulary_key_now_resolves_to_an_implementation`);
  `WorkflowInterfaceGraphTests.cs:31-33` (`PendingImplementations`, empty), `:36-45`
  (`Declared_edge_count_is_pinned`, 16);
  `tests/Tamma.Core.Tests/Documents/Policy/AcceptanceDefaultsDriftTests.cs:47/:55/:56`;
  `tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:82` (`Bindings`), `:361`
  (`EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken`), `:616-651` (the universal
  `DocumentType.Validate`-authority pin + `NonDocumentTypeResidual`), `:681`
  (`EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`)
- **Producing cells that already exist** (verified files present under `Tamma.Api/Prompts/`):
  `product_owner/define-acceptance-criteria.md`, `product_owner/prioritize-backlog.md`,
  `tester/plan-test-strategy.md`, `security/threat-model.md`. **NOT FOUND** (41-1a mints them):
  `scrum_master/plan-sprint.md`, `ux_designer/author-ui-spec.md`

## Corrections to the story

- **C1 — `AcceptanceDefaults.For`'s switch is at `:129-134`, not `:128-133`.** `:128` is the closing
  `</summary>`. Cosmetic, but the story cites it three times and D1 hangs off it.
- **C2 — "register the key now, implement the type later" is not possible, and the story does not say
  why.** Two gates make enum-member and `IDocumentType` registration strictly atomic:
  `DocumentTypeRegistryTests.Every_vocabulary_key_now_resolves_to_an_implementation` (`:113+`) iterates
  `Enum.GetValues<DocumentTypeKey>()` and resolves each, and
  `WorkflowInterfaceGraphTests.PendingImplementations` (`:31-33`) — the old escape hatch — is now
  deliberately **empty** with `Pending_entry_is_not_already_registered` failing on a re-added entry. The
  story names only the two count pins (AC4); these two are the ones that actually forbid a partial land.
- **C3 — the umbrella's "41-1a and 41-1b are independent" is false for two of six types.** The story's own
  Dependencies says `SprintPlan` and `UxSpec` need 41-1a for their producing role; the umbrella
  (`41-1-team-role-and-document-type-extensions.md:47`) says the two sub-stories are independent. Both
  cannot hold. Concretely: AC6 wants one `ContractBindingTests` entry per producing cell, and
  `EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` (`ContractBindingTests.cs:361`) resolves
  that cell's template through `SystemPrompts.GetRoleAction` — which throws for a cell that does not
  exist. See D3 for the split.
- **C4 — AC6's "no new `IntentionallyUnbound` or residual entry" is satisfiable, for a reason the story
  does not give.** The coverage guard `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted`
  (`ContractBindingTests.cs:681`) enumerates only pairs emitted by **compiled dispatch sites** (via
  `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs`), and `EveryReviewProducerDispatchablePair_IsClassified`
  (`:547`) only reviewer pairs. None of the six producing cells is dispatched by any workflow until
  41-2/41-3/41-6/41-13/41-19/41-27 land. So no allowlist entry is *required*; a `Bindings` entry is
  *permitted* (there is no stale-`Bindings` guard) but is checked against the template. See D3.
- **C5 — AC6's shared-contract note is right but understates the constraint.** `RenderContract` is per
  document type (`IDocumentType.cs:47-50`), and `Plan.cs` returns one `Contract` const for the two `plan`
  producers. The consequence for *this* story is stronger than "declare one producing cell": for
  `AcceptanceCriteria` the story leaves the cell as "`(product_owner, …)` — named by 41-2", which means
  the contract token set cannot be pinned here at all. D4 resolves it.
- **C6 — the two types' domain rules cite validation the payload cannot see.** `AcceptanceCriteria`'s "no
  criterion references unimplemented scope" and `UxSpec`'s "maps to acceptance criteria" are
  **cross-document** rules, not payload rules. The seam for those is `ValidateWithContext`
  (`IDocumentType.cs:47-50`, the additive default interface member `TestSpec` uses for its task-ID ring),
  and the *consumed* document (`Decomposition` for the first, `AcceptanceCriteria` for the second) must be
  threaded in by the consuming workflow. See D5.

## Design Decisions

- **D1 — acceptance posture is chosen per type, and four of six get their own arm.** `For`'s `_ => Rules`
  catch-all (`AcceptanceDefaults.cs:133`) is a real default, not a bug — but it means single-`architect`
  unanimous, which is wrong for most of these. Chosen rows:

  | Type | Row | Rationale |
  |---|---|---|
  | `AcceptanceCriteria` | `s_panelRules` (7-role majority panel) | it is the merge gate's definition of done and 41-15 verifies against it; the same breadth `plan`/`review` get |
  | `BacklogOrdering` | base `Rules`, reviewer overridden to `product_owner` (**new** `s_productOwnerRules`) | ranking a backlog is a PO judgment; an architect reviewer is nonsense |
  | `SprintPlan` | base `Rules` + `AcceptorRequirement.Human`, reviewer `product_owner` (**new** `s_humanProductOwnerRules`) | a capacity commitment is a human commitment — same posture 39-13 D4 gave `Design` |
  | `TestPlan` | base `Rules`, reviewer `tester` (**new** `s_testerRules`) | strategy is reviewed by QA, not architecture |
  | `ThreatModel` | base `Rules` + `AcceptorRequirement.Human`, reviewer `security` (**new** `s_securityRules`) | unmitigated high-risk ⇒ escalation is a security-owned call |
  | `UxSpec` | `s_panelRules` | a UX spec is cross-functional; the existing 7-role panel is the honest default until 41-28 defines a design panel |

  Each new `AcceptanceRules` static is built `Rules with { … }` and `.Validate()`d exactly like
  `s_panelRules` (`:100-108`) and `s_humanAcceptorRules` (`:113-116`); the static-ctor loop at `:119-121`
  then validates all 16 keys at class load, so an invalid row fails the build immediately. `ux_designer`
  and `scrum_master` are **not** added to any roster here — that is 41-1a's D2 and would make this story
  depend on it for all six types instead of two.
- **D2 — no `WorkflowDocumentInterface` edges in this story; `WorkflowInterfaceGraphTests.cs:45` is not
  touched.** `BuildSeed` (`DocumentTypeRegistry.cs:134-174`) is keyed by Elsa `DefinitionId`, not by
  document type; a registered type with no producing workflow is legal, and the graph tests only constrain
  the edge direction (every declared `produces` key must be registered — satisfied trivially by declaring
  none). Each of 41-2/41-3/41-6/41-13/41-19/41-27 owns its own `+1` on the `HaveCount(16)` pin, per the
  epic README's rule-1 clause (f). Restated from the story because it is the single most likely thing to
  be got wrong by someone pattern-matching "new document type ⇒ bump the graph pin".
- **D3 — ship in two batches, split on the 41-1a dependency (C3).** **Batch A (independent, land first):**
  `AcceptanceCriteria`, `BacklogOrdering`, `TestPlan`, `ThreatModel` — all four producing cells exist with
  shipped templates. **Batch B (after 41-1a):** `SprintPlan`, `UxSpec`. Batch A can merge with the pins at
  10 → 14; Batch B takes them 14 → 16. If 41-1a lands first, collapse to one batch. The alternative —
  register all six and defer only the `ContractBindingTests` entries — is rejected because AC6 is a
  per-type AC and a half-satisfied AC is worse than a sequenced one.
- **D4 — every type declares exactly one producing cell, and `AcceptanceCriteria`'s is named *here*, not
  deferred to 41-2 (C5).** The cell is **`(product_owner, define-acceptance-criteria)`** — it exists
  (`AgentAction.cs:25`, `RolePhaseMap.cs:52`, `Prompts/product_owner/define-acceptance-criteria.md`) and
  is the only candidate. Pinning it here is what makes AC6 checkable at all; 41-2 then binds the cell it
  is handed. Full map: `AcceptanceCriteria` → `(product_owner, define-acceptance-criteria)`;
  `BacklogOrdering` → `(product_owner, prioritize-backlog)`; `TestPlan` → `(tester, plan-test-strategy)`;
  `ThreatModel` → `(security, threat-model)`; `SprintPlan` → `(scrum_master, plan-sprint)`; `UxSpec` →
  `(ux_designer, author-ui-spec)`.
- **D5 — cross-document rules go on `ValidateWithContext`, not `Validate`, and ship *inert* here (C6).**
  `AcceptanceCriteria.ValidateWithContext(payload, decompositionJson)` adds
  `CRITERION_REFERENCES_UNPLANNED_SCOPE`; `UxSpec.ValidateWithContext(payload, acceptanceCriteriaJson)`
  adds `FLOW_UNMAPPED_TO_ACCEPTANCE_CRITERION`. Both are no-ops on empty context (the DIM default at
  `IDocumentType.cs:52` and the lifecycle's "empty context is a no-op" contract), so they are dead until
  the consuming workflow threads a sibling document — exactly the `TestSpec` precedent (39-15 D3). The
  payload-only halves of each rule (each criterion independently verifiable; every flow has entry +
  success + error states) are on `Validate` and are live from day one.
- **D6 — closed enums where the vocabulary is genuinely closed, free non-empty strings where it is not.**
  New `[Wire]` enums: `StrideCategory { spoofing, tampering, repudiation, information-disclosure,
  denial-of-service, elevation-of-privilege }` and `RiskLevel { low, medium, high, critical }` for
  `ThreatModel`; `CriterionForm { given-when-then, checklist }` for `AcceptanceCriteria`. Deliberately
  **not** closed: `BacklogOrdering`'s value/effort estimate units (teams differ), `TestPlan`'s risk-area
  names, `UxSpec`'s a11y requirement text — required non-empty, per 39-4 D10's precedent for `Review`'s
  `category`. "STRIDE (or configured)" from the README becomes: STRIDE is the shipped closed set, and a
  configurable taxonomy is explicitly out of scope until a consumer asks.
- **D7 — `BacklogOrdering`'s "no ties" and `SprintPlan`'s capacity rule are arithmetic, not schema, and
  each gets its own violation code.** `RANK_DUPLICATED` (naming both item ids and the rank),
  `RANK_NOT_TOTAL_ORDER` (a gap or a non-1-based sequence), `ITEM_MISSING_RATIONALE`,
  `ITEM_MISSING_ESTIMATE`; `COMMITMENT_EXCEEDS_CAPACITY` (naming the committed sum and the stated
  capacity), `COMMITTED_ITEM_MISSING_OWNER_ROLE`, `COMMITTED_ITEM_MISSING_ESTIMATE`,
  `CARRYOVER_NOT_FLAGGED`. The codes are the deliverable — AC2 requires the *code*, not "invalid" —
  and they are what 39-9's repair ring feeds back to the model.
  > **Amendment (2026-07-29, adversarial review):** four duplicate-identifier codes were added after
  > review found the shipped validators accepted ambiguous identifier sets (all follow the
  > `CRITERION_ID_DUPLICATED` pattern): `BacklogOrdering` gains `ITEM_ID_DUPLICATED` (the same
  > `itemId` at two ranks validated, breaking the total-order rule), `TestPlan` gains
  > `RISK_AREA_NAME_DUPLICATED` (duplicate risk-area names made `riskAreaRef` ambiguous), and
  > `ThreatModel` gains `ASSET_ID_DUPLICATED` and `THREAT_ID_DUPLICATED`. One rejecting and one
  > accepting fixture per new rule in the corresponding `Tamma.Core.Tests` type-test files.
- **D8 — tests live in `Tamma.Core.Tests` only, except the two `ContractBindingTests` entries.** 39-3 D7 /
  39-4 D8 settled that `Tamma.Core.Tests` takes no `ProjectReference` to `Tamma.ElsaServer` or
  `Tamma.Activities`. AC3's store round-trip therefore runs where the existing 39-11 store tests run
  (`tests/Tamma.Data.Tests` / the `Tamma.Activities.Tests` Testcontainers fixture), not in
  `Tamma.Core.Tests`. Keep the pure-validation half in Core.

## Task Breakdown

**Batch A** (steps 1–5), **Batch B** (steps 6–7), **both** (steps 8–9).

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Types/AcceptanceCriteria.cs`** — payload record
   `{ issueId, criteria[] { id, form, statement | { given, when, then }, verifiable } }`; `Validate`
   rules: ≥1 criterion (`NO_CRITERIA`), each with a non-empty id unique within the doc
   (`CRITERION_ID_DUPLICATED`), `form` in the closed set (`CRITERION_FORM_OUT_OF_VOCABULARY`),
   given/when/then all present when `form = given-when-then` (`GWT_INCOMPLETE`), non-empty statement
   otherwise (`CHECKLIST_ITEM_EMPTY`), `verifiable` true for every criterion
   (`CRITERION_NOT_INDEPENDENTLY_VERIFIABLE`), payload `issueId` non-empty (`ISSUE_ID_MISSING`);
   `ValidateWithContext` per D5; `RenderContract` naming the cell from D4; ≥1 valid + ≥1 invalid example
   per rule, each declaring its exact `ExpectedViolationCodes` (the registry loop at
   `DocumentTypeRegistryTests.cs:71-100` enforces exactness).

2. **CREATE `Types/BacklogOrdering.cs`** — `{ items[] { itemId, rank, rationale, value, effort } }`;
   rules per D7. Reuse nothing from `DependencyGraphCheck.cs` (no graph here).

3. **CREATE `Types/TestPlan.cs`** — `{ scope, riskAreas[] { name, rank, rationale }, strategyLines[]
   { description, coverageTarget, riskAreaRef }, environments[], entryCriteria[], exitCriteria[] }`;
   rules: risk areas ranked with a total order (`RISK_RANK_NOT_TOTAL_ORDER`), every strategy line maps to
   a declared risk area (`STRATEGY_LINE_UNMAPPED_RISK_AREA`) and names a coverage target
   (`STRATEGY_LINE_MISSING_COVERAGE_TARGET`), entry and exit criteria both non-empty
   (`ENTRY_CRITERIA_MISSING` / `EXIT_CRITERIA_MISSING`).

4. **CREATE `Types/ThreatModel.cs`** — `{ assets[] { id, name }, threats[] { id, assetRef, category,
   description, mitigation, residualRisk }, escalation? }`; rules: ≥1 asset and ≥1 threat, every threat
   references a declared asset (`THREAT_UNKNOWN_ASSET`), `category` in the D6 STRIDE enum
   (`THREAT_CATEGORY_OUT_OF_VOCABULARY`), every threat has a non-empty mitigation
   (`THREAT_MISSING_MITIGATION`) and a `residualRisk` in the `RiskLevel` enum
   (`RESIDUAL_RISK_OUT_OF_VOCABULARY`), and — the load-bearing rule — a threat with
   `residualRisk ∈ {high, critical}` and no `escalation` block fails with
   `UNMITIGATED_HIGH_RISK_WITHOUT_ESCALATION`.

5. **MODIFY `DocumentTypeKey.cs`, `DocumentTypeRegistry.cs`, `AcceptanceDefaults.cs`, and the two count
   pins (Batch A):** four `[Wire]` members (`acceptance-criteria`, `backlog-ordering`, `test-plan`,
   `threat-model`) appended at `DocumentTypeKey.cs:33`; four registrations appended to
   `s_registrations` (`DocumentTypeRegistry.cs:39`) with a comment naming this story, mirroring the
   39-3/39-4 comment style at `:22-26`; four new `AcceptanceRules` statics + four `For` arms per D1;
   `DocumentTypeKeyTests.cs:20` `Be(10)` → `Be(14)` and `DocumentTypeRegistryTests.cs:37`
   `HaveCount(10)` → `HaveCount(14)`, each with a one-line reason. **All in one commit** (C2).

6. **CREATE `Types/SprintPlan.cs` and `Types/UxSpec.cs` (Batch B, after 41-1a).** `SprintPlan`:
   `{ sprintId, capacity, committed[] { issueId, ownerRole, estimate }, carryOver[] { issueId,
   reason } }`; rules per D7 plus `ownerRole` parsed through `AgentRoleExtensions`
   (`OWNER_ROLE_UNKNOWN`) — which is why it needs 41-1a's `scrum_master`/`ux_designer` in the enum to
   express a realistic example. `UxSpec`: `{ flows[] { id, name, entryState, successState,
   errorStates[] }, screens[] { id, flowRef, a11yRequirements[] }, acceptanceCriteriaRefs[] }`; rules:
   every flow has entry + success + ≥1 error state (`FLOW_MISSING_ENTRY_STATE` /
   `FLOW_MISSING_SUCCESS_STATE` / `FLOW_MISSING_ERROR_STATE`), every screen references a declared flow
   (`SCREEN_UNKNOWN_FLOW`) and lists ≥1 a11y requirement (`SCREEN_MISSING_A11Y_REQUIREMENTS`);
   `ValidateWithContext` per D5.

7. **MODIFY the same four files again (Batch B):** two members, two registrations, two `For` arms, pins
   14 → 16.

8. **MODIFY `tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`** — six `Bindings` entries,
   one per D4 cell, each with `Parser` = `"{Type}DocumentType.Validate"` (mandatory: the universal pin at
   `:626-651` rejects any non-`DocumentType.Validate` authority not in `NonDocumentTypeResidual`) and
   `RequiredTokenGroups` = the JSON field names its template already shows. Batch A's four are checkable
   immediately against the shipped templates; Batch B's two are checkable once 41-1a's templates exist.
   Add **no** `IntentionallyUnbound` and **no** `NonDocumentTypeResidual` entry (C4).

9. **Run the gate:** `dotnet test` (`Tamma.Core.Tests` first — the registry contract loop at
   `DocumentTypeRegistryTests.cs:44-100` is the densest feedback), then `dotnet ef migrations
   has-pending-model-changes` (must stay clean; this story adds no column).

## Test Plan

NUnit + FluentAssertions. Pure-validation tests in `Tamma.Core.Tests`; the store round-trip on the
existing 39-11 Testcontainers fixture (D8).

- **`DocumentTypeKeyTests` / `DocumentTypeRegistryTests` (existing files).** AC1: `Parse` round-trips all
  six wire strings (`acceptance-criteria`, `backlog-ordering`, `sprint-plan`, `test-plan`,
  `threat-model`, `ux-spec`) — each throws `DOCUMENT.TYPE.UNKNOWN` today; `Resolve` returns an
  `IDocumentType` for each — each throws `DOCUMENT.TYPE.NOT_REGISTERED` today. AC4: the two count pins.
  The existing per-type contract loop (`:44-100`) then covers unique-key, deterministic-non-empty
  contract, and the exact-`ExpectedViolationCodes` example discipline for all six with **no edit** — that
  loop is the real quality gate and every new type must satisfy it. `Every_vocabulary_key_now_resolves_to_
  an_implementation` (`:113+`) is the C2 atomicity proof. **Covers AC1, AC4.**
- **Six `{Type}DocumentTypeTests` fixtures (NEW, `tests/Tamma.Core.Tests/Documents/Types/`).** AC2: one
  *rejecting* and one *accepting* fixture **per rule**, each asserting the specific violation code, not
  `IsValid == false`. Named counter-examples from the story: a `BacklogOrdering` with two items at the
  same rank → `RANK_DUPLICATED`; a `SprintPlan` whose committed estimates exceed capacity →
  `COMMITMENT_EXCEEDS_CAPACITY`; a `ThreatModel` with an unmitigated high-risk threat and no escalation →
  `UNMITIGATED_HIGH_RISK_WITHOUT_ESCALATION`. Plus: determinism of `RenderContract` (called twice, equal)
  and a JSON round-trip of the typed record through `DocumentJson.Options`. **Covers AC2.**
- **`AcceptanceDefaultsDriftTests` (existing file, extended).** AC5/D1: assert the exact
  `AcceptorRequirement` + `ReviewerSelection` for each of the six, so a type that *should* fall through to
  the base row does so on purpose and one that should not has its own arm. Assert the four
  no-longer-catch-all types do **not** equal `AcceptanceDefaults.Rules`. The existing `PanelRoster`
  pins at `:47/:55/:56` stay unchanged (this story adds no role). **Covers AC5.**
- **`DocumentTypesCrossDocumentValidationTests` (NEW, `Tamma.Core.Tests`).** D5: `ValidateWithContext`
  with an empty context is byte-identical to `Validate` for all six (the DIM default holds); with a
  populated context, `AcceptanceCriteria` rejects a criterion naming scope absent from the supplied
  `Decomposition` (`CRITERION_REFERENCES_UNPLANNED_SCOPE`) and `UxSpec` rejects a flow with no matching
  acceptance criterion (`FLOW_UNMAPPED_TO_ACCEPTANCE_CRITERION`). **Covers C6/D5.**
- **`NewDocumentTypeStoreRoundTripTests` (integration, extends the 39-11 store fixture).** AC3: for each
  of the six, mint a draft envelope via `DocumentEnvelope.CreateDraft`, persist through
  `IDocumentInstanceRepository.InsertAsync` (which re-validates against the registry before writing —
  `IDocumentInstanceRepository.cs:16-18`), read back through `ListByIssueAsync` and the lineage assembler,
  and assert `DocumentType`, `IssueId`, `SchemaVersion` and `BodyJson` survive. A negative case: a payload
  failing that type's rules is rejected with `DOCUMENT.STORE.INVALID_BODY` and **nothing** is persisted.
  **Covers AC3.**
- **`ContractBindingTests` (build gate, existing file).** AC6:
  `EveryBoundCell_TemplateStillCarriesEveryParserRequiredToken` (`:361`) green for all six new entries;
  `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` (`:626`) green with no
  new `NonDocumentTypeResidual` entry; `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` (`:681`) green
  with no new `IntentionallyUnbound` entry. **Covers AC6.**
- **`WorkflowInterfaceGraphTests` (build gate, existing file, NO edit).** AC7:
  `Declared_edge_count_is_pinned` (`:45`) stays at `HaveCount(16)`, and
  `Every_declared_produces_key_is_registered_or_pending` stays green. Leaving it untouched is the
  evidence for D2. **Covers AC7.**

## Risks & Mitigations

- **The registry's example loop is stricter than it looks.** `DocumentTypeRegistryTests.cs:88-99` requires
  an invalid example to emit **exactly** its declared `ExpectedViolationCodes` — not a superset. A rule
  that incidentally trips a second code (e.g. a `SprintPlan` fixture that both exceeds capacity *and*
  omits an owner role) fails. *Mitigation:* write one minimal fixture per rule, isolating the single
  violation; write the fixtures before the validators.
- **Six types is genuinely 5–6 days of validator + fixture work** (39-4's evidence: six types, 5–6 days,
  ~250 lines each). *Mitigation:* D3's two batches give a shippable midpoint; the two batches also
  de-risk the 41-1a coupling.
- **D1's five new `AcceptanceRules` statics could each be invalid.** *Mitigation:* the static-ctor loop at
  `AcceptanceDefaults.cs:119-121` calls `For` for every `DocumentTypeKey` at class load, so an invalid row
  is a hard, immediate, un-missable failure — write the rows first and run any Core test.
- **D1's `SprintPlan` reviewer is `product_owner`, not `scrum_master`, deliberately.** Choosing
  `scrum_master` would make this story depend on 41-1a for a *fifth* type. *Mitigation:* recorded as a
  41-6 follow-up — 41-6 may override via the per-document-type autonomy override without touching
  `AcceptanceDefaults`.
- **A future reader bumps `WorkflowInterfaceGraphTests.cs:45` "because a document type was added".**
  *Mitigation:* D2 and AC7 both say not to, and step 9's gate catches the mistake as a failing test either
  way (the pin would then be wrong by 6).

## Est. Effort

**5.5 days**, matching the story's 5–6 (39-4 shipped six types in 5–6).

| Step | Work | Days |
|---|---|---|
| 1–4 | Batch A: four payload records + validators + enums + examples | 2.0 |
| 5 | Batch A: key/registry/acceptance-defaults/pins | 0.25 |
| 6–7 | Batch B: `SprintPlan` + `UxSpec` + registration + pins | 1.0 |
| 8 | Six `ContractBindingTests` entries verified against templates | 0.25 |
| — | Six `{Type}DocumentTypeTests` fixtures (per-rule pairs) | 1.25 |
| — | Cross-document `ValidateWithContext` tests + store round-trip | 0.5 |
| 9 | Full-gate run + review polish | 0.25 |

## Blocks / Blocked by

- **Blocked by:** Epic 39 — **39-2** (registry, `DocumentTypeKey`, `IDocumentType`, envelope, drift
  tests), **39-3**/**39-4** (the type pattern this copies), **39-11** (store + lineage, for AC3). All
  landed.
- **Partially blocked by:** **41-1a** — `SprintPlan` needs `(scrum_master, plan-sprint)` and `UxSpec`
  needs `(ux_designer, author-ui-spec)` (role, action **and** prompt template). Batch A is unblocked
  today; Batch B is not (C3/D3).
- **Blocks:** **41-2** (`AcceptanceCriteria`), **41-3** (`BacklogOrdering`), **41-6** (`SprintPlan`, with
  41-1a), **41-13** (`TestPlan`), **41-19** (`ThreatModel`), **41-27** (`UxSpec`, with 41-1a).
  Transitively **41-15** (gated on 41-2).
- **Does not block:** 41-1c, 41-29, 41-4/41-5/41-7/41-8/41-9 and the whole prose family (they wait on
  41-1c and/or 41-1a, not on this).
- **Shares files with:** **41-1c** — `DocumentTypeKey.cs`, `DocumentTypeRegistry.cs`,
  `AcceptanceDefaults.cs`, and the two count pins. Whichever merges second rebases the arithmetic.
