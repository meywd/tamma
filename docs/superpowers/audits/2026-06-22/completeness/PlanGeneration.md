# Completeness Audit — `PlanGenerationWorkflow`

**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs`
**Definition ID:** `plan-generation`
**Audit date:** 2026-06-22
**Verdict:** **PARTIAL** — the core happy path is real and correctly mediated (single architect LLM call via the `llm-call` sub-workflow, JSON extraction + field validation, a bounded retry-with-error-feedback loop, and a distinct success/error output contract). But it is missing several correctness/contract pieces (drops the `success`/cost/tokens/provider signals the sub-workflow returns, no terminal-fault distinction between "invalid plan" and "all providers failed", no workflow-level DCB audit events, lenient validation that admits near-empty plans) and most of the *intended* scope from Story 2.3 (ambiguity detection, multiple implementation options, structured plan schema with risks/effort/testing, the `PLAN.GENERATED.SUCCESS/FAILED` audit events).

---

## 1. Purpose & owner

**Purpose (one line):** Produce the implementation blueprint for one work item — an architect-role LLM (prompt resolved from the Epic-27 registry, `role=architect` / `action=plan-system-design`) generates a plan, the workflow extracts + validates the JSON, retries up to `maxRetries` feeding validation errors back into the prompt, and returns either `planJson` (valid) or `error` (gave up).

**Owning epic/story:** This is the **`PLAN_GENERATION`** step of the **14-step autonomous loop** (`docs/architecture.md`; `RolePhaseMap.cs` line 251 maps `PLAN_GENERATION → plan-system-design`). The product story is **Epic 2, Story 2.3 — "Development Plan Generation with Approval Checkpoint"** (`docs/epics.md` line 729; spec `docs/stories/epic-2/story-2-3/2-3-development-plan-generation-with-approval-checkpoint.md`). The LLM mediation it rides on is owned by **Epic 32** (revised agent architecture, `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`) — the `call-LLM` mediation (32-5) and tenant/BYOK threading (32-3/32-16), today fulfilled by `LlmCallWorkflow`.

**Consumer contract (from `SingleIssueCycleWorkflow.cs` lines 156-184):** input `{ repository, issueNumber, poSummary, contextIds, workItemJson, reviewNotes, revisionNumber, conventions, tenantId, maxRetries? }`; output `{ planJson }` on success or `{ planJson:"", error }` on give-up. The downstream **plan-review** sub-workflow (7-role panel) + the approve/modify/defer/split/needs-human routing live in `SingleIssueCycleWorkflow` — **NOT** in this workflow. The "approval checkpoint" half of Story 2.3 is therefore the *cycle's* responsibility; this workflow owns only the *generate + validate* half.

---

## 2. Maturity: **PARTIAL**

Not a stub and not merely "thin": it has a real, non-trivial control structure — generate → extract → validate → branch on validity → bounded retry that **feeds `validationErrors` back into the next generation** (a genuine self-correction loop, not just a re-roll) → distinct valid/error output sequences. It correctly routes **all** LLM work through the `llm-call` sub-workflow and holds no provider key (the cross-cutting Epic-32 rule §1 is honored). The prompt comes from the registry, not inline. That is more than a happy-path skeleton.

But it is not complete. It (a) ignores the sub-workflow's `success`/`providerUsed`/`costUsd`/`tokensUsed` outputs, so a hard provider failure ("all providers failed", empty response) is indistinguishable from "LLM returned a malformed plan" — both funnel through the same retry-then-`validationErrors` path and the same `error` output; (b) emits **no** workflow-level DCB audit events (`PLAN.GENERATED.SUCCESS/FAILED`), which Story 2.3 AC7 and `docs/architecture.md` ("every state transition must emit a corresponding DCB event") explicitly require; (c) validates only the loosest possible shape (presence of *either* `tasks`/`steps` *and* *one of* `fileMap`/`files`/`filesToModify` — an empty `"tasks":[]` passes); and (d) implements essentially none of the richer Story-2.3 plan content (ambiguity report, multiple options, risks, effort/confidence, testing strategy).

---

## 3. Current capabilities (what it does today)

- **Init** (`SetVariable "Init"`): reads `repository`, `issueNumber`, `poSummary`, `contextIds`, `workItemJson`, `reviewNotes`, `revisionNumber`, `tenantId`, and optional `maxRetries` (default 2) from workflow input into variables.
- **Generate Plan** (`DispatchWorkflow("llm-call")`): role `architect`, action `plan-system-design`, `enableTools=true`, threads `tenantId` (good — SaaS prompt + BYOK resolution works), and passes `variables { workItemJson, contextFindings(=poSummary), poSummary, contextIds, repository, reviewNotes, revisionNumber, validationErrors }`. **Mediation-correct:** the engine never calls a provider directly.
- **Extract & Validate** (`SetVariable "ExtractValidate"` → `PlanValidationHelper.ValidatePlan`): pulls `llmResponse` from the sub-workflow result, extracts the outermost `{...}` block, parses JSON, and checks for *(tasks OR steps)* and *(fileMap OR files OR filesToModify)*. Sets `planValid` + `validationErrors`.
- **Valid?** (`FlowDecision` on `planValid`): True → set `planJson` output → `Finish`.
- **Retry loop** (`IncrRetry` `SetVariable` → `CanRetry` `FlowDecision` on `retryCount < maxRetries`): on invalid, increment and — if budget remains — loop **back to Generate Plan with the validation errors now in the prompt variables** (self-correcting re-generation). Exhausted → error output.
- **Outputs:** valid → `SetOutput planJson`. Give-up → `Sequence` emitting `planJson:""` + `error = validationErrors`. Both reach a single `Finish`.
- **Self-correction signal:** `validationErrors` is wired into the regeneration variables, so retries are informed, not blind.

---

## 4. Intended full scope (with citations)

1. **It is the `PLAN_GENERATION` step of the 14-step loop** (`RolePhaseMap.cs:251`; `docs/architecture.md`). Its output is the contract every later step consumes: `SingleIssueCycleWorkflow` feeds `planJson` into **plan-review**, **task-creation**, and the **TDD/implementation** path (`SingleIssueCycleWorkflow.cs` lines 198, 286, 317, 392; `TaskCreationWorkflow.cs`, `TddWithDebugRetryWorkflow.cs`, `TaskReviewWorkflow.cs` all key off `planJson`). A complete plan therefore must be *structurally* trustworthy enough for task-decomposition and TDD to run off it.
2. **Story 2.3 defines a rich plan, not a 2-field blob.** The spec's `DevelopmentPlan` (`2-3-...md` lines 75-152) carries `summary`, `approach` (methodology + phases), `files[]` (path/action/complexity/testsRequired), `testing` (strategy + coverage targets + test types), `risks[]`, `estimatedEffort` (totals + confidence + breakdown). AC1-2: "generate development plan based on issue context… includes implementation approach, file changes, and testing strategy." The current validator accepts a plan with none of approach/testing/risks/effort and an empty task list.
3. **Ambiguity detection (AC3) + multiple options (AC4).** Story 2.3 AC3: "System detects ambiguity in requirements and flags for clarification"; AC4: "provides multiple implementation options when appropriate." `detectAmbiguity()` / `generateOptions()` are first-class methods in the spec (lines 69-70, 456-499) and tie into PRD **FR-3** (clarifying questions on ambiguous specs) and Epic-3 ambiguity stories (`docs/epics.md` 1096-1128). None of this exists in the workflow.
4. **Audit trail / DCB events (AC8 + architecture).** Story 2.3 AC7-8: "Plan and approval status logged to event trail for audit"; the spec body shows the exact events: **`PLAN.GENERATED.SUCCESS`** (with tags `issueId/issueNumber/planId/ambiguityScore`, data `fileCount/estimatedMinutes/optionsCount/generationTime`) and **`PLAN.GENERATION.FAILED`** (`2-3-...md` lines 247-279). `docs/architecture.md` Logging Requirements: "Every state transition must emit a corresponding DCB event." `CLAUDE.md` §"Emitting Events for Audit Trail" mandates it for every operation. The workflow emits **zero** workflow-level events today.
5. **Cost / usage metering must not be discarded.** `LlmCallWorkflow` returns `success`, `providerUsed`, `costUsd`, `tokensUsed`, `workflowOutput` (lines 583-681). The revised agent architecture (`2026-06-20-epic-32-revised-agent-architecture.md` §0 rule 2(e)) makes metering a first-class output of the mediated call; Epic 36 analytics + Epic 32-9 usage events consume per-step cost/tokens. PlanGeneration currently reads only `llmResponse` and throws the rest away, so the plan step is invisible to cost analytics.
6. **No-false-success / no-silent-failure.** Project rule (`MEMORY` `feedback_resolution_no_empty_fallback`; `CLAUDE.md`): resolution is tenant→system→error, never empty/plain; failures must surface, not be papered over. A hard provider failure (sub-workflow `success=false`, empty `llmResponse`) currently degrades into the *same* "Empty plan" validation error and burns retry attempts re-prompting a dead provider chain, rather than failing fast with a distinct fault — and on give-up the parent only sees a generic `error` string, never "the LLM itself never ran."
7. **Mediated, tenant/BYOK-correct LLM path (Epic 32).** §1 of the pivot spec: "A workflow STEP MUST NEVER call an external API/provider directly… the engine never holds a provider key." **Honored** — and `tenantId` is threaded (32-3/32-16), so SaaS prompt + credential resolution works. This is the one large thing the workflow already gets right.
8. **Determinism / reproducibility of validation.** Because this output drives autonomous code generation, the validation contract should be a single shared, versioned schema (so plan-generation, plan-review, and task-creation agree on "what a valid plan is"). Today `PlanValidationHelper` encodes an ad-hoc tolerant superset (`tasks|steps`, `fileMap|files|filesToModify`) that no other component references.

---

## 5. Missing capabilities (gap to complete)

| # | Missing capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Distinguish "LLM call failed" from "plan invalid."** Read the sub-workflow `success` output; on `success=false` (all providers failed / circuit-open / budget exhausted) take a **distinct terminal-fault edge** — do NOT consume a validation retry re-prompting a dead chain, and surface a fault `error` (e.g. `LLM call failed: <providerUsed/diagnostics>`) so the parent can route to needs-human, not "bad plan." Honors no-false-success. | **P0** | 32-5 (`call-LLM` already returns `success`/diagnostics) |
| 2 | **Emit workflow-level DCB events.** `PLAN.GENERATED.SUCCESS` on valid (tags `issueId/issueNumber/tenantId/revisionNumber`; data `taskCount/fileCount/retryCount/provider/costUsd/tokensUsed/durationMs`) and `PLAN.GENERATION.FAILED` on give-up/fault (data `reason: invalid-after-retries | llm-failure`, `validationErrors`, `retryCount`). Required by Story 2.3 AC8 + architecture "every state transition emits a DCB event." Today: none. | **P0** | none (use the existing `TammaAsyncActivity` / event-bag pattern) |
| 3 | **Tighten validation to the consumer contract.** Current check passes `{"tasks":[],"files":[]}` (empty but present) and accepts either `tasks` or `steps` / any of three file keys — a permissive superset no downstream step actually agrees on. Require ≥1 non-empty task/step, ≥1 file entry with a usable `path`, and reject plans that parse but are semantically empty. Make it a single shared/versioned schema referenced by plan-generation, plan-review, and task-creation. | **P0** | none |
| 4 | **Stop discarding cost/tokens/provider.** Capture `providerUsed`, `costUsd`, `tokensUsed`, `workflowOutput` from the `llm-call` result and (a) include them in the success DCB event and (b) surface them as workflow outputs so the cycle/analytics can meter the plan step. Plan generation is currently invisible to cost analytics. | **P1** | Epic 36 (analytics consumer) / 32-9 (usage events); the sub-workflow already returns the values |
| 5 | **Structured plan schema (Story 2.3 content).** Validate/normalize `summary`, `approach` (methodology+phases), `files[]` (path/action/complexity/testsRequired), `testing` (strategy+coverage), `risks[]`, `estimatedEffort` (confidence+breakdown) — not just "tasks + a file key." The prompt template and validator must agree on this shape so the plan is rich enough for task-decomposition and TDD. | **P1** | Epic 27 prompt template for `architect/plan-system-design` must emit the schema |
| 6 | **Ambiguity / clarification signal (AC3, FR-3).** Detect requirement ambiguity and surface an `ambiguityScore` + `clarifications[]` (or a `needsClarification` outcome) so the cycle can pause for human input rather than generating a confident plan over unclear specs. | **P2** | Epic 3 ambiguity stories (3.4-3.6); none for the signal field |
| 7 | **Multiple implementation options (AC4).** Optionally produce 2-3 ranked options with pros/cons/effort/risk and a recommended pick, recorded in the plan + event. | **P2** | Story 2.3 |
| 8 | **Brittle JSON extraction.** `ExtractJson` takes the outermost `{`..`}`; an architect response with prose containing a stray `{` or fenced code blocks before the JSON yields a malformed/wrong block and a "Invalid JSON" error that *consumes a retry* unnecessarily. Prefer fenced-block (```json) extraction with a brace-scan fallback, and don't count a parse-only failure the same as a content failure. | **P2** | none |
| 9 | **No idempotency / re-run guard.** Re-dispatching for the same `(repository, issueNumber, revisionNumber)` re-spends LLM budget with no dedupe or "already generated this revision" short-circuit. (Revisions are legitimate re-runs, so the key must include `revisionNumber`/`reviewNotes` hash.) | **P3** | none |
| 10 | **Retry exhaustion telemetry.** When all retries fail, the only signal is the last `validationErrors`; the per-attempt errors and the provider diagnostics aren't aggregated into the failure event, so debugging "why did planning fail 3×" requires log archaeology. | **P3** | none (folds into #2) |

