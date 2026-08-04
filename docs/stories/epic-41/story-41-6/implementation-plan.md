# Implementation Plan — Story 41-6: Sprint Planning Workflow

## Scope & Deliverable

When this story is done a new Elsa workflow `SprintPlanningWorkflow` (DefinitionId `sprint-planning`) is a
**thin binding** over `document-lifecycle` in the landed producer shape: it reads the accepted
`BacklogOrdering` (41-3) plus stated capacity and prior-sprint carry-over, dispatches `document-lifecycle`
with `documentType = "sprint-plan"` and the producer cell `(scrum_master, plan-sprint)`, routes the typed
exit, and exposes the accepted commitment. Zero `Finish`, zero `llm-call` dispatch, zero validate/retry
plumbing, exactly one `DispatchWorkflow` targeting `document-lifecycle`.

Alongside the binding: a `SPRINT.*` DCB event family; the sprint lineage anchor (shared normalisation with
41-3/41-4/41-5); a **typed loud exit** when no accepted `BacklogOrdering` exists (AC2's "hard-fails loud"
reconciled with rule 1's zero-`Finish` requirement); the `WorkflowDocumentInterface` edge + its three pin
edits; the `ContractBindingTests` `Bindings` entry; and the structure/execution suites. The `SprintPlan`
**type is 41-1b's** and the `scrum_master` role + `plan-sprint` cell are **41-1a's** — neither is this
story's.

## Pre-Reading

- `docs/stories/epic-41/story-41-6/41-6-sprint-planning.md` — the story (ACs are source of truth, less the Corrections below)
- `docs/stories/epic-41/README.md` — rules 1–5; ~~the Dependencies table row naming **41-6:45** as an AC that fails at the AC level today~~ *[2026-08-01: the README rows at `:507` and `:511` still name **41-6:45** as an AC-level failure gated on 39-20, and `:507` still cites the stale `Program.cs:445-447`. Both are superseded by this story's Amendment A1 — AC3 is narrowed, and 39-20 is not what it was waiting for. The README is epic-owned; the correction is recorded here, not made there. 41-7:49 and 41-8:46 in the same rows are **not** in this story's scope and may or may not have the same defect — do not assume they do.]*
- `docs/stories/epic-41/story-41-1/41-1a-agent-taxonomy-extension.md` — mints `AgentRole.ScrumMaster` + `(scrum_master, plan-sprint)`; its **D2** (scrum_master on *neither* review panel) and **D3** (`LegacyRoleAliases` removal polarity) are load-bearing here
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — the `SprintPlan` type + its **D1** (acceptance posture: "plausibly wrong for at least `SprintPlan` — a scrum_master/product_owner acceptor")
- `docs/stories/epic-41/story-41-3/implementation-plan.md` — the upstream producer; **its D2 anchor helper is a shared contract this story consumes, not re-derives**
- `docs/stories/epic-41/story-41-2/implementation-plan.md` — D7's shared `EmitDomainLifecycleEventActivity`; the `[ResumeBehavior]` correction; the rule-1 clause (f) two-edit lockstep
- **THE RECIPE:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs`; `PlanGenerationWorkflow.cs` for the consumes-an-accepted-document variant
- `apps/tamma-elsa/src/Tamma.Activities/Documents/FetchLatestAcceptedDocumentActivity.cs` — **fail-closed by design**: absent upstream ⇒ `Found=false`, it never throws. This is why D4 exists.
- ~~`apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:230-242` — `LegacyRoleAliases`, incl. `scrum_master → product_owner` (`:239`), the entry 41-1a removes; `:376-387` — `GetReviewActionForRole`, 7 arms, **throws for any role not listed**~~ *[2026-08-01: alias removed by 41-1a (`RolePhaseMap.cs:288`); `GetReviewActionForRole` is now `:433-450` with **9** arms and still throws for `scrum_master`. Correction 11/13.]*
- ~~`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:128-133` — the `_ => Rules` catch-all a new type silently falls through~~ *[2026-08-01: superseded — the switch is `:207-223` and `sprint-plan` has its own arm at `:216`. Read `:141-147` (the `s_humanProductOwnerRules` row) instead.]*
- ~~`apps/tamma-elsa/src/Tamma.Api/Program.cs:445-447` — `ITaskAudienceResolver` is stubbed fail-closed by `InitiatorOnlyTaskAudienceResolver`. **This is why AC3 cannot be claimed** (Correction 3).~~ *[2026-08-01: the registration is `Program.cs:410-411`, and it is no longer why — AC3 is narrowed, not deferred. Read `docs/stories/epic-44/story-44-4/44-4-…md:35`, `:58-89` for the owner of the correct behaviour.]*
- **`apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/plan-sprint.md`** — the SHIPPED v2 producer template. Read its front matter (`:1-6`) **before** writing `BuildProducerVariables`; the replacement D5 exists because this plan's original guess did not match it.
- **`apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceFloors.cs`** + `Tamma.Core/Actions/ActionCatalog.Descriptors.cs:238-256` — the two surfaces besides `AcceptanceDefaults` that pin `sprint-plan` to a human acceptor (replacement D6).
- **The gates this story must move:** ~~`WorkflowInterfaceGraphTests.cs:45`~~ `:52` (`HaveCount(18)`) + the `reconciled` array `:109`; `ContractBindingTests.cs` — **move** `(scrum_master, plan-sprint)` out of `PendingProducerCells` (`:797-806`) into `Bindings`, and out of `TemplateExampleConformanceTests.ConformingUnboundCells` (Correction 10); `TaxonomyDriftBuildTests.cs:125`, `:460`; `ResumableStandardStructuralTests.cs:108/:159/:238/:266`. **Re-derive every one of these line numbers before use** (Correction 11).

## Corrections to the story

1. **AC4's `[ResumeBehavior(Both)]` is wrong and would fail the build.** As in 41-2/41-3/41-4/41-5: `Both`
   requires a canonical suspend node from `LifecycleBookmarks.CanonicalSuspendActivities` in the binding's
   **own** graph (`ResumableStandardStructuralTests.cs:159` + the inverse honesty check at `:205`); a thin
   binding owns none, because the accept gate suspends inside the dispatched child. **Declare
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.**
2. **AC2's "hard-fails loud if none exists" collides with rule 1 clause (c) (zero `Finish`) and with the
   read seam's fail-closed contract.** `FetchLatestAcceptedDocumentActivity` **never throws** — a missing
   upstream document yields `Found=false`, `DocumentJson="{}"`. So "hard-fail" cannot be an exception, and
   it cannot be a `Finish` terminal (the structure test forbids one). D4 resolves it as a **typed loud
   exit**: a `FlowDecision` on the fail-closed `Found` flag routing to a `SPRINT.PLANNING.FAILED` emission
   plus the single `ExposeOutput` region with `status="failed"` and a named detail — loud in the DCB stream
   and in the outputs, structurally identical to every other non-accept exit. This is an *additional*
   `FlowDecision` beyond the `{FreshRun, LifecycleAccepted}` pair the siblings pin; it routes a typed value,
   never raw LLM output, which is exactly what 39-12 D2 sanctions.
3. ~~**AC3 ("Committed items produce role-scoped Task View entries via 39-20") is NOT achievable and must not
   be claimed.** `ITaskAudienceResolver` is registered as `InitiatorOnlyTaskAudienceResolver`
   (`Tamma.Api/Program.cs:445-447`) — a fail-closed stub that admits only the issue initiator — and 39-19
   (the Task View itself) has not landed either (`AgentOfflineChatRelay` is the registered
   `IOrchestratorChatRelay`, `:448-451`, and refuses every message). The epic README names **41-6:45**
   explicitly as one of three ACs that "fail at the AC level, not merely in prose". **This plan does not
   plan AC3.** What it *does* deliver is the precondition: the accepted `SprintPlan` payload carries an
   `ownerRole` per committed item (41-1b's domain rule) and the acceptance publishes the standard
   `AcceptanceRequest` — so when 39-19/39-20 land, routing is a consumer of this document, not an edit to
   this binding.~~
   **[RIGHT CONCLUSION, WRONG REASON — REPLACED 2026-08-01. AC3 is narrowed in the story; see the story's
   Amendment A1. "Not achievable *yet*" implied 39-19/39-20 would make it achievable. They would not.]**

3. **AC3 is narrowed, and the plan now PLANS it.** A sprint commitment has no 39-8 bookmark and no pending
   decision, so a Task-View row for it could never be cleared by any resume — landing 39-19/39-20 would only
   make a permanently-stuck row buildable (39-19 AC3,
   `docs/stories/epic-39/story-39-19/39-19-orchestrator-chat-primary-user-interface-and-task-view.md:33`).
   The correct consumer is a **tracker** mutation, owned by **Story 44-4**
   (`POST /api/iterations/{id:guid}/apply-plan` → `WorkItem.IterationId`, raising no Task-View entry —
   44-4 AC9/AC10, `docs/stories/epic-44/story-44-4/44-4-…md:58-89`); 44-4's Out of Scope (`:124`) refuses to
   reword Epic 41's file, so the narrowing lands in 41-6.

   **What this plan now delivers for AC3** (two clauses, both able to fail):
   (a) the accepted body deserializes into the shipped `Tamma.Core.Documents.Types.SprintPlan`
   (`Types/SprintPlan.cs:35-41`) and every `Committed` entry carries an `AgentRole`-parsable `ownerRole` —
   already enforced by `SprintPlanDocumentType.Validate` (`COMMITTED_ITEM_MISSING_OWNER_ROLE` `:69`,
   `OWNER_ROLE_UNKNOWN` `:72`), asserted here on an **accepted** row plus a negative case;
   (b) a source-level test that no file this story creates references `ITaskAudienceResolver`,
   `ChannelAudience`, or a literal with the ordinal prefix `"TASK."` (never `Contains` — `AGENT.TASK.*`
   exists, `Tamma.Api/Services/Agents/AgentTrailEventTypes.cs`).

   *Stub state, for the record only — it is no longer the reason:* `ITaskAudienceResolver` is registered as
   `InitiatorOnlyTaskAudienceResolver` at **`Tamma.Api/Program.cs:410-411`** (not `:445-447`), and
   `IOrchestratorChatRelay` as `AgentOfflineChatRelay` at **`:414-415`** (not `:448-451`). Both line
   citations in the struck text were stale; see Correction 11.
4. **A `SprintPlan` is not issue-scoped, so it needs its own lineage anchor.** `DocumentInstance.IssueId` is
   a required non-null string (`:37`) and the only store read key (`IDocumentInstanceRepository.cs:40-50`).
   D2 defines `sprint:{repository}:{sprintKey}`. The upstream `BacklogOrdering` is likewise read through
   **41-3's** `BacklogBindingHelper.BuildAnchor(repository, backlogScope)` — called, never re-derived.
5. **The reviewer cannot be a `scrum_master`.** 41-1a **D2**'s default position is that `scrum_master` and
   `project_manager` join **neither** panel ("they produce and accept, they do not critique documents"), and
   `RolePhaseMap.GetReviewActionForRole` throws for any unlisted role (`:376-387`), called unguarded at
   `DocumentLifecycleWorkflow.cs:1199`. D6 pins a **`product_owner`** reviewer, which already resolves
   (`ProductOwner => ReviewScope`) and is the right lens for a commitment. Recorded because "the scrum
   master's plan is reviewed by the scrum master" is the intuitive-but-broken default.
6. **The `scrum_master` alias removal is a behaviour change this story inherits.** `LegacyRoleAliases`
   currently maps `scrum_master → product_owner` (`RolePhaseMap.cs:239`), so **today**
   `AgentRoleExtensions.Parse("scrum_master")` returns `ProductOwner`. 41-1a AC1/AC5/D3 own the removal and
   its data migration; this story must not ship against the alias (a binding that resolved
   `scrum_master` to `product_owner` would silently dispatch a PO cell). Step 1 gates on 41-1a's AC1 being
   true.
7. **Rule-1 clause (f) is a two-edit lockstep and the epic README names only one.** Besides
   `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)`, the same file's
   `Seeded_declarations_are_provisional_except_reconciled_bindings` (`:96`) asserts bidirectionally against
   the hardcoded `reconciled` array (`:102-123`).
8. **The story's "the accepted plan seeds Task View assignments per committed item's owner-role" and
   "Over-commit beyond capacity is a validator rejection" split cleanly.** The second **is** achievable —
   it is 41-1b's `SprintPlanDocumentType.Validate` domain rule and this story proves it end-to-end. The
   first is ~~Correction 3's unachievable half~~ **[2026-08-01: not "unachievable" — wrongly shaped. The
   correct consumer is 44-4's tracker apply seam; see the replacement Correction 3.]**. They are stated
   together in the story; they are not equally deliverable.

### Corrections added 2026-08-01 (scoping round against the tree at commit `6429691`)

9. **D5's prompt front matter did not match the file 41-1a shipped, and the mismatch would have failed
   SILENTLY.** Full detail in the replacement D5. The load-bearing point: `revisionNotes` appears in
   **zero** of the 123 files under `apps/tamma-elsa/src/Tamma.Api/Prompts/`, `PromptStoreService.Render`
   substitutes only placeholders present in the body (`Services/PromptStore/PromptStoreService.cs:555-589`),
   and an unrendered variable is dropped with no error — so a revise turn would re-prompt blind, produce a
   plausible second draft, and look like a working loop. That is strictly worse than a crash. Fix: edit
   `Prompts/scrum_master/plan-sprint.md` v2 → v3 to declare **and render** the carrier, supply it as an
   empty string on the first turn, and pin the dispatch value against the template body with a test.

10. **The `(scrum_master, plan-sprint)` cell is already CLASSIFIED — step 6 is a MOVE, not an ADD.** The
    cell sits in `ContractBindingTests.PendingProducerCells`
    (`apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs:797-806`) with 41-1b's
    token groups and the reason *"no compiled dispatch site exists until 41-6 lands its workflow"*, and the
    comment above it (`:793-796`) records that `TemplateExampleConformanceTests.ConformingUnboundCells`
    holds the template to the `SprintPlan` shape **until 41-6 binds it**. Landing this story therefore
    moves the cell out of `PendingProducerCells` into `Bindings` and out of `ConformingUnboundCells` —
    adding a `Bindings` entry while leaving the pending one in place will trip the classification gate.
    Also note the template edit in Correction 9 must keep `ConformingUnboundCells`'s worked-example
    assertion green in the same commit.

11. **Stale line citations throughout this plan (the tree moved; the claims mostly did not).** Corrected:

    | Cited as | Actually | Claim still true? |
    |---|---|---|
    | `Program.cs:445-447` (`ITaskAudienceResolver` stub) | `Program.cs:410-411` | yes |
    | `Program.cs:448-451` (`AgentOfflineChatRelay`) | `Program.cs:414-415` | yes |
    | `RolePhaseMap.cs:230-242` `LegacyRoleAliases` incl. `scrum_master` at `:239` | alias **removed**; the comment recording the removal is `RolePhaseMap.cs:288` | **no — see Correction 13** |
    | `RolePhaseMap.cs:376-387` `GetReviewActionForRole`, 7 arms | `RolePhaseMap.cs:433-450`, **9** arms (`tech_writer` and `ux_designer` joined in 41-1a) | throws for `scrum_master`: yes |
    | `DocumentLifecycleWorkflow.cs:1199` calls `GetReviewActionForRole` | `DocumentLifecycleWorkflow.cs:1235` calls `RolePhaseMap.GetPanelActionForRole(role, typeKey)`, which delegates to `GetReviewActionForRole` for every non-`triage-decision` type (`RolePhaseMap.cs:496-499`) | yes, one hop deeper |
    | `AcceptanceDefaults.cs:128-133` `_ => Rules` catch-all | `AcceptanceDefaults.cs:207-223`; `sprint-plan` no longer reaches it (`:216`) | superseded |
    | `WorkflowInterfaceGraphTests.cs:45` `HaveCount(16)` | `:52` `HaveCount(18)` | pin still moves, new baseline |
    | `WorkflowInterfaceGraphTests.cs` `reconciled` array `:102-123` | array starts `:109`; the bidirectional test is `:103` | yes |

    Re-derive every remaining line number in the Pre-Reading and Implementation Steps before use; they were
    written against a tree that is ~2 weeks and several landed stories old.

12. **D6's "acceptor per the autonomy dial" was false and the always-escalate class was inexpressible.**
    See the replacement D6 and the story's Amendment A2. Three independent human-acceptor pins, each with a
    green test: `AcceptanceDefaults.cs:216`/`:144-147`; `ActionCatalog.Descriptors.cs:253` at
    `AutonomyDial.AlwaysHuman`; `AcceptanceFloors.cs:69-95` as a non-lowerable floor.

13. **Correction 6 is CLOSED, not pending — 41-1a landed the alias removal.** `AgentRole.ScrumMaster` exists
    (`Tamma.Core/Agents/AgentRole.cs:23`), `AgentAction.PlanSprint` exists (`Agents/AgentAction.cs:132`),
    the primary-action row is `RolePhaseMap.cs:229`, and the `scrum_master → product_owner` entry is gone
    from `LegacyRoleAliases` (the comment at `RolePhaseMap.cs:288` records the 41-1a D3 removal). The
    `AgentRole` enum has **11** members, not 8, and `RolePhaseMap.ValidRoles`' own doc says 11
    (`RolePhaseMap.cs:239`). Step 1's gate (i) is therefore already satisfied and becomes a five-minute
    verification, not a schedule risk. **D1's supporting greps are now false and must not be repeated:**
    `plan-sprint`/`PlanSprint` has many hits (e.g. `AgentAction.cs:132`,
    `ActionCatalog.Descriptors.cs:199-201`, `ContractBindingTests.cs:797`), and the produce cell is minted.

14. **41-3 has NOT landed and is the real gate.** `BacklogBindingHelper.BuildAnchor` — which D3 and
    Correction 4 pin as "called, never re-derived" — has **no source file**: there is no
    `BacklogBindingHelper.cs` under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/` (18 helpers,
    none named that), and 41-3's own plan is where it is created
    (`docs/stories/epic-41/story-41-3/implementation-plan.md:143-144`). 41-3 is `drafted`
    (`docs/sprint-status.yaml:633`). **41-6 waits on 41-3 LANDING, not on 41-3 being scheduled.** See
    Dependencies & Sequencing.

