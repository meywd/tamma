# Story 39-6: DocumentLifecycleWorkflow — generic produce/validate/review/revise/accept

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **workflow author (and the orchestrating workflows that compose issue cycles)**,
I want a **single generic Elsa sub-workflow implementing `produce → validate → review → revise (bounded) → accept` for any registered document type — parameterized by a producer dispatch spec, a document type key, and the resolved acceptance rules — that exits only on done or a typed unhandleable outcome, and emits a `DOCUMENT.*` DCB event on every transition**,
So that the quality lifecycle is written once instead of re-invented per workflow, every producing workflow gains review-with-notes and bounded revision for free, and failure is always a typed, lineage-carrying outcome — never a bare dead-end.

## Priority

P0 — This is the epic's centerpiece: the "one lifecycle, written once" pillar. 39-12 (pilot), 39-13/39-14/39-15 (family migrations) are all "re-point workflow X at this sub-workflow." Its outcome contract is what makes 39-8's escalation surface and 39-10's resumability standard uniform.

## Architectural Context (READ FIRST)

The workflow lands in `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs`, following the house `IWorkflow`-builder style of its siblings; supporting pure helpers in `Workflows/Helpers/` (the `ReviewAggregationHelper.cs` / `TriagePoDecisionHelper.cs` precedent: fail-closed logic in static, Elsa-free, unit-testable classes).

**The llm-call mediation invariant holds.** Every LLM interaction goes through dispatch of the `llm-call` workflow — `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` (`DefinitionId = "llm-call"`, inputs `agentRole`/`action`/`variables`) — exactly as the 29 existing dispatch sites do. The lifecycle's PRODUCE and REVISE steps are `llm-call` dispatches built from the producer spec; the lifecycle itself never calls a provider directly.

