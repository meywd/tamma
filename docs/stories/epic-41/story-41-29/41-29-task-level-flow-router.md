# Story 41-29: Task-Level Flow Router (+ issue-level pre-route)

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

As the **orchestrator processing a selected issue**, I want the single-issue cycle to **route each task to
the workflow that matches its kind** — code to TDD, docs to the docs workflow, investigation to research,
design to the UX/design flow, and so on — and to **pre-route whole issues** whose type never needs the
code-writing pipeline (a `question`), so that a documentation, design, or question issue is no longer
forced through plan → code tasks → TDD and made to produce the wrong artifact.

## Priority

P0 / Wave 1 — **the activation story for Epic 41.** Every other Epic 41 story turns a recurring activity
into a lifecycle workflow, but until the issue pipeline can *dispatch* those workflows by task/issue kind,
they are only reachable ad-hoc. This story is the front door that makes the per-role workflows reachable
from `single-issue-cycle`. It ships value immediately against the workflows that exist today and lights up
each new kind as its Epic 41 target lands.

## The gap (READ FIRST)

Triage already classifies (`TriageIssueType` = bug/feature/chore/question/security/docs; `TriageAutomation`
= tamma-auto/tamma-assist/needs-human — `Tamma.Core/Documents/Types/TriageDecision.cs`), but that
classification is consumed **only** by `SelectWorkItemActivity` for *selection*, never for *routing*.

`SingleIssueCycleWorkflow` (`single-issue-cycle`) has **no type switch**. Every selected issue runs one
hardcoded chain: context → plan-generation → plan-review → task-creation → task-review → branch-creation →
**per-task TDD** → CI → code-review → merge → deployment. It branches only on *review decisions*
(`ReviewOutcome` / `TaskReviewOutcome`: Approved/NeedsModification/Defer/Split), never on issue or task
kind. The per-task loop (`hasMoreTasks` `:530` → `extractCurrentTask` `:546` → `tddForTask` `:571` →
`incrementTask` `:590`; connections `:1180-1190`) runs the **inline `tddForTask` `ExecuteAgentActivity`
with `Task = "implement"`** for **every** task regardless of what the task actually is —
`tdd-with-debug-retry` is dispatched only on that node's *failure* edge (`dispatchTddRetry` `:940`, wired
at `:1183`). *Corrected: an earlier draft described `tdd-with-debug-retry` as the loop's normal dispatch;
it is the recovery path.* So a docs/question/design issue is marched through code generation and TDD and
produces nothing useful.

The loop is also strictly **pre-CI and pre-merge**: `hasMoreTasks/False` leads to `ciGate` (`:1196`), then
code review + the merge-approval gate, then `WaitForPRMerged` (`:701`) — which is the *first* writer of
`MergeSha`. Nothing downstream of the merge is reachable from inside the loop. That fact settles the
`infra` routing question below.

The fix has two parts on the Epic 39/41 substrate: a **task `kind`** on the `Plan`, and two places that
read a kind — a narrow **issue-level pre-route** at the head of the cycle (a small new sub-graph, not just
a switch) and the primary **task-level flow switch** inside the per-task loop.

## Part 1 — Task `kind` on the Plan

`PlanTask` (`Tamma.Core/Documents/Types/Plan.cs`) today is `{ id, description, files, dependsOn, testing }`.
Add a **closed** `kind` field.

```csharp
public enum TaskKind
{
    [Wire("code")] Code,                   // implementation change → TDD
    [Wire("test")] Test,                   // test authoring / exploratory charter
    [Wire("docs")] Docs,                   // documentation / release notes / runbook
    [Wire("infra")] Infra,                 // infra / CI config / IaC file change (a code change)
    [Wire("design")] Design,               // UX flow, UI spec, or architecture design doc
    [Wire("investigation")] Investigation, // research / spike / debug-diagnosis
    [Wire("chore")] Chore,                 // mechanical change (dep bump, rename, config edit)
}
```

- `PlanTask` gains `[JsonPropertyName("kind")] public string? Kind { get; init; }` (nullable string on the
  record, validated against the enum — same string-stored + validated pattern as `TriageDecision`'s
  classification fields, not a hard enum on the wire).
