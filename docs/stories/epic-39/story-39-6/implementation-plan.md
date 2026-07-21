# Implementation Plan — Story 39-6: DocumentLifecycleWorkflow — generic produce/validate/review/revise/accept

## Scope & Deliverable

When this story is done, `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` exists as a generic Elsa sub-workflow (`DefinitionId = "document-lifecycle"`) that runs the five-stage lifecycle PRODUCE → VALIDATE (bounded repair) → REVIEW → REVISE (bounded) → ACCEPT for any registered document type, driven purely by inputs (producer dispatch spec, document type key, lineage anchors, resolved acceptance rules). All stage/round/outcome logic lives in a pure, Elsa-free `DocumentLifecycleHelper`; every transition emits a `DOCUMENT.*` DCB event via a new `Tamma.Activities/Documents/` event family; the ACCEPT stage publishes an `AcceptanceRequest` through a publisher seam and suspends on the 39-8 gate — it contains no accept-decision `llm-call` and no branch that skips the decision. The workflow exits only as `Accepted(documentId)` or one of the four `DocumentLifecycleOutcome` values, each carrying full lineage. No existing workflow is rewired (39-12 pilots that).

## Pre-Reading

- `docs/stories/epic-39/story-39-6/39-6-document-lifecycle-workflow-generic-produce-validate-review-revise-accept.md` — the story (source of truth for ACs)
- `docs/stories/epic-39/README.md` — lifecycle diagram, "the acceptor is an actor, not a branch", "autonomy is a dial"
- `docs/stories/epic-39/story-39-2/implementation-plan.md`, `story-39-3/implementation-plan.md`, `story-39-4/implementation-plan.md` — the planned `Tamma.Core/Documents` contracts this story consumes (see NOT FOUND note below)
- `docs/stories/epic-39/story-39-5/39-5-acceptance-policy-per-mode-accept-escalation-configuration.md` — `AcceptanceRules`, `AcceptanceDecision`, guardrail function (prerequisite contract)
- `docs/stories/epic-39/story-39-8/39-8-escalation-and-approval-surface-events-suspend-resume-lineage.md` — `WaitForDocumentDecisionActivity` + `DocumentDecisionResumeEndpoint` (lockstep partner)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` — `DefinitionId = "llm-call"`, inputs `role`/`action`/`variables`/`tenantId`/`enableTools`, outputs `success`/`llmResponse`/`workflowOutput`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs` — today's only review/revision loop (round vars, `maxRounds`, `ForceNeedsHuman` exhaustion) — the thing this generalizes
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs` — the pilot producer shape (`ReadSuccessFlag`, fail-closed `FlowDecision`, `Emit*EventActivity` per transition); the producer-spec input must express this workflow's dispatch comfortably
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`, `AdlOrchestratorWorkflow.cs` — parent-side `DispatchWorkflow(WorkflowDefinitionId, WaitForCompletion=true, Result)` consumption
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewAggregationHelper.cs`, `TriagePoDecisionHelper.cs`, `ValidationFeedbackHelper.cs` — the pure fail-closed helper style (and `AppendFeedback` for violation feedback into declared variables)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/WorkflowVersions.cs` — `builder.Version = WorkflowVersions.ComputedVersion` (hash auto-bumps when the new file lands)
- `apps/tamma-elsa/src/Tamma.Activities/Decomposition/DecompositionEvents.cs` + `EmitDecompositionEventActivity.cs` — the event-catalogue + emit-activity pair `DocumentEvents.cs` copies
- `apps/tamma-elsa/src/Tamma.Activities/Testing/TestingEvents.cs`, `Debug/DebugEvents.cs` — sibling catalogues (`StatusForEvent`, `ParseTenantId`)
- `apps/tamma-elsa/src/Tamma.Activities/Core/TammaActivity.cs` (`TammaEvent`/`TammaEventEmitter`) + `Core/EventPersistenceMiddleware.cs` — the durable drain events flow through
- `apps/tamma-elsa/src/Tamma.Activities/Design/WaitForDesignApprovalActivity.cs` + `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DesignResumeEndpoint.cs` — the tenant-folded bookmark gate 39-8 generalizes (bookmark-name parity, 404/409 posture, `ResumeInput.AsBool`)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs`, `ContractBindingTests.cs` — the dispatch-pair reflection + coverage guard AC7 extends
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/IssueDecompositionWorkflowStructureTests.cs` (structure-test style, `WorkflowTestHelper`), `TriageItemCycleApplyFaultExecutionTests.cs` + `tests/Tamma.Activities.Tests/Core/EventPersistencePipelineTests.cs` (real-`IWorkflowRunner` execution harness with capturing event client)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`, `Entities/DomainEvent.cs` — the DCB store rows the drain writes
- **NOT FOUND (planned, not yet implemented):** `apps/tamma-elsa/src/Tamma.Core/Documents/` (39-2: `DocumentEnvelope`, `DocumentStateMachine`, `DocumentLifecycleOutcome`, `DocumentTypeRegistry`, `IDocumentType`, `DocumentJson`), `Tamma.Core/Documents/Types/` (39-3/39-4: validators + `Review`/`ReviewDecision`), `Tamma.Core/Documents/Policy/` (39-5: `AcceptanceRules`, `AcceptanceDecision`, `IAcceptanceRulesResolver`, guardrails), `Tamma.Activities/Documents/` (39-8 gate). These are hard prerequisites — see Dependencies & Sequencing.

## Design Decisions

- **D1 — All decision logic in `DocumentLifecycleHelper`; the Elsa graph only routes.** Stage transitions, round accounting, violation-feedback composition, ambiguity-threshold checks, and outcome/lineage assembly are pure static functions over a serializable `LifecycleState` record held in ONE workflow variable as JSON (`DocumentJson.Options`). Elsa persists workflow variables across suspend/restart, which is exactly what 39-10 needs (technical note: "keep all loop state in workflow variables Elsa persists"). Precedent: `TriagePoDecisionHelper` (fail-closed, Elsa-free) — vs. `PlanReviewWorkflow`'s 30+ scattered variables, the anti-pattern this story retires.
- **D2 — Producer dispatch spec = `producerRole` + `producerAction` + `producerVariablesJson` inputs, validated fail-loud at Init.** The generic workflow's `(role, action)` is data, so Init parses them via `AgentRoleExtensions.Parse`/`AgentActionExtensions.Parse` and asserts `RolePhaseMap.IsRoleEligibleForPhase` (throw `TammaError DOCUMENT.LIFECYCLE.INVALID_PRODUCER` on failure) — the taxonomy check moves from build-time constants to a fail-loud runtime parse, compensated by D3. `documentType` resolves fail-loud through `DocumentTypeRegistry.Resolve` (AC1). `IssueDecompositionWorkflow`'s dispatch is expressible as `("senior_developer", "decompose-issue", {workItemJson, findings, conventions})` — the 39-12 pilot check.
- **D3 — Mediation-invariant test via a justified data-driven allowlist, not fake defaults.** `TaxonomyDriftBuildTests` materializes dispatch `Input` delegates against declared variable defaults; the lifecycle's role/action variables default to `""`, so its three `llm-call` dispatch sites (produce, repair, revise) cannot materialize a constant pair. Do NOT paper over this with a fake default pair. Instead add an explicit `DataDrivenDispatchAllowList` set beside `NonMaterializableSupplement`, keyed `(Workflow, DispatchId)`, each entry documenting that the pair is input-driven AND runtime-validated (D2); the coverage guard is extended so a data-driven dispatch must be in this list, and a list entry must correspond to a workflow whose Init provably parses role/action (asserted by the structure test, step 8). `ContractBindingTests`: the same three sites join the intentionally-unbound allowlist with the justification "contract is carried by the producer's own cell, already bound by the producing family's entries". This satisfies AC7's "bound or allowlisted per the existing coverage guard". The REVIEW step dispatches a *workflow* (39-7's producer), not `llm-call`, and the ACCEPT stage dispatches nothing — both contribute zero pairs (story technical note).
- **D4 — Acceptance rules arrive as a server-resolved input, defaults as fallback.** Input `acceptanceRulesJson` carries the serialized effective `AcceptanceRules` (resolved by the parent/API via `IAcceptanceRulesResolver` — the engine never resolves per-principal storage itself, matching 39-5's "39-6 should depend only on the model + the resolver interface" and the conventions-resolution discipline in `LlmCallWorkflow`). Empty input → `AcceptanceRules` static defaults (autonomy 70, conservative bounds) so a bare dispatch is safe (39-5 AC5). The resolved rules ride inside the `AcceptanceRequest` verbatim (39-5 AC3b).
- **D5 — ACCEPT = persist-shaped publish + ONE gate; both routing outcomes resume it.** The stage sequence is: build `AcceptanceRequest` (document + `Review` + lineage + resolved rules incl. autonomy level + decision-session id) → `PublishAcceptanceRequestActivity` (publisher seam, D6) → suspend on 39-8's `WaitForDocumentDecisionActivity` (bookmark folds tenant + decision-session id) → decision arrives → 39-5 guardrail function wraps it → route on the (possibly clamped) `AcceptanceDecision`. There is no autonomy-level branch anywhere in the workflow — the orchestrator (39-17) decides WHO decides. The decision-session id IS the bookmark session id (39-18 technical note) and is minted at Init as an unguessable `Guid`.
- **D6 — Channel publish behind `IAcceptanceRequestPublisher`, stubbed until 39-18.** New interface (in `Tamma.Activities/Documents/`) with `Task PublishAsync(AcceptanceRequest request, CancellationToken ct)`; the activity resolves it via `context.GetService<T>()` (the `EventPersistenceMiddleware` service-resolution pattern — no captive dependency). This story registers a `LoggingAcceptanceRequestPublisher` (logs + no-op; the suspended gate still waits, matching 39-18's "no orchestrator connected ⇒ request waits, never defaulted"). 39-18 swaps in the outbox+SignalR implementation behind the same interface; tests use a capturing fake. The payload record is 39-5's canonical `AcceptanceRequest` (`Tamma.Core/Documents/Policy/`); 39-18's channel messages reuse it by reference (its D3) — one record, one name, no `AcceptanceRequestWire` variant.
- **D7 — Exit contract: helper-built `DocumentLifecycleResult`, no bare fault path.** New record in `Tamma.Core/Documents/DocumentLifecycleResult.cs` (owned by this story per story AC3 "outcome enum is owned by [39-2]; the lineage-carrying result is 39-6's"): `Status` (`accepted | rejected | escalated`), `Outcome` (`DocumentLifecycleOutcome?`, null on accept), `DocumentId`, and a `DocumentLineage` record (every draft envelope id + state, every `Review` id, rounds used, repair attempts used, last domain-phrased violations, effective-rules reference). Every stage-failure edge (llm-call `success=false`, unparseable payload, validator exhaustion, review failure) routes through fail-closed `FlowDecision`s into `DocumentLifecycleHelper.BuildOutcome(...)` — the `IssueDecompositionWorkflow.ReadSuccessFlag` posture generalized. Mapping: PRODUCE/repair `llm-call` failure or invalid payload consumes a repair attempt, exhaustion → `ValidationExhausted`; REVIEW producer failure or an unusable `Review` → `ReviewUndecidable`; revise rounds exhausted → `RoundsExhausted`; ambiguity score over the rules threshold → `AmbiguityAboveThreshold` (D8). All four exit `Escalated` envelope state (39-2 D4). Outputs: `status`, `outcome`, `documentId`, `lifecycleResult` (full JSON) — parents branch on `outcome`.
- **D8 — `AmbiguityAboveThreshold` checked post-VALIDATE.** `DocumentLifecycleHelper.CheckAmbiguityThreshold(typeKey, payload, rules)`: when the produced type is `ambiguity-assessment`, read its `score` and compare against `AcceptanceRules`' ambiguity threshold; additionally an optional `ambiguityScore` input lets a parent thread an associated assessment's score for non-assessment types. Over threshold → escalate before REVIEW (no point reviewing what policy already routes to a human).
- **D9 — Revision mints a NEW envelope; state transitions only via `DocumentStateMachine`.** Draft → `Validated` on validator pass; `Validated → Reviewed` when the `Review` lands; `Reviewed → Accepted` on `Accept`, `Reviewed → Rejected` on a human `Reject` (human-only — an orchestrator-channel `Reject` is clamped to `Escalate(RejectRequiresHuman)` by the 39-5 guardrail before it reaches the state machine); any typed outcome → `Escalated`. A revise turn creates a fresh `DocumentEnvelope.CreateDraft(..., supersedesDocumentId: prior.Id)` — never rewinds (39-2 D4). All transitions go through `envelope.WithState(...)` so an illegal transition throws `DOCUMENT.STATE.ILLEGAL_TRANSITION` (AC6). Envelopes live in the `LifecycleState` JSON; durable document-store persistence is 39-11 (events + workflow state suffice for 39-10 re-entry).
- **D10 — REVIEW consumes 39-7 by definition id `"document-review"`, overridable by input.** Input `reviewWorkflowDefinitionId` (default `"document-review"`) dispatched with `{documentJson, documentType, issueId, correlationId, tenantId, acceptanceRulesJson}`; expected outputs `success`, `reviewJson` (validated unified `Review` payload), `reviewDocumentId`. This is the definition-id contract to agree with 39-7 NOW (story technical note); the integration test registers a stub workflow honoring it.
- **D11 — Revise feedback goes into variables the producer's template declares.** `DocumentLifecycleHelper.BuildRevisionVariables(producerVariablesJson, review)` folds the `Review`'s summary + issues (severity/category/suggestedFix) as a delimited block appended via `ValidationFeedbackHelper.AppendFeedback`-style composition into a `revisionNotes` variable AND appended to the spec's designated feedback variable (input `feedbackVariableName`, default `revisionNotes`) — the `ValidationFeedbackHelper` lesson: a supplied-but-undeclared variable is silently dropped at render. Repair turns reuse the same mechanism with domain-phrased `DocumentViolation` messages.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentLifecycleResult.cs`** (new types; requires 39-2's namespace to exist):

   ```csharp
   namespace Tamma.Core.Documents;
   public sealed record DocumentLineage(
       IReadOnlyList<DraftRef> Drafts,          // record DraftRef(Guid Id, string State)
       IReadOnlyList<Guid> ReviewIds,
       int RoundsUsed, int RepairAttemptsUsed,
       IReadOnlyList<DocumentViolation> LastViolations,
       string? RulesReference);
   public sealed record DocumentLifecycleResult(
       string Status,                            // "accepted" | "rejected" | "escalated"
       DocumentLifecycleOutcome? Outcome,        // null iff Status == "accepted"/"rejected"
       Guid? DocumentId, DocumentLineage Lineage);
   ```

   All wire properties get explicit `[JsonPropertyName]` (39-2 D8 discipline); serialize via `DocumentJson.Options`.

2. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/DocumentEvents.cs`** — copy `Decomposition/DecompositionEvents.cs` file shape: the ten constants (see Events), `StatusForEvent` (`*.FAILED`, `DOCUMENT.ESCALATED`, `DOCUMENT.REJECTED` → `"error"`; `DOCUMENT.REVIEW_REQUESTED`, `DOCUMENT.REVISION_STARTED` → `"started"`; else `"success"`), `ParseTenantId` (mirrors `DecompositionEvents.ParseTenantId`).

3. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/EmitDocumentEventActivity.cs`** — copy `EmitDecompositionEventActivity` verbatim in structure: inputs `EventType`, `DocumentId`, `DocumentType`, `Round`, `IssueId`, `CorrelationId`, `SessionId`, `TenantId`, `Detail`, `DataJson`; pure static `BuildTammaEvent(...)` mapping tags (`issueId`, `documentId`, `documentType`, `round`, `correlationId`, `sessionId`, `tenantId`) and data; emits via `TammaEventEmitter.Emit` into `tamma:events` (drained durably by `EventPersistenceMiddleware`).

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/IAcceptanceRequestPublisher.cs` + `PublishAcceptanceRequestActivity.cs` + `LoggingAcceptanceRequestPublisher.cs`** (D5/D6). The activity takes `RequestJson` input (the serialized `AcceptanceRequest`), resolves the publisher via `context.GetService<IAcceptanceRequestPublisher>()`, and fails LOUD (TammaError `DOCUMENT.ACCEPT.PUBLISH_FAILED`) only if no publisher is registered; a publish transport error logs ERROR and continues (the gate still suspends; delivery is 39-18's outbox job). **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`**: register `LoggingAcceptanceRequestPublisher` as the default `IAcceptanceRequestPublisher`.

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/DocumentLifecycleHelper.cs`** — the pure core (doc-comment in the `TriagePoDecisionHelper` narrative style):

   ```csharp
   public static class DocumentLifecycleHelper
   {
       public sealed record LifecycleState(/* typeKey, issueId, correlationId, sessionId,
           drafts (id+state), reviewIds, round, repairAttempts, lastViolationsJson, rulesJson */);
       public static void ValidateProducerSpec(string role, string action, string typeKey); // D2, throws TammaError
       public static LifecycleState Init(...);                                              // from workflow inputs
       public static string BuildRepairVariables(string variablesJson, IReadOnlyList<DocumentViolation> v, string feedbackVar);
       public static string BuildRevisionVariables(string variablesJson, string reviewJson, string feedbackVar); // D11
       public static bool ShouldRepair(LifecycleState s, AcceptanceRules r);   // attempts < MaxValidationRepairAttempts
       public static bool ShouldRevise(LifecycleState s, AcceptanceRules r);   // round < MaxRevisionRounds
       public static bool IsAmbiguityAboveThreshold(string typeKey, string payloadJson, AcceptanceRules r, double? inputScore); // D8
       public static DocumentLifecycleResult BuildAccepted(LifecycleState s, Guid docId);
       public static DocumentLifecycleResult BuildRejected(LifecycleState s, Guid docId);
       public static DocumentLifecycleResult BuildOutcome(LifecycleState s, DocumentLifecycleOutcome o); // D7 lineage
   }
   ```

   Every function is total: unparseable inputs produce a typed failure result, never a throw out of a routing lambda (the `TriagePoDecisionHelper.ParseDecision` posture).

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs`** — house `WorkflowBase`/Flowchart style (copy skeleton from `IssueDecompositionWorkflow`, loop wiring from `PlanReviewWorkflow`): `builder.DefinitionId = "document-lifecycle"; builder.Version = WorkflowVersions.ComputedVersion;`. Graph: `Init` (read+validate inputs per D2/D4, mint sessionId, seed `LifecycleState`) → `DispatchProduce` (`DispatchWorkflow("llm-call")` with producer spec, `WaitForCompletion=true`) → `EmitProduced` (SUCCESS/FAILED) → `ValidateDraft` (SetVariable lambda delegating to `DocumentTypeRegistry.Resolve(typeKey).Validate(payload)` + `EmitValidated`) → `RepairCheck` (`FlowDecision` on `ShouldRepair`; True → `DispatchRepair` llm-call with `BuildRepairVariables` → back to `ValidateDraft`; False+invalid → `BuildOutcome(ValidationExhausted)`) → `AmbiguityCheck` (D8; over → `Escalated` path) → `EmitReviewRequested` → `DispatchReview` (D10) → `EmitReviewed` → `ReviseCheck` (`ReviewDecision.Approve` → ACCEPT; `RequestChanges`/`NeedsDiscussion` + `ShouldRevise` → `EmitRevisionStarted` → `DispatchRevise` (llm-call, `BuildRevisionVariables`, new superseding envelope) → back to `ValidateDraft`; exhausted → `BuildOutcome(RoundsExhausted)`; unusable review → `BuildOutcome(ReviewUndecidable)`) → **ACCEPT**: `BuildAcceptanceRequest` → `PublishAcceptanceRequestActivity` → `WaitForDocumentDecisionActivity` (39-8 gate; sessionId + tenantId inputs) → `ApplyGuardrails` (39-5 guardrail function in a SetVariable lambda) → route `Accept` → `EmitAccepted` + envelope→`Accepted`; `RequestRevision(notes)` → revise loop (counts a round; guardrail already converts over-budget to Escalate); `Escalate(reason)` → `EmitEscalated` + `BuildOutcome`. Terminal `SetOutputs` (`status`/`outcome`/`documentId`/`lifecycleResult`) → `Finish` — one output shape on every path, per D7.

7. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` and `ContractBindingTests.cs`** (D3): add `DataDrivenDispatchAllowList` with the three `(DocumentLifecycleWorkflow, DispatchProduce|DispatchRepair|DispatchRevise)` entries + guard extension; add the same sites to `ContractBindingTests`' justified allowlist. Also add `"DocumentLifecycleWorkflow"` handling so `ExpectedContributingWorkflows` stays truthful (it contributes no constant pairs — document why inline).

8. **CREATE structure tests** `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleWorkflowStructureTests.cs` — `WorkflowTestHelper.BuildWorkflow` topology assertions (see Test Plan).

9. **CREATE helper unit tests** `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleHelperTests.cs` (see Test Plan — the AC4 termination proofs live here).

10. **CREATE the execution/integration tests** `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/DocumentLifecycleExecutionTests.cs` — the `TriageItemCycleApplyFaultExecutionTests` harness (real `IWorkflowRunner`, event drain installed, capturing API client) extended with a registered workflow runtime so `DispatchWorkflow` resolves: register `DocumentLifecycleWorkflow`, a **`StubLlmCallWorkflow`** (`DefinitionId "llm-call"`, scripted per-call responses: invalid draft → repaired draft → revised draft), a **`StubDocumentReviewWorkflow`** (`DefinitionId "document-review"`, scripted `Review` JSON honoring D10), and a capturing `IAcceptanceRequestPublisher` fake; Elsa EF persistence (bookmark store) on Testcontainers Postgres; decisions injected by invoking 39-8's `DocumentDecisionResumeEndpoint.Resume` statics directly (the `DesignResumeEndpoint` test seam). See Test Plan for scenarios.

## Data & Migrations

None. Document persistence is 39-11; `DOCUMENT.*` events land in the existing `domain_events` table through the existing drain → `EventRepository` path. `dotnet ef migrations has-pending-model-changes` stays clean (no entity changes).

## Events

Constants in `Tamma.Activities/Documents/DocumentEvents.cs` (emitted; none consumed):

- `DOCUMENT.PRODUCED.SUCCESS` / `DOCUMENT.PRODUCED.FAILED`
- `DOCUMENT.VALIDATED.SUCCESS` / `DOCUMENT.VALIDATED.FAILED`
- `DOCUMENT.REVIEW_REQUESTED`
- `DOCUMENT.REVIEWED`
- `DOCUMENT.REVISION_STARTED`
- `DOCUMENT.ACCEPTED`
- `DOCUMENT.REJECTED`
- `DOCUMENT.ESCALATED`

Every event tagged `issueId`, `documentId`, `documentType`, `round` (+ `correlationId`, `sessionId`, `tenantId` when set). `APPROVAL.*`/`ESCALATION.*` are 39-8's — this story emits only `DOCUMENT.*` (39-8 hooks its family into the gate/publisher it owns).

## Test Plan

All NUnit + FluentAssertions (+ Moq for service fakes, Testcontainers for step 10), in `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/`.

- **`DocumentLifecycleHelperTests.cs`** (unit, Elsa-free) — `ValidateProducerSpec` accepts `("senior_developer","decompose-issue","decomposition")`, throws on unknown role/action, ineligible pair, unknown type key; `BuildRepairVariables`/`BuildRevisionVariables` append delimited blocks into the declared feedback variable only (byte-identical passthrough when no feedback — the `ValidationFeedbackHelper` contract); `IsAmbiguityAboveThreshold` for assessment payloads + threaded scores; **termination property tests**: for arbitrary generated sequences of verdicts/violations (approve/concerns/invalid in any order, seeded random, few hundred cases), driving `ShouldRepair`/`ShouldRevise`/`BuildOutcome` through the helper state machine always terminates within `MaxRevisionRounds`/`MaxValidationRepairAttempts` in one of {Accepted, Rejected, Escalated}, and exhaustion yields `RoundsExhausted`/`ValidationExhausted` with complete lineage (every draft id, review id, rounds used, last violations non-empty on validation exhaustion). **Covers AC3 (payload completeness), AC4.**
- **`DocumentLifecycleWorkflowStructureTests.cs`** (topology, `IssueDecompositionWorkflowStructureTests` style) — builds without error; `DefinitionId == "document-lifecycle"`; exactly three `DispatchWorkflow("llm-call")` nodes (produce/repair/revise) and one `DispatchWorkflow` whose definition id is variable-backed (review) — and **no other** LLM path (no `CallLlmInlineActivity` node); exactly one `WaitForDocumentDecisionActivity` and one `PublishAcceptanceRequestActivity`, with NO `FlowDecision` between publish and gate (the "never an if-else that skips the decision" structural pin) and no llm-call reachable from the ACCEPT region; an `EmitDocumentEventActivity` per transition with the pinned constant set; threads `TenantId`. **Covers AC1 (definition id/inputs), AC2 (stage graph), AC5 (emit sites), AC7 (structural half).**
- **`TaxonomyDriftBuildTests` / `ContractBindingTests` modifications** (step 7) — the coverage guards now fail if a lifecycle dispatch site is neither materializable nor in the data-driven allowlist, and the allowlist is cross-checked against the Init-validation structure test. A pin on `DocumentEvents` (exact ten constants) and on `DocumentLifecycleOutcome` (exactly 4 members — extends 39-2's `DocumentStateMachineTests` pin from the consumer side). **Covers AC3 (drift pin), AC5 (constants), AC7.**
- **`DocumentLifecycleExecutionTests.cs`** (integration, Testcontainers Postgres, step 10 harness) — scenario (a) full cycle on `decomposition`: invalid draft → `DOCUMENT.VALIDATED.FAILED` → repair → valid → review with concerns → `DOCUMENT.REVISION_STARTED` → revise → approve → captured `AcceptanceRequest` asserts resolved rules incl. autonomy level + full lineage + sessionId, and the instance is genuinely SUSPENDED (bookmark exists, not short-circuited) → resume with `Accept` → final state `Accepted`, `DOCUMENT.ACCEPTED` emitted, outputs carry `documentId`; (b) assigned-user variant: the stub "orchestrator" answers by resuming with a decider identity of an assigned user — asserting both paths resume the SAME bookmark; (c) `ValidationExhausted` (always-invalid stub) and `RoundsExhausted` (always-concerns stub) each: typed outcome output + `DOCUMENT.ESCALATED` + lineage completeness; (d) replay assertion — the captured `DOCUMENT.*` stream for the issue, ordered by timestamp, reconstructs the exact transition history (AC5's replay clause); (e) forged-approval guardrail: resume `Accept` against a blocking `Review` → decision clamped to `Escalate` (39-5 AC8 consumed here). **Covers AC2, AC3 (runtime), AC5 (replay), AC6 (state transitions asserted per event), AC8.**
- **AC6 negative pin** — a helper/unit test drives an illegal `WithState` (e.g. `Draft → Accepted`) through the lifecycle's transition seam and asserts `TammaError DOCUMENT.STATE.ILLEGAL_TRANSITION` (loud, not overwritten).

## Definition of Done

| AC | Satisfied by | Verified by |
|---|---|---|
| 1 — generic, dispatchable standalone from inputs alone, fail-loud type key | Steps 5, 6 (Init/D2/D4) | StructureTests (definition id, inputs); ExecutionTests (a) dispatches with no scaffolding; HelperTests (`ValidateProducerSpec`) |
| 2 — five stages incl. bounded repair seam and always-submit ACCEPT | Step 6 (graph), 4 (publisher), 5 (bounds) | StructureTests (stage graph, publish→gate with no branch, no accept llm-call); ExecutionTests (a)(b) |
| 3 — exit = Accepted or typed outcome with lineage, drift-pinned | Steps 1, 5 (D7), 6 | HelperTests (lineage completeness); ExecutionTests (c); outcome-enum pin (step 7 tests) |
| 4 — provably bounded loops | Step 5 | HelperTests termination property tests; ExecutionTests (c) |
| 5 — `DOCUMENT.*` on every transition, tagged, replayable | Steps 2, 3, 6 | StructureTests (emit sites); constants pin; ExecutionTests (d) replay |
| 6 — legal state transitions only | Step 5/6 via 39-2 `WithState` (D9) | AC6 negative pin; ExecutionTests final-state asserts |
| 7 — mediation invariant enforced by test | Steps 6, 7 (D3) | TaxonomyDrift/ContractBinding modifications + StructureTests no-other-LLM-path |
| 8 — integration test, full cycle + both routing variants + unhandleable paths | Step 10 | ExecutionTests (a)–(e) |
| 9 — no existing workflow rewired; bounded diff | All steps touch only new files + Program.cs registration + the two test-guard files | Reviewer diff inspection; `ExpectedContributingWorkflows` untouched except documented addition |

## Dependencies & Sequencing

- **Hard prerequisites (must merge first):** 39-2 (`DocumentEnvelope`/`DocumentStateMachine`/`DocumentLifecycleOutcome`/`DocumentTypeRegistry`/`DocumentJson` — none implemented yet), 39-3 (at least `DecompositionDocumentType` for the pilot type), 39-4 (`Review`/`ReviewDecision`), 39-5 (`AcceptanceRules` + defaults, `AcceptanceDecision`, guardrail function — the model half only; the resolver/admin API halves are NOT needed by this story thanks to D4).
- **Lockstep — 39-8:** this story's ACCEPT stage calls `WaitForDocumentDecisionActivity` and its tests call `DocumentDecisionResumeEndpoint`. Agree ownership early: 39-8 lands the gate + endpoint first (preferred), or 39-6 ships the minimal activity + bookmark-name builder in `Tamma.Activities/Documents/` (copying `WaitForDesignApprovalActivity`/`DesignResumeEndpoint` byte-for-byte in posture) and 39-8 extends it with `APPROVAL.*`/`ESCALATION.*` events. Either way ONE bookmark-name builder shared suspend/resume.
- **Lockstep — 39-7:** only the definition-id contract (`"document-review"`, D10 input/output names) must be agreed now; the stub review workflow in step 10 carries development until 39-7 lands.
- **Stubbed — 39-18:** `IAcceptanceRequestPublisher` + `LoggingAcceptanceRequestPublisher` (D6); the test fake captures requests. 39-17's agent is entirely out of scope — the "orchestrator" in tests is the resume caller.
- **Feeds:** 39-9 (plugs into the `BuildRepairVariables`/`ShouldRepair` seam), 39-10 (`LifecycleState` in one persisted variable + bookmark posture), 39-11 (persists what `DOCUMENT.*`/envelopes emit), 39-12..15 (migrations onto this workflow).

## Risks & Mitigations

- **Prerequisite stack (39-2/3/4/5) is plan-only today** — the largest schedule risk. Mitigation: this plan cites only contracts pinned in those stories' plans/canon (names verified against them); any drift there is a mechanical rename here. Do not start step 1 until 39-2 compiles.
- **Full-runtime integration test is the heaviest single artifact in the epic so far** (dispatcher + registered sub-workflows + bookmarks + Postgres — the `TriageItemCycleApplyFaultExecutionTests` doc-comment explicitly called end-to-end "far heavier"). Mitigation: the harness is reusable by 39-8/39-12 (build it as a shared fixture class); scope scenarios to AC8's list, nothing more; the helper property tests carry the correctness burden so the integration test only proves wiring.
- **Drift-test coverage guard weakening (D3).** A data-driven allowlist is a hole if unguarded. Mitigation: the allowlist requires a matching structure-test assertion that Init validates role/action fail-loud; the guard fails on stale or unjustified entries (the `KnownContractViolations` ratchet discipline).
- **ACCEPT-stage regression to a branch under future edits.** Mitigation: the structural pin (publish→gate adjacency, no `FlowDecision`, no llm-call in the ACCEPT region) makes the regression a test failure, not a review catch.
- **Story-vs-canon tension noted:** none found — story 1.3.0 and the canon block agree (single orchestrator routing path; ACCEPT is the deliberate non-LLM exception). The only gap the plan fills: the story doesn't name the lineage record — `DocumentLifecycleResult` (D7) is NEW here, in `Tamma.Core/Documents` beside the outcome enum it wraps.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | `DocumentLifecycleResult` + `DocumentLineage` | 0.5 |
| 2–3 | `DocumentEvents` + `EmitDocumentEventActivity` | 0.5 |
| 4 | Publisher seam + activity + registration | 0.5 |
| 5 | `DocumentLifecycleHelper` (state machine, feedback, outcomes) | 1.25 |
| 6 | `DocumentLifecycleWorkflow` flowchart | 1.5 |
| 7 | Drift-guard extensions (taxonomy + contract-binding) | 0.5 |
| 8–9 | Structure tests + helper unit/property tests | 1.25 |
| 10 | Execution/integration harness + scenarios (a)–(e) | 1.5 |
| — | 39-7/39-8 contract coordination, review polish | 0.5 |
| **Total** | | **8.0** (story estimate: 6–8 days) |
