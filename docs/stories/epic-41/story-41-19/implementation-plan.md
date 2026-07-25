# Implementation Plan — Story 41-19: Threat Modeling Workflow

## Scope & Deliverable

When this story is done, a feature or system surface gets a typed `ThreatModel` on the Epic 39
lifecycle. A new thin binding `DefinitionId = "threat-model"` assembles the security context (an
optional accepted `Design` from 41-10, the issue, a context scan, the data-flow surface), dispatches
`document-lifecycle` with `documentType = "threat-model"` and the existing
`(security, threat-model)` producer cell, and routes typed exits. It contributes no parse, no
`Finish`, no `llm-call`. Unmitigated high-risk threats cannot reach a silent acceptance — enforced
in the type's `Validate`, so the state is *unrepresentable*, not merely discouraged. The accepted
model is retrievable by `issueId` through 39-11 and consumable by `plan-generation` and 41-15.

**This story cannot start until 41-1b lands the `ThreatModel` type.** Its produce cell, prompt file
and reviewer path already exist; the type is the whole gate.

## Pre-Reading

- `docs/stories/epic-41/story-41-19/41-19-threat-modeling.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/story-41-1/41-1b-new-document-types.md` — **the hard prerequisite**; especially its `ThreatModel` row (`:35`), D1 (per-type acceptance posture is chosen, not inherited), D2 (no workflow edges — this story owns the edge), AC4 (the two vocabulary count pins `Be(10)`→`Be(16)`, `HaveCount(10)`→`HaveCount(16)`), AC5 (`AcceptanceDefaults.For` arm), AC6 (`RenderContract` + a `ContractBindingTests` entry)
- `docs/stories/epic-41/README.md` — rule 1's six thinness clauses (a)–(f)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — **the reference binding and the reference structure-test set**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ResearchWorkflow.cs` — the closest sibling in shape (a run-to-completion producer with a pre-produce context fetch)
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/ReviewDocumentType.cs:33-38`, `:88-97` — **the flagship precedent this story copies**: `APPROVE_WITH_BLOCKING_ISSUES` makes a bad state unrepresentable rather than escalatable
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:195-210` (`EscalationClass`, `EscalationClassKind` = `document-type` | `agent-action` **only**) and `AcceptanceGuardrails.cs:45-80` (`TryPreGate`) / `:96-134` (`Clamp`, incl. the `BlockingReviewViolation` arm) — read these **before** designing AC2
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:123-134` — `For(DocumentTypeKey)`; note the `_ => Rules` catch-all
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs:88` (`[Wire("threat-model")] ThreatModel`), `RolePhaseMap.cs:129` (in `Security`'s eligible set), `apps/tamma-elsa/src/Tamma.Api/Prompts/security/threat-model.md` — **all three exist; this story mints no cell**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewerSelectionHelper.cs:108-171` + `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs:376-436` — the review-stage lens: a non-`triage-decision` document routes through `GetReviewActionForRole`, which covers all 7 non-`tech_writer` roles
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` (`Bindings`, the universal-authority pin `:626`, the staleness guard `:724-737`), `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs:134-174`, `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`
- `docs/stories/epic-39/story-39-12/implementation-plan.md` — D2/D3/D5/D7, the reusable binding recipe
- **NOT FOUND (built by 41-1b, no code in tree yet):** `DocumentTypeKey.ThreatModel`, `Tamma.Core/Documents/Types/ThreatModel.cs`, `ThreatModelDocumentType`, its registry registration, its `AcceptanceDefaults.For` arm. `DocumentTypeKey.cs` today has **exactly ten** members (`:23-33`).

## Corrections to the story

1. **CONFIRMED — the blocker is real and total.** `DocumentTypeKey` has ten members
   (`DocumentTypeKey.cs:23-33`), `threat-model` is not one of them,
   `DocumentTypeKeyExtensions.Parse` throws `DOCUMENT.TYPE.UNKNOWN` for it, and
   `DocumentTypeRegistry.Resolve` throws `DOCUMENT.TYPE.NOT_REGISTERED`. No part of this workflow can
   persist, validate or review a document until **41-1b** lands. The README's "hard blocker on BOTH
   paths" framing is accurate — a human assignee is equally blocked.

2. **CONFIRMED — the cell is NOT a blocker.** `(security, threat-model)` exists in full:
   `AgentAction.cs:88`, `RolePhaseMap.cs:129` (`Security`'s `FreezeSet`), and
   `src/Tamma.Api/Prompts/security/threat-model.md`. The story never claimed otherwise; recording it
   so nobody adds a spurious 41-1a dependency. **This story mints no `(role, action)` cell** and
   therefore bumps neither `AgentActionTests.cs:38` (`Be(80)`) nor `RolePhaseMapTests.cs:64`
   (`HaveCount(80)`).

3. **CONFIRMED — the review lens needs no new selector arm.** `ThreatModel` is not
   `triage-decision`, so `RolePhaseMap.GetPanelActionForRole(role, "threat-model")` falls through to
   `GetReviewActionForRole` (`:376-387`), which returns `plan-review-security` for `security` and
   `plan-review` for `architect` — exactly the story's "reviewed via security/architect lens". No
   41-1a review-selector work is needed. (The `tech_writer` throw at `:385` is irrelevant here.)

4. **NEW, and this is the story's real design problem — AC2 is NOT expressible as an escalation
   class.** AC2 says "Unmitigated high-risk cannot be accepted silently — it is a typed escalation."
   Verified: the only escalation-class mechanism is
   `AcceptanceRules.AlwaysEscalate : EscalationClass[]` where
   `EscalationClassKind` is **`document-type` or `agent-action` and nothing else**
   (`AcceptanceRules.cs:200-210`), matched by exact string equality in
   `AcceptanceGuardrails.TryPreGate` (`:50-68`). There is **no payload-conditional escalation class**
   anywhere in the tree. So `{"kind":"document-type","key":"threat-model"}` escalates *every* threat
   model — which contradicts the story's own Autonomy row ("agent drafts and self-accepts a
   fully-mitigated model"). Two mechanisms *do* exist and both are payload-aware:
   `IDocumentType.Validate` (reject the state outright) and
   `AcceptanceGuardrails.Clamp`'s `BlockingReviewViolation` arm (`:103-110` — an `Accept` over a
   review that is not a clean approval, or that carries a blocking issue, is forcibly converted to
   `Escalate`). **See D2** — this plan uses the first, with the second as the belt-and-braces
   backstop. Adding a third `EscalationClassKind` would be a 39-5 generic-layer change and is out of
   scope; it is recorded as a gap, not silently absorbed.

5. **NEW — AC3's "consumable by `plan-generation`" is a store-read assertion, not a wiring change.**
   `plan-generation`'s declared interface today is
   `("plan-generation", consumes [Decomposition], produces Plan)`
   (`DocumentTypeRegistry.cs:151`). Making it *also* consume `ThreatModel` would edit another
   workflow's binding and its declared row — out of scope for 41-19 and not what the AC asks. AC3 is
   satisfied by proving the accepted `ThreatModel` is retrievable by `issueId` through the 39-11
   repository read and that a `plan-generation`-shaped consumer can read it. Wiring
   `plan-generation` to actually consume it is a separate, later edit (and would move
   `plan-generation`'s row, not add an edge).

6. **NEW — AC4's `[ResumeBehavior(Both)]` is the wrong mode for a thin binding.**
   `ResumableStandardStructuralTests` clause (b) requires a `Both`-declaring workflow's graph to
   contain a canonical suspend activity from `LifecycleBookmarks.CanonicalSuspendActivities`. A thin
   binding never suspends — the accept-gate suspend lives inside the dispatched `document-lifecycle`
   child, waited on with `WaitForCompletion=true`. Every landed thin binding declares
   `LatestStateReEntry` (`TaskCreationWorkflow.cs:47`, `ResearchWorkflow.cs:35`,
   `IssueDecompositionWorkflow`; 39-12 D7 states the rule). **Correction:
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`.** AC4's substance — 39-10 green with no
   allowlist entry — is unaffected.

7. **NEW — the story omits the two lockstep obligations rule 1(f) imposes.** A new *producing
   workflow* must declare a `WorkflowDocumentInterface` row in `DocumentTypeRegistry.BuildSeed` and
   bump `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` (`:45`, `HaveCount(16)` today) in
   the same change. 41-1b deliberately moves **no** edges (its D2), so the edge is 41-19's to add.
   The story's AC list does not mention either; they are added to this plan's DoD as AC4's second
   half.

8. **NEW — the story omits the `ContractBindingTests` obligation.** The moment this binding compiles,
   `(security, threat-model)` becomes a *discovered* dispatch pair via the lifecycle-binding walk,
   and `EveryDispatchedPair_IsBoundOrExplicitlyAllowlisted` (`:681`) fails until it is classified.
   Per `UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual` (`:626`) it
   must go into `Bindings` with authority `ThreatModelDocumentType.Validate`. 41-1b's AC6 promises
   "one `ContractBindingTests` entry" per new type — **coordinate**: either 41-1b adds a placeholder
   and 41-19 fills the token groups, or 41-19 owns the entry outright. This plan assumes the latter
   and says so in step 6.

## Design Decisions

- **D1 — New `DefinitionId = "threat-model"`; no incumbent, no rewiring.** Inputs: `issueId`,
  `issueTitle?`, `repository`, `issueNumber?`, `tenantId`, `designDocumentId?` (the 41-10 `Design`),
  `dataFlowJson?`, `contextIds?`, `acceptanceRulesJson?`, `conventions?`. Outputs: `status`,
  `outcome`, `documentId`, `parentDocumentId`, `threatModelJson`, `unmitigatedHighRiskCount`.
  `builder.Version = WorkflowVersions.ComputedVersion`.

- **D2 — AC2 is enforced by making the state unrepresentable, exactly as 39-4 did for
  `APPROVE_WITH_BLOCKING_ISSUES` (Correction 4).** The rule belongs to `ThreatModelDocumentType`,
  which **41-1b owns** — so this is a *contract this story depends on and must agree in lockstep*,
  not code 41-19 writes:
  - `ThreatModelDocumentType.Validate` rejects a payload carrying a threat whose residual risk is
    high **and** whose mitigation is absent/empty, with a violation code
    (`UNMITIGATED_HIGH_RISK`) naming the offending threat and asset. A threat model in that state
    therefore never becomes a valid document and never reaches the accept gate at all — it drives the
    lifecycle's repair/revise ring, and on exhaustion exits as a typed
    `validation-exhausted` escalation with full lineage. **That IS the "typed escalation" AC2 asks
    for**, and it is stronger than a policy rule because no configuration can switch it off.
  - **Belt and braces:** where a model is *representable* but the reviewer flags residual risk, the
    existing `AcceptanceGuardrails.Clamp` `BlockingReviewViolation` arm (`:103-110`) already forces
    `Accept` → `Escalate` when the review is not a clean approval or carries a blocking issue. No new
    code; the security reviewer raising a critical-severity issue is sufficient.
  - **Deliberately NOT chosen:** `{"kind":"document-type","key":"threat-model"}` in
    `AlwaysEscalate` — it escalates *every* model regardless of content, contradicting the story's
    own 85–100 autonomy row. It stays available as a *deployment* choice for a paranoid tenant, and
    the tests assert it works, but it is not the mechanism AC2 rests on.
  - **Recorded gap:** a payload-predicate `EscalationClassKind` does not exist and is not built here.

- **D3 — `AcceptanceDefaults.For(DocumentTypeKey.ThreatModel)` gets an explicit arm; it must not fall
  through.** `For` ends in `_ => Rules` (`AcceptanceDefaults.cs:133`), whose base row is a
  **single-`architect`, unanimous** reviewer selection — for a threat model that is the wrong
  reviewer entirely. 41-1b's D1 already flags this and its AC5 requires an explicit answer. **41-19's
  position, to be handed to 41-1b as the contract:** `ThreatModel` maps to a row whose
  `ReviewerSelection` is a panel of `security` + `architect` (security decides, architect sanity-checks
  the surface), with the acceptor requirement left at the base (orchestrator-routable) so the 70–100
  dial still governs WHO accepts. This is a one-line arm in `AcceptanceDefaults`; agree it in
  lockstep and pin it in 41-1b's AC5 test.

- **D4 — Zero parse, zero `Finish`, exactly two typed `FlowDecision`s** (39-12 D2's resolution of
  "no bespoke branch"): `FreshRun` (re-entry position == produce — gates the STARTED emission and the
  `Design` fetch, so a re-entry is not a new threat model) and `LifecycleAccepted` (typed lifecycle
  `status`). Nothing branches on model output. The structure test pins the exact `FlowDecision` id
  set.

- **D5 — Consumed `Design` lineage via the store read seam, fail-loud.**
  `FetchLatestAcceptedDocumentActivity` (documentType `design`, on `issueId`) supplies the consumed
  41-10 design; its resolved `documentId` becomes the output `parentDocumentId` and rides
  `THREATMODEL.STARTED`'s data. Absent design ⇒ `null` and the run proceeds (the story's `Design?` is
  optional). A **supplied-but-unreadable** `designDocumentId` routes to the loud failure edge — never
  silently `null`.

