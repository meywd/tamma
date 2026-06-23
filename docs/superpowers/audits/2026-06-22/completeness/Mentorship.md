# Completeness Audit — MentorshipWorkflow

**Audited:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/Mentorship/*` (notably `FlowNodeActivities.cs`, 1615 lines)
**Definition id:** `mentorship` · **Display name:** "Main Mentorship Workflow"

---

## 1. Purpose & owner

The top-level Elsa `Flowchart` that orchestrates the entire 28-state mentorship session lifecycle:
INIT → VALIDATE → ASSESS → PLAN → IMPLEMENT → MONITOR → QUALITY → REVIEW → MERGE → REPORT →
PROFILE → COMPLETED, plus a bug fast-path (VALIDATE → Debugging → QUALITY), an assessment loop, a
planning loop, a blocker-escalation ladder (HINT → GUIDANCE → ASSISTANCE → ESCALATE), a quality
auto-fix retry loop, and a review-iteration loop. It composes the eight Epic-7 sub-workflows
(`llm-call`, `context-gathering`, `testing-pipeline`, `code-review`, `assessment`,
`blocker-diagnosis`, `tdd-cycle`, `debugging`) via `DispatchWorkflow` and routes between custom
`FlowNode` activities by named outcome, with `FlowDecision` guards and `SetVariable` counters.

**Owning epic / story:** Epic 7 (AI-Powered Mentorship), **Story 7-1A** "Main Mentorship Workflow
(Code-First Flowchart)" (`docs/stories/epic-7/story-7-1A/7-1A-main-workflow-code-first.md`),
enhancing **Story 7-1** "Mentorship State Machine". It is the integration point for stories 7-1B
through 7-1I.

---

## 2. Maturity: **partial**

This workflow is the **opposite** of a thin happy-path stub. Its *topology* is the most complete
in the Epic-7 set: 28 state activities + 4 exception states, ~100 declarative connections (well
past the AC3 "60+" bar), 8 `DispatchWorkflow` sub-workflow invocations, 5 `FlowDecision` guards, 5
counter-increment + 3 reset `SetVariable<int>` nodes, and an `Error` edge on essentially every
node routing to `FAILED`/`DIAGNOSE_BLOCKER`/`ESCALATE_TO_SENIOR`. Structurally it satisfies AC1
(code-first `Flowchart`, registered), AC2 (all 28 states as activities), AC3 (transitions incl.
all named loops and the bug fast path), AC4 (guards as `FlowDecision`), AC5 (sub-workflow
dispatches), and AC8 (issue-type routing).

It is **not complete** for one decisive reason and several supporting ones:

- **Every routing decision is a simulated dice roll.** The `FlowNode` activities in
  `FlowNodeActivities.cs` do not assess, monitor, diagnose, gate, review, or merge anything real —
  they call `Random.Shared.Next(100)` and pick an outcome by probability (22 such calls). The
  workflow "completes" sessions by chance, not by work. `ReviewPlanActivity` even comments
  *"Simulate review (in production, this would use Claude AI or human review)"* (line 430).
- **The rich activities that DO contain real logic are bypassed.** `QualityGateCheckActivity`,
  `MonitorImplementationActivity`, `AssessJuniorCapabilityActivity`, `DiagnoseBlockerActivity`,
  `CodeReviewActivity`, `MergeCompleteActivity`, `ProvideGuidanceActivity` (hundreds of lines each)
  exist in the same folder but the `*FlowActivity` wrappers re-implement the decision inline with
  `Random` instead of delegating to them.
- **No bookmark / timer / wait anywhere** — AC6 (bookmark-based pausing) and AC7 (timeout
  escalation) are entirely unimplemented. `MONITOR_PROGRESS "Steady"→MONITOR_PROGRESS` and
  `MONITOR_REVIEW "Pending"→MONITOR_REVIEW` are tight self-loops with no `Delay`/bookmark, so they
  busy-spin the dice instead of waiting for a submission or review webhook.
- **`DispatchWorkflow` inputs are placeholders** — the `llm-call` dispatch sends a hardcoded
  `taskPrompt="Generate plan decomposition"` regardless of state; `blocker-diagnosis` sends
  `repository=""`/`branchName=""`; `tdd-cycle` sends `taskDescription=""`. The sub-workflows are
  wired but fed empty/constant payloads.

So: **complete skeleton, fabricated brain.** It is "partial" (structure done, the work that gives
the structure meaning is stubbed), not "thin" (the structure itself is genuinely thorough).

---

## 3. Current capabilities (what it does today)

- **Flowchart topology (real & complete):** all 28 `MentorshipState` values are nodes; ~100
  connections implement every path named in AC3 (happy path, bug fast path, assessment loop,
  planning loop, blocker ladder, quality retry, review iteration) plus dense `Error` edges.
- **Configurable retry envelopes:** `InitRetryLimits` reads `maxBasicRetries`/`maxDebugRetries`
  from workflow input; guards `GuardAssessmentRetries`, `GuardPlanIterations`, `GuardQualityRetries`,
  `GuardReviewIterations`, `GuardBlockerEscalation` enforce ceilings; counters increment/reset
  correctly; exhaustion routes to `ESCALATE_TO_SENIOR`/`MANUAL_FIX_REQUIRED`/forced merge (no
  infinite loops).
- **Skill-level adaptation:** `ExtractSkillLevel` maps the `assessment` sub-workflow's confidence
  (0–1) to 1–5; `AdjustSkillOnCorrect/Partial/Incorrect` nudge skill ±1 on re-assessment outcomes.
- **Sub-workflow composition:** 8 `DispatchWorkflow` nodes with `WaitForCompletion=true`; the
  `assessment` dispatch captures its result into a variable and feeds `ExtractSkillLevel`.
- **State persistence + local event log:** every `FlowNode` activity calls
  `IMentorshipSessionRepository.UpdateStateAsync` and `LogEventAsync(MentorshipEvent{...})` inside
  a try/catch, routing exceptions to an `Error` outcome (good error discipline at the activity
  shell level).
- **Retry-exhaustion alerting:** `ClarifyRequirements`, `ReExplainStory`, `AutoFixIssues` emit
  `WORKFLOW.RETRY_EXCEEDED` via `WorkflowRetryEmitter` when their `MaxAttempts` ceiling is hit
  (Wave C.4 §3) — a real, useful audit/alert signal.
- **Exception states:** `Paused`, `Cancelled`, `Failed`, `Timeout` activities persist terminal
  state and log an event.

---

## 4. Intended full scope (with citations)

**Story 7-1A acceptance criteria** define the target:

- **AC2 outcome example:** `AssessJuniorCapabilityActivity` outcomes
  `Correct/Partial/Incorrect/Timeout` must come from a real assessment. The AC's own code sample
  (lines 217-233) routes on `result.Confidence` from the Assessment sub-workflow — *not* a random
  roll.
- **AC5 (Sub-Workflow Invocations):** *"Sub-workflow results flow back into main workflow
  variables"* and each dispatch must carry the right inputs (e.g. `LlmCall` from
  AUTO_FIX `role=implementer "fix these issues"`, from PLAN `role=analyst "decompose this story"`).
  Today only `assessment` flows its result back; the others discard outputs and send constant
  inputs.
- **AC6 (Bookmark-Based Pausing):** workflow MUST pause at `MONITOR_PROGRESS` (await code
  submission/timeout), `MONITOR_REVIEW` (await review webhook), `PROVIDE_HINT/GUIDANCE/ASSISTANCE`
  (await junior response), `MANUAL_FIX_REQUIRED`, `ESCALATE_TO_SENIOR`; bookmarks resumable via
  Elsa REST API and surviving server restart; `USER_PAUSE`/`USER_RESUME` from any state. **None of
  this exists.**
- **AC7 (Timeout Handling):** per-state configurable timers (15/30/45 min defaults) and the
  escalation chain Hint(15)→Guidance(30)→Assistance(45)→Escalate(60)→Timeout(120); cancel on normal
  transition; session-level 120-min cap. **No timers exist** — `TimeoutSessionActivity` is reachable
  only via `ASSESS_JUNIOR "Timeout"` (itself never produced by the dice wrapper).
- **AC9 (Execution Log & Observability):** every transition emits structured
  `{sessionId, fromState, toState, event, timestamp, duration}`, and metrics
  `mentorship.transitions.total`, `mentorship.state.duration`, `mentorship.timeouts.total`. **No
  metrics are emitted**; `MentorshipEvent` rows carry no duration.
- **Logging requirements (7-1A):** structured context `{sessionId, juniorId, storyId,
  currentState}` on every entry; never log PII or full LLM content.
- **Agent-architecture pivot — cross-cutting rule**
  (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §0 rules 1-2):
  *"A workflow STEP MUST NEVER call an external API/provider directly … The LLM path is mediated by
  a `call-LLM` endpoint."* The mentorship machine must obtain every AI judgement (assessment,
  plan review, blocker diagnosis, quality gate, code review) through `DispatchWorkflow("llm-call")`
  / the tamma-api `call-LLM` seam — never from in-process logic, and certainly not from `Random`.
- **CLAUDE.md project rules:** tenant→system→error resolution with **no empty/plain fallback**; no
  silent-failure / false-success; emit DCB audit events for every operation; SaaS tenant scoping.

**Domain best-practice** for a long-running, human-in-the-loop mentorship orchestrator adds:
durable suspension at every "wait for human/CI/webhook" point (not busy-loops); idempotent
side-effects across resume/replay (no duplicate PRs, merges, or LLM spend on replay); real
deterministic guards driven by sub-workflow outputs rather than chance; a workflow output contract
(final session status/skill delta) for the caller/dashboard; and tenant threading so SaaS sessions
resolve the right prompts/credentials/budget.

---

## 5. Missing capabilities

| # | Capability | Priority | dependsOn |
|---|---|---|---|
| 1 | **Real assessment routing** — `AssessJuniorFlowActivity` must route `Correct/Partial/Incorrect/Timeout` from the `assessment` sub-workflow's confidence/status (per AC2 sample) instead of `(skill*20 - complexity*10 + 50)` vs `Random`. Today the entire mentorship path is chosen by dice. | P0 | 7-1E assessment output; 32-5 mediation (assessment's own AI) |
| 2 | **Real quality-gate routing** — `QualityGateFlowActivity` must consume the `testing-pipeline` (7-1C) result (build/test/lint/coverage) for `Passed/Failed`, not `Random.Next<75`. Delegate to the existing `QualityGateCheckActivity` logic. | P0 | 7-1C testing output |
| 3 | **Real review-status routing** — `MonitorReviewFlowActivity` must read actual PR review state (from `code-review` 7-1D output / platform webhook) for `Approved/ChangesRequested/Pending`, not `Random`. The `Pending` self-loop must be a bookmark wait, not a busy spin. | P0 | 7-1D code-review output; AC6 bookmark |
| 4 | **Real progress monitoring** — `MonitorProgressFlowActivity` must derive `Steady/Complete/Stalled/Circular/Slowing` from actual progress signals (commits, CI, elapsed time via `DetectProgressActivity`/`MonitorImplementationActivity`), not `Random`; `Steady→MONITOR` must suspend on a bookmark/timer. | P0 | AC6 bookmark + AC7 timer |
| 5 | **Real blocker diagnosis routing** — `DiagnoseBlockerFlowActivity` must route `Hint/Guidance/Assistance/Escalate` from the `blocker-diagnosis` (7-1G) severity output (or `DiagnoseBlockerActivity`), not `Random`. | P0 | 7-1G blocker output |
| 6 | **Real plan-review / pattern / auto-fix / manual-fix routing** — `ReviewPlanActivity` (70% dice), `DetectPatternActivity` (60% dice), `AutoFixIssuesActivity` (70% dice), `ManualFixRequiredActivity` (70% dice) must route on actual review/analysis/fix results (via `llm-call` / quality re-run), not `Random`. | P0 | 32-5 mediation (`llm-call`); 7-1C |
| 7 | **Bookmark-based suspension (AC6)** — create durable, resumable bookmarks at `MONITOR_PROGRESS`, `MONITOR_REVIEW`, `PROVIDE_HINT/GUIDANCE/ASSISTANCE`, `MANUAL_FIX_REQUIRED`, `ESCALATE_TO_SENIOR`; resumable via REST; survive restart; `USER_PAUSE`/`USER_RESUME` from any state. Entirely absent. | P0 | Elsa bookmark API; resume endpoints |
| 8 | **Timeout handling + escalation chain (AC7)** — per-state durable timers (15/30/45 default), the Hint→Guidance→Assistance→Escalate→Timeout chain, cancel-on-normal-transition, and a 120-min session cap that routes to `TimeoutSessionActivity`. No timers exist; `Timeout` is currently unreachable. | P0 | Elsa scheduling/`Delay` |
| 9 | **Correct `DispatchWorkflow` inputs** — feed each sub-workflow its real per-state payload: `llm-call` per-state `role`/`action`/`taskPrompt` (PLAN=analyst/decompose, AUTO_FIX=implementer/fix), `blocker-diagnosis` real `repository`/`branchName`, `tdd-cycle` real `taskDescription` from the plan. Today they are hardcoded/empty. | P0 | plan/context outputs |
| 10 | **Sub-workflow outputs flow back (AC5)** — capture and use `testing-pipeline`, `code-review`, `blocker-diagnosis`, `tdd-cycle`, `llm-call` results in workflow variables; only `assessment` does so today. Drives gaps #2/#3/#5/#6. | P0 | none |
| 11 | **No false-success on sub-workflow failure** — a `DispatchWorkflow` that returns failure currently flows straight on to the success outcome (e.g. `tddWorkflow → monitorProgress`, `testingWorkflow → resetReviewIteration`). Add a failure-aware decision after each dispatch routing to `DIAGNOSE_BLOCKER`/`FAILED`. | P0 | none |
| 12 | **Idempotency across resume/replay** — guard side-effecting nodes (`MERGE_AND_COMPLETE`, `PREPARE_CODE_REVIEW`/PR creation, LLM-spending dispatches) so a bookmark resume or workflow replay does not re-merge, re-open a PR, or re-bill. None guarded. | P0 | none |
| 13 | **Tenant scoping (SaaS)** — thread `tenantId` from workflow input through `llm-call` and all sub-workflow dispatches for SaaS prompt/credential/budget resolution; `MentorshipWorkflow` references no tenant id (0 occurrences); `WorkflowRetryEmitter` already needs it and currently best-effort-reads a variable that is never set. | P1 | 32-5 / 27 prompt source |
| 14 | **DCB audit events** — emit system-wide DCB events (`MENTORSHIP.SESSION_STARTED`, `MENTORSHIP.STATE_TRANSITIONED`, `MENTORSHIP.ESCALATED`, `MENTORSHIP.MERGED.{SUCCESS,FAILED}`, `MENTORSHIP.COMPLETED`, …) with tags `{sessionId, juniorId, storyId, tenantId, fromState, toState}`; today only local `MentorshipEvent` rows exist (no `IEventStore` append). | P1 | none |
| 15 | **Observability metrics (AC9)** — `mentorship.transitions.total`, `mentorship.state.duration`, `mentorship.timeouts.total`, plus per-state duration on each `MentorshipEvent`. None emitted. | P1 | none |
| 16 | **Workflow output contract** — expose a final result (`sessionStatus`, `finalState`, `skillLevelDelta`, `prUrl`) via `SetOutput` so the dashboard/caller and `MentorshipService` can consume the outcome; `MentorshipWorkflow` has 0 outputs. | P1 | none |
| 17 | **Input validation / fail-fast** — validate `SessionId != Guid.Empty` and non-empty `StoryId`/`JuniorId` at INIT; bad input currently proceeds and fails deep (e.g. `AssessJuniorFlow` maps a missing story/junior to a bare `Error`). | P2 | none |
| 18 | **Delegate to the rich activities** — route `*FlowActivity` wrappers to the existing `QualityGateCheckActivity` / `MonitorImplementationActivity` / `DiagnoseBlockerActivity` / `CodeReviewActivity` / `MergeCompleteActivity` / `ProvideGuidanceActivity` (hundreds of lines of real logic) rather than re-implementing with dice — removes duplication and dead code. | P2 | overlaps #1-#6 |
| 19 | **PII-safe logging pass** — confirm no junior PII / full LLM content logged at INFO across the activities, per 7-1A logging requirements. | P3 | none |

---

## 6. Ordered build-out spec (to reach complete)

Each step names the activity/edge to add or change, the branch condition, the DCB event, and the
failure edge. Honor: steps route LLM/AI/git work via `DispatchWorkflow` to the sub-workflows /
tamma-api `call-LLM` seam (never call providers directly); no empty/plain fallback
(tenant→system→error); no silent-failure / false-success; emit DCB audit events.

1. **Thread tenant + validate inputs at INIT.** In `InitStoryProcessingActivity`, validate
   `SessionId != Guid.Empty` and non-empty `StoryId`/`JuniorId`; on failure route `Error → FAILED`.
   Read `tenantId` from workflow input into a `tenantId` variable (so `WorkflowRetryEmitter` and all
   dispatches can use it). Emit DCB `MENTORSHIP.SESSION_STARTED` with `{sessionId, juniorId,
   storyId, tenantId}`. *(Missing #13, #17)*

2. **Make every `DispatchWorkflow` failure-aware.** After each dispatch (`assessment`,
   `context-gathering`, `testing-pipeline`, `code-review`, `blocker-diagnosis`, `tdd-cycle`,
   `debugging`, `llm-call`), bind `.Result` to a variable and insert a `FlowDecision` on
   `success`/status: success → existing next node; failure → `DIAGNOSE_BLOCKER` (recoverable) or
   `FAILED` (unrecoverable), emitting `MENTORSHIP.SUBWORKFLOW.FAILED`. Removes the current
   straight-through false-success. *(Missing #10, #11)*

3. **Replace `AssessJuniorFlowActivity` dice with the assessment result.** Route
   `Correct/Partial/Incorrect/Timeout` from the captured `assessmentDispatchResult`
   (`status` + `confidence`, per the AC2 sample) — the `assessment` sub-workflow already runs before
   it. On missing/failed result → `Error → FAILED` (not a fabricated outcome). Keep `Random` only
   behind an explicit `Mentorship:UseMock` flag, logged as mock. *(Missing #1)*

4. **Replace `QualityGateFlowActivity` dice with the testing result.** Move the `testing-pipeline`
   dispatch to *before* the gate (or read its result), and route `Passed/Failed` from real
   build/test/lint/coverage status (delegate to `QualityGateCheckActivity`). `Error → DIAGNOSE_
   BLOCKER`. Emit `MENTORSHIP.QUALITY_GATE.{PASSED,FAILED}`. *(Missing #2, #18)*

5. **Replace `MonitorReviewFlowActivity` dice + busy-loop.** Route `Approved/ChangesRequested/Pending`
   from the `code-review` (7-1D) output / PR review state. Convert the `Pending → MONITOR_REVIEW`
   self-loop into a **bookmark** `review-{sessionId}` resumed by a review webhook, with a timer that
   resumes `Timeout → ESCALATE_TO_SENIOR`. Emit `MENTORSHIP.REVIEW.{APPROVED,CHANGES_REQUESTED}`.
   *(Missing #3, #7, #8)*

6. **Replace `MonitorProgressFlowActivity` dice + busy-loop.** Derive
   `Steady/Complete/Stalled/Circular/Slowing` from real progress signals (commits/CI/elapsed via
   `MonitorImplementationActivity`/`DetectProgressActivity`). Convert `Steady → MONITOR_PROGRESS`
   into a **bookmark/timer** wait (await code-submission event or per-skill-level timeout) so it
   suspends instead of spinning. Timeout → `PROVIDE_HINT` (AC7 chain). *(Missing #4, #7, #8)*

7. **Replace `DiagnoseBlockerFlowActivity` dice.** Route `Hint/Guidance/Assistance/Escalate` from
   the `blocker-diagnosis` (7-1G) severity output (or `DiagnoseBlockerActivity`). `Error →
   ESCALATE_TO_SENIOR` (already wired). *(Missing #5, #18)*

8. **Replace `ReviewPlan` / `DetectPattern` / `AutoFix` / `ManualFix` dice.** `ReviewPlan` routes on
   an `llm-call(role=analyst, action=review-plan)` verdict (Approved/NeedsAdjustment); `DetectPattern`
   on a real circular-pattern check over session history; `AutoFixIssues` on the result of an
   `llm-call(role=implementer, action=fix-issues)` + quality re-run; `ManualFixRequired` on a
   bookmark resume signalling the fix was applied. Each: failure → `Error` edge already present.
   *(Missing #6)*

9. **Fix `DispatchWorkflow` inputs.** PLAN's `llm-call` → `{tenantId, role:analyst,
   action:decompose-story, taskPrompt:<story>, sessionId}`; AUTO_FIX's `llm-call` →
   `{role:implementer, action:fix-issues, variables:{qualityReport}}`; `blocker-diagnosis` → real
   `repository`/`branchName` from context; `tdd-cycle` → `taskDescription` from the plan output.
   Pull these from the `context-gathering`/plan variables, not constants. *(Missing #9, #10, #13)*

10. **Add bookmark resume endpoints + USER_PAUSE/RESUME.** In `Tamma.ElsaServer`/`Tamma.Api`, add
    authenticated endpoints to resume `review-{sessionId}`, `progress-{sessionId}`,
    `junior-response-{sessionId}`, `manual-fix-{sessionId}`, `senior-{sessionId}`, and a
    pause/resume signal usable from any active state. Reject when no live bookmark. *(Missing #7)*

11. **Implement the AC7 timeout chain.** Schedule durable per-state timers
    (Hint 15 / Guidance 30 / Assistance 45 / Escalate 60) alongside each wait bookmark; first
    signal wins, the other is burned; a session-level 120-min timer routes to
    `TimeoutSessionActivity`. Cancel timers on normal transition. Emit `MENTORSHIP.TIMEOUT`.
    *(Missing #8)*

12. **Guard side-effects for idempotency.** Before `MERGE_AND_COMPLETE` and `PREPARE_CODE_REVIEW`
    (PR creation) and before LLM-spending dispatches, check a per-session "already done" marker so a
    resume/replay does not re-merge / re-open / re-bill. *(Missing #12)*

13. **Delegate wrappers to the rich activities.** Where a real `CodeActivity` already exists
    (`QualityGateCheckActivity`, `MonitorImplementationActivity`, `DiagnoseBlockerActivity`,
    `CodeReviewActivity`, `MergeCompleteActivity`, `ProvideGuidanceActivity`), have the `*FlowActivity`
    invoke it and map its result to outcomes instead of re-implementing. *(Missing #18)*

14. **Emit DCB audit events + per-state duration.** On each transition emit a DCB
    `MENTORSHIP.STATE_TRANSITIONED` (`{sessionId, juniorId, storyId, tenantId, fromState, toState,
    durationMs}`) via the system `IEventStore`/DCB seam in addition to the local `MentorshipEvent`;
    emit terminal `MENTORSHIP.COMPLETED`/`MENTORSHIP.FAILED`/`MENTORSHIP.TIMEOUT`. *(Missing #14)*

15. **Emit metrics + expose workflow output.** Add `mentorship.transitions.total`,
    `mentorship.state.duration`, `mentorship.timeouts.total`; record state duration on events. Add
    `SetOutput` for `{sessionStatus, finalState, skillLevelDelta, prUrl}` so callers/dashboard
    consume the outcome. *(Missing #15, #16)*

16. **PII-safe logging pass.** Confirm no junior PII / full LLM content is logged at INFO across all
    Mentorship activities (log ids/lengths/token-counts/summary only). *(Missing #19)*

---

## 7. Summary

`MentorshipWorkflow` has the richest, most complete *topology* of the Epic-7 workflows — all 28
states, ~100 connections, every named loop and the bug fast path, real retry guards/counters,
8 sub-workflow dispatches, and dense `Error` edges. But it is **partial**, not complete, because
the decision logic inside its `FlowNode` activities is **simulated with `Random.Shared.Next()`**
(22 call sites): assessments, quality gates, reviews, progress, blockers, plan reviews, and
auto-fixes all route by chance, the richer real activities that exist in the same folder are
bypassed, and the sub-workflows are fed hardcoded/empty inputs with their outputs (except
`assessment`) discarded. On top of that, the two large behavioural ACs are unimplemented: **AC6
bookmark-based pausing** and **AC7 timeout/escalation** — the "wait" states are tight busy-loops
with no suspension, so the workflow can neither durably wait for a human/CI/webhook nor time out.
Reaching "complete" is overwhelmingly P0 work: replace the dice with real sub-workflow-output- and
`llm-call`-driven routing (mediation rule §0), add durable bookmarks + timers + resume endpoints,
make dispatch failures non-silent, and guard side-effects for idempotency — then P1 tenant
threading, DCB audit events, metrics, and a workflow output contract.