- **Assigned by the producer.** `(senior_developer, create-tasks)` — the `task-creation` producer feeding
  the loop — is the assignment the router depends on. Per the decision below, `(architect,
  plan-system-design)` is instructed to assign kinds as well, since the two share one contract block.
- **The contract block is SHARED — decide once, changes both producers.** *Corrected: an earlier draft
  spoke of teaching `kind` to the `create-tasks` cell alone. There is no per-cell contract.*
  `IDocumentType.RenderContract()` is **per document type**, not per cell (`Plan.cs:135` returns the single
  `private const string Contract` at `:144`), and **two** producers emit documentType `plan`:
  `(architect, plan-system-design)` for `plan-generation` and `(senior_developer, create-tasks)` for
  `task-creation` (`DocumentTypeRegistry.cs:151`/`:154`; `ContractBindingTests.cs:160`/`:172`). Teaching the
  rendered contract a `kind` therefore instructs **both**.
  **Decision: teach `kind` in the shared `PlanDocumentType` contract block and let `plan-generation` emit
  kinds too.** It is upstream of `task-creation`, so an earlier kind signal is useful rather than harmful,
  and it avoids a per-cell contract-scoping change to 39-16 that this story would otherwise have to own.
  The alternatives — per-cell contract scoping (a real 39-16 design change) or keeping `kind` out of the
  rendered contract and teaching it only in `create-tasks.md` prose — are rejected as, respectively, out of
  scope and silently divergent. `plan-generation` output with no `kind` stays valid (see Validation), so
  this changes instruction, not validation.
- **The prompt edit is by hand.** 39-16's generated-region markers do not exist in any shipped prompt file
  (no `Prompts/**/*.md` carries one), and both plan templates are hand-authored `version: 1` whose shape
  already diverges from the rendered contract (templates say `dependencies` and files-as-objects
  `{path, action}`; the contract says `dependsOn` and files-as-strings). So this story hand-edits
  `Prompts/senior_developer/create-tasks.md` and `Prompts/architect/plan-system-design.md`, or takes an
  explicit dependency on 39-16's regeneration actually landing first.
- **Validation.** `PlanDocumentType.Validate` adds a rule: a present `kind` must be in-vocabulary
  (`TASK_KIND_OUT_OF_VOCABULARY` naming the task + value), mirroring `TriageDecision`'s
  `OUT_OF_VOCABULARY` — never a silent clamp. An **absent** `kind` is **valid** and means `code`
  (backward-compatibility carry-through, exactly as the D5 root-`files` carry-through preserves old plans).
  So every plan produced before this story validates and behaves precisely as today.

## Part 2 — Issue-level pre-route (minimal)

A new `IssuePreRoute` `FlowSwitch` at the head of `SingleIssueCycleWorkflow`, immediately after
`ValidateWorkItem` and before `emitCycleStarted`/`GatherContext`. It reads the issue's `TriageIssueType`
(and `TriageAutomation`) from the accepted `TriageDecision` document in the 39-11 store, falling back to
the work-item labels when no decision is stored yet.

**Decision (was Open Design Question 2): the pre-route branches on `question` only** — plus the defensive
`needs-human` terminal. A `docs`-only issue is **not** pre-routed; it flows into decompose → plan and
becomes a plan of all-`docs` tasks that the task-level switch handles. *Corrected: an earlier draft both
asked whether the `docs`-only pre-route was worth its weight and pinned it in an acceptance criterion.*
Rationale: a docs pre-route and an all-`docs` plan reach the same place today (human-assigned, since
41-24/25/26 are unbuilt), so it buys a second routing mechanism and a second place to flip when those
stories land. One mechanism, one flip.

| Triage type / automation | Pre-route | Rationale |
|---|---|---|
| `question` | **Answer flow** — `research` (or `clarifying-questions`) → post the answer as an issue comment → close. No plan, no code. | A question needs an answer, not a PR. The only issue type with **no repo artifact at all**, so the plan pipeline is the wrong shape before decomposition even happens. |
| `TriageAutomation = needs-human` | Escalate (should not have been auto-selected; `SelectWorkItemActivity` already excludes `needs-human` labels, so this is a defensive terminal). | Loud handoff, never silent processing. |
| `docs` | **Not pre-routed** — plan pipeline; the plan's tasks carry `kind: docs` and the task switch routes them. | Keeps the task-level switch the single routing mechanism. |
| `bug` / `feature` / `chore` | **Existing plan pipeline** (unchanged path). | The task-level switch is the primary mechanism for these. |
| `security` | Plan pipeline, but the accept gates apply the always-escalate acceptance-rules class. | Not the router's job to hardcode escalation — it is 39-5 policy; the pre-route only forwards. |

