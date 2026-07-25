# Implementation Plan — Story 41-4: Roadmap Shaping Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `RoadmapShapingWorkflow` (DefinitionId `roadmap-shaping`) is a
**thin binding** over `document-lifecycle` in the landed producer shape: it reads the accepted
`BacklogOrdering` (41-3) plus supplied strategic inputs, dispatches `document-lifecycle` with
`documentType = "prose"`, `kind = roadmap`, `audience = stakeholder` and the producer cell
`(product_owner, plan-roadmap)`, routes the typed exit, and exposes the accepted roadmap text. Zero
`Finish`, zero `llm-call` dispatch, zero validate/retry plumbing, exactly one `DispatchWorkflow` targeting
`document-lifecycle`.

Alongside the binding: the **rewritten** `plan-roadmap` prompt template (the shipped one emits a
`Plan`-shaped task array with `files[]` — see Corrections); a `ROADMAP.*` DCB event family; the
roadmap lineage anchor (shared with 41-3); the `WorkflowDocumentInterface` edge + its three pin edits; the
`ContractBindingTests` `Bindings` entry — **the first bound prose cell in the codebase**; and the
structure/execution suites. The `prose` type and the `Audience` field are **41-1c's**, not this story's.

## Pre-Reading

- `docs/stories/epic-41/story-41-4/41-4-roadmap-shaping.md` — the story (ACs are source of truth, less the Corrections below)
- `docs/stories/epic-41/README.md` — rules 1–5; the rule-1 Corrected note ("prose has no mechanism in code")
- `docs/stories/epic-41/story-41-1/41-1c-prose-documents-and-audience-tags.md` — **the enabler this story cannot exist without**: the `prose` `DocumentTypeKey`, `ProseDocumentType` (`{kind, audience, title, body}`, body unvalidated), `DocumentEnvelope.Audience` + `DocumentInstance.Audience` + migration, the audience/kind vocabularies, D2's acceptance row, D3's one-contract-many-kinds rule
- `docs/stories/epic-41/story-41-3/implementation-plan.md` — the upstream producer; **its D2 anchor
  (`BuildAnchor(repository, backlogScope)`) is a shared contract this story consumes, not re-derives**
- `docs/stories/epic-41/story-41-2/implementation-plan.md` — D7's shared `EmitDomainLifecycleEventActivity`; the `[ResumeBehavior]` correction; the rule-1 clause (f) two-edit lockstep
- **THE RECIPE:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`; `PlanGenerationWorkflow.cs` for the consumes-an-accepted-document variant
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs` — the store read seam (fail-closed)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/LifecycleBindingHelper.cs`, `CreationBindingHelper.cs`
- `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/plan-roadmap.md` — the cell being rewritten (front matter `variables: role, workItemJson, contextFindings, conventions`, `enableTools: true`, `maxTokens: 8192`)
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-387` — `GetReviewActionForRole` **throws for `TechWriter`**; `DocumentLifecycleWorkflow.cs:1199` calls it unguarded. This is why D5 exists.
- **The gates this story must move:** `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)` + the `reconciled` array `:102-123`; `ContractBindingTests.cs:82` `Bindings`, `:626` `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual`, `:655` `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode`, `:681` classify-or-fail; `TaxonomyDriftBuildTests.cs:125`, `:460`; `ResumableStandardStructuralTests.cs:108/:159/:238/:266`

## Corrections to the story

1. **AC3's `[ResumeBehavior(Both)]` is wrong and would fail the build.** As in 41-2/41-3: `Both` requires a
   canonical suspend node (`LifecycleBookmarks.CanonicalSuspendActivities`) in the binding's **own** graph
   (`ResumableStandardStructuralTests.cs:159` + the inverse honesty check at `:205`); a thin binding owns
   none, because the accept gate suspends inside the dispatched child. **Declare
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.**
2. **The shipped `plan-roadmap.md` produces a `Plan`, not a roadmap.** Its body says "Break the work item
   into discrete, ordered tasks, where each task is a roadmap milestone" and outputs
   `{"tasks":[{"id","description","files":[{"path","action"}],"dependencies","complexity","testing"}],
   "totalComplexity","estimatedDuration"}` — a **file-level implementation plan**, complete with
   create/modify file actions, for a document the story describes as "themes × horizons with rationale" in
   **prose**. This is the single largest gap in the batch: the template must be rewritten from a JSON task
   emitter to a markdown prose author. In scope; moves the estimate.