**Composition precedents to read:**

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` and `AdlOrchestratorWorkflow.cs` — how parent workflows dispatch sub-workflows by `WorkflowDefinitionId` and consume outputs
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs` — today's only real review/revision loop (discussion rounds, `ReviewAggregationHelper`, `ValidationFeedbackHelper.cs`); the lifecycle generalizes it, and 39-14 retires the bespoke copy
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs` — the pilot producer (39-12) whose shape the producer-spec input must comfortably express

**Event emission** follows the established activity-side pattern: events staged by activities are flushed through `apps/tamma-elsa/src/Tamma.Activities/Core/EventPersistenceMiddleware.cs` into the DCB store (`apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs`, `Entities/DomainEvent.cs`), tags carrying `issueId` per the CLAUDE.md convention. Event-type families per aggregate live beside their activities (e.g. `Tamma.Activities/Testing/TestingEvents.cs`, `Debug/DebugEvents.cs`) — a `DocumentEvents.cs` constants class follows that shape.

**Consumed contracts:** 39-2 envelope/state machine + registry, 39-3/39-4 validators, 39-5 acceptor contract (`AcceptanceDecision`, `IAcceptanceRulesResolver`, guardrail function), 39-7 review producers (single/panel — dispatched by definition id from the review step), 39-8 suspend/escalation surface (the gate BOTH acceptor paths resume through and the unhandleable-outcome sink), 39-17/39-18 (the orchestrator agent + the channels the ACCEPT stage publishes on).

## Acceptance Criteria

1. **Generic sub-workflow, dispatchable standalone.** `DocumentLifecycleWorkflow` registers with a stable `DefinitionId` (e.g. `"document-lifecycle"`) and runs from inputs alone: a **producer dispatch spec** (`agentRole` + `action` + `variables` for the `llm-call` producing the draft), a **document type key** (resolved fail-loud against `DocumentTypeRegistry`), lineage anchors (`issueId`, `correlationId`), and an acceptance-rules reference (resolved effective rules or resolver key). A parent workflow — or an API test dispatching it directly — can run one lifecycle with no other scaffolding.

2. **The five stages per the README diagram.** PRODUCE (dispatch `llm-call` with the producer spec) → VALIDATE (the type's `Validate`; on violations, a **bounded repair turn** feeding domain-phrased violations back via `llm-call` — the innermost ring; full deterministic-repair sophistication is 39-9, but the bounded retry seam exists here) → REVIEW (dispatch the 39-7 producer, yielding a validated `Review` document) → on concerns, REVISE (bounded rounds: re-dispatch the producer with the `Review`'s notes — summary + issues with suggested fixes — included in `variables`) → ACCEPT (**always submits to the orchestrator**: build the `AcceptanceRequest` — document + `Review` + lineage + resolved rules incl. autonomy level — publish it on the workflow↔orchestrator channel (39-18), and suspend on the 39-8 gate until the `AcceptanceDecision` resumes it. The orchestrator (39-17) decides itself or assigns the decision to a 39-20-eligible user per the autonomy level — the lifecycle neither knows nor cares which: never an if-else that skips the decision, never an embedded accept-decision `llm-call`, and the 39-5 guardrail function wraps whichever actor answers).

3. **Exit contract: done or typed unhandleable outcome, nothing else.** The workflow's outputs are exactly: `Accepted(documentId)` — or one of `ReviewUndecidable`, `AmbiguityAboveThreshold`, `RoundsExhausted`, `ValidationExhausted` (closed outcome enum in `Tamma.Core/Documents`), each carrying the full document lineage (envelope ids of every draft, every `Review`, rounds used, last violations). No bare exception path: an unexpected internal failure surfaces as a typed outcome with lineage, never a silent workflow fault. Parent workflows branch on the outcome enum — a drift test pins the outcome set.

4. **Bounded loops, provably.** Revision rounds and validation-repair attempts respect the 39-5 rules bounds; helper-level unit tests (Elsa-free, in the `Workflows/Helpers` style) prove termination for arbitrary verdict/violation sequences within `MaxRevisionRounds`/`MaxValidationRepairAttempts` — exhaustion yields `RoundsExhausted`/`ValidationExhausted` with lineage.

5. **`DOCUMENT.*` DCB events on every transition.** Following the `AGGREGATE.ACTION.STATUS` convention: at minimum `DOCUMENT.PRODUCED.SUCCESS`/`.FAILED`, `DOCUMENT.VALIDATED.SUCCESS`/`.FAILED`, `DOCUMENT.REVIEW_REQUESTED`, `DOCUMENT.REVIEWED`, `DOCUMENT.REVISION_STARTED`, `DOCUMENT.ACCEPTED`, `DOCUMENT.REJECTED`, `DOCUMENT.ESCALATED` — constants in a `DocumentEvents.cs` class, every event tagged with `issueId`, `documentId`, `documentType`, `round`, flowing through the existing `EventPersistenceMiddleware` path. Replaying the `DOCUMENT.*` stream for an issue reconstructs the lifecycle's transition history (asserted in a test).

6. **Envelope state transitions are legal.** Every state change goes through the 39-2 `DocumentStateMachine` (`Draft → Validated → Reviewed → Accepted/Rejected/Escalated`); an attempted illegal transition is a bug that fails loud in tests, not a silent overwrite.

7. **The mediation invariant is enforced by test.** A test (extending the `ContractBindingTests`/`TaxonomyDriftBuildTests.EnumerateAllDispatchPairs` reflection over the compiled workflow graph) asserts `DocumentLifecycleWorkflow`'s LLM interactions occur only via `llm-call` dispatches, and that its dispatched `(role, action)` pairs are bound or allowlisted per the existing coverage guard.

8. **Integration test, one full cycle.** An NUnit integration test (Testcontainers Postgres, stubbed `llm-call` responses in the established test style) drives one type (e.g. `Decomposition`) through: invalid draft → repair → valid → review with concerns → revise → approve → a stubbed orchestrator consumer receives the published `AcceptanceRequest` (asserting it carries the resolved rules incl. autonomy level + full lineage — the request was actually published and suspended on, not short-circuited) and answers `Accept` through the resume path; a variant where the stub instead assigns to a user and the stubbed user decides; and a second path to each unhandleable outcome class — asserting emitted events, final envelope state, and the outcome payload's lineage completeness.

9. **No existing workflow rewired.** No current producer is migrated in this story (39-12 pilots that). Diff surface: the new workflow + helpers + `DocumentEvents.cs` + `WorkflowVersions.cs` registration + tests.

## Technical Notes

- **Keep decision logic out of the Elsa graph.** Stage transitions, round accounting, note-composition for revise turns, and outcome construction belong in pure helpers (`DocumentLifecycleHelper` beside `ReviewAggregationHelper.cs`) so the fail-closed behavior is unit-testable without a workflow runtime — the house lesson `TriagePoDecisionHelper.cs`'s doc comment records.
- The REVIEW step consumes 39-7's producer by definition id; until 39-7 lands, develop against a stub review workflow honoring the same contract (the two stories should land adjacently — coordinate the definition-id contract early).
- The accept step registers ONE bookmark via the 39-8 generalized gate (bookmark name folding tenant + the decision-session id, per the `DesignResumeEndpoint.cs` posture) regardless of who ends up deciding — orchestrator self-decision and orchestrator-assigned human decision resume the same gate. The accept step contributes NO dispatch pair to the mediation-invariant test (it makes no `llm-call`); the invariant continues to cover the produce/repair/revise/review dispatches.
- `AmbiguityAboveThreshold` is rules-triggered (39-5 threshold vs. an `AmbiguityAssessment` score) — the lifecycle raises it when the produced/associated assessment crosses the configured threshold; it is listed here because the outcome enum is owned by this story.
- Resumability hardening (crash re-entry from latest accepted state) is 39-10; this story must not *preclude* it — persist enough via envelopes + events that 39-10 can reconstruct, and keep all loop state in workflow variables Elsa persists.

## Dependencies

- **Prerequisite:** 39-2 (envelope/state/registry/outcome home), 39-3 + 39-4 (validators and `Review`), 39-5 (acceptor contract + rules + guardrails).
- **Lockstep:** 39-7 (review producer contract), 39-8 (suspend/resume + escalation sink for the accept gate and unhandleable outcomes), 39-18 (the channels the ACCEPT stage publishes on; a stub consumer suffices until 39-17's agent lands).
- **Feeds:** 39-9 (repair ring plugs into the VALIDATE seam), 39-10 (resumability standard over this workflow), 39-12..39-15 (migrations), 39-11 (store persists what this emits).

## Estimated Effort

6–8 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
| 2026-07-20 | 1.1.0   | ACCEPT stage redesigned per review: always submits to the 39-5 acceptor (orchestrator in full-auto via `decide-acceptance` dispatch with injected rules; human in supervised via 39-8) — never an if-else that skips the decision | Claude |
| 2026-07-20 | 1.2.0   | ACCEPT transport redesign: publish `AcceptanceRequest` on the 39-18 channel (orchestrator agent 39-17 in full-auto; user channel in supervised) + suspend on the 39-8 gate — no accept-decision `llm-call`; both acceptor paths structurally identical | Claude |
| 2026-07-20 | 1.3.0   | Single routing path: every `AcceptanceRequest` goes to the orchestrator, which decides itself or assigns to an eligible user per the autonomy level (70–100); the lifecycle is routing-agnostic | Claude |
