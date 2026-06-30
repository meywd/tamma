# Assessment P0 — Replace fake-AI steps with real `llm-call` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Replace `AssessmentWorkflow`'s two fake heuristic "AI" steps (`GenerateQuestionsActivity` hardcoded question bank; `AnalyzeResponseActivity` keyword-counting that *fabricates the confidence the mentorship machine routes on*) with real `DispatchWorkflow("llm-call")` calls (per the Epic-32 rule: a workflow step never calls a provider directly), feeding the existing `context-gathering` output as context and threading `tenantId`. Add the two assessment prompt `(role, action)` cells the registry needs.

**Architecture:** `llm-call` resolves prompts by `(role, action)` from the jagged `RolePhaseMap` taxonomy (SPEC §4); resolution is tenant→system→**error** (no empty fallback). So the new assessment actions must be added consistently across `AgentAction` (enum) + `RolePhaseMap` (the role's action set) + `SystemPrompts.RoleActionTemplates` (a non-empty body per cell) — the taxonomy-drift tests enforce this. Then the workflow dispatches `llm-call` with `role`+`action`+`variables` and parses the structured result (mirroring `ContextGatheringWorkflow`'s JSON-slice pattern). **Epic-6 RAG is deferred** — `IIntelligenceHttpClient` lives in `Tamma.Api` with zero references from `Tamma.Activities`/`Tamma.ElsaServer`; the achievable context is the existing `context-gathering` output (currently dropped into a placeholder).

**Tech Stack:** .NET 9 / EF Core 9 / Elsa 3.5.3 workflows / NUnit + Moq / tests via `sg docker -c "dotnet test ..."`.

## Global Constraints

- **Build gate:** `dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly` → 0 errors. **Test runner:** `sg docker -c "dotnet test ..."`. Run the FULL `Tamma.Api.Tests` (taxonomy/prompt tests) + `Tamma.Activities.Tests` (workflow) before committing.
- **Taxonomy drift is enforced:** every `RolePhaseMap` `(role, action)` cell MUST have a non-empty `SystemPrompts.RoleActionTemplates` body (and matching convention cell if conventions use the same taxonomy). Adding an action to `RolePhaseMap` without a template (or vice versa) fails a drift test. Add ALL sides atomically in Task 1; run the drift tests.
- **No empty fallback:** prompt resolution is tenant→system→error. The new templates must be real, non-empty bodies.
- **Canonical role:** use `product_owner` (the audit's `analyst` aliases to `product_owner` via `RolePhaseMap.cs:229`). The `llm-call` wire `role` may be `"analyst"` (normalizes) or `"product_owner"` — use the canonical `AgentRole.ProductOwner.ToWire()` to avoid the alias indirection. Confirm `product_owner` is the right home vs `senior_developer` by checking what role `MentorshipWorkflow` (Assessment's parent) uses; prefer consistency.
- **No schema change** (no migration, no `TammaModelConfiguration`). The prompt templates are code (`SystemPrompts.cs`), not DB rows.
- **Branch:** `feat/assessment-p0-llm-call` off `origin/main` (`f118e58d`) in worktree `/home/meywd/tamma-wt/assessment-p0`.

---

## Verified current-state appendix (origin/main `f118e58d`, 2026-06-30 — do not re-derive)