The `question` branch is a **new sub-graph**, not a switch case: read triage → dispatch `research` /
`clarifying-questions` → post the answer via the existing `NotifyIssue` helper (`:1266`) → close → a
terminal that never reaches `GatherContext`. Costed as such under Estimated Effort.

## Part 3 — Task-level flow switch (primary mechanism)

In the per-task loop, `extractCurrentTask` is followed by a new `extractCurrentTaskKind` (reads `.kind`
from `currentTaskJson`, defaulting absent → `code`) and a `FlowSwitchByKind` `FlowSwitch` that dispatches
the matching lifecycle-bound workflow instead of unconditionally entering the coding node. The
`code`/`infra`/`chore`/absent cases enter the **existing coding node and its recovery edges unchanged** —
they are moved behind the switch, not rebuilt. Every kind's dispatch converges back to `incrementTask`;
every kind's failure edge converges on the **existing shared loud fail-the-cycle sink** (`emitStepFailed`
→ `notifyError` → `reportError`), reusing the `StepGate` pattern already in the file.

**This region is also rewired by Epic 40** — see Dependencies. 41-29 rebases onto the post-40 loop, where
the coding node is `WaitForAgentRunActivity` rather than today's `ExecuteAgentActivity`. The switch is
orthogonal to that swap; only the node type behind the `code` case differs.

| `kind` | Dispatched workflow | Target | Exists today? |
|---|---|---|---|
| `code` | The existing coding step (agent run + `tdd-with-debug-retry` on failure) | the loop's `tddForTask` node | ✅ exists (today's only path) |
| `test` | Test-case authoring, gated by the testing pipeline; exploratory charter via 41-14 | `test-case-creation` + `testing-pipeline` | ✅ exists (charter target **41-14** pending) |
| `docs` | Docs / release-notes / runbook production | *(41-24 / 41-25 / 41-26)* | ✗ **Epic 41 story** — human-assigned until built |
| `infra` | The **coding path** — an infra task is a code change to infra files (CI yaml, Dockerfile, compose, IaC) | the loop's `tddForTask` node | ✅ exists |
| `design` | UX flow + UI spec (**41-27**); architecture design doc via `design-proposal` | `design-proposal` (arch) / *(41-27)* (UX) | ◑ arch exists; UX target **41-27** pending |
| `investigation` | Research / debug-diagnosis | `research` / `debugging` | ✅ exists |
| `chore` | Minimal apply (interim: the coding path as a safe superset) | the loop's `tddForTask` node | ◑ interim — dedicated `chore-apply` is a small follow-up (Open Design Question 1) |
| *(absent)* | Treated as `code` (back-compat) | the loop's `tddForTask` node | ✅ |
| *(unknown / out-of-vocab)* | **Escalate** — loud needs-human via the shared sink | — | fail-safe |

**Corrected — `infra` does NOT route to `deployment-pipeline`.** An earlier draft mapped it there. That is
structurally impossible from inside the per-task loop, not merely undesirable:

- `deployment-pipeline` is the **post-merge** step-15 promotion (QA → UAT → Prod + release cut) — its own
  header says so (`DeploymentPipelineWorkflow.cs:21-22`), and it is dispatched at
  `SingleIssueCycleWorkflow.cs:721`, *after* `WaitForPRMerged` and `closeIssue`.
- It requires a **merged commit sha**: `mergeSha` is a declared input (`:94`, read at `:169`), threaded into
  the deploy call (`:609`) and used as the release tag source and the release `TargetRef` (`:345`, `:360`).
  Inside the loop that sha does not exist yet — `WaitForPRMergedActivity` (`:701-708`) is its first writer.
- There is no non-empty-sha guard, so the failure is silent rather than loud: an empty sha yields an empty
  release tag (`:345`) and either a fail-closed stage (`ExtractStageResult` defaults to `failed`,
  `:624-630`) or a QA → UAT → Prod promotion and release cut against an undefined ref, for code that is not
  merged, not CI-passed and not reviewed.

