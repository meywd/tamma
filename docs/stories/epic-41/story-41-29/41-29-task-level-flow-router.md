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
the workflow that matches its kind** — code to TDD, docs to the docs workflow, infra to the deployment
pipeline, design to the UX/design flow, and so on — and to **pre-route whole issues** whose type never
needs the code-writing pipeline (a `question`, a docs-only ask), so that a documentation, design, or
question issue is no longer forced through plan → code tasks → TDD → deploy and made to produce the wrong
artifact.

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
kind. The per-task loop (`hasMoreTasks` at ~L530 → `extractCurrentTask` → `tddForTask` /
`dispatchTddRetry` → `incrementTask`) dispatches `tdd-with-debug-retry` for **every** task regardless of
what the task actually is. So a docs/question/design issue is marched through code generation and TDD and
produces nothing useful.

The fix has two parts on the Epic 39/41 substrate: a **task `kind`** on the `Plan`, and two switches that
read it — a lightweight **issue-level pre-route** at the head of the cycle and the primary **task-level
flow switch** inside the per-task loop.

## Part 1 — Task `kind` on the Plan

`PlanTask` (`Tamma.Core/Documents/Types/Plan.cs`) today is `{ id, description, files, dependsOn, testing }`.
Add a **closed** `kind` field.

```csharp
public enum TaskKind
{
    [Wire("code")] Code,                   // implementation change → TDD
    [Wire("test")] Test,                   // test authoring / exploratory charter
    [Wire("docs")] Docs,                   // documentation / release notes / runbook
    [Wire("infra")] Infra,                 // infra / CI config / deploy change
    [Wire("design")] Design,               // UX flow, UI spec, or architecture design doc
    [Wire("investigation")] Investigation, // research / spike / debug-diagnosis
    [Wire("chore")] Chore,                 // mechanical change (dep bump, rename, config edit)
}
```

- `PlanTask` gains `[JsonPropertyName("kind")] public string? Kind { get; init; }` (nullable string on the
  record, validated against the enum — same string-stored + validated pattern as `TriageDecision`'s
  classification fields, not a hard enum on the wire).
- **Assigned by the producer.** `(senior_developer, create-tasks)` — the `task-creation` producer feeding
  the loop — assigns each task's `kind`. The prompt contract (39-16 renderer + `PlanDocumentType` contract
  block) is extended to teach the closed set and require a `kind` per task going forward.
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

| Triage type / automation | Pre-route | Rationale |
|---|---|---|
| `question` | **Answer flow** — `research` (or `clarifying-questions`) → post the answer as an issue comment → close. No plan, no code. | A question needs an answer, not a PR. Pure conversational artifact. |
| `docs`-only | **Docs-production flow** (Epic 41 `41-24/41-25/41-26`). If not yet built → human-assigned docs task via the orchestrator (rule 4). | A docs-only issue skips code planning entirely. |
| `bug` / `feature` / `chore` | **Existing plan pipeline** (unchanged path). | The task-level switch is the primary mechanism for these. |
| `security` | Plan pipeline, but the accept gates apply the always-escalate acceptance-rules class. | Not the router's job to hardcode escalation — it is 39-5 policy; the pre-route only forwards. |
| `TriageAutomation = needs-human` | Escalate (should not have been auto-selected; `SelectWorkItemActivity` already excludes `needs-human` labels, so this is a defensive terminal). | Loud handoff, never silent processing. |

Keep it minimal on purpose (see Open Design Question 2): the pre-route exists only for issues whose type
means the plan pipeline is the wrong shape *before decomposition even happens*. Everything else flows into
decompose → plan, where the **task-level switch is the primary mechanism**.

## Part 3 — Task-level flow switch (primary mechanism)

In the per-task loop, `extractCurrentTask` is followed by a new `extractCurrentTaskKind` (reads `.kind`
from `currentTaskJson`, defaulting absent → `code`) and a `FlowSwitchByKind` `FlowSwitch` that dispatches
the matching lifecycle-bound workflow instead of the hardcoded `tddForTask`. Every kind's dispatch
converges back to `incrementTask`; every kind's failure edge converges on the **existing shared loud
fail-the-cycle sink** (`emitStepFailed` → `notifyError` → `reportError`), reusing the `StepGate` pattern
already in the file.