> **Explicitly NOT this workflow's gap (owned by the cycle):** the human approval checkpoint (Story 2.3 AC5-7 — approve/reject/edit, expiration, notification channels) and plan review are implemented in `SingleIssueCycleWorkflow` via the **plan-review** sub-workflow and the approve/modify/defer/split/needs-human `FlowSwitch`. Do not duplicate them here.

---

## 6. Ordered build-out spec (to reach complete)

Each step names the activity/node, the branch condition, the event type, and the failure edge. All LLM work stays mediated through `llm-call` (never a direct provider call); resolution stays tenant→system→error; no silent success.

1. **Emit `PLAN.GENERATION.STARTED`.** Add an `EmitPlanEventActivity` (or reuse the `TammaAsyncActivity` event-bag pattern as `ContextGathering`/tenant-lifecycle workflows do) right after `Init`. Tags: `issueId(=issueNumber)`, `repository`, `tenantId`, `revisionNumber`. Data: `maxRetries`, `hasReviewNotes`. Edge: `Init → EmitStarted → GeneratePlan`.

2. **Capture the full `llm-call` result, not just text.** After `GeneratePlan`, in (or before) `ExtractValidate`, read `success`, `providerUsed`, `costUsd`, `tokensUsed`, `workflowOutput` from `llmResult` into new variables (`llmSucceeded`, `providerUsed`, `costUsd`, `tokensUsed`). These feed the events and outputs below.

