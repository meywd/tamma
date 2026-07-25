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
4. **Contract + prompts — SHARED, edited by hand.** `RenderContract()` is per document **type**
   (`Plan.cs:135` → the single `Contract` const at `:144`), and two cells produce documentType `plan`:
   `(architect, plan-system-design)` and `(senior_developer, create-tasks)` (`ContractBindingTests.cs:160`,
   `:172`; `DocumentTypeRegistry.cs:151`, `:154`). *Corrected: an earlier draft asked for a 39-16
   regeneration of the `create-tasks` cell alone — that is not achievable today.* Per the story's decision:
   teach `kind` in the shared `Plan.Contract` block, then hand-edit **both** templates
   (`Prompts/senior_developer/create-tasks.md`, `Prompts/architect/plan-system-design.md`) with the closed
   set + one-line kind definitions. No prompt file carries a 39-16 generated-region marker, so there is
   nothing to regenerate; if 39-16 lands first, replace the hand edit with its output. Keep both
   `ContractBindingTests` entries unchanged and green, and keep a kind-less `plan-generation` fixture
   validating (AC1).
5. **Helper:** `PlanRouting.ResolveKind(taskJson) → TaskKind` (absent → `Code`; out-of-vocab → a typed
   `Unknown` sentinel the switch escalates on). Unit-test the three cases.

## Phase 2 — Task-level switch in `SingleIssueCycleWorkflow`

Rewire the per-task loop. **Today** it is `hasMoreTasks` (`:530`) → `extractCurrentTask` (`:546`) →
`tddForTask` (an inline `ExecuteAgentActivity`, `:571`; `Failed` → `dispatchTddRetry` `:940`) →
`incrementTask` (`:590`); vars at `:513-514`, connections at `:1180-1190`. **Rebase first:** Epic 40 lands
here before this story (order `40-2 → 40-4 → 40-5 → 41-29`), so at implementation time expect the coding
node to be `WaitForAgentRunActivity` (40-2), a `ComputeTaskResumeIndexActivity` ahead of `hasMoreTasks`
(40-4), and `[ResumeBehavior]` on the class (40-5) — **all line cites above will have shifted.** Re-derive
them from the file; do not trust the numbers.

1. Add `extractCurrentTaskKind` (`SetVariable` → `CurrentTaskKind`) after `extractCurrentTask`, using
   `PlanRouting.ResolveKind`.
2. Add `FlowSwitchByKind` (`FlowSwitch`) with a case per `TaskKind` + a default `Unknown` case.
3. Per-kind dispatch activities (reuse the existing `DispatchWorkflow` + `StepGate` + shared-sink pattern):
   - `code` / `infra` / `chore` / absent → the existing coding node and its `dispatchTddRetry` recovery
     edge — **unchanged**, just moved behind the switch. Post-40 that node is `WaitForAgentRunActivity`
     with `Received`/`Timeout`/`Failed` outcomes rather than `ExecuteAgentActivity`'s
     `Completed`/`Failed`; wire the switch to whichever shape is in the file at rebase time.
     *Corrected: an earlier draft routed `infra` to `DispatchWorkflow("deployment-pipeline")`. That
     workflow is the post-merge step-15 promotion and requires a `MergeSha` that does not exist inside the
     loop (`DeploymentPipelineWorkflow.cs:94`/`:169`/`:345`; first written by `WaitForPRMerged` at
     `SingleIssueCycleWorkflow.cs:701-708`). Deployment is not a per-task target.*
   - `test` → `DispatchWorkflow("test-case-creation")` (+ `testing-pipeline` gate).
   - `investigation` → `DispatchWorkflow("research")` / `debugging`.
   - `design` → `DispatchWorkflow("design-proposal")` (arch) / human-assigned UX until `41-27`.
   - `docs` → human-assigned until `41-24/25/26`; interim emits `ROUTE.TASK.DEFERRED_TO_HUMAN`.
   - `Unknown` → `emitStepFailed` (shared loud sink) with `ROUTE.TASK.UNKNOWN_KIND`.