An infra task *is* a code change to infra files, so it takes the coding path like any other file change —
and, like any other task, its result reaches production through the unchanged post-merge dispatch.
**Deployment stays step 15 and is not a per-task routing target.**

**Where a target is an unbuilt Epic 41 story**, the switch dispatches the human-assigned path per Epic 41
rule 4 (the orchestrator assigns the task to a holder of the appropriate tenant role; it lands in their
Task View) and emits `ROUTE.TASK.DEFERRED_TO_HUMAN` with the missing-target reason — never a crash, never
a silent skip, never a mis-route into TDD. As each Epic 41 target lands, its row flips from human-assigned
to the concrete workflow with **no change to the router** (the mapping is config/registry, not new graph).

## Consistency with the platform

- **Dispatched flows are already lifecycle-bound producers** (Epic 39). The router does not invent
  execution — it composes existing/near-existing lifecycle workflows. "Vocabulary static, composition
  dynamic": the `kind→workflow` map is the dynamic composition; the kinds and doc types are static.
- **Acceptance stays orchestrator-routed and autonomy-gated.** The router itself is **deterministic
  dispatch, not a decision** — routing is composition, so it needs no accept gate. The correctness check
  on the *routing* (did create-tasks assign the right kind?) is caught upstream at **task-review**, which
  is already a `Review` that flows through the orchestrator + autonomy dial, human-or-agent — a mis-typed
  task is a review concern before any dispatch, so no new gate is added. Each dispatched flow's *output*
  keeps its own orchestrator-routed accept gate unchanged.
- **Human-or-agent execution** is inherited: every routing target is a lifecycle workflow that already
  assigns to a human role at lower autonomy and an `AgentRole` at higher autonomy (rule 4). The router
  chooses the *workflow*, not the *executor*.
- **DCB events.** New generic routing family alongside the existing `CYCLE.*` (emitted through
  `EmitCycleEventActivity` / the durable engine drain, not a direct repo write):
  `ROUTE.ISSUE.PREROUTED` (tags: `issueType`, `chosenFlow`), `ROUTE.TASK.DISPATCHED` (tags: `taskId`,
  `kind`, `targetWorkflow`), `ROUTE.TASK.DEFERRED_TO_HUMAN` (tags: `taskId`, `kind`, `missingTarget`),
  `ROUTE.TASK.UNKNOWN_KIND` (tags: `taskId`, `rawKind`). All carry `issueId`/`repository`/`tenantId`.
- **Resumable by design.** The router adds **no non-durable state**: the loop position (`CurrentTaskIndex`)
  is an existing persisted workflow variable, and `kind` is re-read from the durable `TasksJson` each
  iteration — so a crash mid-loop re-enters at the same task and re-derives the same route. The dispatched
  sub-workflows are each independently resumable (Epic 39 standard). `SingleIssueCycleWorkflow` keeps its
  `ContinueWithIncidentsStrategy`; new dispatch sites use `WaitForCompletion=true` + a `StepGate` exactly
  like the existing `dispatchTddRetry`/`ciGate` sites, so a faulted target routes to the sink, never halts.

## Acceptance Criteria

1. **`kind` on the Plan.** `PlanTask` carries a validated `kind`; `PlanDocumentType` rejects an out-of-vocab
   `kind` (`TASK_KIND_OUT_OF_VOCABULARY`) and accepts an absent `kind` as `code`. Every pre-existing plan
   fixture (no `kind`) still validates and round-trips — **including a `plan-generation` fixture**, since
   `(architect, plan-system-design)` emits the same document type. Its `ContractBindingTests` entry
   (`ContractBindingTests.cs:160`) is unchanged by this story; only the shared `PlanDocumentType` contract
   block gains `kind`, and both plan prompt templates are updated by hand to match.
2. **Task-level switch dispatches by kind.** The per-task loop no longer enters the coding node
   unconditionally; a `FlowSwitchByKind` routes each task to the mapped target. Graph tests assert:
   `code`/`infra`/`chore`/absent → the coding node, `test` → `test-case-creation`, `investigation` →
   `research`/`debugging`, and — as a standing guard against the mis-route this story corrects — that
   `deployment-pipeline` is **not** reachable from inside the per-task loop from any `kind`.