| `kind` | Dispatched workflow | DefinitionId | Exists today? |
|---|---|---|---|
| `code` | TDD with bounded debug-retry | `tdd-with-debug-retry` | ✅ exists (today's only path) |
| `test` | Test-case authoring, gated by the testing pipeline; exploratory charter via 41-14 | `test-case-creation` + `testing-pipeline` | ✅ exists (charter target **41-14** pending) |
| `docs` | Docs / release-notes / runbook production | *(41-24 / 41-25 / 41-26)* | ✗ **Epic 41 story** — human-assigned until built |
| `infra` | Deployment / promotion pipeline | `deployment-pipeline` | ✅ exists |
| `design` | UX flow + UI spec (**41-27**); architecture design doc via `design-proposal` | `design-proposal` (arch) / *(41-27)* (UX) | ◑ arch exists; UX target **41-27** pending |
| `investigation` | Research / debug-diagnosis | `research` / `debugging` | ✅ exists |
| `chore` | Minimal apply (interim: TDD debug-retry as a safe superset) | `tdd-with-debug-retry` | ◑ interim — dedicated `chore-apply` is a small follow-up (Open Design Question 1) |
| *(absent)* | Treated as `code` (back-compat) | `tdd-with-debug-retry` | ✅ |
| *(unknown / out-of-vocab)* | **Escalate** — loud needs-human via the shared sink | — | fail-safe |

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
   `kind` (`TASK_KIND_OUT_OF_VOCABULARY`) and accepts an absent `kind` as `code`. The `create-tasks`
   producer contract (39-16) requires a `kind` per task; the drift/contract-binding tests are green. Every
   pre-existing plan fixture (no `kind`) still validates and round-trips.
2. **Task-level switch dispatches by kind.** The per-task loop no longer hardcodes `tdd-with-debug-retry`;
   a `FlowSwitchByKind` routes each task to the mapped workflow. Unit/graph tests assert: `code`→
   `tdd-with-debug-retry`, `infra`→`deployment-pipeline`, `test`→`test-case-creation`, `investigation`→
   `research`/`debugging`, absent→`code`.
3. **Fail-safe on kind.** An unknown/out-of-vocab `kind` escalates through the shared loud sink
   (needs-human), never crashes and never mis-routes into TDD (test with a poisoned `kind`). A kind whose
   Epic 41 target is unbuilt routes to the human-assigned path with `ROUTE.TASK.DEFERRED_TO_HUMAN`, not a
   dispatch to a non-existent DefinitionId.
4. **Issue-level pre-route.** `IssuePreRoute` branches on the stored/accepted `TriageDecision` (label
   fallback): `question`→answer flow (no plan/code), `docs`-only→docs flow (human-assigned until 41-24/25/26),
   `bug`/`feature`/`chore`→existing pipeline, `needs-human`→escalate. Tests cover each branch; the
   `bug`/`feature` path is byte-for-byte the pre-story behavior.
5. **Events.** The four `ROUTE.*` events are emitted at their transitions alongside `CYCLE.*`, tagged
   `issueId`/`repository`/`tenantId`; a replay test reconstructs the route taken per task from the stream.
6. **Acceptance unchanged.** Each dispatched target keeps its own orchestrator-routed, autonomy-gated
   accept gate; the router adds no accept gate of its own and no embedded `llm-call`. A mis-typed task is
   demonstrated to be catchable at `task-review` before dispatch.
7. **Resumability.** A crash mid-loop (after task N dispatched, before `incrementTask`) re-enters at task N
   with the same derived route and no duplicate dispatch (integration test). No new allowlist entry is
   needed for the 39-10 structural test.
8. **No behavior change for legacy issues.** A `feature` issue whose plan tasks are all `code` (or all
   absent) produces an identical run to today — same dispatches, same events except the additive `ROUTE.*`
   rows.

## Dependencies

- **Blocking:**
  - **Plan schema change** (this story) — `PlanTask.Kind` + `PlanDocumentType` validation + 39-16 contract
    regeneration for the `create-tasks` cell.
  - **Story 39-15** (Remaining Producers Migration — Triage, TaskCreation) so a typed `TriageDecision` is
    in the store for the pre-route and `task-creation` produces a typed `Plan` the router can read `kind`
    from.
  - **Epic 39** lifecycle/store/events/orchestrator-routing (all targets are lifecycle producers);
    **Epic 40** durable runner for `code`/`chore` execution.
- **Routing targets (soft — router ships against what exists, human-path otherwise):**
  - Exists today: `tdd-with-debug-retry`, `test-case-creation`/`testing-pipeline`, `deployment-pipeline`,
    `research`/`debugging`, `design-proposal`.
  - Epic 41 stories the router lights up as they land: **41-24/41-25/41-26** (docs), **41-27** (UX design),
    **41-14** (exploratory charter). A dedicated `chore-apply` is an optional small follow-up.
- **Unblocks:** every Epic 41 per-role workflow becoming reachable from the issue pipeline; correct
  handling of docs/question/design/infra issues end-to-end.

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

## Open Design Questions (for the user)

1. **`chore` flow:** interim-reuse `tdd-with-debug-retry` (safe superset, ships now) vs. a dedicated
   lightweight `chore-apply` (apply → verify → CI, no red-test-first) as a small new story? The interim is
   correct but heavier than a chore needs.
2. **How thin should the pre-route be?** The minimal position is: pre-route **only** `question` (a pure
   answer with no repo artifact), and let *docs/design/infra* issues flow through decompose→plan where the
   task-level switch handles them by kind (a docs-only issue → a plan of all-`docs` tasks). This makes the
   task switch the single mechanism and shrinks the issue-level branch to one case. Is the explicit
   `docs`-only pre-route worth its weight, or is a docs-typed decomposition cleaner?
3. **Who assigns `kind`?** This story puts it solely on `create-tasks`. Should `issue-decomposition` also
   pre-tag kind at the sub-work level (earlier signal, but two producers to keep consistent), or is
   single-producer assignment the right simplicity?

## Estimated Effort

5–7 days (schema + validation + two switches + events + resumption tests; heavier than a thin 41 binding
because it touches the `Plan` type and rewires the `single-issue-cycle` per-task loop, but it adds no new
lifecycle machinery).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-24 | 1.0.0   | Initial story creation | Claude |
