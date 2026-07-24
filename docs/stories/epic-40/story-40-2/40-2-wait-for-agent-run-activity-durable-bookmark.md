# Story 40-2: `WaitForAgentRunActivity` — Durable Bookmark Suspend + `DelayFor` Timeout

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

As a **platform operator** (and the orchestrator running unattended),
I want the coding/TDD step to **dispatch the agent run and then suspend on a durable
bookmark** — resuming when the run completes or timing out on a durable deadline — instead of
holding the workflow instance Running for ~35 minutes inside an inline `await`,
So that a deploy, pod eviction, or crash during a coding run does not kill the in-flight
monitor and force the whole issue cycle to restart from scratch.

## Priority

P0 — This is the core "resumable by design" change for the coding step. Without it, the
signal plane (40-3) and re-entry (40-4) have no suspend point to resume, and 40-5's
`[ResumeBehavior]` declaration would be a lie (the workflow declares `BookmarkSuspend` but
has no canonical suspend activity).

## Architectural Context (READ FIRST)

**Today the coding step is inline and non-durable.** In `SingleIssueCycleWorkflow.cs:571` the
TDD loop node `tddForTask` is an `ExecuteAgentActivity`
(`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs`). Its
`ExecuteAsync` calls `executor.ExecuteAsync(request, ct)` and **awaits inline**
(`ExecuteAgentActivity.cs:199`). For the `GitHubActionsExecutor`
(`GitHubActionsExecutor.cs:42`) that single `await` covers dispatch → **`MonitorAsync` (a
~35-minute poll/webhook loop)** → collect. Throughout, the Elsa workflow instance stays
**Running** (a live async task holds the wait); nothing is persisted as a suspended bookmark.
A restart mid-wait loses the monitor task, and — with no task re-entry (40-4) — the
orchestrator re-dispatches the cycle from the start.

**The durable primitive already exists in the same activity family — copy its SHAPE, not its
naming.** `WaitForCIResultsActivity`
(`apps/tamma-elsa/src/Tamma.Activities/Testing/WaitForCIResultsActivity.cs`) is the suspend
mechanics to mirror:

- `context.CreateBookmark(payload, OnResumeAsync)` — a result bookmark resumed by an external
  webhook → **`Received`** outcome (`WaitForCIResultsActivity.cs:87`).
- `context.DelayFor(TimeSpan.FromMinutes(timeout), OnTimeoutAsync)` — a **durable** scheduled
  delay bookmark the scheduler auto-resumes at the deadline → **`Timeout`** outcome
  (`WaitForCIResultsActivity.cs:94`). No thread is held for the wait; Elsa burns the loser
  bookmark on completion.