3. **Fail-safe on kind.** An unknown/out-of-vocab `kind` escalates through the shared loud sink
   (needs-human), never crashes and never mis-routes into TDD (test with a poisoned `kind`). A kind whose
   Epic 41 target is unbuilt routes to the human-assigned path with `ROUTE.TASK.DEFERRED_TO_HUMAN`, not a
   dispatch to a non-existent DefinitionId.
4. **Issue-level pre-route.** `IssuePreRoute` branches on the stored/accepted `TriageDecision` (label
   fallback): `question` → the answer sub-graph (an answer comment is posted and the issue closed, with
   **no** plan-generation or branch-creation dispatch), `needs-human` → escalate terminal, everything else
   (`bug`/`feature`/`chore`/`docs`/`security`) → the existing pipeline. Tests cover each branch; a `docs`
   issue is asserted to take the **plan** path (the decision above), and the `bug`/`feature` path is
   byte-for-byte the pre-story behavior.
5. **Events.** The four `ROUTE.*` events are emitted at their transitions alongside `CYCLE.*`, tagged
   `issueId`/`repository`/`tenantId`; a replay test reconstructs the route taken per task from the stream.
6. **Acceptance unchanged.** Each dispatched target keeps its own orchestrator-routed, autonomy-gated
   accept gate; the router adds no accept gate of its own and no embedded `llm-call`. A mis-typed task is
   demonstrated to be catchable at `task-review` before dispatch.
7. **Resumability.** A crash mid-loop (after task N dispatched, before `incrementTask`) re-enters at task N
   with the same derived route and no duplicate dispatch (integration test). The 39-10 structural test stays
   green **without** re-adding the `SingleIssueCycleWorkflow` allowlist entry that 40-5 removes — the router
   introduces no non-durable state, so it must not cost the workflow its `[ResumeBehavior]` declaration.
8. **No behavior change for legacy issues.** A `feature` issue whose plan tasks are all `code` (or all
   absent) produces an identical run to today — same dispatches, same events except the additive `ROUTE.*`
   rows.

## Dependencies

