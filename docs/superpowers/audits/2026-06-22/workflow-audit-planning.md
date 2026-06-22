# Workflow Audit — Planning / Assessment / Mentorship (2026-06-22)

Auditor scope: 7 workflows under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`. READ-ONLY audit of code.
Reference: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (locked-model 7 rules),
Epic 6 (`docs/stories/epic-6/`), Epic 7 (`docs/stories/epic-7/`).

## Summary
- **PlanGenerationWorkflow** — GOOD — P0 0 / P1 1 / P2 2
- **PlanReviewWorkflow** — NEEDS-WORK — P0 0 / P1 3 / P2 2
- **AssessmentWorkflow** — STALE — P0 1 / P1 3 / P2 1
- **ContextGatheringWorkflow** — NEEDS-WORK — P0 0 / P1 2 / P2 2
- **MentorshipWorkflow** — NEEDS-WORK — P0 0 / P1 3 / P2 2
- **TaskCreationWorkflow** — GOOD — P0 0 / P1 1 / P2 1
- **TaskReviewWorkflow** — NEEDS-WORK — P0 0 / P1 2 / P2 1

**Totals: P0 = 1 · P1 = 15 · P2 = 11**

The single P0 is a STALE rule-violating stub in Assessment (LLM analysis is fake heuristics, never reaches a provider — fail-open quality risk, not a key-leak). No rule-1 vendor-key violations were found: every LLM step in this cluster dispatches the mediated `llm-call` sub-workflow; the one HTTP step (`StoreRoleFindingActivity`) goes through the tamma-api engine callback, not a vendor.

---

## PlanGenerationWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs`)
- **Purpose / owner story:** Architect LLM produces an implementation blueprint from a work item + gathered context; validates required fields; retries on invalid (max 2). Serves the SingleIssueCycle planning step (Epic 7 / planning). Still needed.
- **Health:** GOOD
- **Findings:**
  - [P1] Architecture/pivot — `tenantId` is threaded into the `llm-call` dispatch (`PlanGenerationWorkflow.cs:96`), making this the *reference* compliant caller — but it is the ONLY workflow in this cluster that does so. This is correct; flagged here only as the positive baseline the others should match (see PlanReview/TaskReview/ContextGathering P1s). No fix needed in this file.
  - [P2] Error handling — `ExtractValidate` (`:122-133`) treats a missing/empty `llmResponse` the same as an invalid plan, so a hard `llm-call` failure (all providers failed) is funneled into the retry-then-`needsHuman` path rather than surfaced as a distinct error. Acceptable, but the error output (`:171-172`) loses the underlying provider-failure diagnostics. **Fix:** when `llmResult.success == false`, copy `workflowOutput`/`error` into `validationErrors` so the audit trail shows provider exhaustion vs. schema failure.
  - [P2] Event emission — the workflow emits no DCB events of its own; `GeneratePlan`/`ExtractValidate`/outputs are silent `SetVariable`/`DispatchWorkflow` nodes. Plan generation success/failure is a meaningful milestone. **Fix:** emit `PLAN.GENERATED.SUCCESS` / `PLAN.GENERATED.FAILED` (e.g. via a `TammaAsyncActivity` wrapper or `Engine:CallbackUrl` event post) at `setOutputs`/`setErrorOutputs`.
- **Depends on:** none blocking. tenantId threading already done.

## PlanReviewWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs`)
- **Purpose / owner story:** Structured 3-phase multi-agent debate (7 independent reviews → anonymized rebuttals → PO decision, loop to max rounds). Serves the planning-review step (Epic 7 / planning). Still needed; this is the most elaborate workflow in the cluster.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Architecture/pivot — none of the 14 `llm-call` dispatches (`RoleReviewDispatch` `:857-869`, `RebuttalDispatch` `:891-904`, Phase 3 PO `:490-503`) pass `tenantId`. In SaaS mode this breaks tenant-scoped prompt/convention resolution AND BYOK credential resolution (Story 27-6 / 32-3) — the `llm-call` workflow reads `tenantId` at `LlmCallWorkflow.cs:153` and defaults to `""`. **Fix:** add a `tenantId` workflow variable read from input in `Init` (`:137-155`) and pass `["tenantId"] = tenantId.Get(ctx)` in all three dispatch builders, mirroring PlanGeneration. **Depends on: 32-5 caller-cutover** (the value source is the same one 32-5 standardizes).
  - [P1] Structural — the PO `needsModification` loop re-enters at `buildAnonymized` (`:831`) but does NOT re-run Phase 1 reviews against the *modified* plan; reviewers in the next round still rebut over the original Phase-1 reviews (`anonymizedReviewsJson` is rebuilt from `allReviewsJson`, which is never refreshed after round 1). So "rounds" only re-run rebuttals + PO decision on stale reviews. **Fix:** on `needsModification`, loop back to the first Phase-1 review (`phase1ArchCall`) so reviews reflect the modified plan, or document that rounds are intentionally rebuttal-only.
  - [P1] Event emission — per-role reviews/rebuttals/PO-decision are persisted via `StoreRoleFindingActivity` (good for retrieval), but the workflow emits no `PLAN.REVIEW.*` lifecycle events (approved / needs-human / max-rounds-exceeded). The forced-needs-human escalation (`:649-659`) is audit-significant. **Fix:** emit `PLAN.REVIEW.APPROVED` / `PLAN.REVIEW.ESCALATED_HUMAN` at the terminal `setOutputs`.
  - [P2] Error handling — `ExtractPODecision` (`:559-563`) catches parse failure and silently defaults to `needsHuman` with the raw text stuffed into notes. That is a reasonable fail-safe (escalates, doesn't fake success), but a hard `llm-call` failure (empty `llmResponse`) is indistinguishable from a malformed-JSON PO response. **Fix:** branch on `llmResult.success` before parsing and emit a distinct failure note.
  - [P2] Naming — Phase 2 `RebuttalDispatch` reuses `GetReviewActionForRole` (`:894`) i.e. rebuttals run under the same *review* action as Phase 1; the "rebuttal" vs "review" distinction lives only in a `phase` variable inside `variables`. Works, but the action name is misleading in the audit trail. **Fix:** consider a dedicated rebuttal action or document the `phase` discriminator.
- **Depends on:** 32-5 caller-cutover (tenantId threading).

## AssessmentWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs`)
- **Purpose / owner story:** Story 7-1E sub-workflow — generates skill-adapted questions, delivers to junior, waits (bookmark) for a response, analyzes it, classifies, updates skill profile. Invoked by MentorshipWorkflow. Still needed.
- **Health:** STALE
- **Findings:**
  - [P0] Architecture/pivot + Structural — the two "AI" steps do NOT call an LLM at all. `GenerateQuestionsActivity` (`GenerateQuestionsActivity.cs:137-200`) returns hardcoded canned question strings; `AnalyzeResponseActivity` (`AnalyzeResponseActivity.cs:120-231`) scores the junior purely on response *length* + keyword counting. Both carry stale comments ("In production, delegates to the LLM Call sub-workflow 7-1B"). Epic-7 README states 7-1E "needs 7-1B". Result: the assessment outcome and the skill-level fed back into Mentorship (`MentorshipWorkflow.cs:435-467`) are meaningless — a long response of nonsense scores "Correct". This is a fail-open correctness defect on a control-flow-driving signal. **Fix:** replace the heuristic bodies with `llm-call` dispatches (role=analyst, an assessment action) — either inside the activities via the engine callback or by restructuring these as `DispatchWorkflow` nodes in the flowchart, threading `tenantId`. **Depends on: 32-5 caller-cutover.**
  - [P1] Per-mode — no `tenantId` is read from input or threaded anywhere; once the LLM steps are wired (P0 fix), tenant-scoped prompt/BYOK resolution will be missing. **Fix:** add a `tenantId` input + variable in `ReadInputs` (`:78-110`) and pass to the new LLM dispatches.
  - [P1] Error handling — `AnalyzeResponseActivity` catch block (`:100-113`) returns a synthesized `Incorrect` result on exception (fail-toward-incorrect, acceptable), but the workflow has no failure outcome for `GenerateQuestionsActivity` or `AnalyzeResponseActivity` throwing — a thrown activity faults the whole sub-workflow with no `Failed`/timeout output for the parent. Only `WaitForResponse` has a `Timeout` branch (`:463`). **Fix:** add error outcomes/handlers around `generateQuestions` and `analyzeResponse` producing an `assessmentResult` with an error status the parent can route on.
  - [P1] Structural — `storeContextResult` (`:130-140`) discards the `gatherContext` DispatchWorkflow output and replaces `storyContext` with a literal string `"Assessment context for story {id} gathered..."`. The gathered context is never actually consumed by `GenerateQuestions`/`AnalyzeResponse` (they get this placeholder). **Fix:** capture the ContextGathering `summary`/`contextIds` output via `Result =` binding and pass the real summary into `storyContext`.
  - [P2] Event emission — the workflow relies on per-activity repository event logging (e.g. `AnalyzeResponseActivity.cs:91` logs `AIAnalysis`); the flowchart itself emits no `ASSESSMENT.COMPLETED.*` event distinguishing response vs timeout outcomes for the DCB trail. **Fix:** emit `ASSESSMENT.COMPLETED.SUCCESS` / `ASSESSMENT.TIMEOUT` at the two expose-output sequences.
- **Depends on:** 32-5 caller-cutover; Epic 6 (real context wiring for the P1 placeholder fix).

## ContextGatheringWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs`)
- **Purpose / owner story:** Story 7-1F / Epic 6 — sequential 5-role codebase scan (dev→QA→security→devops→architect) each via `llm-call`, storing per-role findings in the vector DB through the engine callback, then a PO summary. Still needed (RAG/knowledge feed for planning).
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Architecture/pivot — the 5 `RoleScan` dispatches (`:290-302`, `:321-333`) and the PO `DispatchWorkflow` (`:156-171`) do NOT pass `tenantId`. Same SaaS prompt/convention/BYOK-scoping gap as PlanReview. **Fix:** add a `tenantId` input/variable in `Init` (`:65-83`) and thread it into all six `llm-call` dispatches. **Depends on: 32-5 caller-cutover.**
  - [P1] Per-mode — `StoreRoleFindingActivity` (`StoreRoleFindingActivity.cs:79-89`) posts to `/api/engine/store-context` with `{repository, issueNumber, findings}` and no tenant id. In SaaS mode the vector-store write is not tenant-scoped on the engine→API boundary (relies entirely on the API resolving tenancy from the connection). **Fix:** include `tenantId` in the store-context payload and thread the variable from the workflow. (Epic 6 RAG wiring.)
  - [P2] Error handling — `StoreRoleFindingActivity` swallows non-2xx and exceptions, setting `ContextId = ""` (`:104-113`) and continuing. Per-role partial-persistence is the stated design, but a *silent* empty context-id means downstream `contextIds` can contain `""` with no warning surfaced beyond a log. Also the mock fallback (`:67-72`, when `Engine:CallbackUrl` unset) returns fake ids — acceptable for self-hosted dev but should not silently no-op in SaaS. **Fix:** emit a `CONTEXT.STORE_ROLE.FAILED` event (the activity already has `EventType = "CONTEXT.STORE_ROLE"`) and consider failing closed when a callback URL is expected.
  - [P2] Error handling — `extractPO` (`:181-203`) returns `""` when `llmResult` has no `llmResponse` (provider failure). An empty PO summary then propagates to `summary` output with no error signal — borderline silent-failure for the consumer (PlanGeneration uses `poSummary` as `contextFindings`). **Fix:** branch on `llmResult.success` and set an explicit error/empty-summary marker.
- **Depends on:** 32-5 caller-cutover; Epic 6 (RAG store tenant-scoping).

## MentorshipWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs`)
- **Purpose / owner story:** Story 7-1A — top-level 28-state mentorship flowchart orchestrating assessment, planning, implementation (TDD), blocker escalation, quality, review, merge, and 8 sub-workflow dispatches. Still needed; this is the cluster's orchestrator.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Per-mode / Architecture — no `tenantId` is read from workflow input or set as a variable, yet downstream mentorship activities call `ReadMentorshipTenantId(context)` (`FlowNodeActivities.cs:235-248`) expecting a `TenantId`/`tenantId` workflow variable. That always returns `null` here, so retry-exhaustion alerts (`WorkflowRetryEmitter.EmitAsync`, `:206`) fire with a null tenant and the 8 dispatched sub-workflows (`llm-call`, `assessment`, `context-gathering`, `blocker-diagnosis`, etc.) receive no `tenantId`. **Fix:** add a `tenantId` workflow variable, read it from input in an init step, and pass it into every `DispatchWorkflow.Input` (`:345-559`). **Depends on: 32-5 caller-cutover.**
  - [P1] Architecture/pivot — the `llmCallWorkflow` dispatch (`:345-359`) hardcodes `taskPrompt = "Generate plan decomposition"` and a fixed `role=senior_developer / action=MentorFeedback`, then routes unconditionally to `planDecomposition` (`:869`). This inline LLM call has a stale hardcoded prompt that conflicts with the registry-driven model (prompts should come from Epic 27, not literals). **Fix:** drop the literal `taskPrompt` and rely on registry resolution via role+action; pass real `variables`.
  - [P1] Structural — several sub-workflow dispatches pass empty placeholder inputs: `tddWorkflow` `taskDescription = ""` (`:537`), `blockerDiagnosisWorkflow` `repository = ""`, `branchName = ""` (`:520-521`), `codeReviewWorkflow` etc. These children will run with no real context. Combined with the fact that dispatch results are mostly NOT captured (only `assessmentWorkflow` binds `Result`, `:423`), the orchestration is partly decorative — outputs of TDD/testing/code-review/blocker don't influence subsequent routing. **Fix:** populate dispatch inputs (repository/branch/task description) from session state and bind+consume the dispatch `Result`s that should gate transitions.
  - [P2] Structural — `monitorProgress` "Steady" self-loops (`:998-999`) and `monitorReview` "Pending" self-loops (`:1216-1217`) with no bounded counter or delay, relying entirely on the activity to block/yield. If either activity returns the looping outcome synchronously without a bookmark/delay, this is a tight infinite loop. **Fix:** confirm both activities yield (bookmark/timer); if not, add a bounded wait/iteration guard.
  - [P2] Naming — the LLM dispatch input key is `agentRole` (`:352`) whereas every other workflow in this cluster uses `role`; `LlmCallWorkflow` accepts both (`LlmCallWorkflow.cs:155`) so it works, but the inconsistency is a trap. **Fix:** standardize on `role`.
- **Depends on:** 32-5 caller-cutover; Epic 7 follow-ups for the placeholder sub-workflow inputs.

## TaskCreationWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs`)
- **Purpose / owner story:** Senior-dev LLM decomposes an approved plan into a detailed task DAG; validates a non-empty `tasks` array; retries (max 2). Serves the post-plan task-breakdown step. Still needed.
- **Health:** GOOD
- **Findings:**
  - [P1] Architecture/pivot — the `generateTasks` dispatch (`:87-100`) does NOT pass `tenantId` (same SaaS prompt/BYOK gap). It does correctly read `maxRetries` from input and feed `validationErrors` back on retry. **Fix:** add a `tenantId` input/variable in `Init` (`:62-77`) and pass it to the dispatch. **Depends on: 32-5 caller-cutover.**
  - [P2] Event emission — no `TASK.CREATED.*` DCB events; success/give-up are silent. **Fix:** emit `TASKS.GENERATED.SUCCESS` / `TASKS.GENERATED.FAILED` at `setOutputs`/`setErrorOutputs`.
- **Depends on:** 32-5 caller-cutover.

## TaskReviewWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskReviewWorkflow.cs`)
- **Purpose / owner story:** 4-role LLM panel (architect, senior-dev, dev, tester) reviews implementation tasks; no debate rounds; all-must-approve → `approved` else `needsChanges`. Serves the pre-execution task gate. Still needed.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Architecture/pivot — the 4 `RoleReviewDispatch` calls (`:317-329`) do NOT pass `tenantId` (SaaS prompt/convention/BYOK gap). **Fix:** add `tenantId` input/variable in `Init` (`:86-98`) and thread into the dispatch builder. **Depends on: 32-5 caller-cutover.**
  - [P1] Structural — unlike PlanReview, role reviews are NOT persisted (`StoreRoleFindingActivity` is not used here) and no DCB lifecycle event is emitted; the only record of a `needsChanges` decision is the returned `reviewNotes`. For a gate that can block execution, this is a thin audit trail. **Fix:** persist each role review and emit `TASK.REVIEW.APPROVED` / `TASK.REVIEW.NEEDS_CHANGES`.
  - [P2] Structural — the `decision` variable defaults to `"needsHuman"` (`:68`) but the workflow only ever sets `approved` or `needsChanges` (`:204-225`); `needsHuman` is unreachable despite being documented as an output value (`:31`). Dead/aspirational state. **Fix:** either add a `needsHuman` path (e.g. on repeated `needsChanges`/parse failure) or remove it from the documented outputs.
- **Depends on:** 32-5 caller-cutover.

---

## Cross-cutting observations (patterns shared across this cluster)

1. **tenantId is threaded by exactly ONE of 7 workflows.** PlanGeneration passes `["tenantId"]` into `llm-call`; PlanReview, TaskCreation, TaskReview, ContextGathering, Assessment, and Mentorship all omit it. `LlmCallWorkflow` reads `tenantId` (`LlmCallWorkflow.cs:153`) and threads it to `CallLlmInlineActivity.TenantIdProp` for BYOK + to prompt/convention resolution for tenant-scoped overrides. So in SaaS mode, six of these flows silently fall back to the system-default prompt/convention layer and platform credentials — a per-mode correctness gap, not a key leak. This is the single highest-leverage fix and is the natural unit of the **32-5 caller-cutover**: standardize a `tenantId` workflow input and pass it on every `llm-call` dispatch. (P1 across 6 workflows.)

2. **No rule-1 vendor violations.** Every LLM operation routes through the `llm-call` sub-workflow (which holds the provider chain, circuit breaker, budget, allowlist — `LlmCallWorkflow.cs`), and the one outbound HTTP step (`StoreRoleFindingActivity`) targets the tamma-api engine callback (`/api/engine/store-context`), never a vendor SDK. The Elsa engine holds no provider keys in this cluster. Assessment/Mentorship state activities touch only the session repository. Confirmed via grep for `Anthropic|OpenAI|HttpClient|ILLMProvider|new *Client(` over the Assessment + Mentorship activity dirs (no hits).

3. **DCB event emission is sparse at the workflow level.** These flowcharts lean on `SetVariable`/`SetOutput`/`DispatchWorkflow` nodes that emit nothing; meaningful milestones (plan generated, review approved/escalated, tasks created, assessment completed/timed-out) have no `AGGREGATE.ACTION.STATUS` event. PlanReview/ContextGathering get partial coverage via `StoreRoleFindingActivity` (which DOES carry an `EventType`), but decisions and terminal states are largely unaudited. Recommend a small reusable "emit lifecycle event" async activity (engine-callback based) dropped at each terminal/decision node. (P2 across most workflows.)

4. **The Assessment "AI" is a heuristic placeholder (the lone P0).** `GenerateQuestionsActivity` and `AnalyzeResponseActivity` never reach a provider despite their doc-comments and the Epic-7 dependency graph (7-1E needs 7-1B). The resulting skill signal drives Mentorship routing (`MentorshipWorkflow.cs:435-467`) and skill-profile updates, so the fake score has real control-flow impact. Wiring these to `llm-call` is gated on the same 32-5 cutover (and Epic 6 for the discarded-context placeholder).

5. **Two failure modes are handled inconsistently:** a hard `llm-call` failure (`success=false`, empty `llmResponse`) is, in most extract steps, indistinguishable from a malformed-but-present LLM response — both fall into "invalid → retry → needsHuman/needsChanges". The fail-*closed* intent (escalate to human, never fake success) is mostly preserved (good), but the audit trail loses the provider-exhaustion vs schema-failure distinction. Branch on `llmResult.success` before parsing in every extract node.