3. **The story's `consumes: [BacklogOrdering, Findings, stakeholder inputs]` is not readable as written.**
   Every document read is `(tenantId, issueId)`-anchored (`IDocumentInstanceRepository.cs:40-50`), and a
   `BacklogOrdering` is written under 41-3's **synthetic anchor**, not a real issue id. This story must
   consume it through `BacklogBindingHelper.BuildAnchor(repository, backlogScope)` — the same string, from
   the same helper. "Stakeholder inputs" have no store representation at all and are a caller-supplied input
   string (D3).
4. **A roadmap is not issue-scoped, so it needs its own lineage anchor.** `DocumentInstance.IssueId` is a
   required non-null string (`:37`) and the only read key. D2 defines
   `roadmap:{repository}:{horizonScope}`, computed by the same normalisation 41-3 uses.
5. **The story's review stage may need 41-1a, which its Dependencies do not name.** 41-1c D2 sets prose's
   default acceptance row to a **`tech_writer` single reviewer** — and
   `RolePhaseMap.GetReviewActionForRole` **throws `ArgumentOutOfRangeException` for `TechWriter`**
   (`:376-387`), with `DocumentLifecycleWorkflow.cs:1199` calling it unguarded, so such a run fails at
   runtime until 41-1a adds the arm (41-1a AC3). D5 resolves this **without** taking the dependency: this
   story pins a `product_owner` reviewer for `kind=roadmap` via the per-document-type override, which
   `GetReviewActionForRole` already serves (`ProductOwner => ReviewScope`). 41-1a remains a *soft* upgrade,
   not a gate.
6. **Rule-1 clause (f) is a two-edit lockstep and the epic README names only one.** Besides
   `WorkflowInterfaceGraphTests.cs:45`, the same file's
   `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:96`) asserts bidirectionally against
   the hardcoded `reconciled` array (`:102-123`). Omitting the new id fails the build.
7. **This is the first *bound* prose cell, and two universal pins have never seen one.**
   `ContractBindingTests.UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual`
   (`:626`) is satisfied by `ProseDocumentType.Validate`; but
   `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` (`:655`) currently encodes "prose output ⇒
   unbound" as a *classification rationale* (the three prose entries at `:290-300` are justified as free
   text). Binding a prose cell does not break that pin — the pin constrains `IntentionallyUnbound`, and this
   cell moves into `Bindings` from nowhere — but the two categories now overlap conceptually, and step 6
   must add a comment saying so, or the next reader will assume prose can never be bound.
8. **Rule-3/rule-4 reachability.** The accept gate publishes and suspends; 39-17/39-19/39-20 are fail-closed
   stubs, so the story's "roadmap is typically a human-accepted artifact … expressed as an always-escalate
   policy class" is expressible in the *rules* today but nothing consumes the resulting escalation
   interactively. Tests inject the decision through the 39-8 resume statics.

## Design Decisions

- **D1 — New DefinitionId `roadmap-shaping`; greenfield, no call site moves.** Nothing dispatches
  `(product_owner, plan-roadmap)` today (repo-wide grep: zero `.cs` references outside `AgentAction.cs:27` /
  `RolePhaseMap.cs:54`), so the `Bindings` entry is purely additive and no `IntentionallyUnbound` entry
  moves. Inputs: `repository`, `tenantId`, `horizonScope` (e.g. `2026-H2`), `backlogScope` (to locate the
  upstream ordering), `strategicInputs` (free text), `acceptanceRulesJson?`. Outputs: `status`, `outcome`,
  `documentId`, `roadmapMarkdown`, `roadmapAnchor`.
