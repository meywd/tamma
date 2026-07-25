# Implementation Plan — Story 41-10: System Design Document Workflow

## Scope & Deliverable

When this story is done, a multi-surface feature gets a **reviewed, accepted, stored `Design`** before
implementation planning instead of an improvised design inside the plan step. A new
`SystemDesignWorkflow` (`DefinitionId = "system-design"` — free today, no workflow claims it) is a THIN
BINDING over `document-lifecycle`: it assembles the context (issue work item, accepted `Findings`, an
optional `AcceptanceCriteria` once 41-2 lands, a `context-gathering` scan), dispatches
`document-lifecycle` with `documentType = "design"` and the **new** `(architect, design-system)` producer
cell (minted by 41-1a), forwards a `validationContextJson` that turns on the facet rule, and routes the
typed exit. Zero `Finish`, zero `llm-call`, zero parsing. Review is the 39-7 panel over the draft
(architect + senior-dev + security lenses via the existing `GetReviewActionForRole`); accept is 39-8's
gate, which for `design` already defaults to a **human acceptor** (`AcceptanceDefaults.For(Design)` →
`s_humanAcceptorRules`, 39-13 D4). The accepted `Design` is readable by `plan-generation` and by 41-9's
ADR seeding through the 39-11 store.

`(architect, plan-system-design)` — `plan-generation`'s `Plan` producer — is **byte-unchanged**, and the
three `design-*` facet cells stay unbound; the API-contract / data-model / integration concerns become
*sections* of the one `Design`.

## Pre-Reading

- `docs/stories/epic-41/story-41-10/41-10-system-design-document.md` — the story (its two embedded
  Corrected notes are verified accurate; see Corrections for what is still wrong)
- `docs/stories/epic-41/README.md` — rule 1 clauses (a)–(f); **41-10 is one of the six stories that carry
  clause (f) as an explicit AC** (its AC5)
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — Scope 2 mints
  `(architect, design-system)` **and its `Prompts/architect/design-system.md` template**; Scope 5 / AC7
  list the count pins it moves
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DesignProposalWorkflow.cs` — the landed single-surface
  `Design` producer; **this is the sibling to copy**, including its `sessionId`-as-decision-session
  threading and its pre-ACCEPT delivery hook
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs:32-33`, `:147-148` — the ONE
  landed precedent for `validationContextJson` → `IDocumentType.ValidateWithContext` (D3 depends on it)
