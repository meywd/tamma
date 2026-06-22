# Completeness Audit — DebuggingWorkflow

**Date:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/Debug/**`
**Scope source of truth:** `docs/stories/epic-7/story-7-1I/7-1I-debugging-sub-workflow.md` (Story 7-1I, Epic 7 — Mentorship / autonomous loop)
**Companion structural audit:** `docs/superpowers/audits/2026-06-22/workflow-audit-agent-llm.md` (this completeness pass cross-references it)

---

## Purpose & owner

One-line purpose: a reusable, hypothesis-driven, auditable debugging sub-workflow that handles three entry contexts (`TddFailure`, `RuntimeError`, `BugInvestigation`), runs an AI diagnose → fix → verify loop, and either records a resolution or escalates with a full report.

Owner: **Epic 7 — Mentorship / autonomous loop**, Story **7-1I**. Dispatched (definitionId `debugging`) by `TddWithDebugRetryWorkflow` (mode=`TddFailure`), `CiWithDebugRetryWorkflow` (mode=`RuntimeError`), and the mentorship/ADL loop; can also run standalone via the Elsa REST API. Registered via assembly scan (`AddWorkflowsFrom<LlmCallWorkflow>()` in `Program.cs`) — AC1 satisfied.

---

## Maturity: **partial**

This is **not** a thin stub. Unlike `PullRequestWorkflow` (CreatePR → 3x SetOutput), DebuggingWorkflow is a genuine, well-structured flowchart: 3-way classify routing, a 5-branch parallel Fork/Join context-gather, AI diagnosis, a real iterate-refine loop with an iteration cap and an exhausted-hypotheses early exit, a BugInvestigation regression-test branch, a resolution path, and an escalation/report path. Twelve real custom activities back it (none are placeholder; `AIDiagnosis`/`RefineHypothesis`/`WriteRegressionTest` make real LLM calls with no simulated fallback). The graph covers most of Story 7-1I.

It is **partial, not complete**, because several Story 7-1I acceptance criteria are unmet and three are outright correctness bugs that make the workflow report success/structured data it does not actually carry:

- **Both terminal outputs are always empty.** `resolution` and `debugReport` outputs read `debugResultJson` (DebuggingWorkflow.cs lines 467, 544), but `debugResultJson` is declared (line 91) and **never assigned anywhere**. The escalation `CompileDebugReportActivity` output and the resolution payload are computed and then dropped — callers receive `{}`. (AC2 outputs `debugReport`/structured `DebugResult` are not delivered.)
- **`allFilesModified` is initialized to `[]` and never updated** (only init at line 112; read at 444/454/531). So `RecordResolution.FilesChangedJson`, `CompileDebugReport.FilesInvestigated`, and `UpdateCodeIndex.ChangedFilesJson` are always empty — AC10 ("files involved") and AC2 (`filesChanged`) are effectively non-functional, and the code-index update is a no-op.
- **`applyFix` (dispatch to `llm-call`) has no result capture and no failure edge** — it unconditionally proceeds to `runTests` whether or not a fix was produced/applied. A failed fix dispatch is silently treated as "fix applied", relying on the test step to notice. This is a no-false-success gap.

---

## Current capabilities (what it does today)

- **Init** start-time, iteration=1, maxIterations=5, filesModified=`[]`, regressionTestWritten=false, empty iterationContext.
- **ClassifyDebugContext** routes to one of three emphasis log lines (TddFailure / RuntimeError / BugInvestigation; unknown → RuntimeError). Routing is real (FlowNode outcomes), though the per-mode "emphasis" is only a `WriteLine` and is not actually fed differently into diagnosis.
- **Parallel context gather** via `FlowFork`/`FlowJoin(WaitAll)`: CollectErrorMessages, CollectRelevantCode, CollectGitHistory, CollectTestResults, CollectReproductionSteps. Typed outputs are serialized into string vars for diagnosis. (CollectErrorMessages does real stack-trace/error parsing.)
- **AIDiagnosis** — real LLM call (engine callback `/api/engine/execute-task` or direct Anthropic `/v1/messages`; **no simulated fallback** — throws if unconfigured). Returns ranked hypotheses; serialized to `hypothesesJson`.
- **SelectHypothesis** — picks highest-confidence `Untried` hypothesis; returns null when exhausted or past maxIterations → routes to escalate.
- **BugInvestigation guard branch** — `isBugMode` decision; if true and not yet written, runs `WriteRegressionTest` (real LLM, role=tester) then marks written, then applies fix.
- **ApplyFix** — dispatches `llm-call` (role=Developer, action=Debug) with sanitized hypothesis (`SecurityHelpers.SanitizeForPrompt`). **Pivot-compliant** (mediated, not a direct provider call).
- **RunTests** — dispatches `testing-pipeline`; captures result; `testsPass` decision reads `passed`.
- **Resolution path** — `RecordResolution` (logs a `debug_resolved` MentorshipEvent, categorizes root cause), `UpdateCodeIndex`, then SetOutput(success=true, resolution, iterations).
- **Refine/loop** — `RefineHypothesis` (real LLM, role=debugger; **no fallback**), re-serialize hypotheses, update iterationContext, increment iteration, loop back to SelectHypothesis. Accumulated previous-attempts context is passed to refine so the LLM avoids repeating approaches.
- **Escalation path** — `CompileDebugReport` (rich markdown report + suggested next steps), SetOutput(success=false, debugReport, iterations), Finish.
- **Observability** — every activity injects `ILogger<T>` and logs hypotheses/attempts/metrics lines (AC11 largely met at the log level).

---

## Intended full scope (with citations)

From **Story 7-1I** (`docs/stories/epic-7/story-7-1I/7-1I-debugging-sub-workflow.md`):

- **AC2 Output contract:** a `DebugResult` with `status` (`Resolved`/`Unresolved`/`Escalated`), `rootCause`, `fixApplied`, `attempts`, `hypotheses[]` (with outcomes), `regressionTestAdded`, `filesChanged[]`, `debugReport`. Today only `success`/`iterations` + an always-empty `resolution`/`debugReport` are emitted.
- **AC6 step 2 (mode-specific fix):** TddFailure→`ModifyImplementation` (role=implementer), RuntimeError→`ApplyFix` (role=implementer), BugInvestigation→regression test first then `WriteFix`. The workflow collapses all three into one generic `applyFix` (role=Developer/action=Debug) — acceptable simplification, but the role/action should reflect implementer semantics.
- **AC7 (TDD-for-bugs guard):** the regression test must be run and **must FAIL before fixing** ("if it passes, the bug might be fixed or the test is wrong"); and after the fix the regression test + all tests must pass. Today `WriteRegressionTestActivity` returns `TestGenerationResult.FailsAsExpected`, but the workflow **discards the result** — there is no run-and-must-fail guard. (AC7 unmet.)
- **AC8 (context accumulation):** previous hypotheses + their **outcomes** and previous fix attempts accumulate. Today `Hypothesis.Outcome` is never set to `DidNotFix`/`MadeWorse`, and `DebugIterationContext.PreviousAttempts`/`FixAttempt` records are never populated (`updateIterationContext` only sets Hypotheses/LatestTestResults/LatestErrors). So the report's "Fix Attempts" section and the outcome tags are always empty.
- **AC9 (escalation):** compile report **and send a notification to a senior developer** with the report; status `Escalated`. Today the report is compiled but (a) never reaches the output (`debugResultJson` bug) and (b) **no notification/Slack/issue-comment is sent**. (AC9 partially unmet.)
- **AC10 (resolution recording):** record root-cause category, fix approach, files, time **and** the **commit** with message `fix({storyId}): {rootCause} [debug]`; resolution data should feed Context Gathering (7-1F). Today there is **no commit step** and files are empty. (AC10 partially unmet.) Recording is also logged to the mentorship repo only — not as a DCB `DEBUG.*` audit event, and the recording failure is swallowed non-fatally.
- **AC4 (Join timeout 15s) / Config `ContextCollectionTimeoutSeconds`:** the Fork/Join is `WaitAll` with **no timeout** — a hung collector hangs the workflow. (AC4 timeout unmet.)
- **Config block** (`Debugging.MaxIterations`, `ContextCollectionTimeoutSeconds`, `EscalationChannel`, `CommitMessageFormat`, `BugInvestigation.RequireRegressionTest/MaxReproductionAttempts`) — maxIterations is hardcoded to 5 in-graph; none of the other config keys are honored.

From the **architecture pivot** (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §1, audit table line 102) and the structural audit README: a workflow **step must never call an external provider directly**; the nine direct-LLM activities (including `AIDiagnosisActivity`, and by the same pattern `RefineHypothesisActivity`/`WriteRegressionTestActivity`) must drop their direct keyed fallback and route through the `call-LLM` mediation endpoint. `applyFix` already complies (it dispatches `llm-call`); the three diagnosis/refine/test-gen activities do not. The structural audit classifies the `AIDiagnosis` repoint as **Bucket A — auto-resolved by 32-5 T6** (fix is in the activity, not the graph) and the loop-bound-not-graph-enforced point as **Bucket D**.

DCB best practice (CLAUDE.md): every operation emits an audit event (`AGGREGATE.ACTION.STATUS`). DebuggingWorkflow emits **no** `DEBUG.*` DCB events at the graph level (diagnosis started, hypothesis selected, fix attempted, tests passed/failed, resolved, escalated) — only logs and one mentorship-repo row.

---

## Missing capabilities

| # | Capability (gap to complete) | Priority | Depends on |
|---|---|---|---|
| 1 | **Populate `debugResultJson`** before both terminal output sequences (serialize a real `DebugResult`: status, rootCause, fixApplied, attempts, hypotheses+outcomes, regressionTestAdded, filesChanged, debugReport). Today `resolution`/`debugReport` outputs are always `{}`. | P0 | none |
| 2 | **Track modified files**: capture `applyFix` / `writeRegressionTest` / diagnosis `affected_files` into `allFilesModified` so RecordResolution, CompileDebugReport, and UpdateCodeIndex receive real data. | P0 | none |
| 3 | **`applyFix` failure handling**: capture the `llm-call` Result, branch on `success`, and add a failure edge (treat as a failed attempt → refine, not silent "fix applied"). No-false-success. | P0 | none |
| 4 | **AC7 regression-test-must-fail guard** (BugInvestigation): after WriteRegressionTest, run it and require it to FAIL before fixing; if it passes, abort/escalate (bug already fixed or test wrong). Honor `RequireRegressionTest`. | P0 | none |
| 5 | **AC8 hypothesis outcome + attempt tracking**: set tried hypotheses to `DidNotFix`/`MadeWorse`, build `FixAttempt` records into `iterationContext.PreviousAttempts`. Without this the report's attempts/outcomes are empty and select/refine can churn. | P1 | none |
| 6 | **AC9 escalation notification**: send the compiled report to a senior developer (Slack/issue-comment), driven by `EscalationChannel`. Active escalation, not just an output string. | P1 | Epic 38 (non-LLM step mediation — Slack/git via internal API) |
| 7 | **AC10 commit the fix** on resolution with `fix({storyId}): {rootCause} [debug]`, and feed resolution data to Context Gathering (7-1F). | P1 | Epic 38 (git write mediation) |
| 8 | **Emit DCB audit events** at graph boundaries: `DEBUG.SESSION.STARTED`, `DEBUG.DIAGNOSIS.SUCCESS/FAILED`, `DEBUG.HYPOTHESIS.SELECTED`, `DEBUG.FIX.ATTEMPTED`, `DEBUG.TESTS.PASSED/FAILED`, `DEBUG.RESOLVED.SUCCESS`, `DEBUG.ESCALATED` (tags: sessionId, storyId, mode, tenantId). | P1 | none |
| 9 | **Repoint `AIDiagnosisActivity` / `RefineHypothesisActivity` / `WriteRegressionTestActivity`** off direct engine-callback/Anthropic onto the mediated `call-LLM` endpoint (consistent with `applyFix`). Graph unchanged; activity edit only. | P1 | 32-5 (mediation, Bucket A) |
| 10 | **Thread `tenantId`** into the `applyFix` (`llm-call`) and `runTests` (`testing-pipeline`) dispatches (and into the repointed diagnosis calls) so SaaS resolves tenant prompts/conventions/creds, not system defaults. DebuggingWorkflow does not declare or forward `tenantId` today (`TddWithDebugRetry` already passes one through). | P1 | 32-5 (Bucket B) |
| 11 | **AC4 Join timeout (15s)** + `ContextCollectionTimeoutSeconds`: bound the Fork/Join so a hung collector cannot hang the workflow; proceed with partial context on timeout (and record which collectors timed out). | P1 | none |
| 12 | **Graph-enforced loop bound**: the iteration cap lives inside `SelectHypothesis`; add an explicit graph guard (FlowDecision on `currentIteration > maxIterations` → escalate) so the bound is visible/auditable in the graph (structural-audit Bucket D). | P1 | none |
| 13 | **Honor `Debugging.*` config** (MaxIterations, EscalationChannel, CommitMessageFormat, BugInvestigation.MaxReproductionAttempts) instead of hardcoding maxIterations=5 in-graph. | P2 | none |
| 14 | **Per-mode fix semantics (AC6)**: route TddFailure/RuntimeError to role=implementer and BugInvestigation to a write-fix-after-test sub-step (today all use a single generic Developer/Debug dispatch). | P2 | 32-5 |
| 15 | **Make per-mode emphasis real**: the classify-branch `WriteLine`s do not change what diagnosis receives. Pass a mode-emphasis hint into AIDiagnosis (or weight the context) so classification has effect beyond a log line. | P2 | none |
| 16 | **`Unresolved` vs `Escalated` status fidelity** + AC2 `status` field: emit a real status enum on the output (currently only boolean `success`); "no hypothesis selected" and "max iterations" both route to the same escalate path with `success=false`. | P2 | none |
| 17 | **Make `RecordResolution` failure observable**: it currently swallows recording failures non-fatally with only a log — emit at least a `DEBUG.RECORD.FAILED` warning event so the audit trail reflects the miss. | P2 | none |

---

## Ordered build-out spec (to reach complete + robust)

Steps are ordered so that independent correctness fixes land first (no pivot dependency), then accumulation/audit, then mediation-coupled and Epic-38-coupled work.

### Phase 1 — Correctness / no-false-success (P0, independent)

1. **Build a `DebugResult` and assign `debugResultJson`.** Add a `serializeResolvedResult` `SetVariable<string>` immediately before `setResolvedOutputs` that serializes a `DebugResult { Status=Resolved, RootCause, FixApplied, Attempts=currentIteration, Hypotheses (with outcomes), RegressionTestAdded, FilesChanged=allFilesModified }`, and a `serializeEscalatedResult` before `setEscalatedOutputs` that serializes the `CompileDebugReport` output (`DebugReport.ReportText` into `DebugResult.DebugReport`, `Status=Escalated`). Wire `CompileDebugReportActivity.Result` into a typed var (currently its output is discarded). Outputs then carry real data.
2. **Capture `applyFix` result + failure edge.** Give `applyFix` a `Result = new(applyFixOutput)` (`IDictionary<string,object>?`). Add a `fixApplied?` FlowDecision reading `success`. On `False` → go to `refineHypothesis` (count it as a failed attempt) rather than `runTests`. Also pull the LLM-reported `filesChanged`/affected files out of the result here (feeds step 3). Event: `DEBUG.FIX.ATTEMPTED` (success flag in data).
3. **Accumulate `allFilesModified`.** After `applyFix` (and after `writeRegressionTest`), add a `SetVariable<string>(allFilesModified, ...)` that JSON-merges new file paths from the dispatch result + selected hypothesis `affected_files` into the existing list (dedup). This makes RecordResolution/CompileDebugReport/UpdateCodeIndex non-empty.
4. **AC7 regression-test guard (BugInvestigation).** After `writeRegressionTest`, capture its `TestGenerationResult`; add `runRegressionTest` (dispatch `testing-pipeline` scoped to the new test) → `regressionFailsAsExpected?` FlowDecision. If the regression test **passes** (does not reproduce the bug) → branch to escalate with reason "regression test did not reproduce bug" (do not silently proceed). If it **fails as expected** → mark written → `applyFix`. Event: `DEBUG.REGRESSION_TEST.WRITTEN` / `DEBUG.REGRESSION_TEST.INVALID`.
5. **AC4 Join timeout.** Replace the bare `FlowJoin(WaitAll)` with a bounded wait (Elsa `Timer`/`Delay` race or per-branch timeout reading `Debugging:ContextCollectionTimeoutSeconds`, default 15s). On timeout, proceed to serialization with whatever collectors completed and record `DEBUG.CONTEXT.TIMEOUT` (which collectors timed out) — never hang.

### Phase 2 — Iteration fidelity + audit trail (P1, independent)

6. **Hypothesis outcome + FixAttempt tracking (AC8).** In the test-fail branch, before `refineHypothesis`: set the tried hypothesis's `Outcome` to `DidNotFix` (or `MadeWorse` if failing-test count increased vs. prior `TestResultsContext`), set `FixAttempted`/`FailureReason`, and append a `FixAttempt { Iteration, HypothesisDescription, Approach, TestResult, Resolved=false, FilesModified, Duration }` into `iterationContext.PreviousAttempts`. Now CompileDebugReport renders real attempts/outcomes.
7. **DCB events at graph boundaries.** Add an `EmitDebugEventActivity` (or reuse the existing engine-callback event-emit seam used by analytics/tenant workflows) and emit: `DEBUG.SESSION.STARTED` (after classify), `DEBUG.DIAGNOSIS.SUCCESS|FAILED` (after AIDiagnosis), `DEBUG.HYPOTHESIS.SELECTED`, `DEBUG.FIX.ATTEMPTED`, `DEBUG.TESTS.PASSED|FAILED` (after testsPass), `DEBUG.RESOLVED.SUCCESS`, `DEBUG.ESCALATED`. Tags: `{ sessionId, storyId, mode, tenantId }`. This closes the systemic DCB gap noted in the structural audit.
8. **Real status fidelity (AC2 `status`).** Emit a `status` output (`Resolved`/`Escalated`) from each terminal sequence (in addition to `success`). Route "no hypothesis selected" (early exhaustion) and "max iterations" through CompileDebugReport but tag the report with the distinct reason.
9. **Honor config + graph-enforced loop bound (Bucket D).** Read `Debugging:MaxIterations` into `initMaxIterations`; add an explicit `iterationsExhausted?` FlowDecision on the loop-back edge (`currentIteration > maxIterations` → CompileDebugReport) so the bound is graph-visible, not only internal to SelectHypothesis.

### Phase 3 — Mediation + tenant correctness (P1, coupled to 32-5)

10. **Repoint diagnosis/refine/regression-test LLM calls** (`AIDiagnosisActivity`, `RefineHypothesisActivity`, `WriteRegressionTestActivity`) onto `POST /api/v1/llm/call` via `TammaApiClient`, deleting their direct engine-callback/Anthropic fallback — matching `applyFix`. Activity-level edits; graph unchanged (Bucket A, auto-resolved by 32-5 T6).
11. **Thread `tenantId`.** Add a `tenantId` workflow input/variable; forward it in the `applyFix` (`llm-call`) and `runTests` (`testing-pipeline`) dispatch inputs and into the repointed diagnosis calls, and into the DCB event tags. (Bucket B.)
12. **Per-mode fix role (AC6).** Use role=implementer/action=Implement for the fix dispatch (and a write-fix step for BugInvestigation) rather than the single generic Developer/Debug; pass the mode-emphasis hint so AIDiagnosis weights context per mode (makes the classify branches functional).

### Phase 4 — Escalation + commit (P1, coupled to Epic 38)

13. **AC9 escalation notification.** After CompileDebugReport, dispatch a notification through the internal API (Slack/issue-comment) per `Debugging:EscalationChannel` — never a direct in-engine integration call (Epic 38 non-LLM mediation). Emit `DEBUG.ESCALATION.NOTIFIED`.
14. **AC10 commit on resolution.** After RecordResolution/UpdateCodeIndex, add a commit step (via the git mediation endpoint, Epic 38) using `Debugging:CommitMessageFormat` → `fix({storyId}): {rootCause} [debug]`. Feed resolution data to Context Gathering (7-1F) for future similar-issue lookup.

### Phase 5 — Polish (P2)

15. Make `RecordResolution` recording-failure observable (`DEBUG.RECORD.FAILED` warning event instead of a swallowed log). Add the remaining `Debugging.*` config keys (`MaxReproductionAttempts`). Tighten `Unresolved` vs `Escalated` semantics on the output.

---

## Overall

- **Maturity:** partial (solid skeleton + real activities; three P0 correctness gaps where outputs/file-tracking/fix-failure are non-functional, plus several unmet 7-1I ACs).
- **Overall priority:** P1 (contains P0 correctness bugs but the workflow runs and is reachable; the P0s are "reports empty/false data", not "crashes on every run").
- **Effort:** L (Phases 1–2 are the bulk of the build-out and are independent; Phases 3–4 ride the 32-5 and Epic 38 workstreams).