### The 2 fake-AI activities (convert) + the 1 to keep
- `apps/tamma-elsa/src/Tamma.Activities/Assessment/GenerateQuestionsActivity.cs` — `CodeActivity<QuestionSet>`; hardcoded question bank (`GetSkillLevelQuestions` L168-197); comment L135 "in production, this would delegate to the LLM Call workflow." **FAKE → replace with llm-call.**
- `apps/tamma-elsa/src/Tamma.Activities/Assessment/AnalyzeResponseActivity.cs` — heuristic `PerformAnalysis` (L120): confidence from response-length/question-count (L152-171) + technical-term counting (L174-183) + substring presence (L186-193); comments L80-82/L117-118 admit it should be llm-call. **FAKE → replace with llm-call.** (Injects `IMentorshipSessionRepository` only to log an event.)
- `apps/tamma-elsa/src/Tamma.Activities/Assessment/ClassifyResultActivity.cs` — deterministic threshold router (L112-129). **KEEP** — just feed it the real LLM confidence.
- Models: `apps/tamma-elsa/src/Tamma.Activities/Assessment/Models/AssessmentModels.cs` (`QuestionSet`, `AnalysisResult`, `PreviousAttempt`, `AssessmentResult`).

### The `llm-call` dispatch + result pattern (copy)
- Compliant ref `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs`: `var llmResult = builder.WithVariable<IDictionary<string,object>?>();` (L59); dispatch L88-113 `new DispatchWorkflow { WorkflowDefinitionId = new("llm-call"), Input = new(ctx => new Dictionary<string,object>{ ["role"]=..., ["action"]=..., ["tenantId"]=tenantId.Get(ctx), ["variables"]=new Dictionary<string,object>{...}, ["enableTools"]=true }), WaitForCompletion = new(true), Result = new(llmResult) };` then read L118-135 `result.TryGetValue("llmResponse", out var r)`.
- Structured-JSON parse ref `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs:177-204` (slice `output[jsonStart..jsonEnd]` + deserialize). AnalyzeResponse must mirror this to recover `{status, confidence, gaps[], strengths[], rationale}`.
- `llm-call` contract — `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` (`DefinitionId="llm-call"`, L55). Inputs read in `InitInputs` (L109-198): `role`/`agentRole`, `action`, `tenantId`, `variables` (nested dict the prompt template renders), `enableTools`, optional `context`/`systemPromptOverride`/`taskPrompt`. Outputs (readable from `Result` dict): `success`, `llmResponse` (text), `workflowOutput` (full JSON), `costUsd`, `tokensUsed`. Defaults role to `"developer"` if none resolves (L196).
- Roles `apps/tamma-elsa/src/Tamma.Core/Agents/AgentRole.cs:11-18` (`developer/tester/security/devops/architect/product_owner/senior_developer/tech_writer`). Aliases `RolePhaseMap.cs:221-233` (`analyst`→`product_owner` L229). `AgentAction` enum `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs:16` + `ToWire()` L112. **No assessment action exists yet.**

### The prompt taxonomy (where Task 1 adds cells)
- `apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs`: `RoleActionTemplates = BuildRoleActionTemplates()` (L101) — the jagged `(role, action)` template list (~72 cells, "one non-empty body per cell in each role's `RolePhaseMap` action set"). `RoleSystemPrompts` (L76, per-role identity). Resolution is tenant→system→error (header L34-56). `PromptStoreService` (`Tamma.Api/Services/PromptStore/PromptStoreService.cs`) resolves; `ResolvePromptFromRegistryActivity` (in the llm-call path) calls it by `(role, action)`.
- `apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs` — each role's action set (the taxonomy the templates must match). Conventions key off the same taxonomy (`Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs` — check whether a convention cell is also required per role+action, to keep the convention drift test green).

### AssessmentWorkflow shape — `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs`
`ReadInputs` (L80; **add `tenantId` read here**) → `GatherContext` DispatchWorkflow("context-gathering") (L114-116; result currently NOT captured) → `StoreContextResult` (L132; **writes a placeholder — wire the real output**) → `GenerateQuestions` activity (L143-145; **→ llm-call**) → `StoreQuestions` (L159) → `DeliverQuestions` (L171) → `WaitForResponse` (L184) → [Responded] `StoreResponse` (L199) → `AnalyzeResponse` activity (L211-213; **→ llm-call**) → `StoreAnalysis` (L227) → `ClassifyResult` (L272; keep) → `UpdateSkillProfile` (L300) → `SetOutputResult` (L333; **hardcodes `AnalysisRationale="Assessment completed"` — use the real rationale**); [Timeout] branch L246/315/363. Flowchart root L416, connections L442.

