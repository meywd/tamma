# Story 40-7: End-to-End Crash/Restart + Mode-Matrix Integration Proof

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

As a **platform maintainer**,
I want an integration suite that proves the coding step **survives a crash mid-run, resumes
across pods, re-enters at the right task without re-implementing landed work, and behaves
identically in SaaS (GHA) and single-user (local) modes**,
So that "the coding step is resumable by design" and "the runner works end-to-end" are proven
against a real Postgres + Elsa persistence, not just asserted by unit tests.

## Priority

P0 — Unit tests prove each piece (40-2 suspend, 40-3 resume, 40-4 re-entry); only an integration
suite proves they compose into the resumability guarantee the epic exists to deliver. This is the
epic's end-to-end acceptance proof (the 39-10 `LifecycleReEntryIntegrationTests` analogue for
code).

## Architectural Context (READ FIRST)

**The precedent is 39-10's crash/restart integration.** 39-10 AC7/AC8 (its D8) run a lifecycle
workflow to a mid-point on **Testcontainers Postgres with Elsa EF persistence**, **kill the
instance without a graceful suspend** (dispose the host/provider), build a **fresh provider on the
same database**, dispatch a **fresh instance for the same issue**, and assert correct re-entry +
no duplicate work + exactly-one acceptance. This story does the same for the coding step, with git
+ DCB events (not the document store) as the re-entry truth.

**Two modes to prove** (CLAUDE.md universal rule):

- **SaaS / GitHub Actions** — dispatch via the real `tamma-agent.yml` runner (40-1) on a test
  repo (or a mocked `IGitHubActionsClient` when a live runner is impractical in CI); resume via the
  durable webhook path (40-3); re-enter from git + events (40-4).
- **Single-user / local** — the in-process local runner (40-1 AC8); no external webhook; the wait
  completes in-process; re-entry from local git + central-schema events.

**The composition under test:**

```
dispatch → WaitForAgentRunActivity suspends (durable bookmark)   [40-2]
         → workflow_run.completed on pod B resumes it            [40-3]
         → collect → loop advances
   ── crash mid-loop (instance disposed) ──
   fresh SingleIssueCycle for the same issue
         → ComputeTaskResumeIndexActivity reconstructs index     [40-4]
         → skips landed tasks, resumes at the in-flight task
         → completes; exactly one agent dispatch per task
```

## Acceptance Criteria

1. **Durable suspend/resume proven (Testcontainers).** A test runs the TDD loop to a mid-task
   suspend on the durable bookmark (Elsa EF persistence on Postgres), delivers the completion
   signal, and asserts the workflow resumes and advances. No thread was parked for the wait
   (the instance was genuinely suspended, verified via the bookmark store).

2. **Cross-pod delivery proven.** Suspend on host A; **dispose host A** (no graceful resume);
   deliver the `workflow_run.completed` signal via a **fresh host B on the same store**; assert
   resume on B — with the in-memory `WebhookSignalRegistry` unwired, so only the durable path
   (40-3) can be responsible. (Shared with 40-3 AC5.)

3. **Crash re-entry skips landed tasks.** Run a multi-task cycle so tasks 0..k land (commits on
   the branch + `AGENT_RUN.*` events), **kill the instance** (dispose, no suspend), dispatch a
   **fresh** `SingleIssueCycleWorkflow` for the same issue; assert it re-enters at task k+1
   (`ComputeTaskResumeIndexActivity` output), does **not** re-dispatch an agent run for tasks 0..k,
   and completes. (Shared with 40-4 AC2.)
   **The fixture must register the REAL `TaskLoopReEntryService`** (or set
   `Coding:TaskReEntryDisabled=false`): 40-4 ships the Null seam as the DI default, which returns
   index `0` unconditionally, so against the stock registration this assertion is unreachable —
   it would "pass" only by re-implementing everything. The registration is part of the scenario,
   asserted explicitly, not inherited.

4. **Exactly-once per task.** Across the crash + re-entry, the final DCB stream contains exactly
   one successful agent run (`AGENT_RUN.RECEIVED` success) per landed task — no duplicate
   implementation, no duplicate events (the 39-10 "exactly one acceptance" analogue).

5. **Timeout path proven.** A run whose completion signal never arrives hits the durable
   `DelayFor` deadline, takes the `Timeout` edge, and the cycle escalates (needs-human), never
   hangs.

