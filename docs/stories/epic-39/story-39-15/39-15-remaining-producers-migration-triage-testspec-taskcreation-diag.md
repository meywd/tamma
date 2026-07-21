# Story 39-15: Remaining Producers Migration — Triage, TestSpec, TaskCreation, Diagnosis

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

As the **Epic 39 program closing out the migration**,
I want the remaining document producers — the Triage family, TestCaseCreation, TaskCreation, and Debug diagnosis — rebuilt on `DocumentLifecycleWorkflow` producing typed `TriageDecision`, `TestSpec`, `Plan`(tasks), and `Diagnosis` documents,
So that every producing workflow in the platform declares `consumes`/`produces`, no per-workflow ok/no parse pattern survives anywhere, and the build-time graph check covers the whole workflow surface.

## Priority

P2 — Last migration wave. Sequenced after 39-12/39-13/39-14 have hardened the lifecycle on richer loops; these producers are mostly simpler (produce + validate + light review), so the wave is broad rather than deep. Finishing it is what makes the epic's claims universal — one straggler with a bespoke parser keeps the "implicit micro-language" class of bug alive.

## Architectural Context (READ FIRST)

**Triage family (all `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`):** `IssueTriageWorkflow.cs`, `TriageContextGatheringWorkflow.cs`, `TriagePanelReviewWorkflow.cs`, `TriagePODecisionWorkflow.cs`, `TriageItemCycleWorkflow.cs` — today's decisions are parsed by the **`Triage*Helper` hand parsers** in `Workflows/Helpers/`: `TriageContextHelper.cs`, `TriagePanelAggregationHelper.cs`, `TriagePoDecisionHelper.cs`, `TriageItemCycleHelper.cs` (tests: `tests/Tamma.Activities.Tests/Workflows/Triage*Tests.cs`). Their classification/verdict shape knowledge moves into the `TriageDecision` type (39-4: closed enums for every classification field; reasoning required); the panel aggregation moves onto 39-7's unified Review/panel machinery.