4. Every per-kind success edge → `incrementTask`; every failure edge → the existing `emitStepFailed` sink.
   Add each new activity to the flowchart `Activities`/`Connections` lists.
5. Emit `ROUTE.TASK.DISPATCHED` (+ the deferred/unknown variants) via a new `EmitCycleEventActivity` config
   or an added `RouteEvents` constant set alongside `CycleEvents`.

## Phase 3 — Issue-level pre-route (a new sub-graph, not a switch case)

Two of the three cases are terminals the workflow does not have today, which is why this phase carries its
own 1.5–2 days rather than riding along with Phase 2.

1. Add `readTriage` (read accepted `TriageDecision` from the 39-11 store by `issueId`; fall back to
   work-item labels) after `ValidateWorkItem` (`:148`).
2. Add `IssuePreRoute` `FlowSwitch` with **two** non-default cases (per the story's decision — `docs` is
   *not* pre-routed): `question` → the answer sub-graph; `needs-human` → escalate terminal; default
   (`bug`/`feature`/`chore`/`docs`/`security`) → the existing `emitCycleStarted`/`GatherContext` path,
   unchanged.
3. Build the answer sub-graph: `DispatchWorkflow("research")` (or `clarifying-questions`) with
   `WaitForCompletion=true` + a `StepGate` → extract the answer → post it with the existing `NotifyIssue`
   helper (`:1266`) → close → terminal. It must never reach `GatherContext`, `plan-generation` or
   branch creation; a graph test asserts that.
4. Emit `ROUTE.ISSUE.PREROUTED`.

## Phase 4 — Tests

- `Tamma.Core.Tests`: `PlanDocumentType` kind validation (valid/absent/out-of-vocab); `PlanRouting.ResolveKind`;
  a kind-less `plan-generation`-shaped fixture still validates and round-trips (the sibling producer, AC1).
- Graph/structure tests: switch dispatches the mapped target per kind; no non-existent DefinitionId is ever
  a dispatch target; **`deployment-pipeline` is unreachable from the loop for every kind** (AC2);
  `code`/`infra`/`chore`/absent path identical to pre-story.
- Pre-route graph test: the `question` terminal reaches neither `GatherContext` nor `plan-generation`; a
  `docs` issue takes the plan path.
- Resumption integration test: crash after a task dispatch re-enters at the same task, no duplicate dispatch;
  the 39-10 structural test is green with no allowlist entry for `SingleIssueCycleWorkflow` (AC7).
- Replay test: `ROUTE.*` events reconstruct the per-task route and the issue pre-route.
- Back-compat: existing plan fixtures (no `kind`) run identically (AC8).

## Sequencing

**39-15 has landed** (`TaskCreationWorkflow.cs:19`, `ContractBindingTests.cs:166-175`,
`DocumentTypeRegistry.cs:154`), so this story is sequencing-unblocked on that axis and **Phase 1 is the
true critical path**. *Corrected: an earlier draft said "land after 39-15".*

The remaining constraint is file-level, not story-level: `SingleIssueCycleWorkflow.cs`'s per-task loop is
also written by **40-2 → 40-4 → 40-5**, and 41-29 rebases onto the post-40 shape (see the shared-edit row
in `docs/stories/epic-40/EXECUTION-PLAN.md`). Phase 1 can proceed in parallel with Epic 40 — it touches
`Tamma.Core` and the prompt files only. Phase 2 is the one that must queue behind 40-5.

Ship Phase 1–2 against today's workflows first (immediate value: test/investigation tasks route correctly
and `docs`/`design` tasks stop being marched through TDD); Phase 3 pre-route next; flip the docs/UX rows
from human-assigned to concrete workflows as **41-24/25/26/27** and **41-14** land — no router change
required.