- **D6 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, no allowlist entry** (Correction 6), with
  one `ComputeReEntryPositionActivity` node in the graph (39-10 clause (c)) and no `Wait*` activity.
  The re-entry anchor is the plain `issueId` — unlike 41-18, `threat-model` has exactly **one**
  producer, so no producer-scoping is needed (`TaskCreationWorkflow`'s D2 problem does not arise).

- **D7 — New event family `THREATMODEL.*`.** New file
  `apps/tamma-elsa/src/Tamma.Activities/Security/ThreatModelEvents.cs` in the `ResearchEvents.cs`
  shape: `Started` = `THREATMODEL.STARTED`, `Drafted` = `THREATMODEL.DRAFTED`,
  `Accepted` = `THREATMODEL.ACCEPTED`, plus a LOUD `Failed` = `THREATMODEL.FAILED` for the
  `rejected`/`escalated` exits (the story's three-event list has no failure member; every landed
  family has one, and a typed escalation must not exit silently). `ParseTenantId` +
  `StatusForEvent` per house convention. Data on `.ACCEPTED`: `documentId`, `threatCount`,
  `unmitigatedHighRiskCount` (which, per D2, is necessarily 0 on a valid accepted model — asserting
  it is the cheap end-to-end proof of the invariant).

- **D8 — "Seeds security tasks" is out of scope for this cut.** The story's orchestrator section says
  an unmitigated high-risk model "can seed security tasks". Task seeding from an accepted document is
  the orchestrator's job (39-17), which is stubbed fail-closed in the tree
  (`GetAcceptanceRulesTool` is deliberately unregistered; `OrchestratorChannelHandler.cs:11` waits on
  39-17). Nothing in this story's ACs requires it. Record it as a downstream consumer of the accepted
  `ThreatModel`, not as a deliverable.

## Implementation Steps

0. **HARD GATE (no code).** 41-1b must be merged and compiling: `DocumentTypeKey.ThreatModel`
   present, `ThreatModelDocumentType` registered in `DocumentTypeRegistry.s_registrations`, the two
   vocabulary count pins bumped (`DocumentTypeKeyTests.cs:20`, `DocumentTypeRegistryTests.cs:37`),
   `AcceptanceDefaults.For` carrying the D3 arm, and `Validate` implementing the `UNMITIGATED_HIGH_RISK`
   rule of D2. **If the D2 rule is not in 41-1b's implementation, stop and negotiate it — do not
   re-implement it locally**, and do not fall back to an always-escalate class (D2's rejected option).

1. **Precondition check (no code).** `dotnet build` green; confirm `document-lifecycle`,
   `document-review`/`review-panel`, `FetchLatestAcceptedDocumentActivity`,
   `ComputeReEntryPositionActivity`, `LifecycleBindingHelper`, `WorkflowVersions` all present (all
   verified present at plan time), and that `ReviewerSelectionHelper.Resolve("security", null,
   "document", "threat-model")` returns `(security, plan-review-security)` (Correction 3).

2. **HAND-EDIT `apps/tamma-elsa/src/Tamma.Api/Prompts/security/threat-model.md`** — bring the body
   onto the canonical `ThreatModel` wire that 41-1b's `RenderContract()` defines (assets, threats
   with `category`/`asset`/`mitigation`/`residualRisk`, STRIDE or the configured taxonomy). Bump
   `version` in the front matter. **No 39-16 generated-region marker exists in any prompt file**
   (verified), so this is a hand edit; if 39-16 lands first, replace it with its output. The body
   must literally contain every token group step 6 pins.

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Security/ThreatModelEvents.cs`** (+ an
   `EmitThreatModelEventActivity` if the house per-family emitter pattern applies) — D7.

4. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ThreatModelBindingHelper.cs`** —
   pure, Elsa-free, total, fail-closed:

   ```csharp
   public static class ThreatModelBindingHelper
   {
       public static (int ThreatCount, int UnmitigatedHighRisk) ReadCounts(string documentJson); // (0,0) on unreadable
       public static string? ResolveParentDocumentId(bool designFound, string? designDocId);
       public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit);
   }
   ```

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ThreatModelWorkflow.cs`** — the binding.
   Copy `TaskCreationWorkflow.cs`'s skeleton. `builder.DefinitionId = "threat-model"`,
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (D6). Graph:
   `ReadInputs → ComputeReEntryPosition(issueId, "threat-model") → ReadPositionStage
   → FreshRun(FlowDecision)`
   → *(True)* `EmitThreatModelStarted` → `FetchConsumedDesign` (`FetchLatestAcceptedDocumentActivity`,
   documentType `design`) → join; *(False)* join
   → `DispatchLifecycle` (`document-lifecycle`, `WaitForCompletion=true`) with
   `documentType = "threat-model"`, `producerRole = AgentRole.Security.ToWire()`,
   `producerAction = AgentAction.ThreatModel.ToWire()`, `producerVariablesJson` (design payload,
   data flow, context findings, conventions), a **declared** `feedbackVariableName` naming a variable
   `threat-model.md` actually declares (clause (e) — verify against the front matter; this is the
   render-drop lesson), `issueId`, `correlationId`, `tenantId`, `acceptanceRulesJson`
   → `ReadLifecycleExit` (`LifecycleBindingHelper.ReadLifecycleResult` + `IsAccepted`; also
   `ReadCounts` into the output variables) → `LifecycleAccepted(FlowDecision)`
   → `EmitThreatModelDrafted`/`EmitThreatModelAccepted` vs `EmitThreatModelFailed` → `ExposeOutput`.
   **Zero `Finish`; zero `DispatchWorkflow("llm-call")`; exactly one `DispatchWorkflow`, literal
   definition id `document-lifecycle`; no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`
   variables.**

6. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**
   (Correction 8) — add to `Bindings`:

   ```csharp
   // Story 41-19 — ThreatModelWorkflow binds (security, threat-model) as the produce step of its
   // document-lifecycle binding; shape authority is
   // Tamma.Core/Documents/Types/ThreatModel.cs (ThreatModelDocumentType.Validate).
   [("security", "threat-model")] = new("ThreatModelDocumentType.Validate",
   [
       One("\"assets\""), One("\"threats\""), One("\"category\""), One("\"asset\""),
       One("\"mitigation\""), One("\"residualRisk\""),
   ]),
   ```

   (Final token groups follow 41-1b's landed wire — take them from `ThreatModelDocumentType.Examples`,
   not from this plan.) Then run the whole fixture: the pair must be discovered via the
   lifecycle-binding walk (clause (c) staleness), the universal-authority pin must pass, and there
   must be no `IntentionallyUnbound` entry to contradict it.

7. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (Correction 7,
   rule 1(f)) — add to `BuildSeed`:
   `new WorkflowDocumentInterface("threat-model", new[] { DocumentTypeKey.Design }, DocumentTypeKey.ThreatModel, false)`.
   **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`** —
   bump `HaveCount(16)` by one (to whatever the count is when this story lands), with a comment
   naming Story 41-19 and the added edge.

8. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`** —
   add `"ThreatModelWorkflow"` to `ExpectedContributingWorkflows` with the standard
   lifecycle-binding-walk comment. `MinExpectedDispatchPairs` (`:110`, 21) needs no change.

9. **CREATE the tests** — see Test Plan. Finish with full `dotnet test` and
   `dotnet ef migrations has-pending-model-changes` (must stay clean).

## Data & Migrations

None **in this story**. `ThreatModel` documents persist through 39-11's existing `document_instances`
table (no schema change — `DocumentInstance.DocumentType` is a wire string);
`THREATMODEL.*` rides the existing `TammaEventEmitter` → `EventPersistenceMiddleware` →
`EventRepository` → `domain_events` drain. `dotnet ef migrations has-pending-model-changes` stays
clean. *(41-1b likewise ships no migration for a new type — only 41-1c's `Audience` column does.)*

## Events

- **Emits (new constants, `Tamma.Activities/Security/ThreatModelEvents.cs`):**
  `THREATMODEL.STARTED` (fresh runs only; data `parentDocumentId`),
  `THREATMODEL.DRAFTED` (data `threatCount`),
  `THREATMODEL.ACCEPTED` (data `documentId`, `threatCount`, `unmitigatedHighRiskCount`),
  `THREATMODEL.FAILED` (LOUD, on `rejected`/`escalated`, detail names the typed outcome wire — D7).
  Tags: `issueId`, `repository`, `tenantId`, `correlationId`.
- **Emitted by the machinery this binding wires in (not by this story's code):** the `DOCUMENT.*`
  family (incl. `DOCUMENT.VALIDATED.FAILED` carrying `UNMITIGATED_HIGH_RISK`, and
  `DOCUMENT.ESCALATED`), `APPROVAL.REQUESTED`/`PROVIDED`, `ESCALATION.TRIGGERED`.
- **Consumes:** none at runtime.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`ThreatModelDocumentType` AC1/AC2 fixture sweep** (`Tamma.Core.Tests`) — **owned jointly with
  41-1b; 41-19 must not ship without it.** One rejecting and one accepting fixture per rule, each
  asserting the **violation code**: a threat with no categorisation; a threat with no asset; a threat
  with no mitigation; a threat with high residual risk and an empty mitigation ⇒
  `UNMITIGATED_HIGH_RISK` naming the threat; and the positive control — a fully-mitigated model
  validates. **Covers AC1 (validation half), AC2 (the unrepresentable-state half).**
- **`ThreatModelWorkflowStructureTests`** (modelled on `TaskCreationWorkflowStructureTests`) — the six
  thinness clauses executable: exactly one `DispatchWorkflow` with literal def id
  `document-lifecycle`; zero `llm-call` dispatches; `OfType<Finish>()` empty; no
  `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid` variables;
  `TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches()` contains
  `(ThreatModelWorkflow, DispatchLifecycle, security, threat-model)` and `MaterializeDispatchInput`
  yields `documentType == "threat-model"` plus a declared `feedbackVariableName`;
  `DefinitionId == "threat-model"`, threads `TenantId`, one `ComputeReEntryPositionActivity`, no
  `Wait*`, `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. **Plus** a pin on the exact
  `FlowDecision` id set `{FreshRun, LifecycleAccepted}` (D4). **Covers AC1 (structure), AC4.**
- **`ThreatModelBindingHelperTests`** — `ReadCounts` on a valid payload / unreadable JSON → `(0,0)`;
  `ResolveParentDocumentId` across found/not-found/supplied-but-unreadable;
  `BuildFailureDetail` names each reachable `DocumentLifecycleOutcome` wire
  (`review-undecidable`, `ambiguity-above-threshold`, `rounds-exhausted`, `validation-exhausted`) +
  `rejected`.
- **`ThreatModelAcceptancePolicyTests`** (D3) — `AcceptanceDefaults.For(DocumentTypeKey.ThreatModel)`
  returns the agreed security+architect row, **not** the `_ => Rules` base row. (This test is
  41-1b's AC5 obligation; 41-19 asserts the specific value it depends on.) Plus: a rules JSON
  carrying `{"kind":"document-type","key":"threat-model"}` in `AlwaysEscalate` drives
  `AcceptanceGuardrails.TryPreGate` to `Escalate(AlwaysEscalateClass)` — proving the *deployment*
  option of D2 works, while the story's default path does not use it.
- **Drift-guard runs (steps 6–8, self-verifying)** — full `ContractBindingTests` fixture green;
  `ResumableStandardStructuralTests` green with **no** `ThreatModelWorkflow` allowlist entry;
  `WorkflowInterfaceGraphTests` green at the bumped count. **Covers AC4.**
- **`ThreatModelLifecycleExecutionTests`** (Testcontainers, on the shared 39-6/39-10 fixture) —
  (a) **happy path:** seed an accepted `Design` for the issue; dispatch `threat-model`; scripted
  fully-mitigated draft → security-lens review approve → orchestrator `Accept` resume → outputs carry
  `parentDocumentId` = the seeded design's id and `unmitigatedHighRiskCount == 0`;
  `THREATMODEL.STARTED`/`.DRAFTED`/`.ACCEPTED` present with matching `issueId` tags alongside the
  `DOCUMENT.*` family. **Covers AC1.**
  (b) **AC2 end-to-end, the flagship:** a draft carrying a high-residual-risk threat with no
  mitigation is rejected at VALIDATE with `UNMITIGATED_HIGH_RISK` and drives a repair/revise round;
  an always-invalid stub exhausts the ring and exits as a typed `validation-exhausted` escalation
  with lineage (`ESCALATION.TRIGGERED` payload asserted) plus `THREATMODEL.FAILED`; workflow output
  `status = escalated`; **no `Finish` reached, and no `DOCUMENT.ACCEPTED` anywhere on the stream**.
  **Covers AC2.**
  (c) **belt-and-braces:** a *representable* model whose security reviewer raises a
  critical-severity issue — an orchestrator-side `Accept` is clamped to `Escalate`
  (`BlockingReviewViolation`) by the existing guardrail; assert the escalation reason wire.
  **Covers AC2 (second mechanism).**
  (d) **AC3:** the accepted `ThreatModel` is retrievable by `issueId` through the 39-11 repository
  read, and a `plan-generation`-shaped consumer reads it (assert the read + the consumer input shape,
  not a rewiring of `plan-generation` — Correction 5). **Covers AC3.**
  (e) **re-entry:** crash after acceptance → fresh `threat-model` dispatch for the same issue
  re-enters at `Complete`; exactly one `DOCUMENT.ACCEPTED` and one `THREATMODEL.ACCEPTED` on the
  stream. **Covers AC4.**
  (f) **reviewer lens (Correction 3):** assert the dispatched reviewer pair for the `security`
  panellist is `(security, plan-review-security)` — the existing selector, no new arm.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — thin binding; `ThreatModel` validated (categorisation, mitigation + residual per threat) | 5 (D1/D4), 0 (41-1b) | `ThreatModelWorkflowStructureTests`; `ThreatModelDocumentType` fixture sweep; execution (a) |
| 2 — unmitigated high-risk cannot be accepted silently; typed escalation | 0 (D2), 5 | `ThreatModelDocumentType` fixture (`UNMITIGATED_HIGH_RISK`); execution (b) + (c) |
| 3 — consumable by `plan-generation` / 41-15 via 39-11 | 5 | execution (d) |
| 4 — resume declared; 39-10 green without allowlist | 5 (D6) | `ResumableStandardStructuralTests` |
| 4b — *(added, rule 1(f) — Correction 7)* interface row declared + edge pin bumped | 7 | `WorkflowInterfaceGraphTests` at the bumped count |
| 4c — *(added, Correction 8)* cell classified in `Bindings` with the typed authority | 6 | full `ContractBindingTests` fixture |

## Risks & Mitigations

- **The whole story is gated on 41-1b, and its most important rule (D2's `UNMITIGATED_HIGH_RISK`)
  lives in 41-1b's code.** Largest risk. Mitigation: step 0 is a real gate, not a formality; the rule
  is named in this plan and in 41-1b's AC2 (which already says "a `ThreatModel` with an unmitigated
  high-risk threat and no escalation is rejected"); agree the violation-code string in lockstep
  before either story merges. Steps 3, 4 and the structure/helper tests can be built early against
  the pinned contract.
- **AC2 gets "solved" with an always-escalate class.** That contradicts the story's own 85–100
  autonomy row and would make the workflow useless at high autonomy. Mitigation: D2 records the
  rejection explicitly and the test suite pins both the real mechanism (validation) and the
  deployment option (the class) separately, so a reviewer can see they are different things.
- **`AcceptanceDefaults.For` silently falls through to a single-architect unanimous panel (D3).**
  A `ThreatModel` reviewed only by an architect is a real correctness bug that no test would catch,
  because the catch-all *compiles and runs*. Mitigation: `ThreatModelAcceptancePolicyTests` asserts
  the specific row; 41-1b's AC5 requires the arm.
- **Edge-pin and `ContractBindingTests` collisions with sibling Epic 41 stories.**
  `WorkflowInterfaceGraphTests.cs:45` and `ContractBindingTests.Bindings` are edited by every
  producing-workflow story in the epic. Mitigation: rebase-and-bump-last; comments name the story.
- **Story-vs-code tensions:** D6 (resume mode) and Corrections 5/7/8 (three obligations the story
  omits) deviate from or extend story text with reasons recorded. Correction 4 is a genuine
  mechanism gap in the platform, stated rather than papered over.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 0–1 | 41-1b gate verification + lockstep agreement on the `UNMITIGATED_HIGH_RISK` contract | 0.25 |
| 2 | `threat-model.md` rewrite onto the canonical wire | 0.4 |
| 3–4 | `ThreatModelEvents` + `ThreatModelBindingHelper` | 0.4 |
| 5 | `ThreatModelWorkflow` binding | 0.75 |
| 6–8 | Contract entry + registry row + edge-pin bump + drift-guard | 0.25 |
| 9 | Document-type fixture sweep (joint with 41-1b) + acceptance-policy test | 0.5 |
| 9 | Structure + helper tests | 0.4 |
| 9 | Testcontainers scenarios (a)–(f) | 0.9 |
| **Total** | | **3.85** (story estimate: 4 days — confirmed) |

## Blocks / Blocked by

- **Blocked by (hard, total): [41-1b](../story-41-1/41-1b-new-document-types.md)** — the
  `ThreatModel` `DocumentTypeKey` member, `ThreatModelDocumentType` (incl. the D2
  `UNMITIGATED_HIGH_RISK` rule), its registry registration, its two vocabulary count-pin bumps, and
  its `AcceptanceDefaults.For` arm. Nothing in this story — not even the human-assigned path — can
  run before it: an unregistered type is unparsable (`DOCUMENT.TYPE.UNKNOWN`) and unpersistable.
- **Blocked by:** Epic 39 — `document-lifecycle` (39-6), `document-review`/`review-panel` (39-7),
  the accept gate (39-8), the resume standard (39-10), the document store + lineage API (39-11).
  **All landed and verified in tree.**
- **NOT blocked by 41-1a** — `(security, threat-model)` already exists in `AgentAction.cs:88` and
  `RolePhaseMap.cs:129`, its prompt file ships, and the review lens needs no new selector arm
  (Corrections 2 and 3).
- **NOT blocked by 41-1c** — `ThreatModel` is a structured type, not prose.
- **Blocked by (soft, for the intended `consumes` edge): [41-10](../story-41-10/41-10-system-design-document.md)**
  — the accepted `Design` this workflow prefers to consume. 41-10 is itself blocked on 41-1a's
  `(architect, design-system)` cell. This story does **not** inherit that blocker: `Design` is
  optional (D5), scenario (a) seeds the design document directly into the store, and the run
  completes with `parentDocumentId = null` when none exists.
- **Blocks:** **41-15** (acceptance verification may verify against threat mitigations) and any
  future security-task-seeding work (D8, needs 39-17). Neither is a hard edge.
- **Shared-file register (coordinate before editing):** `ContractBindingTests.Bindings`
  (also 41-17, 41-18, 41-20, 41-21, 41-1a); `DocumentTypeRegistry.BuildSeed` +
  `WorkflowInterfaceGraphTests.cs:45` (every producing-workflow story in the epic — serialize the
  bumps); `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`;
  `AcceptanceDefaults.For` (41-1b owns the arm, 41-19 pins its value).