## Design Decisions

- **D1 — New DefinitionId `sprint-planning`; produce cell `(scrum_master, plan-sprint)` from 41-1a.**
  Greenfield: repo-wide grep for `plan-sprint` / `PlanSprint` returns **zero** hits in `.cs` and `.md`, and
  `project_manager`/`scrum_master` are not `AgentRole` members (the enum has exactly 8). So the `Bindings`
  entry is purely additive and no `IntentionallyUnbound` entry moves. Inputs: `repository`, `tenantId`,
  `sprintKey` (e.g. `2026-S14`), `backlogScope` (to locate the upstream ordering), `capacityJson` (stated
  capacity per owner-role), `priorSprintKey?` (carry-over source), `acceptanceRulesJson?`. Outputs:
  `status`, `outcome`, `documentId`, `sprintPlanJson`, `sprintAnchor`.
- **D2 — Lineage anchor `sprint:{repository}:{sprintKey}`, shared normalisation.** Correction 4.
  Deterministic, so 39-10 re-entry, the carry-over read and any consumer (41-5's `SprintPlan` read)
  recompute it from inputs alone. Written into the existing required `IssueId` column — no schema change.
  Implemented in `SprintBindingHelper.BuildAnchor` delegating to the same segment transform 41-3's helper
  uses, and unit-asserted against it.
- **D3 — Two upstream reads, both behind `FreshRun`, with opposite failure postures.** (i) the accepted
  `BacklogOrdering` under **41-3's** anchor — **required** (D4's loud exit when absent); (ii) the prior
  sprint's accepted `SprintPlan` under `BuildAnchor(repository, priorSprintKey)` — **optional** (absent ⇒
  no carry-over, which is the correct semantics for sprint 1). Both are
  `FetchLatestAcceptedDocumentActivity`; the difference is entirely in the routing, not in the read.