3. **Add an LLM-fault gate BEFORE validation.** New `FlowDecision "LlmSucceeded?"` on `llmSucceeded`:
   - **False →** `SetVariable` set `faultReason = "llm-failure"`, `validationErrors = "LLM call failed: " + providerUsed/diagnostics` → emit **`PLAN.GENERATION.FAILED`** (data `reason:"llm-failure"`, diagnostics) → error-output sequence → `Finish`. Do **not** enter the validation/retry loop on a hard LLM failure (avoids burning retries on a dead chain; honors no-false-success).
   - **True →** proceed to `ExtractValidate`.
   - Edge: `GeneratePlan → LlmSucceeded? →(True) ExtractValidate →(False) SetLlmFault → EmitFailed → setErrorOutputs`.

4. **Replace `PlanValidationHelper.ValidatePlan` with a strict, shared, versioned schema check.** Require: parseable JSON; ≥1 non-empty `tasks`/`steps` entry; ≥1 `files`/`fileMap` entry with a non-empty `path`; (target shape) presence of `summary`, `approach`, `testing`. Distinguish **parse failure** (`Invalid JSON`) from **content failure** (`Missing/empty tasks`) in `validationErrors` so retries and events can tell them apart. Centralize the schema so `plan-review` and `task-creation` validate against the same contract.

