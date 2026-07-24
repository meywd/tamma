# Implementation Plan — Story 40-7: End-to-End Crash/Restart + Mode-Matrix Integration Proof

## Scope & Deliverable

When this story is done, a Testcontainers integration suite proves the composed resumability
guarantee: the coding step suspends durably (40-2), resumes across a disposed host on the same
store (40-3), re-enters at the right task after a crash without re-implementing landed work
(40-4), emits exactly one successful run per task (40-6 events), times out to escalation rather
than hanging, fails loud on git/event inconsistency, and behaves identically in SaaS (GHA) and
single-user (local) modes. The 40-1 runner self-check is wired in so the CI-side contract is also
exercised end-to-end. One production change only, and it is the point of the proof: **this story
flips 40-4's `Coding:TaskReEntryDisabled` default so the real `TaskLoopReEntryService` becomes the
shipped registration** (AC9). Everything else is tests.

## Pre-Reading

- `docs/stories/epic-40/story-40-7/40-7-crash-restart-mode-matrix-integration-proof.md` — this story (ACs are source of truth)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` step 10 (`LifecycleReEntryIntegrationTests`, D8 crash shape) — THE precedent to mirror for code
- `docs/stories/epic-39/story-39-6/implementation-plan.md` step 10 — the shared Testcontainers fixture (Elsa EF persistence, stub sub-workflows, capturing event client)
- `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TriageItemCycleApplyFaultExecutionTests.cs` — real-`IWorkflowRunner` execution harness with capturing event client
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — the workflow under test (loop + re-entry node + suspend node after 40-2/40-4)
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WaitForAgentRunActivity.cs` (40-2), `AgentRunResumer.cs` (40-3), `ComputeTaskResumeIndexActivity.cs` (40-4), `AgentRunWaitEventTypes.cs` (40-6 — renamed from `AgentRunEventTypes`, which 32-5 owns)
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:178-187` + `apps/tamma-elsa/src/Tamma.Api/Program.cs:250-260` — 39-10's shipped `Documents:ReEntryDisabled` seam: the exact shape of the `Coding:TaskReEntryDisabled` default this story flips (AC9)
- `apps/tamma-elsa/src/Tamma.Data/Entities/AgentRunWait.cs` (40-3) — the signal row asserted in cross-pod resume
- `apps/tamma-elsa/runner/github-actions/tamma-agent.yml` (40-1) — the runner the self-check dispatches
- **NOT FOUND (prerequisite):** all of the above land in 40-1..40-6. See Dependencies & Sequencing.

## Design Decisions

- **D1 — Extend the 39-6/39-10 Testcontainers fixture, do not build a new one.** Reuse the
  container-per-fixture Postgres + Elsa EF persistence + capturing event client, adding the
  `agent_run_waits` table (40-3) and stub sub-workflows for the non-coding cycle stages
  (`context-gathering`/`plan-generation`/…/`pull-request` return canned success so the run reaches
  the TDD loop fast). The agent is a fake (`IGitHubActionsClient` mock / `agent_provider=mock`) so
  scenarios are deterministic (Technical Note).
- **D2 — "Crash" = dispose host/provider without graceful suspend, fresh provider on the same DB
  (39-10 D8).** The old instance is abandoned mid-loop; a new `SingleIssueCycleWorkflow` is
  dispatched for the same issue against the same Postgres. Re-entry must succeed from git + events
  alone (the point of 40-4). This is the honest crash, not a graceful checkpoint.
- **D3 — Git is faked via the mediation seam, deterministically.** "Commits on the branch" is
  modeled by the fake `IGitHubActionsClient` compare/PR reads returning the landed tasks' files;
  `AGENT_RUN.*` events are real (emitted by the real activities). So re-entry reads a real event
  stream corroborated by a controllable git fake — exactly the 40-4 evidence model.
- **D3b — The re-entry service under test is the REAL one, registered by the fixture, and the task
  list is seeded stably.** 40-4's DI default is `NullTaskLoopReEntryService` (index 0 always), so
  a fixture that inherits the default proves nothing: scenario 4 would observe "re-implemented
  everything" and could be mistaken for a passing run. The fixture registers
  `TaskLoopReEntryService` explicitly (equivalently `Coding:TaskReEntryDisabled=false`) and asserts
  the resolved implementation type before the scenario body. Likewise the stubbed
  `task-creation`/`task-review` sub-workflows return a **fixed, id-stable** task list, because
  40-4 AC9 falls back to index 0 whenever the rehydrated list's `tasks[i].id` disagrees with the
  `taskId` recorded on the events.
- **D4 — Cross-pod = two `WorkflowRuntime`/host instances over one Postgres.** Host A dispatches
  and suspends; host A is disposed; host B (a fresh runtime on the same connection string) receives
  the `workflow_run.completed` (via `AgentRunResumer`) and resumes the DB bookmark. The in-memory
  `WebhookSignalRegistry` is left unregistered on both so only the durable path can resume (AC2/
  40-3 AC5). This shares the 40-3 cross-pod test — one implementation, referenced from both plans.
- **D5 — Mode matrix via the executor factory override.** SaaS scenarios force `github_actions`
  mode (fake Actions client + durable webhook resume); single-user scenarios force `local` mode
  (fake `InProcessLocalRunner`, in-`Execute` completion). The same workflow definition runs both;
  assertions are identical. Proves the mode-agnosticism (AC7).
- **D6 — The 40-1 runner self-check is a referenced CI job, not a C# Testcontainers scenario.**
  Dispatching a real GitHub Actions workflow needs a live repo/runner, which is a CI-workflow
  concern, not an in-process test. So AC8 is satisfied by *including/referencing* the 40-1 runner
  self-test job in this suite's CI stage (a gate that runs the shipped `tamma-agent.yml` with a
  mock agent), keeping the C# suite hermetic. Recorded so AC8 is honestly scoped.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/CodingStepResumeIntegrationTests.cs`**
   — the fixture extension (D1): Postgres + Elsa EF + `agent_run_waits` + stub cycle sub-workflows
   + fake agent/Actions client + capturing event client.

