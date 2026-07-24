# Implementation Plan — Story 41-29: Task-Level Flow Router

Concrete, ordered steps. No production code is written by this story document; this is the build guide.

## Phase 1 — `kind` on the Plan (`Tamma.Core`)

1. **`TaskKind` enum** in `Tamma.Core/Documents/Types/Plan.cs` (or a sibling), with `[Wire(...)]` tokens
   `code|test|docs|infra|design|investigation|chore` and alias-aware `TryParseKind` — mirror
   `TriageVocabulary` in `TriageDecision.cs`.
2. **`PlanTask.Kind`** — `[JsonPropertyName("kind")] public string? Kind { get; init; }` (nullable string,
   validated; not a hard enum on the wire, matching `TriageDecision`'s classification-as-string pattern).
3. **`PlanDocumentType.Validate`** — add `TASK_KIND_OUT_OF_VOCABULARY` (names task id + raw value) for a
   present-but-invalid `kind`; absent `kind` is **valid** and means `code`. Extend the `Contract` block and
   the valid example to carry `"kind"`.
4. **Contract regeneration (39-16).** Regenerate the `(senior_developer, create-tasks)` cell contract from
   the type so the prompt requires a `kind` per task; update `create-tasks` prompt guidance with the closed
   set + one-line kind definitions. Keep `ContractBindingTests` green.
5. **Helper:** `PlanRouting.ResolveKind(taskJson) → TaskKind` (absent → `Code`; out-of-vocab → a typed
   `Unknown` sentinel the switch escalates on). Unit-test the three cases.

## Phase 2 — Task-level switch in `SingleIssueCycleWorkflow`

Rewire the per-task loop (currently `hasMoreTasks → extractCurrentTask → tddForTask/dispatchTddRetry →
incrementTask`, ~L510–590 / connections ~L1179–1190):

1. Add `extractCurrentTaskKind` (`SetVariable` → `CurrentTaskKind`) after `extractCurrentTask`, using
   `PlanRouting.ResolveKind`.
2. Add `FlowSwitchByKind` (`FlowSwitch`) with a case per `TaskKind` + a default `Unknown` case.
3. Per-kind dispatch activities (reuse the existing `DispatchWorkflow` + `StepGate` + shared-sink pattern):
   - `code` / absent → existing `tddForTask` path (`ExecuteAgentActivity` + `dispatchTddRetry` recovery) —
     **unchanged**, just moved behind the switch's `code` case.
   - `infra` → `DispatchWorkflow("deployment-pipeline")` + `deployOk`-style gate.
   - `test` → `DispatchWorkflow("test-case-creation")` (+ `testing-pipeline` gate).
   - `investigation` → `DispatchWorkflow("research")` / `debugging`.
   - `design` → `DispatchWorkflow("design-proposal")` (arch) / human-assigned UX until `41-27`.
   - `docs` → human-assigned until `41-24/25/26`; interim emits `ROUTE.TASK.DEFERRED_TO_HUMAN`.
   - `chore` → `tdd-with-debug-retry` interim (Open Q1).
   - `Unknown` → `emitStepFailed` (shared loud sink) with `ROUTE.TASK.UNKNOWN_KIND`.
4. Every per-kind success edge → `incrementTask`; every failure edge → the existing `emitStepFailed` sink.
   Add each new activity to the flowchart `Activities`/`Connections` lists.
5. Emit `ROUTE.TASK.DISPATCHED` (+ the deferred/unknown variants) via a new `EmitCycleEventActivity` config
   or an added `RouteEvents` constant set alongside `CycleEvents`.

## Phase 3 — Issue-level pre-route

1. Add `readTriage` (read accepted `TriageDecision` from the 39-11 store by `issueId`; fall back to
   work-item labels) after `ValidateWorkItem`.
2. Add `IssuePreRoute` `FlowSwitch`: `question` → answer sub-flow (`research`/`clarifying-questions` →
   comment → close); `docs`-only → docs flow/human-assigned; `needs-human` → escalate terminal;
   default (`bug`/`feature`/`chore`/`security`) → the existing `emitCycleStarted`/`GatherContext` path
   (unchanged).
3. Emit `ROUTE.ISSUE.PREROUTED`.

## Phase 4 — Tests

- `Tamma.Core.Tests`: `PlanDocumentType` kind validation (valid/absent/out-of-vocab); `PlanRouting.ResolveKind`.
- Graph/structure tests: switch dispatches the mapped DefinitionId per kind; no non-existent DefinitionId
  is ever a dispatch target; `code`/absent path identical to pre-story.
- Resumption integration test: crash after a task dispatch re-enters at the same task, no duplicate dispatch.
- Replay test: `ROUTE.*` events reconstruct the per-task route and the issue pre-route.
- Back-compat: existing plan fixtures (no `kind`) run identically (AC8).

## Sequencing

Land after **39-15**. Ship Phase 1–2 against today's workflows first (immediate value: infra/test/
investigation tasks route correctly); Phase 3 pre-route next; flip docs/UX rows from human-assigned to
concrete workflows as **41-24/25/26/27** and **41-14** land — no router change required.