- **D2 — Lineage anchor `roadmap:{repository}:{horizonScope}`, from the shared normalisation.** Correction 4.
  Deterministic, so 39-10 re-entry and any consumer recompute it from inputs alone. It is written into the
  existing required `IssueId` column — no schema change (the `Audience` column is 41-1c's migration, not
  this story's). Implemented in `RoadmapBindingHelper.BuildAnchor` delegating to the same segment transform
  41-3's `BacklogBindingHelper.BuildAnchor` uses, so the two anchor families are provably consistent.
- **D3 — The consumes side is one store read plus one caller-supplied string, both behind `FreshRun`.** One
  `FetchLatestAcceptedDocumentActivity` for `(BacklogBindingHelper.BuildAnchor(repository, backlogScope),
  "backlog-ordering")` — **41-3's helper, called, not re-derived** (Correction 3) — fail-closed, so a
  roadmap is still authorable with no accepted ordering (unlike 41-6, whose story demands a hard fail).
  `strategicInputs` is a plain input string. Both are folded into the DECLARED `contextFindings` carrier.
- **D4 — `feedbackVariableName = "contextFindings"`; the cell already declares it.** Unlike 41-3, no
  front-matter variable needs adding: `plan-roadmap.md` declares
  `role, workItemJson, contextFindings, conventions`. The consumed ordering + strategic inputs go into
  `contextFindings` and repair/revise notes land in the same carrier (the 39-15 render-drop lesson: an
  undeclared producer variable is silently dropped at render).
- **D5 — Reviewer pinned to `product_owner` for `kind=roadmap`, so 41-1a is NOT a gate.** Correction 5.
  41-1c D2's prose default is a `tech_writer` reviewer, which throws today
  (`RolePhaseMap.cs:376-387`, called unguarded at `DocumentLifecycleWorkflow.cs:1199`). Rather than block on
  41-1a, this story supplies an explicit `acceptanceRulesJson` default naming `product_owner` — an arm that
  already resolves (`ProductOwner => ReviewScope`) and the right lens for a roadmap anyway (scope, not prose
  craft). When 41-1a lands, switching to a `tech_writer` or two-reviewer panel is a **rules change, not a
  code change** (rule 3: policy, never an if-else).
- **D6 — The template is rewritten from JSON tasks to prose; front matter unchanged except `enableTools`.**
  Precedent 39-15 D7. The body instructs: markdown, themes × horizons, one rationale paragraph per theme,
  explicit "not now / why" section, and — per 41-1c D3 — the *envelope* fields (`kind`, `audience`, `title`,
  `body`) are the contract while the per-kind shape guidance lives here in the cell. `enableTools: true` →
  `false`: a roadmap is a judgement over supplied context, and leaving tools on invites unbudgeted repo
  scans (the file-level `files[]` output the old template asked for is exactly what is being removed).
  `maxTokens: 8192` stays. Variable list unchanged, so **no keyset gate moves** —
  `ConventionSeedDriftTests` / `SystemPromptsTests` / `PromptFileLoaderTests` key on `(role, action)` and on
  the four required front-matter *keys*, never on the variable list's contents
  (`PromptFileLoader.cs:122`).
- **D7 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` + `ComputeReEntryPositionActivity` keyed on the
  D2 anchor, no allowlist entry.** Per Correction 1. The position gates the upstream read and the
  `ROADMAP.STARTED` emission.
- **D8 — The `ROADMAP.*` family rides 41-2's shared `EmitDomainLifecycleEventActivity`.** This story ships
  only `RoadmapEvents.cs` (`ROADMAP.STARTED` / `.DRAFTED` / `.ACCEPTED` / `.FAILED`). If 41-2 has not
  landed, carry a local copy and delete it on merge.
- **D9 — Audience is set once, at dispatch, from a constant.** `audience = stakeholder`, `kind = roadmap`,
  both from 41-1c's vocabularies (never string literals in the workflow — the enums'
  `ToWire()`), so an out-of-vocabulary value is a compile error rather than a validation failure.

## Implementation Steps

1. **Precondition check (no code).** 41-1c merged and compiling: `DocumentTypeKeyExtensions.Parse("prose")`
   succeeds, `DocumentTypeRegistry.Resolve("prose")` returns `ProseDocumentType`, `DocumentEnvelope.Audience`
   and `DocumentInstance.Audience` exist with their migration applied, the `kind`/`audience` enums carry
   `roadmap` and `stakeholder`, and `AcceptanceDefaults.For(DocumentTypeKey.Prose)` returns 41-1c D2's row.
   Confirm 41-3 has landed (its `BacklogBindingHelper.BuildAnchor` is D3's input) and 41-2's shared emitter
   is in tree (else D8's fallback). A gap blocks steps 4–7 — file it, do not work around it.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/RoadmapEvents.cs`** — the four constants; tags
   `repository` / `tenantId` / `correlationId` (= the D2 anchor) / `horizonScope` / `audience`.

3. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/RoadmapBindingHelper.cs`** — pure,
   Elsa-free, total, fail-closed: `BuildAnchor(repository, horizonScope)` (D2, delegating to the shared
   segment transform), `BuildContextFindings(orderingJson, strategicInputs)` (D4 carrier composer),
   `ProjectRoadmapMarkdown(documentJson)` (the prose `body` string, `""` on unreadable). Reuse
   `LifecycleBindingHelper.ReadLifecycleResult`/`IsAccepted` and
   `CreationBindingHelper.BuildFailureDetail` verbatim; call — do not copy —
   `BacklogBindingHelper.BuildAnchor` for the upstream read key.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RoadmapShapingWorkflow.cs`** (D1/D2/D3/D7/D9) —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. Graph: `ReadInputs` → `ComputeReEntryPosition` →
   `ReadPositionStage` → `FreshRun` → `EmitRoadmapStarted` → `FetchBacklogOrdering` → `DispatchLifecycle`
   → `ReadLifecycleExit` → `LifecycleAccepted` → `EmitDrafted`/`EmitAccepted` | `EmitFailed` →
   `ExposeOutput` (single terminal region; **no `Finish`**). Dispatch input:

   ```csharp
   ["documentType"]          = "prose",
   ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
   ["producerAction"]        = AgentAction.PlanRoadmap.ToWire(),
   ["producerVariablesJson"] = /* { workItemJson, contextFindings, conventions:"" } */,
   ["feedbackVariableName"]  = "contextFindings",                 // D4 — a DECLARED carrier
   ["documentKind"]          = ProseKind.Roadmap.ToWire(),        // D9, 41-1c vocabulary
   ["audience"]              = ProseAudience.Stakeholder.ToWire(),// D9
   ["issueId"] = anchor, ["correlationId"] = anchor,              // D2
   ["tenantId"] / ["acceptanceRulesJson"]                         // D5's default rules
   ```

   `WaitForCompletion = new(true)`. `FlowDecision` id set exactly `{FreshRun, LifecycleAccepted}`.
   **Lockstep:** the `documentKind`/`audience` input key names are 41-1c's to define on
   `DocumentLifecycleWorkflow`'s input contract (`:169-202` today reads neither) — agree them once with the
   41-1c owner; if 41-1c chose to carry them inside `producerVariablesJson`/the payload instead, follow that
   and delete these two keys.

5. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed`) — add
   `("roadmap-shaping", [BacklogOrdering], Prose, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs`** — bump `:45`
   by exactly one with the reason in the comment, **and** add `"roadmap-shaping"` to the `reconciled` array
   `:102-123` (Correction 6).

6. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Prompts/product_owner/plan-roadmap.md`** (D6, Correction 2) —
   body rewritten from a JSON task emitter to a markdown prose author; `enableTools: true` → `false`;
   variable list and the other two required keys unchanged.

7. **MODIFY the drift gates.** `ContractBindingTests.cs`: add
   `[("product_owner","plan-roadmap")] = new("ProseDocumentType.Validate", [ … 41-1c envelope token groups
   … ])`, with a comment naming `Tamma.Core/Documents/Types/Prose.cs` as the shape authority **and**
   recording Correction 7 (prose can now be bound; the three prose entries in `IntentionallyUnbound` at
   `:290-300` remain unbound for a different reason — they have no document type at all).
   `TaxonomyDriftBuildTests.cs`: add `"RoadmapShapingWorkflow"` to `ExpectedContributingWorkflows` (`:125`).
   Verify (do not pre-edit) `MinExpectedDispatchPairs` (`:110`) and
   `EveryConcreteWorkflow_IsIntrospectableOrAllowListed` (`:397`).

8. **CREATE the test suites** — `RoadmapShapingWorkflowStructureTests.cs`, `RoadmapBindingHelperTests.cs`,
   `RoadmapShapingLifecycleExecutionTests.cs`. See Test Plan.

9. **Full run.** `dotnet test` green; `dotnet ef migrations has-pending-model-changes` clean (the `Audience`
   migration is 41-1c's and must already be applied).

## Data & Migrations

None **in this story**. Prose rows are `document_instances` with 41-1c's `Audience` column (its migration).
`ROADMAP.*` and `DOCUMENT.*` ride the existing emitter → drain → `domain_events` path.
`has-pending-model-changes` must be clean; if it is not, the pending change belongs to 41-1c and is filed
there.

## Events

- **Emits (new constants, this story):** `ROADMAP.STARTED` (fresh runs only), `.DRAFTED` (data
  `themeCount`, `consumedOrderingId`), `.ACCEPTED` (data `documentId`, `audience`), `.FAILED` (detail names
  the typed outcome wire). Tags `repository` / `tenantId` / `correlationId` (= the D2 anchor) /
  `horizonScope` / `audience`.
- **Emitted by the machinery this binding wires in:** the `DOCUMENT.*` family, `APPROVAL.*`,
  `ESCALATION.TRIGGERED`.
- **Consumes:** none at runtime (the `BacklogOrdering` is a store read, not an event read).

## Test Plan

NUnit + FluentAssertions; Testcontainers for the execution suite (the shared 39-6/39-10 fixture).

- **`RoadmapShapingWorkflowStructureTests`** — the rule-1 clause (a)–(f) set, `TaskCreation`-shaped: builds;
  DefinitionId `roadmap-shaping`; threads `TenantId`; **zero** `Finish`; **exactly one** `DispatchWorkflow`,
  literal id `document-lifecycle`; **zero** targeting `llm-call`; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables; `ScanLifecycleBindingDispatches()`
  contains `(product_owner, plan-roadmap)` attributed to this workflow; `MaterializeDispatchInput` yields
  `documentType == "prose"`, the `roadmap` kind, the `stakeholder` audience and
  `feedbackVariableName == "contextFindings"`; one `ComputeReEntryPositionActivity`; one
  `FetchLatestAcceptedDocumentActivity`; `FlowDecision` id set exactly `{FreshRun, LifecycleAccepted}`;
  `[ResumeBehavior(LatestStateReEntry)]`; **no `Wait*` activity** (Correction 1). **Covers AC1, AC3.**
- **`RoadmapBindingHelperTests`** — `BuildAnchor` determinism, tenant/scope folding, hostile-character
  normalisation, and **agreement with `BacklogBindingHelper.BuildAnchor`'s transform** (both anchors
  normalise identically — the D2 consistency claim); `BuildContextFindings` with ordering present / absent /
  malformed; `ProjectRoadmapMarkdown` on a valid prose body and on unreadable JSON (`""`, never throws);
  `BuildFailureDetail` names each reachable outcome wire. **Covers AC2 (composition half).**
- **`RoadmapShapingLifecycleExecutionTests`** (Testcontainers) —
  (a) **happy path:** a seeded accepted `BacklogOrdering` under 41-3's anchor → scripted valid prose draft →
  review approve → `Accept` resume → `status=completed`, markdown projected; store asserts the accepted
  prose instance with `Audience = stakeholder` and its `Review` rows; replay asserts both event families.
  **Covers AC1, AC2.**
  (b) **prose is not schema-checked (41-1c AC2, proven at the consumer):** a draft whose body has headings
  in an unexpected order still validates and accepts; a whitespace-only body is rejected with 41-1c's named
  violation code and loops repair/revise, the notes arriving through `contextFindings` (D4). **Covers AC1.**
  (c) **degraded consumes:** no accepted `BacklogOrdering` exists → the fetch reports `Found=false`, the
  roadmap is still produced from `strategicInputs` alone, and `ROADMAP.DRAFTED` records
  `consumedOrderingId = null`. **Covers AC2 (D3's fail-closed posture).**
  (d) **review over prose (AC1's "reviewed by a `Review`"):** the review stage produces a `Review` whose
  `ParentDocumentId` is the prose document, with the **`product_owner`** reviewer of D5 — and a control case
  asserting that configuring a `tech_writer` reviewer today fails at `GetReviewActionForRole`, pinning
  Correction 5 so the 41-1a upgrade is visible rather than assumed.
  (e) **validation exhaustion:** always-empty-body stub → typed `ValidationExhausted` escalation with
  lineage, `ROADMAP.FAILED` naming the outcome, `status=escalated`, no error terminal.
  (f) **re-entry:** crash after acceptance → short-circuits with the SAME `documentId`, exactly one
  `DOCUMENT.ACCEPTED` and one `ROADMAP.ACCEPTED`, zero extra upstream reads; crash mid-review → resumes at
  review of the same revision. **Covers AC3.**
- **Drift gates (self-verifying, steps 5/7)** — `ContractBindingTests` (incl. both universal pins at `:626`
  and `:655`), `TaxonomyDriftBuildTests`, `WorkflowInterfaceGraphTests` (count **and** `reconciled`) and
  `ResumableStandardStructuralTests` (declares, **no** allowlist entry) green in the same commit.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; prose reviewed by a `Review` | 4, 6, 7 (D1/D6) | StructureTests clause (a)–(f); ExecutionTests (b)(d) |
| 2 — consumes `BacklogOrdering`/`Findings` as inputs | 4 (D3) | ExecutionTests (a)(c); HelperTests composition (Correction 3's anchor caveat recorded) |
| 3 — resumable per the standard, no allowlist entry | 4 (D7) | StructureTests declaration + no-`Wait*`; ExecutionTests (f); `ResumableStandardStructuralTests` |

## Dependencies & Sequencing

- **Blocked by:** **41-1c** — hard, and it is the *whole* mechanism. `DocumentTypeKey` has exactly 10
  members and none is `prose` (verified); `DocumentEnvelope` (11 properties) and `DocumentInstance` (17
  properties) carry **no** `Audience` field; there is no kind/audience vocabulary anywhere in `src/` (the
  only `Audience` in the data layer is `ChannelOutboxMessage.Audience`, 39-18 channel plumbing — unrelated).
  A prose roadmap is unpersistable on the **human path too** until 41-1c lands.
  Also **41-3** (the consumed `BacklogOrdering` and its anchor helper) and **Epic 39** (39-6/39-7/39-8/
  39-10/39-11 — all landed).
- **Soft-blocked by 41-2** — D8's shared emitter only.
- **NOT blocked by 41-1a**, by D5's deliberate choice (Correction 5). The `(product_owner, plan-roadmap)`
  cell exists (`AgentAction.cs:27`, `RolePhaseMap.cs:54`, prompt file present) and `ProductOwner` already
  has a review arm. 41-1a is a **soft upgrade**: it enables a `tech_writer` prose reviewer as a rules
  change.
- **Blocks:** nothing hard. It is one of the eight prose consumers 41-1c unblocks and, per the epic README,
  is Wave 3.
- **Lockstep:** 41-1c's `ProseDocumentType` `Contract` const + the `documentKind`/`audience` input-key names
  on `DocumentLifecycleWorkflow` ↔ step 4's dispatch keys ↔ step 6's template ↔ step 7's `Bindings` token
  groups. **41-3's `BuildAnchor` is a shared contract** — call it, never re-derive it.
- **Stubbed, not pulled in:** 39-17, 39-19, 39-20 (Correction 8).
- **Sequencing within the story:** 1 → 2/3 (parallel) → 4 → 5/6/7 (parallel) → 8 → 9.

## Risks & Mitigations

- **41-1c is the largest risk and it is entirely upstream.** Four of its deliverables are load-bearing here
  (type, `Audience` field + migration, both vocabularies, the acceptance row). Mitigation: step 1 is a real
  gate; steps 2–3 and the helper tests are 41-1c-independent; the dispatch key names are pinned in one
  review (step 4's lockstep note) so a shape change is a mechanical rename caught by a red build.
- **The template rewrite is a change of output medium, not of format** (JSON tasks with `files[]` →
  markdown prose). Highest regression risk in the batch. Mitigation: prose is *unvalidated* by design
  (41-1c AC2), so there is no validator to satisfy — the review stage is the quality gate, and (b) pins
  both directions of "no forced structure".
- **The prose contract is one contract for ten kinds (41-1c D3), so per-kind guidance lives only in the
  cell.** A weak cell body silently yields a weak roadmap with nothing failing. Mitigation: the review stage
  with a `product_owner` (scope) lens is the check; (d) proves the review actually runs over the prose.
- **Correction 7's classification overlap.** A future reader may take
  `UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` to mean prose cells cannot be bound. Mitigation:
  step 7's comment states the distinction explicitly in the file that would mislead.
- **Anchor drift between 41-3 and 41-4.** Two helpers computing "the same" string is a classic divergence.
  Mitigation: one shared normalisation, and `RoadmapBindingHelperTests` asserts agreement with
  `BacklogBindingHelper` directly.
- **"Done" is narrower than the story's prose.** The story's "acceptance still routed to a human by default
  policy" is a rules statement; with 39-17/39-19 unlanded nothing routes it. Mitigation: Correction 8; no AC
  above depends on the orchestrator.
- **Story-vs-code tensions:** Corrections 1–8 all resolve in favour of the code. Corrections 2 and 5 change
  the work; 3, 4 and 7 change the design; 1 and 6 are mechanical.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1c/41-3 precondition verification + dispatch-key agreement | 0.25 |
| 2 | `RoadmapEvents` (+ 41-2 emitter reuse or local copy) | 0.25 |
| 3 | `RoadmapBindingHelper` (anchor, carrier composer, prose projection) | 0.5 |
| 4 | The binding workflow | 1.0 |
| 5 | Registry seed row + the two `WorkflowInterfaceGraphTests` edits | 0.25 |
| 6 | Prompt-template rewrite: JSON task emitter → markdown prose author (Correction 2) | 0.75 |
| 7 | `ContractBindingTests` (incl. the first bound prose cell + Correction 7 comment) + `TaxonomyDriftBuildTests` | 0.5 |
| 8 | Structure + helper + Testcontainers suites (a)–(f) | 1.25 |
| 9 | Full-suite green, review polish | 0.25 |
| **Total** | | **5.0** |

**Est. Effort: 5 days.** The story file says 3 days; that predates three verified facts — the template is a
from-scratch rewrite across output media (Correction 2, +0.75 d), the document needs its own lineage anchor
and must consume 41-3's (Corrections 3/4, +0.5 d), and this is the **first bound prose cell**, so the
`ContractBindingTests` classification is new territory rather than a copied row (Correction 7, +0.25 d). The
story's `## Estimated Effort` section is left at 3 days and this plan is the record of the delta.

## Blocks / Blocked by

- **Blocked by:** 41-1c (hard — the `prose` type, the `Audience` envelope/store field + migration, the
  kind/audience vocabularies, the prose acceptance row); 41-3 (hard — the consumed `BacklogOrdering` and its
  anchor helper); Epic 39 stories 39-6, 39-7, 39-8, 39-10, 39-11 (all landed); 41-2 (soft — the shared
  emitter).
- **Blocks:** nothing hard.
- **Not blocked by:** 41-1a — by D5's deliberate reviewer choice; 41-1a is a soft upgrade that would let a
  `tech_writer` review the prose as a rules change. Not blocked by the tenant-aware scheduled-trigger seam
  (this workflow is caller-triggered, not scheduled — unlike its sibling 41-5).
