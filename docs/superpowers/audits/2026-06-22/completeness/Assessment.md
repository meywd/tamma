# Completeness Audit — AssessmentWorkflow

**Audited:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/Assessment/*`
**Definition id:** `assessment` · **Display name:** "Assessment"

---

## 1. Purpose & owner

Reusable Elsa sub-workflow that evaluates a junior developer's understanding of a story's
requirements: gather context → generate targeted questions → deliver them → wait (bookmark) for the
junior's reply → analyze → classify (Correct / Partial / Incorrect / Timeout) → update the skill
profile → emit a recommended next mentorship state. Consumed by `MentorshipWorkflow` (the 28-state
mentorship machine) which dispatches `assessment` with `WaitForCompletion=true` and maps the result
confidence to a 1-5 skill level.

**Owning epic / story:** Epic 7 (AI-Powered Mentorship), **Story 7-1E** "Assessment Sub-Workflow"
(`docs/stories/epic-7/story-7-1E/7-1E-assessment-sub-workflow.md`), enhancing **Story 7-2** "Skill
Assessment Activity" and **Story 7-4** "Claude Analysis Activity".

---

## 2. Maturity: **partial**

The full step skeleton from the 7-1E spec is present and wired correctly: both the response path
and the timeout path are built, classification thresholds are configurable, the skill profile is
persisted with a running-average, outputs are exposed via `SetOutput`, and the workflow is
registered (assembly scan via `AddWorkflowsFrom<LlmCallWorkflow>()` in `Program.cs:119`, same
assembly). This is well beyond a "thin happy-path skeleton".

It is **not complete** because the two AI steps that are the entire point of the workflow are
**not AI** — they are hardcoded heuristics — and the timeout branch is **structurally dead** (no
timer is scheduled and no endpoint resumes the bookmark), so the workflow can hang forever and the
confidence/skill-level signal is fabricated rather than assessed.

---

## 3. Current capabilities (what it does today)

- **ReadInputs** — reads `sessionId`, `storyId`, `juniorId`, `skillLevel`, `previousAttemptJson`;
  derives `attemptNumber` from the previous attempt.
- **GatherContext** — `DispatchWorkflow("context-gathering", WaitForCompletion=true)` with
  `Purpose="Assessment"`, `MaxContextSize=50000`.
- **GenerateQuestions** (`GenerateQuestionsActivity`) — produces skill-adapted questions from a
  **hardcoded question bank** (`GetSkillLevelQuestions`), counts driven by config/defaults; retry
  attempts target previous gaps with a templated wrapper. **No LLM call.**
- **DeliverQuestions** (`DeliverQuestionsActivity`) — formats a Markdown message and delivers via
  Slack / email / api through `IIntegrationService`, falling back to API mode; logs an `Info`
  mentorship event.
- **WaitForResponse** (`WaitForResponseActivity`) — creates an `AutoBurn` bookmark named
  `assessment-{sessionId}-{attemptNumber}`; resume callback reads `Response` → "Responded" or a
  `Timeout` flag → "Timeout". Computes a per-skill-level timeout value but **does not act on it**.
- **AnalyzeResponse** (`AnalyzeResponseActivity`) — **heuristic** scoring on response length,
  technical-term keyword matching, and structure cues; logs an `AIAnalysis` mentorship event.
  **No LLM call.**
- **ClassifyResult** (`ClassifyResultActivity`) — routes by configurable confidence thresholds
  (Correct ≥0.7 → PLAN_DECOMPOSITION, Partial ≥0.4 → CLARIFY_REQUIREMENTS, else → RE_EXPLAIN_STORY;
  no-response → Timeout → DIAGNOSE_BLOCKER).
- **UpdateSkillProfile** (`UpdateSkillProfileActivity`) — appends an assessment entry to the
  junior's `LearningPatterns` JSON, computes running-average confidence, persists, logs a
  `SkillLevelUpdated` event. Separate instance on the timeout path.
- **SetOutput / Expose** — emits `assessmentResult` (JSON), `nextState`, `status`, `skillLevel`
  on both paths.

---

## 4. Intended full scope (with citations)

Per **Story 7-1E** acceptance criteria:

- **AC4 (Question Generation):** *"`GenerateQuestions` activity: RunWorkflow: LlmCall (7-1B,
  role=`analyst`)"* with prompt including story context, skill level, previous-attempt results;
  questions must be AI-generated and gap-targeted on retry. The spec body says the activity
  *"delegates to the LLM Call workflow for AI-generated questions"* — the implementation's own
  comment admits *"In production, this would delegate…"* but it never does.
- **AC6 (Response Analysis):** *"`AnalyzeResponse` activity: RunWorkflow: LlmCall (7-1B,
  role=`analyst`, AnalysisType=`Assessment`)… LLM returns structured analysis: classification,
  confidence, gaps, strengths, rationale… encouraging but honest."* Implementation comment again
  admits *"For now, perform a heuristic analysis."*
- **AC5 (Wait + timeout):** *"Timeout: 5 minutes default (configurable, per skill level) … Timeout
  → `Timeout` outcome."* Requires the timeout to actually **fire**. Success metric: *"Bookmark-based
  wait survives server restart."* Story 7-2 AC3 adds *"Response is timestamped and stored with the
  session,"* and AC8 requires graceful Claude/Slack/DB failure handling with retry.
- **AC9 / Logging / Success metrics:** per-step timing, plus metrics `assessment.total`,
  `assessment.correct_rate`, `assessment.avg_confidence`, `assessment.timeout_rate`; *"NEVER log
  student PII … log token counts and summary only."*
- **Story 7-2 AC5:** an additional `Error → FAILED` outcome (not just the 4 statuses).
- **Agent-architecture pivot — cross-cutting rule** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`
  §1, lines 17-23): *"A workflow STEP MUST NEVER call an external API/provider directly… The LLM
  path is mediated by a `call-LLM` endpoint."* The sanctioned mediation seam is already in use by
  sibling workflows: `PlanGenerationWorkflow` and others dispatch `WorkflowDefinitionId="llm-call"`
  (the `LlmCallWorkflow`, which owns provider chain, circuit breaker, budget, BYOK, prompt/convention
  resolution). The Assessment AI steps must route through that seam, not embed scoring logic.
- **CLAUDE.md project rules:** tenant→system→error resolution with **no empty/plain fallback**; no
  silent-failure / false-success; emit DCB audit events for every operation.

Domain best-practice for an assessment/quiz-with-timeout flow additionally implies: idempotent
delivery + bookmark (no duplicate questions on resume/replay), a real durable timeout, capturing
the analysis rationale into the output (currently dropped), tenant threading for SaaS scoping, and
distinguishing a hard error from a low-confidence "Incorrect".

---

## 5. Missing capabilities

| # | Capability | Priority | dependsOn |
|---|---|---|---|
| 1 | **Question generation via LLM** — replace `GenerateQuestionsActivity` hardcoded bank with `DispatchWorkflow("llm-call", role=analyst)`; parse structured questions; keep heuristic only as explicit mock mode (7-2 AC7). | P0 | 32-5 mediation (`llm-call`) |
| 2 | **Response analysis via LLM** — replace `AnalyzeResponseActivity` heuristic with `DispatchWorkflow("llm-call", role=analyst, AnalysisType=Assessment)`; LLM returns status/confidence/gaps/strengths/rationale. The current heuristic fabricates the confidence the whole mentorship machine routes on. | P0 | 32-5 mediation (`llm-call`) |
| 3 | **Real, durable timeout** — schedule a per-skill-level timer alongside the bookmark so the "Timeout" outcome actually fires; today no timer is scheduled and no code resumes the bookmark, so the wait blocks forever and DIAGNOSE_BLOCKER is unreachable. | P0 | none (Elsa `Delay`/`StartAt` + signal) |
| 4 | **Bookmark-resume endpoint** — an authenticated API to submit the junior's response and resume `assessment-{sessionId}-{attemptNumber}` (Slack reply + API POST per 7-2 AC2/AC3). No such endpoint exists in `Tamma.ElsaServer`. | P0 | none |
| 5 | **Error outcome → FAILED** — `AnalyzeResponse` currently maps a thrown analysis error to `Incorrect` (false-success: a system failure looks like a wrong answer). Add an `Error` status routing to `MentorshipState.FAILED` per 7-2 AC5, with error detail. | P0 | none |
| 6 | **LLM/Slack/DB failure handling** — explicit failure edges + bounded retry; no silent fallback to empty/plain. `UpdateSkillProfile` swallows all exceptions and returns success; `GatherContext` failure is not checked. | P0 | none |
| 7 | **Capture context + analysis rationale into output** — `StoreContextResult` writes a placeholder string instead of the dispatched ContextGathering output; `SetOutputResult` hardcodes `AnalysisRationale="Assessment completed"` instead of the analysis rationale. | P1 | 7-1F output contract |
| 8 | **Tenant scoping** — thread `tenantId` through inputs into `llm-call` (and prompt/convention resolution) for SaaS-mode prompt/credential scoping; absent today. | P1 | 32-5 / 27 prompt source |
| 9 | **DCB audit events** — current events are local `MentorshipEvent` rows (`Info`/`AIAnalysis`/`SkillLevelUpdated`) only; emit the system-wide DCB audit events (`ASSESSMENT.QUESTIONS_GENERATED.SUCCESS`, `ASSESSMENT.DELIVERED.*`, `ASSESSMENT.RESPONSE_RECEIVED`, `ASSESSMENT.ANALYZED.{SUCCESS,FAILED}`, `ASSESSMENT.CLASSIFIED`, `ASSESSMENT.TIMEOUT`, `ASSESSMENT.COMPLETED`) with tags `{ sessionId, juniorId, storyId, tenantId, attemptNumber }`. | P1 | none |
| 10 | **Idempotency on resume/replay** — `DeliverQuestions` re-sends and `GenerateQuestions` re-runs if the workflow replays before the bookmark; guard delivery (and avoid re-billing the LLM) so a resume doesn't duplicate questions or re-spend. | P1 | none |
| 11 | **Observability metrics** — emit `assessment.total`, `assessment.correct_rate`, `assessment.avg_confidence`, `assessment.timeout_rate`, and per-step durations (AC9); none are emitted. | P2 | none |
| 12 | **Input validation** — `ReadInputs` does not validate required `sessionId`/`storyId`/`juniorId`; bad input proceeds and fails deep in the flow rather than failing fast with a clear error. | P2 | none |
| 13 | **PII-safe logging review** — ensure junior response content / PII is never logged at INFO (log length/token counts only, per 7-1E logging requirements); currently lengths are logged but verify analysis path. | P3 | none |

---

## 6. Ordered build-out spec (to reach complete)

Each step names the activity/edge to add or change, the branch condition, the DCB event, and the
failure edge. Honor: steps route LLM work via `DispatchWorkflow("llm-call")` (never call providers
directly); no empty/plain fallback (tenant→system→error); no silent-failure/false-success.

1. **Add input validation + tenant threading.** In `ReadInputs`, validate `sessionId != Guid.Empty`
   and non-empty `storyId`/`juniorId`; on failure set status=`Error`, emit
   `ASSESSMENT.VALIDATION.FAILED`, route to the new Error path (step 9). Read `tenantId` from input
   into a `tenantId` workflow variable. *(Missing #8, #12)*

2. **Capture ContextGathering output.** Bind `gatherContext.Result` to a variable and have
   `StoreContextResult` write the **actual** returned context (story metadata, files, patterns,
   session history) into `storyContext` — not a placeholder string. If the dispatch returns empty
   context, fail with `CONTEXT.GATHER.EMPTY` (no plain fallback). Emit
   `ASSESSMENT.CONTEXT_GATHERED.SUCCESS`. *(Missing #7)*

3. **Generate questions via `llm-call`.** Replace the `GenerateQuestionsActivity` body (or add a
   `DispatchWorkflow("llm-call")` step) with input `{ tenantId, agentRole:"analyst",
   action:"generate-assessment-questions", variables:{ storyContext, skillLevel, previousAttempt },
   sessionId }`. Parse the returned `workflowOutput` into the question list; on `success=false`
   route to Error path. Keep the heuristic bank behind an explicit `Assessment:UseMock` flag (7-2
   AC7), logged as mock. Emit `ASSESSMENT.QUESTIONS_GENERATED.{SUCCESS,FAILED}`. *(Missing #1, #5)*

4. **Make delivery idempotent.** In `DeliverQuestions`, guard on a per-attempt
   "already delivered" marker (mentorship event or workflow variable) so a replay/resume does not
   re-send. On delivery failure of all channels, emit `ASSESSMENT.DELIVERED.FAILED` and route to
   Error (delivery is required for the wait to be meaningful). Emit
   `ASSESSMENT.DELIVERED.SUCCESS` with the channel used. *(Missing #6, #10)*

5. **Schedule a durable timeout next to the bookmark.** In `WaitForResponseActivity`, in addition to
   the response bookmark, schedule an Elsa timer/`Delay` (or a `StartAt` timer bookmark) at
   `now + GetTimeoutMinutes(skillLevel)` that resumes with `Timeout=true`. Ensure the timer survives
   restart (durable scheduling) per the 7-1E success metric. First resume wins; AutoBurn the other.
   This makes the existing "Timeout" edge live. *(Missing #3)*

6. **Add a response-submission endpoint.** Add an authenticated endpoint in `Tamma.ElsaServer`
   (e.g. `POST /api/v1/mentorship/assessments/{sessionId}/{attemptNumber}/response`) that resumes
   the `assessment-{sessionId}-{attemptNumber}` bookmark with `{ Response, SubmittedAt }`; reject
   if no live bookmark. Wire the Slack reply path to the same resume. Timestamp + persist the
   response on the session (7-2 AC3). Emit `ASSESSMENT.RESPONSE_RECEIVED`. *(Missing #4)*

7. **Analyze response via `llm-call`.** Replace the `AnalyzeResponseActivity` heuristic with
   `DispatchWorkflow("llm-call")` input `{ tenantId, agentRole:"analyst",
   action:"analyze-assessment-response", variables:{ questions, juniorResponse, storyContext,
   skillLevel }, sessionId }`; system prompt instructs "encouraging but honest". Parse structured
   `{ status, confidence, gaps, strengths, rationale, understandingSummary }`. On `success=false`
   or unparseable structured output, route to the Error path — **do not** coerce to `Incorrect`.
   Carry `rationale` into `analysisResultJson`. Emit `ASSESSMENT.ANALYZED.{SUCCESS,FAILED}`. Keep
   the heuristic only under the mock flag. *(Missing #2, #5, #7)*

8. **Use the LLM confidence in ClassifyResult + propagate rationale.** `ClassifyResultActivity`
   already thresholds correctly — feed it the real LLM confidence. In `SetOutputResult`, set
   `AnalysisRationale` from the analysis result instead of the hardcoded
   "Assessment completed"; derive `skillLevel` from the (now real) confidence. Emit
   `ASSESSMENT.CLASSIFIED` with status + nextState. *(Missing #7)*

9. **Add an Error path (status=Error → FAILED).** New flowchart branch fed by steps 1/2/3/4/7
   failures: set status=`AssessmentOutcomeStatus.Error` (add to enum), nextState=
   `MentorshipState.FAILED`, populate `analysisRationale` with the error detail, still run
   `UpdateSkillProfile` (recording the failure, not a fake score) and the SetOutput/Expose sequence.
   Emit `ASSESSMENT.FAILED`. This removes the current false-success where a system error reads as a
   wrong answer. *(Missing #5, #6)*

10. **Harden `UpdateSkillProfile`.** Stop swallowing all exceptions silently; on persist failure,
    retry (3×, exponential backoff per 7-2 AC8) then surface the failure as a workflow error rather
    than logging-and-returning-success. Emit `ASSESSMENT.PROFILE_UPDATED.{SUCCESS,FAILED}`.
    *(Missing #6)*

11. **Emit metrics.** Add counters/histograms `assessment.total`, `assessment.correct_rate`,
    `assessment.avg_confidence`, `assessment.timeout_rate`, and per-step durations
    (question-gen, wait, analysis) via the existing OTel/metrics seam. *(Missing #11)*

12. **PII-safe logging pass.** Confirm no full junior response or PII is logged at INFO across all
    activities (log length/token-count/summary only) per the 7-1E logging requirements.
    *(Missing #13)*

13. **Emit a terminal DCB audit event.** On both the response and timeout/error paths, emit
    `ASSESSMENT.COMPLETED` with tags `{ sessionId, juniorId, storyId, tenantId, attemptNumber,
    status, confidence, nextState }` so the whole assessment is reconstructable in the audit
    trail / time-travel debugger. *(Missing #9)*

---

## 7. Summary

The AssessmentWorkflow has a complete and correct *structure* — both branches, configurable
thresholds, persisted profile, exposed outputs, proper registration — but it is **partial** because
its two defining AI steps are hardcoded heuristics (violating the "steps route LLM via `llm-call`"
mediation rule and fabricating the confidence the mentorship machine routes on), and its timeout
branch is structurally dead (no timer, no resume endpoint), so the workflow can hang indefinitely.
Reaching "complete" is primarily P0 wiring to the existing `llm-call` seam plus a durable
timeout + response endpoint + a real Error/FAILED path, then P1 audit/tenant/idempotency hardening.