- `apps/tamma-elsa/src/Tamma.Core/Documents/IDocumentType.cs:35-43` — the `ValidateWithContext` default
  interface member (additive; every existing type falls back to context-free `Validate`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Design.cs` — `Design`/`DesignAlternative`, the five
  violation codes (`MALFORMED_PAYLOAD`, `MISSING_SUMMARY`, `NO_ALTERNATIVES`,
  `ALTERNATIVE_MISSING_TRADEOFFS`, `RECOMMENDATION_UNKNOWN_ALTERNATIVE`), and the `Contract` const
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:123-133` — `For(Design)` returns
  the human-acceptor row; `_ => Rules` is the catch-all
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the
  `(architect, propose-design)` entry (`DesignDocumentType.Validate`), the `(architect,
  plan-system-design)` entry (`PlanDocumentType.Validate`) that must stay byte-unchanged, and the
  coverage guard
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the
  reference structure-test shape
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45` (`HaveCount(16)`),
  `:102-123` (`reconciled`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs:110`, `:125-150`,
  `:460`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs`,
  `ReviewerSelectionHelper.cs:61-70` (the 7-role document roster)

## Corrections to the story

1. **AC5's `[ResumeBehavior(Both)]` would FAIL the 39-10 gate — declare `LatestStateReEntry`.** Clause (b)
   of `ResumableStandardStructuralTests` requires a `Both`/`BookmarkSuspend` declaration to be backed by a
   canonical suspend-activity node **in this workflow's own graph**. A thin binding has none — the accept
   gate suspends inside the dispatched `document-lifecycle` child. Every landed binding declares
   `LatestStateReEntry`, including the sibling `DesignProposalWorkflow.cs:38`, which has the same human
   accept gate this story wants. Same correction as 41-9 and 41-8.
2. **AC3's `DESIGN_FACET_MISSING` cannot be "a story-local rule" and must not go in
   `DesignDocumentType.Validate`.** `DesignDocumentType` is the SHARED authority for documentType
   `design`, and `design-proposal` (the landed single-surface producer) is validated by the same instance.
   An unconditional facet rule would invalidate every `design-proposal` draft — a regression on a landed
   workflow. There is exactly one seam for a conditional rule: `IDocumentType.ValidateWithContext`
   (39-15 D3), which the lifecycle forwards a non-empty `validationContextJson` to
   (`DocumentLifecycleWorkflow.cs:338-343`), and which `TestSpecDocumentType` already uses for its
   cross-document task-ID ring. **D3 below routes the facet rule through that seam, gated on a
   `requireFacets` flag this binding sets and `design-proposal` does not.** `Design.cs` gains one
   overridden member and one violation constant; `Validate` is untouched, so `design-proposal` is
   byte-behaviour-stable.
3. **AC1 understates the `ContractBindingTests` work: the entry depends on a template 41-1a writes.**
   `(architect, design-system)` gets a `Bindings` entry with authority `DesignDocumentType.Validate` and
   the token groups `"summary"`, `"alternatives"`, `"name"`, `"tradeoffs"`, `"recommendation"` — plus, for
   this story, `"recommendedAlternativeId"`, which `Design.Validate` hard-requires
   (`Design.cs:106-111`: the id must match a listed alternative) but which the incumbent
   `(architect, propose-design)` entry does **not** pin. 41-1a authors
   `Prompts/architect/design-system.md`; if that template does not carry these tokens the build fails the
   day this binding dispatches. **This is a named lockstep with 41-1a, not an assumption** — see
   Dependencies.
4. **The facet vocabulary does not exist anywhere and this story mints it.** AC3 speaks of "a facet
   (API contract / data model / integration)" as if it were a modelled thing. `Design` has
   `summary`/`alternatives`/`recommendation`/`recommendedAlternativeId`/`constraintEvaluation` and no
   facet member (`Design.cs:26-33`). D3 adds an additive, optional `facets` member — additive so
   `design-proposal` payloads round-trip unchanged, and optional so the rule is context-gated.
5. **The story's two embedded Corrected notes are accurate — do not re-litigate them.** Verified:
   `(architect, plan-system-design)` is `plan-generation`'s produce cell
   (`PlanGenerationWorkflow` dispatches it; `DocumentTypeRegistry.cs:151` maps `plan-generation` → `Plan`,
   non-provisional; `ContractBindingTests` pins `PlanDocumentType.Validate` with the
   `"tasks"|"steps"` + `"fileMap"|"files"|"filesToModify"` groups). `(architect, propose-design)` is
   `design-proposal`'s (`ContractBindingTests`, `DocumentTypeRegistry.cs:155`). The three `design-*`
   templates are facet-scoped in their shipped bodies. All three claims hold.
6. **Rule-3/rule-4 reachability.** 39-17/39-19/39-20 have not landed, so the human accept
   `AcceptanceDefaults.For(Design)` demands is published-and-suspended but undecided end-to-end except by
   a test-side resume. AC4's "read by a `plan-generation` run in an integration test" IS claimable (it is
   a store read, not a routing hop). The story's ACs should say which half is claimed.

## Design Decisions

- **D1 — `DefinitionId = "system-design"`; a new workflow, sibling to `design-proposal`, not a rewrite of
  it.** `design-proposal` stays the **single-surface** path (one requirement, one design, its own bespoke
  delivery-to-issue hook); `system-design` is the **multi-surface** path. Two DefinitionIds, two cells,
  one document type, one validator — which is the whole point of the doc-type-parameterized lifecycle.
  Inputs: `issueId`, `repository`, `issueNumber`, `workItemJson`, `contextIds`, `findingsJson`,
  `constraints`, `conventions`, `tenantId`, plus additive `sessionId?`, `acceptanceRulesJson?`.
  Outputs: `status`/`outcome`/`documentId`/`designJson`/`alternativeCount`/`sessionId`.
- **D2 — the binding is `DesignProposalWorkflow` minus the delivery hook.** Graph: `ReadInputs` →
  `ComputeReEntryPosition` (`documentType = "design"`) → `ReadPositionStage` → `FreshRun` `FlowDecision`
  → (True) `EmitSystemDesignStarted` + `GatherContext` (`DispatchWorkflow("context-gathering")`) +
  `FetchConsumedFindings` (`FetchLatestAcceptedDocumentActivity`, type `findings`) → join →
  `DispatchLifecycle` → `ReadLifecycleExit` (`LifecycleBindingHelper.ReadLifecycleResult`) →
  `DesignAccepted` `FlowDecision` → emit → `ExposeOutput`. Exactly TWO `DispatchWorkflow` sites
  (`context-gathering`, `document-lifecycle`), zero `llm-call`, zero `Finish`. No delivery workflow: an
  accepted system design is consumed downstream by `plan-generation` and 41-9, not posted as an issue
  comment (that is `design-proposal`'s job and its `DesignDeliveryWorkflow` is not reused).
- **D3 — the facet rule rides `ValidateWithContext`, gated by a flag only this binding sets.** In
  `Tamma.Core/Documents/Types/Design.cs`:
  - additive optional member `[JsonPropertyName("facets")] IReadOnlyList<DesignFacet>? Facets` where
    `DesignFacet = { name, status, content?, notApplicableReason? }` and `name ∈ {api-contract,
    data-model, integration}` (a `[Wire]`-tagged closed enum with its own drift test), `status ∈
    {present, not-applicable}`;
  - one new violation constant `DesignFacetMissing = "DESIGN_FACET_MISSING"`;
  - an override of `ValidateWithContext(payload, validationContextJson)` that runs `Validate` first, then
    — **only when the context deserializes to `{"requireFacets": true}`** — asserts each of the three
    facet names is either present with non-empty content or explicitly `not-applicable` with a non-empty
    reason. Empty context ⇒ identical to today ⇒ `design-proposal` unaffected.
  The binding sets `validationContextJson = "{\"requireFacets\":true}"`. This keeps ONE type, ONE
  validator authority, ONE `ContractBindingTests` parser name, and satisfies AC3's "rejected by a rule"
  with a real executable check rather than a review-panel hope.
- **D4 — the `Bindings` entry pins six token groups, one more than `propose-design`.** Per Correction 3:
  `"summary"`, `"alternatives"`, `"name"`, `"tradeoffs"`, `"recommendation"`,
  `"recommendedAlternativeId"`. The last is deliberately stricter than the incumbent entry because
  `Design.Validate` hard-fails without it; the incumbent entry is **not** tightened by this story (that
  would be an unrelated behaviour change to a landed producer — file it, do not fold it in). The facet
  fields are NOT token groups: they are context-gated, so a template that omits them is legal for
  `design-proposal` and would make the shared-cell contract lie.
- **D5 — `feedbackVariableName` must name a variable 41-1a's template DECLARES.** This binding sets
  `feedbackVariableName = "contextFindings"` (the carrier `create-tasks`/`decompose-issue` use). 41-1a's
  `Prompts/architect/design-system.md` front matter must therefore include `contextFindings` in
  `variables`. If it does not, repair/revise notes are silently dropped at render time — the 39-15
  render-drop lesson. This is a **named lockstep**, not an assumption; if 41-1a ships a different carrier
  name, this binding changes to match and the structure test's `feedbackVariableName` assertion moves with
  it.
- **D6 — `SYSTEM_DESIGN.*` is a four-member family.** `STARTED`/`DRAFTED`/`ACCEPTED` per the story, plus
  `SYSTEM_DESIGN.FAILED` (LOUD, error status) on `rejected`/`escalated` — the house convention
  (`DecompositionEvents.StatusForEvent`, `DocumentEvents.StatusForEvent`) that a degraded terminal is
  never a success row. New `Tamma.Activities/SystemDesign/SystemDesignEvents.cs` +
  `EmitSystemDesignEventActivity.cs`.
- **D7 — acceptance policy is inherited, not hardcoded.** `AcceptanceDefaults.For(DocumentTypeKey.Design)`
  already returns `s_humanAcceptorRules` (39-13 D4), so a system design already requires a human acceptor
  by default — the story's "contract/boundary-affecting designs always escalate" is *already* the default
  posture, and a caller wanting the panel roster passes `acceptanceRulesJson`. **This story does not edit
  `AcceptanceDefaults`** — that file is per document type, and `design` is shared with `design-proposal`.
- **D8 — the lockstep set, enumerated.** (i) `DocumentTypeRegistry.BuildSeed` +=
  `new WorkflowDocumentInterface("system-design", new[]{ DocumentTypeKey.Findings }, DocumentTypeKey.Design, false)`
  — `Consumes` lists **`Findings` only**; the `AcceptanceCriteria` edge waits for 41-1b/41-2 so this
  story stays off their critical path; (ii) `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned`
  `HaveCount(16)` → `+1` (AC5's explicit clause-(f) requirement); (iii) that test's `reconciled` array
  += `"system-design"`; (iv) `ContractBindingTests.Bindings` += D4's entry; (v)
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` += `"SystemDesignWorkflow"`; (vi) NO
  `ResumableStandardStructuralTests` allowlist entry. **The taxonomy count pins
  (`AgentActionTests.cs:38` `Be(80)`, `RolePhaseMapTests.cs:64` `HaveCount(80)`) are 41-1a's, moved once
  for all fifteen of its cells — this story must not touch them.** Likewise the document-type vocabulary
  pins are 41-1b/41-1c's, and `design` is already registered.

## Implementation Steps

1. **Precondition gate (no code).** Verify `AgentAction.DesignSystem` exists, `(architect, design-system)`
   passes `RolePhaseMap.IsRoleEligibleForPhase`, and `Prompts/architect/design-system.md` exists and
   declares `contextFindings` among its `variables` (D5) and carries D4's six token groups. Any gap is a
   41-1a defect — file it against 41-1a, do not patch the taxonomy from here.
2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Design.cs`** (D3) — `DesignFacetName` enum +
   `DesignFacet` record + optional `Facets` member + `DesignFacetMissing` constant + the
   `ValidateWithContext` override + a facet-bearing example. `Validate` is untouched. `RenderContract()`
   gains an optional-facets paragraph (deterministic ordering — 39-16 diffs this output in CI).
3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/SystemDesign/SystemDesignEvents.cs` +
   `EmitSystemDesignEventActivity.cs`** (D6), copied from the decomposition pair.
4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/SystemDesignBindingHelper.cs`** —
   pure, Elsa-free: `BuildFacetValidationContext()` (the `{"requireFacets":true}` literal),
   `CountAlternatives(documentJson)`, `BuildFailureDetail(exit)`, `BuildProducerVariables(...)`.
   `ReadLifecycleResult`/`IsAccepted` come from `LifecycleBindingHelper`.
5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SystemDesignWorkflow.cs`** per D1/D2,
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, dispatch input: `documentType = "design"`,
   `producerRole = AgentRole.Architect.ToWire()`, `producerAction = AgentAction.DesignSystem.ToWire()`,
   `feedbackVariableName = "contextFindings"`, `validationContextJson =
   SystemDesignBindingHelper.BuildFacetValidationContext()`, plus the `issueId`/`correlationId`/
   `tenantId`/`acceptanceRulesJson`/`sessionId` passthroughs.
6. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** — D8(i).
7. **MODIFY the pins** — D8(ii)–(v), each with a one-line reason naming this story. **Verify
   `ContractBindingTests`'s `(architect, plan-system-design)` entry is byte-unchanged in the diff**
   (AC1's explicit requirement) and that no `Bindings` entry is added for `design-api-contract` /
   `design-data-model` / `design-integration` (AC3).
8. **CREATE the tests** — `DesignFacetValidationTests` (Tamma.Core.Tests),
   `SystemDesignWorkflowStructureTests`, `SystemDesignBindingHelperTests`,
   `SystemDesignLifecycleExecutionTests` (see Test Plan).
9. **Green the suite** — full `dotnet test` + `has-pending-model-changes` clean.

## Data & Migrations

None. `Design` payloads are JSONB in 39-11's document tables; the additive `facets` member needs no schema
change. `has-pending-model-changes` stays clean.

## Events

- **Emits:** `SYSTEM_DESIGN.STARTED` (fresh runs only), `.DRAFTED`, `.ACCEPTED`, `.FAILED` (LOUD) — tags
  `issueId`, `tenantId`, `correlationId`, `documentId`; `.ACCEPTED` data carries `alternativeCount` and
  the facet name→status map.
- **Emitted by the machinery this story wires in:** the `DOCUMENT.*` family incl.
  `DOCUMENT.REVIEW_PANEL_STARTED`/`_COMPLETED`/`_UNDECIDABLE` (39-7), `APPROVAL.*`, `ESCALATION.TRIGGERED`.
- **Consumes:** none at runtime.

## Test Plan

- **`DesignFacetValidationTests` (Tamma.Core.Tests, pure)** — the fixture matrix AC2 asks for, each
  failing on exactly ONE rule: no alternatives ⇒ `NO_ALTERNATIVES`; alternative without trade-offs ⇒
  `ALTERNATIVE_MISSING_TRADEOFFS`; recommendation naming an unlisted alternative ⇒
  `RECOMMENDATION_UNKNOWN_ALTERNATIVE`; empty summary ⇒ `MISSING_SUMMARY`. Plus the D3 additions: with
  `{"requireFacets":true}`, a body missing `data-model` ⇒ `DESIGN_FACET_MISSING`; a body marking it
  `not-applicable` with a reason ⇒ valid; a body marking it `not-applicable` with **no** reason ⇒
  `DESIGN_FACET_MISSING`. **Regression pin: the SAME facet-less body validates clean with an EMPTY
  context** (`design-proposal`'s path unchanged) — this is the test that makes Correction 2 safe.
  **Covers AC2, AC3.**
- **`SystemDesignWorkflowStructureTests`** — the `TaskCreationWorkflowStructureTests` clause set:
  `DefinitionId == "system-design"`; threads `TenantId`; no retry-plumbing variables; **exactly two
  `DispatchWorkflow` sites, ids `GatherContext` (`context-gathering`) and `DispatchLifecycle`
  (`document-lifecycle`)**; **zero** `llm-call` dispatch; **zero `Finish`**;
  `ComputeReEntryPositionActivity` + `FetchLatestAcceptedDocumentActivity` present; declares
  `LatestStateReEntry`; no `Wait*` node; `ScanLifecycleBindingDispatches()` contains
  `(SystemDesignWorkflow, DispatchLifecycle, architect, design-system)`; `MaterializeDispatchInput`
  yields `documentType == "design"`, `feedbackVariableName == "contextFindings"` and a non-empty
  `validationContextJson`. **Negative pin (AC1/AC3):** no `DispatchWorkflow` in the assembly targets a
  `design-api-contract`/`design-data-model`/`design-integration` producer cell, and
  `ScanLifecycleBindingDispatches()` contains no pair whose action is `plan-system-design` attributed to
  `SystemDesignWorkflow`. **Covers rule-1 clauses (a)–(e), AC1, AC3 (no-facet-workflows half).**
- **`SystemDesignBindingHelperTests`** — `CountAlternatives` on a valid/unreadable payload;
  `BuildFailureDetail` names each reachable outcome wire; `BuildFacetValidationContext` is the exact
  literal the `Design` override parses (round-trip pin, so the two halves cannot drift).
- **Pin tests (self-verifying)** — `WorkflowInterfaceGraphTests` (bumped count + `system-design` in
  `reconciled`); `ContractBindingTests` (new entry satisfied by 41-1a's template; the
  `plan-system-design` entry still asserts `PlanDocumentType.Validate`); `TaxonomyDriftBuildTests`;
  `ResumableStandardStructuralTests` green with **no** allowlist entry. **Covers AC1, AC5.**
- **`SystemDesignLifecycleExecutionTests` (Testcontainers, shared 39-6/39-10 fixture)** — (a) happy path:
  valid faceted draft → panel review approve → `Accept` resume → accepted `Design` retrievable by
  `issueId` through `IDocumentInstanceRepository` (AC4 half 1); (b) facet ring: draft missing a facet →
  `DESIGN_FACET_MISSING` → repair/revise → accept (proves the context reaches
  `ValidateWithContext` through the live lifecycle, not just the unit); (c) **AC4 half 2**: a
  `plan-generation` run for the same issue reads the accepted `Design` through the store (and a 41-9 ADR
  binding reads it as its seed); (d) crash after acceptance → fresh dispatch short-circuits to
  `Complete`, exactly one `DOCUMENT.ACCEPTED` and one `SYSTEM_DESIGN.ACCEPTED`; (e) control: a
  `design-proposal` run in the same fixture still accepts a facet-less design (the landed sibling is not
  regressed). **Covers AC4, AC5 (re-entry half), Correction 2's safety.**

> Per the story's closing note, depth / alternative quality / "is this the right design" are NOT acceptance
> criteria and get no test — they are the review panel's job (39-7) and the accept gate's.

## Risks & Mitigations

- **41-1a's template is on this story's critical path and is written by someone else.** If
  `design-system.md` lacks `contextFindings` (D5) or D4's token groups, this story fails at step 1.
  Mitigation: step 1 is a real gate; the two requirements are stated here as a lockstep contract and
  should be mirrored into 41-1a's AC2 before either starts. Fallback: this story rewrites the template
  and 41-1a's AC8 (fail-loud loader over the enlarged grid) still holds.
- **Touching `Design.cs` risks regressing the landed `design-proposal`.** Mitigation: `Validate` is not
  modified at all; the facet rule is an override that no-ops on empty context; the regression pin (unit
  (e) + execution (e)) asserts the sibling's behaviour explicitly in both suites.
- **`RECOMMENDATION_UNKNOWN_ALTERNATIVE` is a strict rule and the new cell is unproven.** A model that
  omits `recommendedAlternativeId` loops through repair/revise. Mitigation: D4 pins the token in the
  contract so the template must instruct it; the `Design.Contract` const already documents the rule; the
  execution test (b) proves the repair ring closes.
- **Edge-pin collision.** `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is moved by 41-9
  (+1), this story (+1) and 41-11 (+2). Mitigation: rebase the number, keep the comment; the pin is
  deliberately a conscious edit, one per producing workflow.
- **Two `Design` producers per issue collide on the 39-11 latest-accepted read.** `design-proposal` and
  `system-design` both write documentType `design` for the same `issueId`, and the store read scopes by
  `(issueId, documentType)` with no producer filter (FILED by 39-15 D2). Mitigation: adopt 39-15's fix —
  scope this binding's lifecycle issue id with `CreationBindingHelper.ScopeIssueId(issueId,
  "system-design")` — and add execution scenario (e') asserting a `design-proposal` design for the same
  issue is not returned as this binding's latest-accepted. **This is not optional; without it the two
  designs overwrite each other's re-entry slice.**

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1a precondition + template verification | 0.25 |
| 2 | `Design.cs` facets + `ValidateWithContext` + contract/examples | 1.0 |
| 3 | `SystemDesignEvents` + emitter | 0.25 |
| 4 | `SystemDesignBindingHelper` (pure) | 0.25 |
| 5 | `SystemDesignWorkflow` binding | 0.75 |
| 6–7 | Registry edge + pin bumps + byte-unchanged verification | 0.25 |
| 8 | Core facet tests + structure + helper + Testcontainers suites | 1.5 |
| 9 | Full-suite green, review polish | 0.25 |
| **Total** | | **4.5** (story estimate: 4–5 days — consistent) |

## Blocks / Blocked by

- **Blocked by — hard, cannot start:**
  - **41-1a** — mints `AgentAction.DesignSystem`, puts it in `AgentRole.Architect`'s eligible set
    (`RolePhaseMap.cs:65-77`), and ships `Prompts/architect/design-system.md`. Blocking on **both**
    execution paths: a human assignee still needs a cell to bind, and `PromptFileLoader` refuses to boot
    on a taxonomy cell with no file.
  - **Epic 39: 39-2/39-4** (`Design` registered), **39-6**, **39-7** (the panel), **39-8**, **39-10**,
    **39-11**, **39-15** (the `ValidateWithContext` seam D3 rides) — **all landed**, verified in tree.
- **NOT blocked by:** **41-1b** (reuses the existing `Design` type — 41-10 is absent from the README's
  41-1b table, correctly) and **41-1c** (produces a typed document, not prose).
- **Blocked by — for end-to-end claimability only:** **39-17**, **39-19**, **39-20** (nothing decides at
  the human accept gate `AcceptanceDefaults.For(Design)` demands).
- **Optional consumer edge, deliberately deferred:** **41-2** (`AcceptanceCriteria`, itself gated on
  **41-1b**) — the story's `consumes` list names it; D8(i) declares `Consumes = [Findings]` only so 41-10
  stays off 41-1b's critical path. Add the second consumed key when 41-2 lands.
- **Blocks / feeds:** **41-9** (an accepted `Design` is its ADR seed — the edge is one-directional and
  neither story blocks the other), `plan-generation` (reads the accepted `Design` through 39-11 with no
  code change), and any future facet-scoped story that binds `design-api-contract` / `design-data-model` /
  `design-integration` (this story deliberately leaves all three unbound and asserts it).
- **Shared edit — `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`):** 41-9 (+1),
  41-10 (+1), 41-11 (+2), and every other Epic 41 producer story. Rebase, don't fight.