- Resume read-back is **serialization-tolerant** (`ResumeInput.AsBool`
  (`Tamma.Activities/ResumeInput.cs:38`), the #15/#437 lesson).
- `[FlowNode("Received", "Timeout")]` (`:42`), fail-closed sentinel on unparseable/timeout.

*Corrected — this story previously said "the pattern to copy **exactly**", which is a
wording hazard:* `WaitForCIResultsActivity` addresses its bookmark with its **own ad-hoc
payload object**, `new CIResultBookmarkPayload(sessionId, runId)`
(`WaitForCIResultsActivity.cs:80`, created at `:87`) — not a `LifecycleBookmarks`-composed
name — and it is deliberately **not** a member of
`LifecycleBookmarks.CanonicalSuspendActivities`, which today holds exactly two entries:
`WaitForDocumentDecisionActivity → "document-decision"` (`LifecycleBookmarks.cs:101`) and
`WaitForDocumentInputActivity → "document-input"` (`:104`). Copying it literally would ship
a third un-folded bookmark scheme that AC2/AC6 and 40-3's resume cannot address.

**DECISION — the new wait joins the canonical registry; Epic 40 does not add a second
ad-hoc scheme.** `WaitForAgentRunActivity` builds its bookmark name through
`LifecycleBookmarks.ForAgentRun(...)` → `LifecycleBookmarks.Compose`
(`LifecycleBookmarks.cs:38`, segments via `WaitForMergeApprovalActivity.NormalizeSegment`
as used at `:43`/`:46`) and registers itself in `CanonicalSuspendActivities` (AC2, AC6).
Rationale: 40-3 must recompute the name from durable inputs on a *different pod*, which the
`CIResultBookmarkPayload` object shape cannot support tenant-folded; and leaving the epic
with two unfolded schemes in one cycle is exactly what 39-10's registry exists to prevent.
`WaitForCIResultsActivity`'s own migration onto the registry is **out of scope here** and
remains unowned — see Dependencies.

**Epic 39-10 has LANDED — this story consumes it, it does not wait for it.**
*Corrected: earlier drafts of this story and its plan treated 39-10 as an unmerged hard gate
and budgeted a "shim".* `LifecycleBookmarks`
(`apps/tamma-elsa/src/Tamma.Activities/Documents/LifecycleBookmarks.cs`) exists and is in
use: `Compose` at `:38`, `ForStageGate` `:55`, `ForDecisionSession` `:66`, `ForDocumentInput`
`:82`, `CanonicalSuspendActivities` `:98`. This story **edits that real file** to add a
`ForAgentRun` shape and the registry entry — there is no shim and no rebase.

**Dispatch and collect are retained, the inline monitor is replaced.** The dispatch half
(`IAgentDispatchService.DispatchAsync`) runs in the activity's synchronous `Execute` *before*
suspending; the collect half (`IAgentResultCollectorService.CollectAsync`) runs in
`OnResumeAsync`/`OnTimeoutAsync` *after* the run completes. Only the ~35-minute
`IAgentMonitorService.MonitorAsync` inline wait is removed — replaced by the bookmark suspend.

## Acceptance Criteria

1. **New `WaitForAgentRunActivity` with a suspend/resume shape.** A new Elsa activity in
   `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/` mirrors `WaitForCIResultsActivity`:
   in `Execute` it **dispatches the agent run** (via `IAgentDispatchService`), then **creates
   a result bookmark + a `DelayFor` timeout bookmark** and returns (the instance suspends). It
   declares `[FlowNode("Received", "Timeout", "Failed")]` — `Failed` for a dispatch that never
   succeeds (no run to wait for).

2. **Tenant-folded, deterministic bookmark name.** The result bookmark name is built through
   `LifecycleBookmarks.ForAgentRun(tenantId, repository, branchName, sessionId)` (a new shape
   delegating to `LifecycleBookmarks.Compose`, all segments via `NormalizeSegment`). Same
   inputs → identical name on suspend and on the 40-3 resume; different tenants → disjoint
   names. The name is recomputable by the resume side from durable inputs alone.

3. **Durable timeout enforced.** A `context.DelayFor(TimeSpan.FromMinutes(TimeoutMinutes), …)`
   arms a durable deadline (default `request.TimeoutMinutes + safety`, matching the current
   `GitHubActionsExecutor` `TimeoutMinutes` computation). On deadline the activity emits a
   fail-closed sentinel result and takes the **`Timeout`** edge — the workflow can never
   suspend forever. No blocking `Task.Delay`; no thread held for the wait.

4. **Resume collects the result.** `OnResumeAsync` reads the run-completion payload
   (workflow_run id/conclusion/artifacts url — delivered by the 40-3 signal), runs
   `IAgentResultCollectorService.CollectAsync` to produce the `AgentExecutionResult`, sets the
   same outputs `ExecuteAgentActivity` sets, and takes the **`Received`** edge (success or
   agent-reported failure both route here; the loop's existing `Completed`/`Failed` gate on the
   result is preserved).

5. **Serialization-tolerant read-back.** Every resumed-input read uses the coercion helpers
   (`ResumeInput.AsBool`/tolerant string/JsonElement matrix), never a bare `is true` — the
   #15/#437 silent-wrong-branch lesson. A read-back test applies the boxed-bool/`"true"`/
   `JsonElement` truthy+falsy matrix.

6. **Registered as a canonical suspend activity.** `WaitForAgentRunActivity`'s type is added to
   `LifecycleBookmarks.CanonicalSuspendActivities` (`LifecycleBookmarks.cs:98`) with gate
   prefix `"agent-run"`, taking the registry from two entries to three, so 40-5's
   `ResumableStandardStructuralTests` recognizes it as a sanctioned suspend point.
   *Falsifiable:* an implementation that addresses its bookmark with a bespoke payload
   object (the `CIResultBookmarkPayload` shape) instead of a `Compose`-built name fails this
   AC and AC2 together — the registry entry alone is not sufficient.

7. **The TDD loop uses it (SaaS/GHA path).** `SingleIssueCycleWorkflow`'s `tddForTask` node
   (`SingleIssueCycleWorkflow.cs:571`) is switched from `ExecuteAgentActivity`
   (`[FlowNode("Completed", "Failed")]`, `ExecuteAgentActivity.cs:37`) to
   `WaitForAgentRunActivity` (inputs mapped identically:
   repository, branchName, issueNumber, `task="implement"`, `plan_json=currentTaskJson`, the
   deterministic `adl-{issue}-task-{index}` session id, provider, timeout, tenantId). The
   loop's existing outcome wiring at `SingleIssueCycleWorkflow.cs:1181-1183` —
   `Completed → incrementTask`, `Failed → notifyTddRetry`, `Failed → dispatchTddRetry` — is
   preserved through the new edges: `Received` → the result gate (advance on success, the
   `dispatchTddRetry` path on agent-reported failure), `Timeout`/`Failed` → the escalation
   sink. *Falsifiable:* a graph test asserts the post-swap node type **and** that every one of
   those three destinations is still reachable from the loop node; dropping the
   `dispatchTddRetry` edge reddens it. *(The `SetVariable`/re-entry wiring interplay lands
   with 40-4/40-5; this story delivers the activity + the GHA-mode suspend.)*

8. **Single-user (Local) mode is not regressed, and is not claimed to work end-to-end.**
   *Corrected — this AC previously read "Local parity **retained**", which asserted a working
   baseline that does not exist.* `AgentExecutorFactory` auto-resolves `local` whenever no
   GitHub App is configured (`AgentExecutorFactory.cs:69-77`), but the local path is
   **broken today**: `LocalExecutor` shells out to `node <CliEntryPoint> execute-agent`, and
   `CliEntryPoint` defaults to the *relative* `packages/cli/dist/index.js`
   (`LocalExecutor.cs:246`) while the child runs in a per-session temp dir
   (`LocalExecutor.cs:94`, `:184-193`), with no `packages/cli/dist/` built in the tree. Fixing
   that is **40-1 AC8**, not this story.

   What this story owns is therefore bounded: where the resolved mode is `local`, the
   activity runs the executor to completion inside `Execute` and short-circuits to
   **`Received`** (no external result bookmark — there is no webhook to fire it), still
   arming `DelayFor` as a hard timeout. The same outputs and edges are produced in both
   modes, so the workflow definition stays mode-agnostic (the `ExecuteAgentActivity`
   guarantee is preserved) and **no local behaviour gets worse** than it is today.

   *Falsifiable:* the local branch is verified against a stubbed `IAgentExecutor` — assert
   `Received`, outputs set, and **no external bookmark created**. The single-user
   *end-to-end* proof (a real local run producing a real `AgentResultArtifact`) is explicitly
   **not** an AC of this story and belongs to 40-1 AC8 / 40-7's mode matrix. Do not write an
   AC here that cannot fail until 40-1 lands.

9. **Fail-loud on missing dependencies.** No `IAgentDispatchService`/collector registered ⇒
   `TammaError`-style loud failure to the `Failed` edge with a diagnostic, never a silent hang
   (mirrors `ExecuteAgentActivity.cs:167` and `DispatchAgentWorkflowActivity` DI guards).

## Technical Notes

- **Dispatch runs before suspend, in `Execute`.** The activity must obtain the run's existence
  (dispatch 204 + the discover window) *before* it can meaningfully wait; a failed dispatch
  takes `Failed` immediately (no bookmark). Consider dispatching synchronously in `Execute`
  then suspending — do not suspend before a run exists to be signaled.
- **The 40-3 signal carries what the webhook lacks.** The `workflow_run.completed` webhook
  knows repo/branch/run-id but not the Tamma session id; 40-3's persisted signal row bridges
  that so the resume can address this exact bookmark. This story defines the bookmark *name
  contract* (AC2) that 40-3's resume recomputes; keep them in lockstep.
- **Do not delete `ExecuteAgentActivity`.** It stays for non-resumable/standalone callers and
  tests; only the SingleIssueCycle TDD loop node migrates (AC7). This bounds blast radius.
- **Timeout semantics ≠ agent failure.** `Timeout` (deadline, no completion) is a distinct edge
  from `Received`-with-`success:false` (agent ran, failed). The cycle routes them differently
  (timeout → escalate/needs-human; agent-failure → tdd-with-debug-retry) — keep them separate.
- **`SingleIssueCycleWorkflow.cs`'s per-task loop is a shared edit surface.** Within Epic 40
  the sequence is 40-2 (node swap) → 40-4 (re-entry node) → 40-5 (`[ResumeBehavior]`). Epic
  41's story 41-29 rewires the *same* region (a kind-switch ahead of the task step) and must
  rebase onto the post-40 shape — its `code` case routes to `WaitForAgentRunActivity`, not to
  the `ExecuteAgentActivity` it currently describes. Line cites in this story (`:571`,
  `:1181-1183`) are against today's file and will shift after the swap.

## Dependencies

- **Story 39-10 (Resumable Standard) — LANDED, consumed.** *Corrected: previously listed as a
  HARD unmerged gate.* `LifecycleBookmarks` (`Compose` `:38`, `NormalizeSegment` via
  `WaitForMergeApprovalActivity`, `CanonicalSuspendActivities` `:98`) and `ResumeInput.AsBool`
  (`ResumeInput.cs:38`) all exist; AC2/AC5/AC6 edit and extend them directly. No gate, no shim.
- **Story 40-1 (AC8) — BLOCKING for AC8's single-user end-to-end only.** The local executor
  *class* is wired and `AgentExecutorFactory` makes `local` the default with no GitHub App,
  but the local **path** cannot resolve its entry point today (AC8). 40-2's local branch is
  code-complete and unit-testable against a stubbed executor without 40-1; a *running*
  single-user coding step is not. This edge does not gate the GitHub Actions path, AC1-AC7, or
  AC9, so it does not move 40-2's start date.
- **Story 40-3** — the durable signal + resume endpoint that actually resumes this bookmark
  from the webhook. Developed in lockstep against AC2's name contract; 40-2 is testable against
  a direct bookmark resume before 40-3 lands.
- **Existing (verified):** `WaitForCIResultsActivity` (the suspend *shape*, not its naming —
  see Architectural Context), `IAgentDispatchService`/`IAgentResultCollectorService`/
  `GitHubActionsExecutor`, `LocalExecutor`/`AgentExecutorFactory` **as wired classes** (their
  runtime path is the 40-1 caveat above), Elsa 3 bookmarks + `DelayFor` + EF persistence.
- **Explicitly NOT owned here (genuinely unowned):** migrating `WaitForCIResultsActivity`
  off `CIResultBookmarkPayload` onto `LifecycleBookmarks` + the canonical registry. After
  this story the codebase still has one ad-hoc scheme (CI results); Epic 40 simply does not
  add a second. No story currently owns that burn-down.

## Estimated Effort

5-7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-23 | 1.0.0   | Initial story creation | Claude |
| 2026-07-24 | 1.1.0   | Code-verified revision: 39-10 recorded as LANDED (gate + shim struck); DECISION recorded that the new wait joins `LifecycleBookmarks.CanonicalSuspendActivities` rather than repeating `WaitForCIResultsActivity`'s ad-hoc `CIResultBookmarkPayload` scheme; AC8 rewritten from "Local parity retained" to an honest, falsifiable statement of what the local branch does, with 40-1 AC8 named as the blocking edge for the single-user end-to-end proof; AC6/AC7 made falsifiable | Claude |