**Single producers:**
- `TestCaseCreationWorkflow.cs` → `TestSpec` (39-4: each case bound to a task ID; one behavior per case). Carries the same retired-pattern fingerprints as the planning family: a `ValidationErrors` variable (line ~53), `ValidationFeedbackHelper.AppendFeedback` retry feedback (line ~103), and an `OutErr` error terminal (line ~231) — all deleted in favor of lifecycle rings.
- `TaskCreationWorkflow.cs` → task breakdown documents (the `Plan`-family shape per 39-4's mapping of `create-tasks`; same `ValidationErrors`/`OutErr` plumbing at lines ~56/~108/~241 to delete).
- `DebuggingWorkflow.cs` (+ its use inside `TddWithDebugRetryWorkflow.cs` / `CiWithDebugRetryWorkflow.cs`) → `Diagnosis` (39-4: hypotheses ranked by confidence; fix references affected files). Only the **diagnosis production** step migrates — the surrounding debug/retry orchestration (build, test, apply-fix loops) is code-side and stays; it consumes the typed `Diagnosis`.

**The pattern being eradicated:** each of these ends in a private `parse-ok? → done : dead` (or lenient-fallback) branch. After this story, zero workflows dispatch a document-producing `llm-call` outside a lifecycle binding, and the 39-1/39-6 build-time graph test can assert the universal property.

## Acceptance Criteria

1. **Triage onto the lifecycle.** The triage workflows are rebuilt as lifecycle bindings producing `TriageDecision` documents (context-gathering feeding `Findings` where it produces research-shaped output — via a new `triage-context-scan` cell split off from `context-scan`, see AC5; panel review via 39-7; the PO decision as the accept gate's policy/human decision per mode). The four `Triage*Helper` parsers are retired or reduced to adapters over the typed deserializer; closed-enum classification invalidity is now a validator failure, not a parse branch.

2. **TestCaseCreation onto the lifecycle.** Binding declares `consumes: [Plan]` / `produces: TestSpec`; the `ValidationErrors` retry plumbing and `OutErr` terminal are deleted; task-ID binding violations (a case referencing a nonexistent task) are validator failures flowing through the rings. The accepted `TestSpec` is persisted with lineage to its `Plan`.

3. **TaskCreation onto the lifecycle.** Same treatment: lifecycle binding with declared `consumes`/`produces`, bespoke retry/terminal plumbing deleted, output persisted through the 39-11 store.

4. **Debug diagnosis onto the lifecycle.** `DebuggingWorkflow`'s diagnosis production becomes a lifecycle binding producing `Diagnosis`; the TDD/CI retry orchestrators consume the accepted `Diagnosis` document (typed read from the store) instead of an informal parse. The retry orchestration loops themselves are untouched. Existing debug event types keep emitting alongside `DOCUMENT.*`.

5. **Universal declaration achieved.** After this story, the build-time graph test (39-1 audit map + 39-6 declaration check) asserts: **every** workflow that dispatches a document-producing `llm-call` is a lifecycle binding with declared `consumes`/`produces`, every producer/consumer edge type-checks, and the 39-10 structural test's allowlist is **empty**. The `ContractBindingTests` free-text allowlist shrinks to only genuinely-prose cells (tech-writer class). **No taxonomy cell serves both a document contract AND a free-text feed:** the `(developer, context-scan)` cell — formerly dispatched both by the Findings-producing triage-context use and, free-text, by the unmigrated `ContextGatheringWorkflow` — is SPLIT. The Findings producer gets a new `triage-context-scan` action bound to `FindingsDocumentType.Validate`; `ContextGatheringWorkflow` keeps `context-scan` as a free-text feed (NOT migrated), which is now contract-clean because it no longer shares a cell with a document producer. Universality holds for the split: every document-producing cell is contract-backed, and free-text cells serve only free-text feeds.

6. **Events preserved per family.** Each migrated workflow's existing event vocabulary (triage, test-creation, task-creation, debug event types) continues at equivalent transitions alongside `DOCUMENT.*`; per-family replay tests assert both streams with matching `issueId` tags.

7. **Resumable, all of them.** All new bindings declare resume behavior and pass the structural test; at least one crash-re-entry integration test covers this wave (suggested: TriageItemCycle mid-panel, the most stateful member).

8. **Test migration, none skipped.** `Triage*HelperTests`, `TriagePanelReviewWorkflowTests`, `TriagePODecisionWorkflowTests`, `TriageItemCycleRoutingTests`, `TddWithDebugRetryWorkflowTests`, `DebuggingWorkflowTests`, and the test/task-creation suites are rewritten or ported to document-type validator tests; full `dotnet test` passes with nothing disabled.

## Technical Notes

- **Wave order:** TaskCreation → TestCaseCreation (near-clones of the 39-14 pattern, cheap wins) → Debug diagnosis (small surface, careful seam with the retry orchestrators) → Triage family last (most workflows, panel machinery, routing tests).
- **Triage panel vs review panel.** `TriagePanelAggregationHelper`'s aggregation semantics must be reconciled with 39-7's — if triage needs an aggregation mode 39-7 lacks (e.g. per-field majority), extend 39-7 with tests rather than keeping a triage-local aggregator. One aggregation engine at the end of this story.
- **TriageItemCycle routing** (`TriageItemCycleRoutingTests`, `TriageItemCycleApplyFaultExecutionTests`) is orchestration ON TOP of decisions — it consumes accepted `TriageDecision` documents and routes; it is not itself a document producer and should not be forced into a lifecycle binding.
- **Diagnosis seam:** `TddWithDebugRetryWorkflow`/`CiWithDebugRetryWorkflow` call the diagnosis binding per retry attempt; each attempt's `Diagnosis` is a new document revision in lineage, which gives time-travel over "what did we believe was wrong on attempt N" for free.
- **Scope discipline:** this is a breadth story. Any lifecycle/store/review gap discovered here gets filed against its owning story's component with tests there — no producer-local workarounds in the last wave, or the "universal" claim (AC5) is false.

## Dependencies

- **Blocking:** 39-12/39-13/39-14 (patterns hardened), 39-4 (`TriageDecision`, `Diagnosis`, `TestSpec`, task-plan types), 39-6/39-7 (lifecycle + panel aggregation), 39-10/39-11 (resume, store).
- **Unblocks:** 39-16 can flip `ContractBindingTests` wholesale once every parser-backed cell is document-type-backed; the epic's build-time universal graph check (AC5) becomes the platform invariant.

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