- **Blocking:**
  - **Plan schema + contract change** (this story) — `PlanTask.Kind`, `PlanDocumentType` validation, and the
    shared `PlanDocumentType` contract block + both plan prompt templates by hand (39-16's generated-region
    markers do not exist in any shipped prompt file, and the contract is per-type, not per-cell — Part 1).
    This is the only genuinely unmet blocker; it is owned by this story.
  - **Epic 39** lifecycle/store/events/orchestrator-routing (all targets are lifecycle producers).
  - **Epic 40 — file-level, same region.** `40-2` swaps the loop's coding node from `ExecuteAgentActivity`
    to `WaitForAgentRunActivity`; `40-4` inserts `ComputeTaskResumeIndexActivity` between `initTaskLoop`
    and `hasMoreTasks`; `40-5` adds `[ResumeBehavior]` to `SingleIssueCycleWorkflow`. All three write the
    same ~80 lines and the same connection block. **Merge order: 40-2 → 40-4 → 40-5 → 41-29** (matching
    `docs/stories/epic-40/EXECUTION-PLAN.md`'s shared-edit row) — 41-29 rebases onto the post-40 loop and
    does not fan out concurrently with it.
- **Already landed (substrate, not a blocker):** *Corrected: an earlier draft listed 39-15 as Blocking.*
  **Story 39-15** has merged — `task-creation` is a thin binding over `document-lifecycle` producing a
  typed `Plan` (`TaskCreationWorkflow.cs:19`, `ContractBindingTests.cs:166-175`), and the triage producers
  put a typed `TriageDecision` in the store (`TriagePODecisionWorkflow.cs:21`,
  `DocumentTypeRegistry.cs:159-164`). Both preconditions this story needs are satisfied today.
- **Routing targets (soft — router ships against what exists, human-path otherwise):**
  - Exists today: the loop's coding node, `test-case-creation`/`testing-pipeline`, `research`/`debugging`,
    `design-proposal`. (`deployment-pipeline` is **not** on this list — it is the post-merge step-15
    dispatch, unreachable from the loop; see Part 3.)
  - Epic 41 stories the router lights up as they land: **41-24/41-25/41-26** (docs), **41-27** (UX design),
    **41-14** (exploratory charter). A dedicated `chore-apply` is an optional small follow-up.
- **Unblocks:** every Epic 41 per-role workflow becoming reachable from the issue pipeline; correct
  handling of docs/question/design/test/investigation work end-to-end.

## Risks

- **Kind mis-assignment** (create-tasks tags a code task as `docs`). Mitigated: caught at `task-review`
  (a `Review`, orchestrator/human-gated) before any dispatch; the switch is downstream of an accepted,
  reviewed plan.
- **Cross-kind dependencies** (a `docs` task depends on a `code` task). The loop already honors the plan's
  topological order (`PlanDocumentType` guarantees one exists); the router dispatches per-task in that
  order, so cross-kind `dependsOn` is respected as long as `create-tasks` keeps dependency ordering — add a
  test for a mixed-kind ordered plan.
- **Back-compat regression.** The single largest risk is changing behavior for existing `code`/absent
  plans; AC1 and AC8 pin it with pre-story fixtures.
- **Unbuilt-target drift.** Routing to an Epic 41 DefinitionId before it exists would hang the cycle; AC3
  forbids dispatching a non-existent DefinitionId — unbuilt kinds take the human-assigned path.
- **Routing a task to a post-merge workflow.** The `infra` → `deployment-pipeline` mapping in this story's
  first draft would have promoted and released unmerged code against an empty `mergeSha`. AC2's negative
  assertion (`deployment-pipeline` unreachable from the loop) is the standing guard; the general rule is
  that a per-task target must be satisfiable from **pre-CI, pre-merge** state alone.
- **Epic 40 rebase.** Four stories write the same ~80 lines of `SingleIssueCycleWorkflow.cs`. Landing 41-29
  concurrently with 40-2/40-4/40-5 produces a hand-merge of the loop graph and its connection block; the
  merge order in Dependencies is the mitigation, not a preference.

## Open Design Questions (for the user)

1. **`chore` flow:** interim-reuse the coding path (safe superset, ships now) vs. a dedicated lightweight
   `chore-apply` (apply → verify → CI, no red-test-first) as a small new story? The interim is correct but
   heavier than a chore needs.
2. **Who assigns `kind`?** The decision above puts assignment in the shared `Plan` contract, so
   `plan-generation` and `create-tasks` both emit it. Should `issue-decomposition` pre-tag kind at the
   sub-work level as well (earlier signal, a third producer to keep consistent), or is stopping at the two
   `plan` producers the right line?

*(The former question 2 — "is the explicit `docs`-only pre-route worth its weight?" — is settled and
recorded as a decision under Part 2: it is not. `docs` issues take the plan path.)*

## Estimated Effort

**6.5–7.5 days.** Heavier than a thin 41 binding because it touches the `Plan` type and rewires the
`single-issue-cycle` per-task loop, but it adds no new lifecycle machinery. (Phases as in the
implementation plan.)

| Phase | Days | Note |
|---|---|---|
| 1 — `kind`: enum, `PlanTask` field, validation, shared contract block + two prompt templates by hand | 1.5 | Two producers share the contract, so both templates move |
| 2 — task-level switch: `extractCurrentTaskKind`, `FlowSwitchByKind`, per-kind edges, `ROUTE.*` events | 2 | A switch over an existing region; `code`/`infra`/`chore` reuse the existing node |
| 3 — `question` pre-route | 1.5–2 | *Not a switch case.* A **new sub-graph**: read triage (store + label fallback) → dispatch the answer flow → post comment → close → terminal, plus the `needs-human` terminal |
| 4 — tests: validation, graph, back-compat fixtures, resumption, replay | 1.5–2 | Includes the pre-40/post-40 rebase of the loop graph tests |

*Corrected: an earlier estimate treated the pre-route as one more switch case. Part 3 builds a path the
workflow does not have today — dispatch, comment, close, terminal — which is why it is costed separately.*

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-24 | 1.0.0   | Initial story creation | Claude |
| 2026-07-24 | 1.1.0   | Code-verified revision: `infra` re-mapped from `deployment-pipeline` (post-merge, needs `MergeSha`) to the coding path + AC2 negative guard; 39-15 moved from Blocking to landed substrate; shared `PlanDocumentType` contract decision recorded (both plan producers); pre-route scoped to `question` only (former Open Q2 decided); Epic 40 merge order added; effort re-broken-out for the pre-route sub-graph | Claude |