2. **Scenario: durable suspend/resume** (AC1) — run to a mid-task suspend; assert a bookmark row
   exists + no thread parked; deliver the signal; assert resume + advance.

3. **Scenario: cross-pod** (AC2, shared with 40-3) — D4: dispose host A, resume on host B, registry
   unwired; assert resume on B + the `agent_run_waits` row marked received.

4. **Scenario: crash re-entry skips landed tasks** (AC3, AC4) — D2/D3/**D3b**: register the real
   `TaskLoopReEntryService` and assert the resolved type first (against the Null DI default), seed
   the id-stable task list, then land tasks 0..k, crash, fresh dispatch; assert
   `ComputeTaskResumeIndexActivity` → k+1, no agent dispatch for 0..k, completion, and exactly one
   `AGENT_RUN.RECEIVED` success per landed task in the final stream.

5. **Scenario: timeout** (AC5) — never deliver the signal; assert the `DelayFor` deadline → `Timeout`
   edge → needs-human escalation, no hang (bounded test time).

6. **Scenario: inconsistency fails loud** (AC6) — craft event-landed-but-commits-absent for the
   boundary task; assert `CODE.REENTRY.INCONSISTENT_STATE` → `emitStepFailed`.

7. **Mode matrix** (AC7) — parameterize scenarios 2/4/… across `github_actions` and `local` modes
   (D5); assert identical behavior.

8. **Wire the 40-1 runner self-check** (AC8, D6) — reference/include the runner CI job in this
   suite's CI stage.

9. **Flip the re-entry seam** (AC9) — with steps 4 and 7 green, change the
   `Coding:TaskReEntryDisabled` default from `true` to `false` in
   `Tamma.ElsaServer/Program.cs` and `Tamma.Api/Program.cs` (40-4 step 6's one-literal branch), and
   update 40-4's `TaskLoopReEntryRegistrationTests` default pin: stock config now resolves
   `TaskLoopReEntryService`, `Coding:TaskReEntryDisabled=true` still resolves the Null seam. Do this
   **last** — the flip is only honest once the crash proof passes. Finish with `dotnet test` (the
   C# suite) + the runner job green.

## Data & Migrations

None. Consumes 40-3's `agent_run_waits` migration + the existing Elsa/`domain_events` tables.
`dotnet ef migrations has-pending-model-changes` stays clean. (Step 9 touches two `Program.cs`
lines — a DI default, no schema.)

## Events

- **Asserts (does not emit):** `AGENT_RUN.WAIT_SUSPENDED`/`RECEIVED`/`TIMED_OUT`/`TASK_REENTERED`
  (40-6) in the captured stream — exactly-once-per-task (AC4), wake-path correctness, re-entry
  event on skip.

## Test Plan

This story *is* the test plan; the suite is `CodingStepResumeIntegrationTests` (NUnit +
FluentAssertions + Testcontainers). Scenarios map 1:1 to ACs (steps 2-7); the mode matrix (step 7)
runs the core three under both modes; the runner self-check (step 8) is the referenced CI job; the
seam flip (step 9) is verified by 40-4's `TaskLoopReEntryRegistrationTests` with its default pin
moved to the real service.

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — durable suspend/resume | 2 | `CodingStepResumeIntegrationTests` suspend/resume scenario |
| 2 — cross-pod delivery | 3 | cross-pod scenario (registry unwired) |
| 3 — crash re-entry skips landed tasks | 4 | crash re-entry scenario (real service asserted, D3b) |
| 4 — exactly-once per task | 4 | stream assertion (one `AGENT_RUN.RECEIVED` success/task) |
| 5 — timeout escalates, no hang | 5 | timeout scenario |
| 6 — inconsistency fails loud | 6 | inconsistency scenario |
| 7 — mode matrix identical | 7 | parameterized SaaS/local runs |
| 8 — runner contract self-check | 8 | referenced 40-1 runner CI job green |
| 9 — re-entry seam flipped to real by default | 9 | `TaskLoopReEntryRegistrationTests` (default pin moved; kill-switch still works) |

## Dependencies & Sequencing

- **Hard prerequisites:** 40-1..40-6 — the entire epic; this is the composition proof and lands
  last. 40-5's build gate is a separate CI check but exercises the same wiring proven here.
- **Owned here:** the 40-4 AC6 seam flip. 40-4 ships Null-by-default deliberately; nothing else in
  the epic turns per-task re-entry on, so if this story is descoped the epic must say so out loud
  rather than let the headline imply day-one behavior.
- **In place, verified:** 39-6/39-10 Testcontainers fixtures, Elsa EF persistence, the
  dispatch/collect/mediation stack, `IEventRepository`, `TriageItemCycleApplyFaultExecutionTests`
  harness.
- **Feeds:** nothing downstream — it is the epic's end-to-end acceptance.
- **Sequencing within the story:** 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8.

## Risks & Mitigations

- **Integration flakiness (host dispose/rebuild on shared Postgres).** Mitigation: reuse the proven
  39-6/39-10 container-per-fixture pattern; keep scenarios to the AC list; deterministic fake agent.
- **Cross-pod scenario is subtle to simulate in one process.** Mitigation: D4's two-runtime-one-DB
  shape is the 39-10 AC8 precedent; share the single implementation with 40-3.
- **Live-runner dispatch is not hermetic.** Mitigation: D6 keeps the C# suite mocked; the real
  runner is a separate referenced CI job (AC8), honestly scoped.
- **Suite rot / sprawl.** Mitigation: one scenario per AC; no speculative scenarios.
- **The re-entry scenario silently tests the Null seam.** A fixture inheriting 40-4's DI default
  would see index 0 and "everything re-implemented" — a green-looking run that proves nothing.
  Mitigation: D3b asserts the resolved `ITaskLoopReEntryService` implementation type before the
  scenario body, and step 9's flip makes the real service the default anyway.
- **Prerequisite stack incomplete when this starts.** Mitigation: sequence last (execution plan
  wave 5); each scenario can land incrementally as its prerequisite story merges.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | fixture extension (agent_run_waits, stubs, fakes) | 1.0 |
| 2, 3 | suspend/resume + cross-pod scenarios | 1.0 |
| 4 | crash re-entry + exactly-once | 1.0 |
| 5, 6 | timeout + inconsistency scenarios | 0.75 |
| 7 | mode matrix parameterization | 0.75 |
| 8 | runner self-check CI wiring | 0.25 |
| 9 | seam flip (two `Program.cs` defaults) + registration-test pin | 0.25 |
| **Total** | | **5.0** (story estimate: 4-5 days) |

> **Knock-on for EXECUTION-PLAN.md — ABSORBED, see that file for the authoritative roll-up.** This
> total was **4.75**; wave 4's pole becomes 5.0. *(Superseded arithmetic: combined with 40-4's +0.5
> this note computed wall-clock 23.25 / epic total 38.0. It predates 40-3's own raise
> **6.75 → 8.25**, which makes 40-3 the wave-2 pole.)* Reconciled figures: total **39.5**, critical
> path **22.5** (`40-2 → 40-3 → 40-6 → 40-7`), wall-clock **24.5**.