- **D4 — The missing-prerequisite exit is a typed loud exit, not a `Finish` and not an exception.**
  Correction 2. A third `FlowDecision` (`BacklogAvailable`) on the fail-closed `Found` flag: `False` →
  `EmitSprintPlanningFailed` (detail `missing-backlog-ordering`, data naming the anchor that was searched)
  → `ExposeOutput` with `status="failed"`, `outcome="missing-prerequisite"`. `True` → `DispatchLifecycle`.
  Loud in the DCB stream, loud in the outputs, structurally a typed exit like every other. The pinned
  `FlowDecision` id set for this workflow is therefore
  `{FreshRun, BacklogAvailable, LifecycleAccepted}` — one more than the siblings, justified here per the
  epic's rule "any story that cannot meet (a)–(f) must name the deviation" (this does not violate (a)–(f);
  it is a documented extra typed gate, the 39-12 D2 allowance).
- ~~**D5 — `feedbackVariableName` is a DECLARED carrier on the new cell.** 41-1a mints
  `Prompts/scrum_master/plan-sprint.md`; this plan specifies its front matter as
  `variables: role, backlogOrderingJson, capacityJson, carryOverJson, revisionNotes` / `enableTools: false`
  / `maxTokens: 8192` / `version: 1`, and the dispatch sets `["feedbackVariableName"] = "revisionNotes"` —
  which is also `DocumentLifecycleHelper.DefaultFeedbackVariable` (`:32`), so the carrier is the canonical
  one rather than a repurposed content variable. An undeclared producer variable is silently dropped at
  render (the 39-15 render-drop lesson). **Lockstep with 41-1a:** the file is created *there*, its contents
  are specified *here*. 41-1a must also create `Prompts/scrum_master/_system.md` and a
  `context-scan.md` (every role's eligible set includes `ContextScan`), or `PromptFileLoader` refuses to
  start (`PROMPT.SEED.NO_BODY_FAMILY`).~~
  **[REWRITTEN 2026-08-01 — the specified front matter does not match the file 41-1a shipped, and the
  mismatch would have broken the revise loop silently. See Correction 9.]**

- **D5 (replacement) — the producer variables are the SHIPPED ones; the revise carrier must be ADDED to the
  template, and `revisionNotes` is the wrong carrier to name without that edit.**

  **The file exists and is v2.** `apps/tamma-elsa/src/Tamma.Api/Prompts/scrum_master/plan-sprint.md:1-6`:

  ```
  variables: role, backlogJson, teamCapacity, carryOverJson, conventions
  enableTools: false
  maxTokens: 4096
  version: 2
  ```

  Against D5's original claim, four of six values differ: `backlogOrderingJson` → **`backlogJson`**
  (rendered `{{backlogJson}}`, `:10`); `capacityJson` → **`teamCapacity`** (`{{teamCapacity}}`, `:13`);
  `maxTokens` 8192 → **4096**; `version` 1 → **2**. `carryOverJson` (`{{carryOverJson}}`, `:16`) and
  `enableTools: false` were right. `conventions` (`{{conventions}}`, `:19`) is declared and **must not be
  supplied by this binding** — `LlmCallWorkflow` resolves it (`Workflows/LlmCallWorkflow.cs:107`, `:222`,
  via `Activities/Context/ResolveConventionsActivity.cs`).

  **So `BuildProducerVariables` composes exactly `{ backlogJson, teamCapacity, carryOverJson }`** (`role`
  is injected by `LlmCallWorkflow` when absent, `:145`). A key named `backlogOrderingJson` or `capacityJson`
  would be supplied, unrendered, and **dropped without a warning** — the failure this D was trying to avoid.

  **`revisionNotes` is NOT a declared carrier anywhere in the repo.** `grep -r revisionNotes
  apps/tamma-elsa/src/Tamma.Api/Prompts/` returns **zero** hits across all 123 template files.
  `DocumentLifecycleHelper.DefaultFeedbackVariable = "revisionNotes"`
  (`Workflows/Helpers/DocumentLifecycleHelper.cs:32`) is the *fallback key the lifecycle writes into the
  variables JSON*, not a carrier any template renders — and `PromptStoreService.Render` substitutes only the
  `{{placeholders}}` present in the body (`Tamma.Api/Services/PromptStore/PromptStoreService.cs:555-589`),
  so an unrendered key vanishes. `ValidationFeedbackHelper`'s class doc states the rule outright: *"a
  supplied-but-undeclared variable … is silently dropped at render, so every retry re-prompted blind"*
  (`Workflows/Helpers/ValidationFeedbackHelper.cs:5-15`). Every landed lifecycle binding obeys it by
  pointing at a carrier its template renders — `contextFindings`
  (`AcceptanceCriteriaAuthoringWorkflow.cs:243`, `PlanGenerationWorkflow.cs:203`,
  `TaskCreationWorkflow.cs:190`, `TriagePODecisionWorkflow.cs:198`), `findings`
  (`AdrAuthoringWorkflow.cs:240`), `errorContext` (`DebugDiagnosisWorkflow.cs:140`), `testTarget`
  (`TestCaseCreationWorkflow.cs:145`), `previousFindings` (`TriageContextGatheringWorkflow.cs:158`).
  **None of them uses the canonical default.**

  **`plan-sprint.md` v2 has no honest carrier to repurpose** — `backlogJson` / `teamCapacity` /
  `carryOverJson` are all content inputs whose meaning review notes would corrupt, and `conventions` is
  workflow-supplied. **So this story edits the template**, exactly as 41-1b did for
  `Prompts/security/threat-model.md` (v1→v2) and 41-9 for `write-adr.md` (v1→v2):

  - add `revisionNotes` to the `variables:` list **and** a rendered `## Revision Notes` /
    `{{revisionNotes}}` section to the body, bumping `version: 2` → **`3`**;
  - keep the dispatch at `["feedbackVariableName"] = "revisionNotes"` — which is then the canonical carrier
    *because the template renders it*, not merely because the constant says so;
  - a declared-but-unsupplied variable leaks a literal `{{revisionNotes}}` on the **first** (non-revise)
    turn, so `BuildProducerVariables` must supply it as an empty string on every turn, and the test asserts
    `PromptStoreService.Render`'s `Unresolved` list is empty for a first-turn variable set.

  **This is a template edit in a file 41-1a already shipped, not a lockstep with an unwritten file.** The
  old "created there, specified here" framing is dead: 41-1a is `done` (`docs/sprint-status.yaml:629`) and
  `_system.md`, `context-scan.md`, `plan-sprint.md` all exist. Editing v2→v3 must be paired in the same
  commit with whatever `TemplateExampleConformanceTests` / `ContractBindingTests` classification the cell
  carries (see Correction 10).

  **The AC that makes this fail loudly instead of silently:** a test asserts the workflow's dispatched
  `feedbackVariableName` value appears as a `{{…}}` placeholder in the producer template body. It goes red
  today (the placeholder is absent) and red again if anyone renames the carrier on one side only.
- ~~**D6 — Reviewer pinned to `product_owner`; acceptor per autonomy.** Correction 5. 41-1b **AC5/D1** owns
  writing the `AcceptanceDefaults.For(DocumentTypeKey.SprintPlan)` arm; this plan **states the required
  row** — `ReviewerSelection` single reviewer `product_owner`, acceptor per the autonomy dial, with the
  story's "commitment beyond a configured capacity band always escalates" expressed as a per-document-type
  always-escalate class in the resolved rules, **not** as a branch in the binding (rule 3). The execution
  test asserts the row, so a silent `_ => Rules` fall-through (single-`architect` unanimous — wrong for a
  sprint commitment) fails here as well as in 41-1b.~~
  **[CORRECTED 2026-08-01 — "acceptor per the autonomy dial" is false, and the always-escalate class is not
  expressible. See Correction 12 and the story's Amendment A2.]**

- **D6 (replacement) — the acceptance row has LANDED; this plan asserts it, it does not state it.** 41-1b
  shipped `DocumentTypeKey.SprintPlan => s_humanProductOwnerRules`
  (`Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:216`), built at `:144-147` as the product-owner-reviewer
  row `with { AcceptorRequirement = AcceptorRequirement.Human }`. So:
  **reviewer** = single `product_owner`, unanimous (Correction 5's substance stands); **acceptor** = a
  **human at every dial position in `[70,100]`**, not "per the autonomy dial". `AutonomyDial.AlwaysHuman`
  (= 101, `Documents/Policy/AutonomyDial.cs:38`) is the catalog's shipped minimum for this action
  (`Actions/ActionCatalog.Descriptors.cs:253`) and is strictly above `AutonomyDial.Max` (`:30`), and
  `AcceptanceFloors.ApplyShippedAcceptorFloor` (`Policy/AcceptanceFloors.cs:69-95`) prevents a base-tier
  override from lowering it. The "capacity band always escalates" clause is dropped: `EscalationClass` is
  `(Kind ∈ {document-type, agent-action}, Key)` (`Policy/AcceptanceRules.cs:200-210`) with no numeric
  dimension, and over-commit is already a validator rejection (`COMMITMENT_EXCEEDS_CAPACITY`,
  `Types/SprintPlan.cs:78`) that never reaches the accept gate.
  The execution test still asserts the resolved row — a `_ => Rules` fall-through (single-`architect`,
  unanimous, `AcceptorRequirement.Any`) would be wrong for a sprint commitment — but it is now a
  **regression** guard on landed code, not a cross-story lockstep. Three green tests already pin it:
  `AcceptanceDefaultsDriftTests:171-183`, `ActionCatalogDefaultsTests:98-120`, `AcceptanceFloorsTests:48-58`.
- **D7 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` + `ComputeReEntryPositionActivity` keyed on the
  D2 anchor, no allowlist entry.** Correction 1. The position gates both upstream reads and the
  `SPRINT.PLANNING.STARTED` emission.
- **D8 — The `SPRINT.*` family rides 41-2's shared `EmitDomainLifecycleEventActivity`.** This story ships
  only `SprintEvents.cs`. Note the story names a `.CLOSED` transition alongside `.ACCEPTED`; **sprint
  closure is not this workflow's** (it happens at the end of the time-box, not at planning time), so the
  constant is defined but emitted by nothing here, and that is recorded in the file's comment rather than
  faked with a spurious emission.
- **D9 — Capacity is data, not code.** `capacityJson` is an input, the ≤-capacity rule lives in
  `SprintPlanDocumentType.Validate` (41-1b), and the binding neither computes nor enforces it. This keeps
  the "over-commit is a validator rejection, not an accept-time surprise" promise (story, Orchestrator
  section) structural rather than aspirational — and it is why Correction 8's second half *is*
  deliverable.

## Implementation Steps

1. **[AMENDED 2026-08-01 — gates (i) and (ii) are already SATISFIED in tree; a third gate is not.** 41-1a
   and 41-1b are `done` (`docs/sprint-status.yaml:629`, `:630`) and every artefact below was verified at
   commit `6429691`, so this step collapses to a short re-verification. **The gate that matters is (iii):
   41-3 must have LANDED** — `BacklogBindingHelper.BuildAnchor` has no source file (Correction 14).
   Additionally, before writing `BuildProducerVariables`, open
   `Prompts/scrum_master/plan-sprint.md` and read its actual front matter — the replacement D5 exists
   because this plan's original guess did not match it (Correction 9).]**

   **Precondition check (no code). Two hard gates.** (i) **41-1a**: `AgentRoleExtensions.Parse("scrum_master")`
   returns `AgentRole.ScrumMaster` — **not** `ProductOwner` (Correction 6 — the alias at
   `RolePhaseMap.cs:239` must be gone); `AgentAction.PlanSprint` exists and is eligible for `ScrumMaster` in
   `RolePhaseMap.EligibleActions`; `Prompts/scrum_master/_system.md`, `context-scan.md` and `plan-sprint.md`
   all exist (D5's front matter) or `PromptFileLoader` refuses to start; and 41-1a **D2**'s panel decision
   for `scrum_master` is recorded (D6 assumes "neither panel"). (ii) **41-1b**: `Parse("sprint-plan")`
   succeeds, `Resolve("sprint-plan")` returns `SprintPlanDocumentType`, its `Contract` const is final, and
   `AcceptanceDefaults.For` carries D6's arm. Also confirm **41-3** has landed (its `BuildAnchor` is D3's
   input) and 41-2's shared emitter is in tree (else D8's fallback). Any gap blocks steps 4–7 — file it, do
   not work around it.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/SprintEvents.cs`** — `SPRINT.PLANNING.STARTED` /
   `.PLANNED` / `.ACCEPTED` / `.FAILED` / `.CLOSED` (the last defined-not-emitted, D8). Tags `repository` /
   `tenantId` / `correlationId` (= the D2 anchor) / `sprintKey`.

3. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/SprintBindingHelper.cs`** — pure,
   Elsa-free, total, fail-closed: `BuildAnchor(repository, sprintKey)` (D2, shared normalisation),
   `BuildProducerVariables(backlogOrderingJson, capacityJson, carryOverJson)` (D5's carrier set),
   `ProjectCommittedItems(documentJson)` (the accepted `committed` array raw text, `"[]"` on unreadable),
   `BuildMissingPrerequisiteDetail(searchedAnchor)` (D4's loud detail). Reuse
   `LifecycleBindingHelper.ReadLifecycleResult`/`IsAccepted` and `CreationBindingHelper.BuildFailureDetail`
   verbatim; call — do not copy — `BacklogBindingHelper.BuildAnchor` for the upstream read key.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SprintPlanningWorkflow.cs`** (D1/D2/D3/D4/D7) —
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. Graph: `ReadInputs` → `ComputeReEntryPosition` →
   `ReadPositionStage` → `FreshRun` → `EmitSprintPlanningStarted` → `FetchBacklogOrdering` →
   `FetchPriorSprintPlan` → `BacklogAvailable` `FlowDecision` → (`False` → `EmitSprintPlanningFailed` →
   `ExposeOutput`) | (`True` → `DispatchLifecycle` → `ReadLifecycleExit` → `LifecycleAccepted` →
   `EmitPlanned`/`EmitAccepted` | `EmitFailed` → `ExposeOutput`). Single terminal region; **no `Finish`**.
   Dispatch input:

   ```csharp
   ["documentType"]          = "sprint-plan",
   ["producerRole"]          = AgentRole.ScrumMaster.ToWire(),   // 41-1a
   ["producerAction"]        = AgentAction.PlanSprint.ToWire(),  // 41-1a
   ["producerVariablesJson"] = /* { backlogOrderingJson, capacityJson, carryOverJson } */,
   ["feedbackVariableName"]  = "revisionNotes",                  // D5 — the canonical declared carrier
   ["issueId"] = anchor, ["correlationId"] = anchor,             // D2
   ["tenantId"] / ["acceptanceRulesJson"]                        // D6's default rules
   ```

   `WaitForCompletion = new(true)`. `FlowDecision` id set exactly
   `{FreshRun, BacklogAvailable, LifecycleAccepted}` (D4's documented deviation).

5. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (`BuildSeed`) — add
   `("sprint-planning", [BacklogOrdering, SprintPlan], SprintPlan, false)` (it consumes its own type as
   carry-over — legal, and `WorkflowInterfaceGraphTests` constrains only that produced keys are registered
   or pending).
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs`** — bump `:45`
   by one with the reason in the comment, **and** add `"sprint-planning"` to the `reconciled` array
   `:102-123` (Correction 7).

6. **MODIFY the drift gates.** `ContractBindingTests.cs`: ~~add~~ **[CORRECTED 2026-08-01 — MOVE, do not
   add. Correction 10.]** the cell already exists in `PendingProducerCells` (`:797-806`) carrying 41-1b's
   token groups and the reason "no compiled dispatch site exists until 41-6 lands its workflow"; landing
   this story **moves** `[("scrum_master","plan-sprint")] = new("SprintPlanDocumentType.Validate", [ … ])`
   into `Bindings` and **removes** the pending entry (and the cell's
   `TemplateExampleConformanceTests.ConformingUnboundCells` classification — see the comment at `:793-796`).
   Keep the comment naming `Tamma.Core/Documents/Types/SprintPlan.cs` as the shape authority. **Also in this
   commit:** the `plan-sprint.md` v2→v3 template edit (D5 / Correction 9) must keep the worked-example
   conformance assertion green.
   `TaxonomyDriftBuildTests.cs`: add `"SprintPlanningWorkflow"` to `ExpectedContributingWorkflows` (`:125`).
   Verify (do not pre-edit) `MinExpectedDispatchPairs` (`:110`) and
   `EveryConcreteWorkflow_IsIntrospectableOrAllowListed` (`:397`).

7. **CREATE the test suites** — `SprintPlanningWorkflowStructureTests.cs`, `SprintBindingHelperTests.cs`,
   `SprintPlanningLifecycleExecutionTests.cs`. See Test Plan.

8. **Full run.** `dotnet test` green; `dotnet ef migrations has-pending-model-changes` clean (this story
   adds no schema).

## Data & Migrations

None. `SprintPlan` rows are `document_instances` (39-11's table, 41-1b's registration); `SPRINT.*` and
`DOCUMENT.*` ride the existing emitter → drain → `domain_events` path. `has-pending-model-changes` stays
clean. **Note:** 41-1a's `scrum_master` alias removal (its D3) may carry a one-shot data migration for
stored agent configs keyed `scrum_master` — that is 41-1a's, not this story's, but it must have run before
this workflow dispatches (Correction 6).

## Events

- **Emits (new constants, this story):** `SPRINT.PLANNING.STARTED` (fresh runs only, data `sprintKey`),
  `.PLANNED` (data `committedCount`, `carryOverCount`, `consumedOrderingId`), `.ACCEPTED` (data
  `documentId`), `.FAILED` — emitted on **two** distinct paths: the D4 missing-prerequisite exit (detail
  `missing-backlog-ordering`, data `searchedAnchor`) and a `rejected`/`escalated` lifecycle exit (detail
  names the typed outcome wire). `.CLOSED` is defined but not emitted here (D8). Tags `repository` /
  `tenantId` / `correlationId` (= the D2 anchor) / `sprintKey`.
- **Emitted by the machinery this binding wires in:** the `DOCUMENT.*` family, `APPROVAL.*`,
  `ESCALATION.TRIGGERED`.
- **Consumes:** none at runtime (both upstream documents are store reads, not event reads).

## Test Plan

NUnit + FluentAssertions; Testcontainers for the execution suite (the shared 39-6/39-10 fixture).

- **`SprintPlanningWorkflowStructureTests`** — the rule-1 clause (a)–(f) set, `TaskCreation`-shaped: builds;
  DefinitionId `sprint-planning`; threads `TenantId`; **zero** `Finish` (the D4 loud exit is a typed route,
  not a terminal — this is the pin that proves Correction 2's reconciliation); **exactly one**
  `DispatchWorkflow`, literal id `document-lifecycle`; **zero** targeting `llm-call`; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables; `ScanLifecycleBindingDispatches()`
  contains `(scrum_master, plan-sprint)` attributed to this workflow; `MaterializeDispatchInput` yields
  `documentType == "sprint-plan"` and `feedbackVariableName == "revisionNotes"`; one
  `ComputeReEntryPositionActivity` and **two** `FetchLatestAcceptedDocumentActivity` nodes (D3);
  `FlowDecision` id set exactly `{FreshRun, BacklogAvailable, LifecycleAccepted}` (D4's documented
  deviation, pinned so a fourth gate cannot appear unnoticed); `[ResumeBehavior(LatestStateReEntry)]`; **no
  `Wait*` activity** (Correction 1); **every graph leaf is inside the single `ExposeOutput` region** (the
  no-dead-end proof, both exits included). **Covers AC1, AC4.**
- **`SprintBindingHelperTests`** — `BuildAnchor` determinism, folding, hostile-character normalisation, and
  **agreement with `BacklogBindingHelper.BuildAnchor`'s transform**; `BuildProducerVariables` with
  ordering/capacity/carry-over present and absent; `ProjectCommittedItems` on a valid body and on unreadable
  JSON (`"[]"`, never throws); `BuildMissingPrerequisiteDetail` names the searched anchor;
  `BuildFailureDetail` names each reachable outcome wire. **Covers AC2 (detail half).**
- **`SprintPlanningLifecycleExecutionTests`** (Testcontainers) —
  (a) **happy path:** a seeded accepted `BacklogOrdering` under 41-3's anchor + a capacity input → scripted
  valid draft → review approve → `Accept` resume → `status=completed`, committed items projected; store
  asserts the accepted `SprintPlan` + its `Review` rows; replay asserts both event families and that
  `SPRINT.PLANNING.PLANNED` carries `committedCount`. **Covers AC1.**
  (b) **capacity rejection (Correction 8's deliverable half, D9):** a draft whose committed estimates exceed
  the stated capacity is rejected by `SprintPlanDocumentType.Validate` with the **named** violation code,
  loops repair/revise (notes arriving through `revisionNotes` — D5), and accepts on the second round. This
  is the "over-commit is a validator rejection, not an accept-time surprise" promise, proven. **Covers AC1.**
  (c) **missing prerequisite (AC2, D4):** no accepted `BacklogOrdering` for the anchor → **no lifecycle
  dispatch at all**, `SPRINT.PLANNING.FAILED` emitted with detail `missing-backlog-ordering` and the
  searched anchor, outputs `status="failed"`/`outcome="missing-prerequisite"`, and **no `SprintPlan` row
  written**. Asserted loudly on all three surfaces (events, outputs, store). **Covers AC2.**
  (d) **carry-over:** a seeded prior `SprintPlan` under `BuildAnchor(repository, priorSprintKey)` is read
  and reaches the producer variables; absent prior sprint ⇒ empty carry-over and a successful plan (D3's
  optional posture).
  (e) **validation exhaustion:** always-over-capacity stub → typed `ValidationExhausted` escalation with
  lineage, `SPRINT.PLANNING.FAILED` naming the outcome, `status=escalated`, no error terminal.
  (f) **re-entry:** crash after acceptance → short-circuits with the SAME `documentId`, exactly one
  `DOCUMENT.ACCEPTED` and one `SPRINT.PLANNING.ACCEPTED`, zero extra upstream reads; crash mid-review →
  resumes at review of the same revision. **Covers AC4.**
  (g) **acceptance posture (D6):** `AcceptanceDefaults.For(DocumentTypeKey.SprintPlan)` returns the
  documented row, and a run with a `product_owner` reviewer completes its review stage; **control case**:
  configuring a `scrum_master` reviewer throws at `RolePhaseMap.GetReviewActionForRole`, pinning Correction
  5 / 41-1a D2 so the "neither panel" decision is asserted rather than assumed.
  (h) **41-5 consumer read:** after (a), `FetchLatestAcceptedDocumentActivity` for
  `(BuildAnchor(repository, sprintKey), "sprint-plan")` returns the accepted body — the seam 41-5 uses,
  proving the D2 anchor is recomputable from inputs alone.
- ~~**AC3 is NOT tested** — Correction 3. `InitiatorOnlyTaskAudienceResolver` admits only the initiator, so
  there is nothing to assert. A single explicit test documents the gap: the accepted payload carries an
  `ownerRole` per committed item (the precondition 39-20 will consume) and the resolver is the stub.~~
  **[REPLACED 2026-08-01 — AC3 is narrowed and IS tested. See the replacement Correction 3.]**
- **AC3 (a) — owner-role guarantee on an accepted row.** After execution case (a), read the persisted
  `document_instances` body, deserialize it with `DocumentJson.Options` into
  `Tamma.Core.Documents.Types.SprintPlan` (`Types/SprintPlan.cs:35-41`), and assert every `Committed` entry
  has a non-empty `OwnerRole` that `AgentRoleExtensions.Parse` accepts. **Negative case:** a scripted draft
  whose committed item names `"scrum_lead"` is rejected with `OWNER_ROLE_UNKNOWN` (`:72`) and never
  reaches acceptance. **Covers AC3(a).**
- **AC3 (b) — structural isolation.** A source-level test over `SprintPlanningWorkflow.cs`,
  `SprintBindingHelper.cs` and `SprintEvents.cs` asserting none contains `ITaskAudienceResolver`,
  `ChannelAudience`, or a string literal with the **ordinal prefix** `"TASK."`. Fails the moment an
  implementer wires the decision-inbox plane into a tracker-shaped commitment. Do **not** use `Contains` —
  `AGENT.TASK.*` (`Tamma.Api/Services/Agents/AgentTrailEventTypes.cs`) would match it. **Covers AC3(b).**
- **D5's carrier pin (Correction 9).** Assert (i) the dispatched `feedbackVariableName` value occurs as a
  `{{…}}` placeholder in `Prompts/scrum_master/plan-sprint.md`'s body, and (ii) `PromptStoreService.Render`
  returns an **empty** `Unresolved` list for the first-turn variable set. Both are red against the shipped
  v2 template and go green only with the v2→v3 edit.
- **Drift gates (self-verifying, steps 5/6)** — `ContractBindingTests`, `TaxonomyDriftBuildTests`,
  `WorkflowInterfaceGraphTests` (count **and** `reconciled`) and `ResumableStandardStructuralTests`
  (declares, **no** allowlist entry) green in the same commit.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `SprintPlan` validated (capacity, owner+estimate, carry-over) | 4, 6 (D1/D9) | StructureTests clause (a)–(f); ExecutionTests (a)(b)(d) |
| 2 — consumes accepted `BacklogOrdering`; hard-fails loud if none | 3, 4 (D3/D4) | ExecutionTests (c) on events + outputs + store; HelperTests detail |
| ~~3 — role-scoped Task View entries via 39-20~~ *[NARROWED 2026-08-01]* | ~~**NOT CLAIMABLE**~~ | ~~**Correction 3.** `ITaskAudienceResolver` is the fail-closed `InitiatorOnlyTaskAudienceResolver` stub (`Program.cs:445-447`) and 39-19 has not landed. The precondition (per-item `ownerRole` in the accepted payload) is delivered and asserted; the routing is not.~~ |
| 3(a) — every `Committed` entry carries an `AgentRole`-parsable `ownerRole` on the accepted row | 4, 7 | ExecutionTests (a) + the `OWNER_ROLE_UNKNOWN` negative case |
| 3(b) — this binding references nothing on the task/decision plane | 4, 7 | Source-level test (`ITaskAudienceResolver` / `ChannelAudience` / ordinal `"TASK."` prefix) |
| 3 — the tracker consumer (`IterationId`) | **NOT THIS STORY** | Story 44-4 `POST /api/iterations/{id}/apply-plan` (44-4 AC9/AC10). Named, not deferred: a Task-View row was the wrong shape, not an early one. |
| D5 — the revise carrier is rendered, not dropped | 4, 7 (+ the `plan-sprint.md` v2→v3 edit) | Placeholder-presence test + `Render().Unresolved` empty |
| 4 — resumable per the standard, no allowlist entry | 4 (D7) | StructureTests declaration + no-`Wait*`; ExecutionTests (f); `ResumableStandardStructuralTests` |

## Dependencies & Sequencing

**[RE-STATED 2026-08-01 — Corrections 13 and 14. Two of the three hard blockers are `done`; the one that is
not is the only thing standing between this story and implementation, and the status file does not name it.]**

- ~~**Blocked by (hard):**~~
  - ~~**41-1a** — `AgentRole.ScrumMaster`, `AgentAction.PlanSprint`, `Prompts/scrum_master/{_system,
    context-scan, plan-sprint}.md`, **and the `scrum_master → product_owner` alias removal**
    (`RolePhaseMap.cs:239`). Verified: `plan-sprint`/`PlanSprint` has **zero** repo-wide hits and the role
    enum has exactly 8 members. Without it the produce cell does not exist on the **human path either** — a
    human assignee still needs a cell to bind.~~ **DONE** (`docs/sprint-status.yaml:629`). Every artefact is
    in tree: `AgentRole.cs:23`, `AgentAction.cs:132`, `RolePhaseMap.cs:229`, alias removed
    (`RolePhaseMap.cs:288`), all three prompt files present. The struck greps are now **false** — do not
    re-run them as gates (Correction 13).
  - ~~**41-1b** — `DocumentTypeKey.SprintPlan` + `SprintPlanDocumentType` + the `AcceptanceDefaults` arm.
    Verified: the vocabulary has exactly 10 members and `sprint-plan` is not one, so the document is
    unparsable (`DOCUMENT.TYPE.UNKNOWN`) and unpersistable.~~ **DONE** (`docs/sprint-status.yaml:630`).
    `DocumentTypeKey.cs:41`, `Types/SprintPlan.cs`, `DocumentTypeRegistry.cs:45`,
    `AcceptanceDefaults.cs:216`. The vocabulary is at **17** members, not 10.
  - **⛔ 41-3 — STILL BLOCKING, and it is a LANDING dependency, not a scheduling one.** This story *calls*
    `BacklogBindingHelper.BuildAnchor(repository, backlogScope)` (D3, Correction 4: "called, never
    re-derived"). **No such file exists** — `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/`
    holds 18 helpers and none is `BacklogBindingHelper.cs`; it is created by 41-3's plan step 3
    (`docs/stories/epic-41/story-41-3/implementation-plan.md:143-144`), which also records that the anchor
    "must be defined in `BacklogBindingHelper.BuildAnchor` and consumed, never re-derived, by
    41-6/41-4" (`:279`). 41-3 is `drafted` (`docs/sprint-status.yaml:633`). Steps 3 and 4 cannot compile
    until it lands; steps 2 (`SprintEvents`) and the D2 anchor half of step 3 can start early.
  - **⚠ `docs/sprint-status.yaml:636` omits 41-3.** It reads "Blocked on 41-1a + 41-1b" — both now `done` —
    so a scheduler reading that line alone will pull 41-6 as unblocked. It is not. (That file is
    coordinator-owned; this is recorded here rather than edited.)
  - **Epic 39** — 39-6/39-7/39-8/39-10/39-11 (all landed and verified in tree).
- **Soft-blocked by 41-2** — D8's shared emitter only. 41-2 is `done` (`docs/sprint-status.yaml:632`), so
  `EmitDomainLifecycleEventActivity` is in tree and the local-copy fallback is moot.
- **Downstream consumer, non-blocking both ways: 44-4.** It reads this story's accepted `sprint-plan`
  document through `IDocumentInstanceRepository` and tests against a hand-built `DocumentInstance` fixture,
  so neither story blocks the other's code. The one live coupling is the unresolved `issueId` join recorded
  in the story's Open Items (`BacklogItem.itemId` vs `SprintCommittedItem.issueId`).
- **Blocks:** **41-5** (its `consumes: SprintPlan`, read through this story's D2 anchor — optional there, so
  soft); **41-7** and **41-8** (both named `Related:` in the story; both read the sprint's commitment as the
  baseline their digest/retro is measured against).
- **Not achievable without:** **39-19** + **39-20** for AC3 (Correction 3). Both are fail-closed stubs;
  neither is in this epic.
- **Lockstep:** 41-1a's `plan-sprint.md` front matter ↔ D5's variable set (specified here, created there);
  41-1a's D2 panel decision ↔ D6/test (g); 41-1b's `SprintPlan` `Contract` const ↔ step 6's `Bindings`
  token groups; 41-1b's `AcceptanceDefaults` arm ↔ D6/test (g); 41-3's `BuildAnchor` ↔ D3.
- **Sequencing within the story:** 1 → 2/3 (parallel) → 4 → 5/6 (parallel) → 7 → 8.

## Risks & Mitigations

- ~~**This is the most-blocked story in the batch: 41-1a AND 41-1b AND 41-3, all three hard.**~~
  *[2026-08-01: two of the three are `done`. **One hard blocker remains — 41-3 — and it is a LANDING
  dependency.** The residual risk inverted: the danger is no longer "three enablers might slip", it is that
  the status file names only the two that have landed (`docs/sprint-status.yaml:636`), so 41-6 reads as
  schedulable. Mitigation: Dependencies & Sequencing states the gate explicitly; steps 2 and the D2-anchor
  half of step 3 are 41-3-independent and can start early; steps 3(upstream read)/4 cannot.]*
- ~~**The `scrum_master` alias removal is a live behaviour change (Correction 6).** If this story ships while
  the alias survives, `AgentRole.Parse("scrum_master")` silently returns `ProductOwner` and the binding
  dispatches a **PO** cell with no error anywhere.~~ *[2026-08-01: CLOSED — 41-1a removed the alias
  (`RolePhaseMap.cs:288`) and `AgentRole.ScrumMaster` is a first-class member (`AgentRole.cs:23`). The
  execution-test guard on the dispatched producer role is still worth keeping as a regression pin, but this
  is no longer a live risk.]*
- **NEW RISK (2026-08-01) — the silent revise loop.** If the binding ships pointing `feedbackVariableName`
  at a variable `plan-sprint.md` does not render, the revise turn re-prompts with **no notes** and produces
  a plausible second draft. Nothing throws, no event records it, and the loop looks healthy while learning
  nothing — strictly worse than a crash. Mitigation: the D5 template edit plus the two carrier tests
  (placeholder-presence and empty `Unresolved`), both red against the shipped v2 template.
- **D4's extra `FlowDecision` invites re-litigation of "thin".** Mitigation: it routes a typed value
  (39-12 D2's explicit allowance), the id set is pinned so a fourth gate fails the build, and the deviation
  is named here per the epic's own rule.
- ~~**AC3 will read as delivered by anyone skimming.** Mitigation: it is marked NOT CLAIMABLE in the DoD
  table, excluded from the test plan with a documenting test, and the epic README already names 41-6:45 as
  an AC-level failure.~~ *[2026-08-01: the residual risk is now the opposite one — that an implementer reads
  the ORIGINAL AC3 (still legible in the story under strikethrough, and quoted verbatim in 44-4 at `:35`)
  and wires an audience resolution per committed item. Mitigation: AC3(b)'s source-level test, which fails
  on the import.]*
- **Anchor drift across four helpers** (41-3/41-4/41-5/41-6). Mitigation: one shared normalisation;
  `SprintBindingHelperTests` asserts agreement with `BacklogBindingHelper` directly.
- **Story-vs-code tensions:** Corrections 1–8 all resolve in favour of the code. Correction 3 removes an AC;
  Corrections 2 and 5 change the design; 4 and 6 add constraints; 1, 7 and 8 are mechanical or
  clarifications.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | 41-1a/41-1b/41-3 precondition verification + front-matter and contract agreement | 0.5 |
| 2 | `SprintEvents` (+ 41-2 emitter reuse or local copy) | 0.25 |
| 3 | `SprintBindingHelper` (anchor, carrier composer, projections, loud detail) | 0.75 |
| 4 | The binding workflow incl. the D4 typed loud exit | 1.25 |
| 5 | Registry seed row + the two `WorkflowInterfaceGraphTests` edits | 0.25 |
| 6 | `ContractBindingTests` + `TaxonomyDriftBuildTests` edits | 0.5 |
| 7 | Structure + helper + Testcontainers suites (a)–(h) + the AC3-gap documenting test | 1.5 |
| 8 | Full-suite green, review polish | 0.25 |
| **Total** | | **5.25** |

**Est. Effort: 5.25 days.** The story file says 4–5 days, which is close: the extra ~0.25–1 d is the D4 loud
exit (a route the siblings do not have, +0.5 d) and the larger execution matrix (two upstream reads with
opposite failure postures, plus the panel-throw control case). ~~Note the estimate **excludes AC3**, which is
not achievable (Correction 3) — if 39-19/39-20 landed, routing the accepted commitment would be additional
work not costed here.~~ *[2026-08-01: AC3 as narrowed IS in scope and costed — clauses (a) and (b) fold into
step 7 (roughly +0.25 d), offset because there is no Task-View work to defer or document. The tracker
consumer is 44-4's story, not uncosted work here. The `plan-sprint.md` v2→v3 edit plus its two carrier tests
add ~0.25 d to steps 4/7. **Net: unchanged at ~5.25 d.**]* The story's `## Estimated Effort` section is left
at 4–5 days and this plan is the record.

## Change Log

| Date       | Version | Changes | Author |
| ---------- | ------- | ------- | ------ |
| 2026-08-01 | 1.1.0   | Scoping round against the tree at commit `6429691`. Correction 3 replaced — AC3 is narrowed, not deferred; 44-4 named as the owner of the correct behaviour and the AC now planned and tested. D5 rewritten against the shipped `plan-sprint.md` v2 front matter; `revisionNotes` shown to be undeclared repo-wide and the v2→v3 template edit specified (Correction 9). D6 replaced — "acceptor per the autonomy dial" was false; three human-acceptor pins cited (Correction 12). Corrections 10 (the cell is already in `PendingProducerCells` — MOVE, not ADD), 11 (stale line-citation table), 13 (41-1a/41-1b landed; D1's greps now false) and 14 (41-3 is a LANDING blocker with no source file) added. Dependencies, DoD, Test Plan, Risks and Est. Effort reconciled. | Claude |
| 2026-07-25 | 1.0.0   | Initial plan | Claude |

## Blocks / Blocked by

- ~~**Blocked by (hard):** 41-1a (`scrum_master` role + `plan-sprint` cell + its prompt files + the alias
  removal); 41-1b (the `SprintPlan` document type + its acceptance row); 41-3 (the consumed
  `BacklogOrdering` + its anchor helper); Epic 39 stories 39-6, 39-7, 39-8, 39-10, 39-11 (all landed).~~
  *[2026-08-01]* **Blocked by (hard): 41-3 ONLY** — and on it **LANDING**, because
  `BacklogBindingHelper.BuildAnchor` has no source file in tree. 41-1a
  (`docs/sprint-status.yaml:629`) and 41-1b (`:630`) are `done`; Epic 39's five are landed.
- **Blocked by (soft):** ~~41-2 (shared emitter)~~ — 41-2 is `done` (`:632`); the emitter is in tree.
- **Blocks:** 41-5 (consumes the accepted `SprintPlan` — soft, optional there); 41-7 and 41-8 (both read the
  sprint commitment as their baseline); **44-4's apply seam** as a *feature* (44-4's code compiles and tests
  without it, `44-4-…md:111`).
- ~~**AC3 additionally requires:** 39-19 (Task View) and 39-20 (teams/roles/repo access + task routing) —
  both fail-closed stubs in tree, neither in this epic. AC3 is excluded from this plan's scope.~~
  **[WRONG — 2026-08-01. AC3 as narrowed requires NEITHER.** Clause (a) is provable against landed code and
  clause (b) is a source-level assertion; the tracker consumer is 44-4's, and it never wanted 39-19/39-20
  either. The struck line implied a Task-View row was merely early. It was mis-shaped. See the replacement
  Correction 3.**]**
- **Not blocked by:** 41-1c (this is a typed document, not prose); the tenant-aware scheduled-trigger seam
  (sprint planning is time-box-triggered by a human or an API call, not a cron sweep — unlike 41-5/41-7);
  39-19/39-20 (per the correction above).