6. **Inconsistency fails loud.** A crafted git/event disagreement (event-landed but commits
   absent) makes re-entry throw `CODE.REENTRY.INCONSISTENT_STATE` and the cycle route to its loud
   fail sink — never silently skipping the task (40-4 AC4).

7. **Mode matrix.** The core scenarios (suspend/resume, crash re-entry, exactly-once) run in
   **both** SaaS (GHA path, real runner or mocked Actions client) and single-user (local runner)
   modes, asserting identical workflow behavior — the `ExecuteAgentActivity`→`WaitForAgentRunActivity`
   mode-agnosticism guarantee.

8. **Runner contract self-check (from 40-1).** The 40-1 runner self-test (dispatch the real
   `tamma-agent.yml` with a mock agent → `tamma-result` artifact → schema-valid `result.json`) is
   included in or referenced by this suite so the CI-side contract is exercised end-to-end, not
   just the C# wait side.

9. **This story flips the re-entry seam live (40-4 AC6).** Once AC3/AC4 are green, 40-7 changes the
   `Coding:TaskReEntryDisabled` default from `true` to `false` in both hosts
   (`Tamma.ElsaServer/Program.cs`, `Tamma.Api/Program.cs`), so the epic's shipped default becomes
   the real `TaskLoopReEntryService` with the flag left as an operator kill-switch — the posture
   39-10 reached for documents (`Program.cs:178-187`). Falsifiable and ratcheted: the DI test from
   40-4 flips with it — stock configuration must now resolve `TaskLoopReEntryService`, and
   `Coding:TaskReEntryDisabled=true` must still resolve `NullTaskLoopReEntryService`. Until this AC
   lands, per-task re-entry is wired but inert; **this AC is what makes the epic's re-entry claim
   true of a stock deployment.**

## Technical Notes

- **Reuse the 39-10 / 39-6 Testcontainers fixture shape.** Container-per-fixture Postgres, Elsa EF
  persistence incl. the bookmark + `agent_run_waits` tables, stub sub-workflows for the non-coding
  cycle stages, a capturing event client. Do not invent a new harness.
- **"Crash" = dispose without graceful suspend + fresh provider on the same DB** (39-10 D8). The
  old instance is dead weight; the new one must not depend on it — that is the honest crash shape.
- **Mock the agent, not the plumbing.** Use `agent_provider=mock` / a fake `IGitHubActionsClient`
  so the suite is deterministic and fast; the point is the suspend/resume/re-entry plumbing, not
  the LLM. The 40-1 self-test covers the real runner separately.
- **Register the real re-entry service explicitly, and pin the task list.** Besides AC3's DI
  registration, the fixture must feed a *stable* task list: 40-4 AC9 rehydrates from the accepted
  task-breakdown `plan` document and verifies `tasks[i].id` against the `taskId` on the per-task
  events, falling back to index 0 on any mismatch. A fixture that lets the stubbed
  `task-creation`/`task-review` return a differently-ordered list will therefore see index 0 and
  the scenario will fail for the *right* reason — seed the stub deterministically and assert the
  rehydration path was used.
- **Keep scenarios to the AC list** — integration suites rot when they sprawl; each AC is one
  scenario.

## Dependencies

- **Stories 40-1..40-6 — HARD.** This is the composition proof; every piece must be in.
  40-1 (runner + local), 40-2 (suspend), 40-3 (durable resume + cross-pod), 40-4 (re-entry +
  the `Coding:TaskReEntryDisabled` seam this story flips), 40-5 (declaration — the structural gate
  is a separate build check but the wiring it guards is exercised here), 40-6 (the event family the
  assertions read, including the `taskId` the rehydration check uses).
- **Owns, not just consumes:** the 40-4 AC6 seam flip (AC9). No other story flips it; if 40-7 slips,
  per-task re-entry ships inert.
- **Existing (verified):** the 39-10/39-6 Testcontainers fixtures, Elsa EF persistence, the
  dispatch/collect/mediation stack, `IEventRepository`.

## Estimated Effort

4-5 days (plan totals 5.0 — +0.25 for the AC9 seam flip + its DI assertions)

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
| 2026-07-24 | 1.1.0   | Review pass: AC3 now requires the fixture to register the REAL `TaskLoopReEntryService` (the DI default is the Null seam, which would make k+1 unreachable); new AC9 — this story owns the seam flip that makes re-entry the shipped default | Claude |