5. **Improve `ExtractJson`.** Prefer a fenced ```json block if present; fall back to outermost-brace scan; on extraction failure set a `extractFailed` flag distinct from a content-validation failure (used to decide whether a retry is worthwhile).

6. **Keep the informed retry loop, but cap on fault type.** `IncrRetry → CanRetry?` stays. On `CanRetry=True`, re-dispatch `GeneratePlan` with `validationErrors` in variables (already wired — good). On `CanRetry=False`, set `faultReason = "invalid-after-retries"` and go to the failure path. Per-attempt `validationErrors` should be accumulated (append, not overwrite) for the failure event.

7. **Success path: validate → emit → output.** On `PlanValid=True`:
   - Emit **`PLAN.GENERATED.SUCCESS`** — tags `issueId/repository/tenantId/revisionNumber`; data `taskCount`, `fileCount`, `retryCount`, `providerUsed`, `costUsd`, `tokensUsed`, `durationMs`, (when present) `ambiguityScore`/`optionsCount`.
   - `SetOutput planJson` **plus** new outputs `providerUsed`, `costUsd`, `tokensUsed` (so the cycle/analytics can meter the step).
   - Edge: `PlanValid? →(True) EmitGenerated → SetOutputs → Finish`.

8. **Failure path: emit → error output.** Both the LLM-fault edge (step 3) and the retries-exhausted edge (step 6) converge on **`PLAN.GENERATION.FAILED`** (data `reason`, aggregated `validationErrors`, `retryCount`, diagnostics) → existing `setErrorOutputs` (`planJson:""`, `error=...`) → `Finish`. Add `faultReason` to the `error` output so the cycle can route `llm-failure` to needs-human vs. `invalid-after-retries` to a different branch if desired.

9. **(P1) Enrich the prompt + schema for Story-2.3 content.** Coordinate with the Epic-27 `architect/plan-system-design` template so the LLM emits `approach/phases`, `testing/coverage`, `risks`, `estimatedEffort` (confidence + breakdown); extend the validator (step 4) to normalize and accept these. This is what makes the plan rich enough for downstream TDD.

10. **(P2) Ambiguity + options.** Either (a) instruct the architect template to include an `ambiguity { score, items[], requiresClarification }` block and an `options[]` block in the same call, or (b) add a preceding `DispatchWorkflow("llm-call")` with `action` = an ambiguity-detect action. On `requiresClarification=true && score>threshold`, emit `PLAN.CLARIFICATION_NEEDED` and add a `NeedsClarification` outcome the cycle can branch on (instead of fabricating a confident plan over unclear specs).

11. **(P3) Idempotency + diagnostics rollup.** Add an optional "already generated for `(repository, issueNumber, revisionNumber, reviewNotesHash)`" short-circuit to avoid re-spend on identical re-runs; aggregate per-attempt validation errors + provider diagnostics into the failure event for debuggability.

---

## 7. Effort & overall priority

- **Overall priority:** **P1** — the workflow is functional and correctly mediated (it will not silently corrupt downstream), but the P0 items (LLM-fault vs invalid-plan distinction, DCB events, strict validation) are real correctness/contract/audit gaps that matter for the autonomous loop's safety and compliance story.
- **Effort:** **M** — the control-flow skeleton, mediation, and retry loop already exist; the work is additive: one fault-gate decision + edge, an event-emit activity reused 3×, a stricter validator, capturing 4 extra sub-workflow outputs, and (for the P1/P2 content) coordinated prompt-template + schema changes. No new external integration; no new provider plumbing (the `llm-call` seam already returns everything needed).