### Out of scope (deferred / recorded)
- **Epic-6 RAG context** — no Activities-layer seam (`IIntelligenceHttpClient` is `Tamma.Api`-only). Use `context-gathering` output instead; file a follow-up to expose RAG to the Activities layer.
- The structurally-dead timeout branch (no timer/resume — audit #3/#4); benchmark/leaderboard wiring for the new actions (Epic-32 later).

---

## File Structure

**Task 1 (prompt taxonomy):** modify `Tamma.Core/Agents/AgentAction.cs` (+2 enum entries), `Tamma.Core/Agents/RolePhaseMap.cs` (add the 2 actions to `product_owner`'s set), `Tamma.Api/Auth/SystemPrompts.cs` (2 `RoleActionTemplates` bodies), and (if the convention drift test requires) `Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs`.

**Task 2 (workflow):** modify `Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs` (replace 2 activity nodes with llm-call dispatch+parse; wire context + tenantId + real rationale); a small `AssessmentLlmParsing` helper if the JSON parsing is non-trivial; the fake activities `GenerateQuestionsActivity`/`AnalyzeResponseActivity` either kept as a documented `mockMode` fallback OR deleted (decide by their references — if only AssessmentWorkflow used them, prefer keeping as an explicit-mock fallback per audit 7-2 AC7, gated off by default).

---

## Shared contract (both tasks must agree on these variable names)

**`generate-assessment-questions`** — template variables (rendered by the prompt; passed in `variables`):
`{{storyContext}}` (context-gathering output), `{{skillLevel}}`, `{{questionCount}}`, `{{previousGaps}}` (empty on first attempt). Expected LLM output: a JSON array of question strings (or `{questions:[...]}`) the workflow parses into `QuestionSet`.

**`analyze-assessment-response`** — template variables: `{{storyContext}}`, `{{questions}}`, `{{response}}`, `{{skillLevel}}`. Expected LLM output: JSON `{"status":"...","confidence":0.0-1.0,"gaps":[...],"strengths":[...],"rationale":"..."}` the workflow parses into `AnalysisResult` and feeds `confidence` to `ClassifyResultActivity`.

---

## Task 1: Add the 2 assessment `(role, action)` cells to the prompt taxonomy

**Files:** `AgentAction.cs`, `RolePhaseMap.cs`, `SystemPrompts.cs`, (maybe) `ConventionSeedSpecs.cs`. **Test:** the existing taxonomy-drift test project (find it — likely `Tamma.Api.Tests`).

**Interfaces:**
- Produces: `AgentAction.GenerateAssessmentQuestions` (wire `"generate-assessment-questions"`) + `AgentAction.AnalyzeAssessmentResponse` (wire `"analyze-assessment-response"`), both in `product_owner`'s `RolePhaseMap` action set, each with a non-empty `SystemPrompts` template using the Shared-contract variables.

- [ ] **Step 1: Find the drift test + the taxonomy wiring.** Locate the test that asserts "every `RolePhaseMap` (role,action) cell has a `SystemPrompts.RoleActionTemplates` body" (grep `RoleActionTemplates`/`drift`/`taxonomy` in `tests/`). Read `RolePhaseMap.cs` to see how a role's action set + the `AgentAction`→wire mapping + the alias map are structured. Read `SystemPrompts.BuildRoleActionTemplates()` to see the `PromptTemplate` shape (role, action, body, variables?). Read `ConventionSeedSpecs.cs` to determine if conventions ALSO require a cell per (role,action) (if so, add there too).

- [ ] **Step 2: Write the failing taxonomy test** — add an assertion (or rely on the existing drift test) that `PromptStoreService` (or the resolver) resolves `(product_owner, generate-assessment-questions)` and `(product_owner, analyze-assessment-response)` to a non-empty template. Run it → FAIL (action/template absent). Capture RED.

- [ ] **Step 3: Add the AgentAction entries** in `AgentAction.cs` (2 enum members; confirm `ToWire()` via `EnumWire` produces `"generate-assessment-questions"`/`"analyze-assessment-response"` — match the project's enum-wire kebab convention; if `EnumWire` needs an attribute/registration, add it).

- [ ] **Step 4: Add the actions to `product_owner`'s `RolePhaseMap` action set** (so the (role,action) cell is part of the taxonomy). Follow the exact structure the other actions use.

- [ ] **Step 5: Add the 2 non-empty templates** in `SystemPrompts.BuildRoleActionTemplates()` for `(product_owner, generate-assessment-questions)` and `(product_owner, analyze-assessment-response)`. Write real, useful prompt bodies using the Shared-contract variables — e.g. the generate body instructs the model to produce N skill-appropriate questions about the story as a JSON array; the analyze body instructs it to assess the junior's response and return the JSON `{status,confidence,gaps,strengths,rationale}` shape. (If `ConventionSeedSpecs` requires a matching cell, add minimal convention bodies too.)

- [ ] **Step 6: Run the taxonomy/drift tests → GREEN.** Run the full `Tamma.Api.Tests` project → no drift failures, prompt resolution passes.

- [ ] **Step 7: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs \
        apps/tamma-elsa/src/Tamma.Core/Agents/RolePhaseMap.cs \
        apps/tamma-elsa/src/Tamma.Api/Auth/SystemPrompts.cs \
        apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs \
        apps/tamma-elsa/tests
git commit -m "feat(assessment): add generate/analyze assessment (role,action) prompt cells"
```

---

## Task 2: Restructure `AssessmentWorkflow` to dispatch `llm-call`

**Files:** `AssessmentWorkflow.cs` (+ a parsing helper if needed). **Test:** the workflow test project (`Tamma.Activities.Tests` / `WorkflowStructureTests`).

**Interfaces:**
- Consumes: the Task-1 actions (`AgentAction.GenerateAssessmentQuestions`/`AnalyzeAssessmentResponse`), `AgentRole.ProductOwner`, the `llm-call` Input/Result contract.

- [ ] **Step 1: Thread `tenantId`** — add `var tenantId = builder.WithVariable<string>("TenantId","")` + set it from `ctx.GetInput<string>("tenantId") ?? ""` in `ReadInputs` (mirror PlanGeneration). (MentorshipWorkflow, Assessment's parent, should pass it; the `?? ""` default is safe.)

- [ ] **Step 2: Capture the context-gathering output** — bind the `GatherContext` DispatchWorkflow's `Result` to a variable and replace `StoreContextResult`'s placeholder with the real gathered context (the `storyContext` fed into the question/analysis variables).

- [ ] **Step 3: Replace `GenerateQuestions`** — write the failing workflow test first (assert the workflow has a `DispatchWorkflow("llm-call")` node for question generation carrying `role=product_owner`/`action=generate-assessment-questions`/`tenantId`; mirror the Bucket-B structural test seam if that's the ceiling). Then replace the `GenerateQuestionsActivity` node with a `DispatchWorkflow{ WorkflowDefinitionId=new("llm-call"), Input=...(role, action=GenerateAssessmentQuestions, tenantId, variables={storyContext,skillLevel,questionCount,previousGaps}, enableTools=false), WaitForCompletion=true, Result=questionLlm }` + a parse step that turns `questionLlm["llmResponse"]` JSON into `QuestionSet` (reuse the ContextGathering JSON-slice helper). On `success=false` route to the existing Error/timeout path (do not proceed with empty questions).

- [ ] **Step 4: Replace `AnalyzeResponse`** — same pattern: `DispatchWorkflow("llm-call")` with `action=AnalyzeAssessmentResponse`, variables `{storyContext,questions,response,skillLevel}`; parse the `{status,confidence,gaps,strengths,rationale}` JSON into `AnalysisResult`; on `success=false`, fail closed (route to Error, do NOT fabricate confidence). Feed the parsed `confidence` to `ClassifyResultActivity` and the parsed `rationale` to `SetOutputResult` (replacing the hardcoded `"Assessment completed"`).

- [ ] **Step 5: Rewire the flowchart `Connections`** (L442) for the new dispatch+parse nodes (each dispatch → parse → next), preserving the Responded/Timeout branches. Add an Error terminal for `success=false` on either llm-call (fail-closed, no fabricated result).

- [ ] **Step 6: Handle the now-unused fake activities** — if `GenerateQuestionsActivity`/`AnalyzeResponseActivity` are referenced ONLY by AssessmentWorkflow, either delete them or keep them behind an explicit `mockMode` (audit 7-2 AC7) defaulted off. Decide by `grep` for their references; record the choice. Don't leave them silently dead.

- [ ] **Step 7: Run the workflow tests + build → green.** Full `Tamma.Activities.Tests` + build 0 errors.

- [ ] **Step 8: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs apps/tamma-elsa/tests
git commit -m "feat(assessment): dispatch llm-call for questions+analysis, wire context+tenantId, real rationale"
```

---

## Risks

| Risk | Mitigation |
|---|---|
| Taxonomy drift test fails (action added to one side only) | Task 1 adds AgentAction + RolePhaseMap + SystemPrompts (+ conventions) atomically; Step 6 runs the drift tests. |
| Prompt resolution 422/errors on the new role+action | The action lives in `product_owner`'s RolePhaseMap set with a non-empty template; Task 1 Step 2 tests resolution. Role passed as canonical `product_owner`. |
| LLM returns unparseable JSON → fabricated/empty result | Parse defensively (slice like ContextGathering); on parse-fail or `success=false`, route to the Error/fail-closed path — never proceed with empty questions or fabricated confidence (the whole point — the heuristic's fabrication was the P0). |
| Wrong role home (product_owner vs senior_developer) | Confirm against MentorshipWorkflow's role usage in Task 1 Step 1; product_owner is the audit's analyst-alias target. |
| Deleting the fake activities breaks other callers | Task 2 Step 6 greps references first. |

## Acceptance criteria

1. `AssessmentWorkflow` generates questions and analyzes responses via `DispatchWorkflow("llm-call")` (role `product_owner`, the 2 new actions), not the heuristic activities; `ClassifyResult` is fed the real LLM confidence; `SetOutputResult` uses the real rationale; `context-gathering` output + `tenantId` are wired through.
2. The 2 new `(product_owner, action)` cells exist in `AgentAction` + `RolePhaseMap` + `SystemPrompts` (+ conventions if required); taxonomy-drift tests green; prompt resolution returns non-empty.
3. Fail-closed: `success=false` or unparseable LLM output routes to Error (no fabricated confidence / empty questions).
4. Build 0 errors; full `Tamma.Api.Tests` + `Tamma.Activities.Tests` green; **no schema change**. RAG explicitly deferred with a follow-up note.

## Self-review
- Spec coverage: audit P0 #1 (GenerateQuestions→llm-call) = Task 2 Step 3; #2 (AnalyzeResponse→llm-call) = Task 2 Step 4; #7 (StoreContextResult placeholder + hardcoded rationale) = Task 2 Steps 2/4. The prompt prerequisite = Task 1. RAG deferred (no seam).
- Coupling: the Shared-contract variable names bind Task 1's templates to Task 2's dispatch `variables` — keep them identical.
- Highest blast radius: Task 1 (the canonical AgentAction enum + the drift-guarded taxonomy) — gated behind the drift tests; Task 2's fail-closed parsing (the P0 fabrication fix).
