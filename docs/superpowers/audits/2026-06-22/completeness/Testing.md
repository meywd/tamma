# Completeness Audit — TestingWorkflow (`testing-pipeline`)

**Audited:** 2026-06-22
**Workflow file:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestingWorkflow.cs`
**DefinitionId:** `testing-pipeline`
**Activities:** `apps/tamma-elsa/src/Tamma.Activities/Testing/*` (`TriggerCIActivity`, `WaitForCIResultsActivity`, `EvaluateResultsActivity`, `CheckCoverageActivity`, `CheckLintingActivity`, `CheckSecurityActivity`, `GenerateQualityReportActivity`, `CommitFixActivity`) + `Tamma.Activities.CodeIndex.UpdateCodeIndexActivity`

---

## Purpose & Owner

Run the full testing / quality-gate pipeline for a repository+branch with **skill-level-aware thresholds**, a **bookmark-based CI wait**, a **major-issues auto-fix loop**, and **teaching feedback**. Produces a `QualityReport` plus a `passed` flag and `teachingFeedback` string.

Owning epics/stories:
- **Epic 2 / Story 2-5** (Test-First Development — the RED phase dispatches this pipeline to run tests). `docs/stories/epic-2/story-2-5/2-5-test-first-development-write-failing-tests.md` AC6/AC7 ("Test execution confirms tests fail as expected", "Test generation and execution logged to event trail").
- **Epic 7 / Story 7-7** (Mentorship Quality Gate — minor→AUTO_FIX, major→REQUIRE_FIXES, critical→BLOCK_PROGRESS; educational feedback; improvement tracking). `docs/epics.md` lines ~1892-1906.
- **Epic 3** (Quality Gates & Intelligence — ESLint/Prettier gates, security scan, 3-retry + escalation). `docs/architecture.md` §"Epic 3", `docs/PRD.md` FR-16 / FR-19d.

It is a shared sub-workflow: dispatched by **TddWorkflow** (RED/GREEN/REFACTOR), **DebuggingWorkflow**, **CiWithDebugRetryWorkflow**, and **MentorshipWorkflow**.

---

## Maturity: **partial**

This is NOT a thin stub. It is a real, multi-branch flowchart with 4 outcome routes (AllPass / MinorIssues / MajorIssues / Critical), a bookmark-suspended CI wait, a bounded retry loop with a max-attempt guard, skill-level threshold logic, progressive tightening, weighted scoring, and skill-calibrated teaching feedback. The activities are substantive (Evaluate ≈330 LOC, Report ≈330 LOC) and contain genuine domain logic.

It is rated **partial** (not complete) because the workflow has a **false-success correctness hole** in its auto-fix loop, **no timeout enforcement** on the CI wait (permanent-hang risk), **no DCB / audit-event emission** anywhere (violating CLAUDE.md "every operation must emit events" and Story 2-5 AC7), and the non-LLM CI/commit calls are not yet formalized under the mediation surface.

---

## Current Capabilities

- **CI trigger** (`TriggerCIActivity`): real mode POSTs `Engine:CallbackUrl/api/engine/trigger-ci`; mock mode behind `Testing:UseMock`. Returns `RunId`. Catches exceptions → returns `Success=false` result (does NOT fail the workflow).
- **CI wait** (`WaitForCIResultsActivity`): suspends on a bookmark `ci-result-{sessionId}-{runId}`; resumes when an external webhook posts results; parses `CIResultsPayload`.
- **Evaluation** (`EvaluateResultsActivity`): weighted score (Coverage 40% / Lint 25% / Security 25% / Build 10%), skill-level thresholds, progressive tightening after 3 consecutive passes; routes to `AllPass | MinorIssues | MajorIssues | Critical`.
- **Detailed checks**: `CheckCoverage`, `CheckLinting`, `CheckSecurity` (skill-level-aware pass/fail + issue lists).
- **Report** (`GenerateQualityReportActivity`): grade A–F, per-category recommendations, skill-calibrated teaching feedback, `ConsecutivePassCount` increment.
- **Auto-fix loop** (MajorIssues): `CommitFix` → `UpdateCodeIndex` → increment attempt → re-trigger CI → re-wait → re-evaluate; bounded by `MaxAttemptGuard` (`attempt < maxAttempts`, default 3); on exhaustion routes to fail outputs.
- **Outputs**: `qualityReport` (JSON), `passed` (bool), `teachingFeedback` (string) on every terminal path (pass / fail / retry-pass).
- **Inputs**: `SessionId`, `Repository`, `Branch`, `SkillLevel`, `ConsecutivePassCount` (name-bound from dispatch input), optional `maxRetries`.

---

## Intended Full Scope (with citations)

A production-complete autonomous quality-gate / testing pipeline should:

1. **Run real tests and gates and report true results** — never report `passed` when the underlying work didn't actually happen (PRD FR-19d "no bypassing quality gates"; CLAUDE.md "no silent-failure / false-success").
2. **Auto-fix that actually fixes** — for `MinorIssues`/`MajorIssues` that are `AutoFixable`, *generate* the fix (LLM-mediated) before committing, then re-run the gate (Story 7-7 AC3/AC4: minor→AUTO_FIX with corrections, major→REQUIRE_FIXES with guided remediation). Today `CommitFix` commits without any preceding fix-generation step.
3. **Bounded retries + escalation** — 3-retry then mandatory escalation, no infinite waits (PRD FR-16 "3-retry limit and mandatory escalation on failure"; architecture "3-retry quality gates").
4. **Time-out the CI wait** and take a deterministic failure edge if CI never reports (the `TimeoutMinutes` input exists but is dead).
5. **Emit a complete audit trail** — DCB-style events at trigger / results / each gate / fix-commit / pass / fail / escalation (CLAUDE.md "All operations must emit events for DCB event sourcing"; Story 2-5 AC7 "logged to event trail"; Story 7-7 AC6 improvement tracking).
6. **Mediate external calls** — non-LLM CI/commit calls go through the formalized internal `/api/v1` engine surface; any LLM fix-generation routes via the `call-LLM` endpoint (`POST /api/v1/llm/call`), never a direct provider call (pivot spec `2026-06-20-epic-32-revised-agent-architecture.md` §1 "steps never call external APIs directly", §5 non-LLM mediation; `TriggerCIActivity` listed as "Compliant — formalize under `/api/v1`").
7. **Distinguish MinorIssues from AllPass** behaviourally (Story 7-7 AC3 auto-fixes minor issues) rather than running the identical check path and only reflecting status in the report.
8. **Surface coverage/lint/security gate categories** to the escalation path with a structured, replayable reason.

---

## Missing Capabilities

| # | Capability (gap to complete) | Priority | DependsOn |
|---|------------------------------|----------|-----------|
| 1 | **Auto-fix is a false success.** MajorIssues path runs `CommitFix` → re-trigger CI, but there is NO step that actually *generates* the fix. `CommitFix` only commits whatever is on disk; its own docstring/`FixDescription` say "Auto-fix … via LLM" but no LLM call is made. A green re-run after a no-op commit reports `passed=true` having fixed nothing. Add an LLM-mediated `GenerateFix` step before `CommitFix`, and treat a zero-files-changed commit as a non-fix (route to escalate, do not loop pretending progress). | P0 | 32-5 mediation (call-LLM) |
| 2 | **No timeout / failure edge on the CI wait.** `WaitForCIResultsActivity.TimeoutMinutes` (default 30) is declared but never enforced — no scheduled bookmark / Timer. If CI never posts results the workflow suspends forever. Add a timeout bookmark → deterministic `CITimedOut` failure edge. | P0 | Epic 38 (non-LLM mediation) for the formalized callback; otherwise none |
| 3 | **No DCB / audit events emitted anywhere** in the testing activities. Violates CLAUDE.md ("every operation must emit events"), Story 2-5 AC7, Story 7-7 AC6. No `TEST.CI_TRIGGERED`, `TEST.RESULTS_RECEIVED`, `GATE.EVALUATED`, `GATE.AUTOFIX_COMMITTED`, `GATE.PASSED/FAILED`, `GATE.ESCALATED`. | P0 | none (record-event activity / mentorship-session sink) |
| 4 | **TriggerCI failure does not fail the workflow.** On a trigger exception the activity returns `Success=false` and the flow proceeds to `WaitForCIResults` with `RunId="unknown"` → guaranteed hang. Add a `FlowDecision` on `CITriggerResult.Success` → escalate edge. | P0 | none |
| 5 | **No escalation/human-review terminal** (Story 7-7 AC5 BLOCK_PROGRESS; PRD FR-16 mandatory escalation). Critical and retry-exhausted paths just emit fail outputs + `Finish`; there is no `RequestHumanReviewActivity`/escalation event so the parent loop can route to a human gate. | P1 | MergeApproval / escalation activity; none blocking |
| 6 | **MinorIssues == AllPass behaviourally.** Both wire to the identical `CheckCoverage→…→GenerateReport→FinishPass` path; minor issues are never auto-fixed (Story 7-7 AC3). Either auto-fix minor `AutoFixable` issues or document the intentional merge. | P1 | 32-5 mediation (for the fix) |
| 7 | **Retry-loop telemetry / consecutive-pass persistence.** `ConsecutivePassCount` is read as input and a *new* count is computed in the report, but nothing persists it back to the session, so progressive tightening can't actually progress across runs (Story 7-8 skill tracking). | P1 | skill-tracker / mentorship session store |
| 8 | **Non-LLM call mediation not formalized.** `TriggerCIActivity` / `CommitFixActivity` POST to `Engine:CallbackUrl/api/engine/*` ad-hoc; pivot spec wants these under `/api/v1`. Functional today, but out-of-contract with the mediation model. | P2 | Epic 38 (non-LLM mediation) |
| 9 | **No artifact/coverage-report capture.** `CIResultsPayload.ArtifactUrl` exists but is never surfaced as a workflow output or audit event for time-travel debugging. | P3 | none |
| 10 | **Flaky-test / partial-result handling absent.** `Status="Cancelled"/"Unknown"` from CI is treated like any other payload; no retry-on-infrastructure-failure vs retry-on-test-failure distinction. | P3 | none |

---

## Ordered Build-out Spec

Steps are ordered so each is independently shippable; P0s first.

### Phase 1 — Correctness & safety (P0)

1. **Fix the no-op auto-fix (Missing #1, #6).**
   - Insert a new `GenerateFixActivity` (LLM-mediated) **before** `CommitFix` on the MajorIssues (and optionally MinorIssues) branch. It must dispatch the `llm-call` sub-workflow (`DispatchWorkflow WorkflowDefinitionId="llm-call"`, `role=Developer`/`Tester`, `action=fix-issues`, `enableTools=true`) with the `AutoFixable` issues + file context. **Never call a provider directly** — route only via `call-LLM` (pivot spec §1/§2).
   - Wire: `MaxAttemptGuard "True" → GenerateFix → CommitFix → …`.
   - In `CommitFix`, treat `FilesChanged == 0` as **not a fix**: emit `GATE.AUTOFIX_NOOP` and route to the escalation edge (Phase 1 step 4) instead of re-triggering CI and pretending progress.

2. **Enforce the CI-wait timeout (Missing #2).**
   - In `WaitForCIResultsActivity`, additionally create a scheduled/timer bookmark for `TimeoutMinutes`; whichever resumes first wins. On timeout, set a sentinel `CIResultsPayload { Status="TimedOut", BuildPassed=false }` and complete with a new `Timeout` outcome (make the activity a `[FlowNode("Received","Timeout")]`).
   - Wire `WaitForCIResults "Timeout" → setOutputFailReport (with finishReason="ci-timeout") → escalate/FinishFail`. Apply identically to `WaitForCIResultsRetry`.

3. **Fail fast on trigger failure (Missing #4).**
   - Add `FlowDecision(ctx => ciTriggerResultVar.Get(ctx)?.Success == true)` after `TriggerCI` (and `ReTriggerCI`).
   - `False → setOutputFailReport (finishReason="ci-trigger-failed") → escalate/FinishFail`. `True → WaitForCIResults`.

4. **Emit the DCB audit trail (Missing #3).**
   - Add `RecordTestingEventActivity` (or reuse the mentorship-session/engine-callback audit sink) calls at: after `TriggerCI` → `TEST.CI_TRIGGERED.SUCCESS`/`.FAILED`; after `StoreCIResults` → `TEST.RESULTS_RECEIVED`; after each `EvaluateResults` → `GATE.EVALUATED` (tags: outcome, score, skillLevel); after `CommitFix` → `GATE.AUTOFIX_COMMITTED` (tags: attempt, filesChanged) or `GATE.AUTOFIX_NOOP`; on each terminal → `GATE.PASSED` / `GATE.FAILED` / `GATE.ESCALATED`.
   - Tags must include `sessionId`, `repository`, `branch`, `runId`, `attempt`. Honor tenant→system→error scoping; never swallow a failed append silently.

### Phase 2 — Scope completion (P1)

5. **Add an escalation terminal (Missing #5).**
   - New `RequestHumanReviewActivity` (or dispatch `merge-approval`/escalation sub-workflow) reached from: Critical path, retry-exhausted (`MaxAttemptGuard "False"`), ci-timeout, ci-trigger-failed, and autofix-noop. Set output `escalated=true` + `escalationReason`, emit `GATE.ESCALATED`, then `Finish`.

6. **Differentiate MinorIssues (Missing #6).**
   - Route `EvaluateResults "MinorIssues"` to a minor-auto-fix branch (GenerateFix scoped to `AutoFixable && Severity==Warning` → CommitFix → re-evaluate once), falling back to pass-with-warnings if not auto-fixable. Or, if intentionally merged, document it in the workflow docstring.

7. **Persist consecutive-pass count (Missing #7).**
   - After `GenerateQualityReport`, write `report.ConsecutivePassCount` back to the mentorship/skill session store (engine callback or a `PersistSkillProgressActivity`) so progressive tightening actually carries across runs.

### Phase 3 — Polish (P2/P3)

8. **Formalize non-LLM mediation (Missing #8).** Move `trigger-ci` / `commit-fix` callbacks under the formalized `/api/v1` engine surface per Epic 38; keep behaviour, change only the endpoint contract + signature.
9. **Surface artifacts (Missing #9).** Add `artifactUrl` / `coverageReportUrl` workflow outputs and include them in the `TEST.RESULTS_RECEIVED` event.
10. **Infra-vs-test failure distinction (Missing #10).** On `Status ∈ {Cancelled, TimedOut, Unknown}` retry as an infrastructure failure (separate, smaller retry budget) rather than running gates on empty results.

---

## Notes

- DCB event sourcing is currently **not implemented at the activity level anywhere in the C# stack** (it was specified for the deleted `packages/events` TS package). The audit recommendation (#3) should land on whatever the project adopts as the canonical C# audit sink (mentorship-session events / engine callback), not invent a parallel store.
- The `Connect(maxAttemptGuard, setOutputFailReport, "False")` edge currently produces fail outputs but no escalation — folding it into the Phase 2 escalation terminal closes both #5 and the retry-exhaustion gap.
