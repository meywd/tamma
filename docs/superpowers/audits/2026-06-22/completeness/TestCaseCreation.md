# Completeness Audit — `TestCaseCreationWorkflow`

**File:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs`
**Definition ID:** `test-case-creation`
**Audit date:** 2026-06-22
**Verdict:** **PARTIAL** — the LLM-generation half is genuinely the best-built in its sibling cluster (a real validate → `Tests Valid?` → bounded retry-with-error-feedback loop, with a distinct error-output path), and it correctly routes all model work through the `llm-call` sub-workflow (Epic-32 §1 mediation rule honored). **But its actual job, as named/described, is not done:** the workflow only emits a `testCasesJson` *string* — it never writes test files, never validates test syntax, never runs the tests to confirm they fail (the TDD RED gate), and never commits anything, despite both its own docstring and the parent cycle's comment claiming it "commits to the PR branch." Worse, the one output it does produce is **orphaned by its only caller** — `SingleIssueCycleWorkflow` captures it into the shared `subResult` var and then overwrites that var without ever reading the test cases; the real RED-phase tests are regenerated from scratch downstream by `ExecuteAgentActivity`. On top of that it shares the post-plan **`tenantId`-drop family defect** (P0 SaaS isolation): the parent passes `tenantId` and `conventions`, but Init reads neither.

> Note: the earlier `workflow-audit-triage.md` rated this workflow **GOOD (0 P0)**. That triage scored only the *generation* sub-machine (which is good) and predates the cross-workflow `tenantId` analysis surfaced in the `TaskCreation`/`SingleIssueCycle` completeness audits. Re-scored against **Story 2.5's acceptance criteria** and the actual consumer wiring, it is **PARTIAL** with real P0 gaps.

---

## 1. Purpose & owner

**Purpose (one line):** Produce the TDD **RED-phase** test cases for an issue — a `tester`-role LLM (prompt resolved from the Epic-27 registry, `role=tester` / `action=write-tests`) turns the decomposed `tasksJson` into test cases, the workflow extracts + validates the JSON (array, or `{testCases|tests:[...]}` object), retries up to `maxRetries` (default 2) feeding `validationErrors` back into the prompt, and returns `testCasesJson` on success or `testCasesJson:"[]"` + `error` on give-up.

**Owning epic/story:** This is the **"Write Failing Tests" step of the 14-step autonomous loop** — **Epic 2, Story 2.5 "Test-First Development – Write Failing Tests"** (`docs/stories/epic-2/story-2-5/`; epic README lists 2.5 as a core loop story). Story 2.5's `ITestFirstGenerator` contract is `generateTests → validateTestSyntax → executeTests → confirmTestsFail → organizeTestFiles`, and its acceptance criteria require generating **test files**, validating syntax, **executing tests to confirm they fail**, and **logging to the event trail**. The LLM mediation it rides on is owned by **Epic 32** (revised agent architecture, `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`): the `call-LLM` mediation (32-5) and tenant/BYOK threading (32-3/32-16), today fulfilled by `LlmCallWorkflow`.

**Consumer contract (from `SingleIssueCycleWorkflow.cs` lines 414-431):** input `{ repository, branchName, tasksJson, contextIds, conventions, tenantId }`; output `{ testCasesJson }` (or `{ testCasesJson:"[]", error }`). **Critical wiring gap:** the parent stores the result in the shared `subResult` variable (`SingleIssueCycleWorkflow.cs:429`) and the *next* read of `subResult` is at line 606 — the per-task TDD loop (lines 434-516) never reads the generated test cases. The real RED-phase test authoring is done by `ExecuteAgentActivity` (`tdd-cycle` / local-or-GitHub-Actions executor) using only `currentTaskJson` (`SingleIssueCycleWorkflow.cs:495-512`). So today this workflow's output is **generated and discarded** — it is, in effect, an unwired pre-pass.

---

## 2. Maturity: **PARTIAL**

The **generation sub-machine is real and well-built** — not a stub, not merely thin:

- Init → Generate Tests (`llm-call`) → Extract & Validate → `Tests Valid?` → on invalid, `Increment Retry` → `Can Retry?` → loop back to Generate Tests **with `validationErrors` now in the prompt variables** (genuine self-correction) → on exhaustion, a distinct `SetErrorOutputs` sequence.
- All model work is dispatched to `llm-call` (`DispatchWorkflow("llm-call")`, lines 81-101); the engine holds no provider key — the Epic-32 §1 boundary is honored. The retry loop here is the *workflow's own validation* retry and correctly does NOT duplicate `LlmCallWorkflow`'s provider-chain / circuit-breaker / budget machinery.
- The extractor is robust about shape: it pulls the outermost `[...]` array, else the outermost `{...}` object, and normalizes `{testCases:[...]}` / `{tests:[...]}` to the inner array (lines 118-171).

But it is **not complete against Story 2.5 or its own name/description**:

- **It generates only a JSON string of test *specs*, not test *files*** — Story 2.5 AC1 ("generates test files") and `organizeTestFiles()` are not implemented.
- **No `validateTestSyntax`** (AC5) — it validates JSON well-formedness, not that the emitted test code parses/compiles. (`TddWorkflow` does run `ValidateTestSyntaxActivity`; this workflow does not.)
- **No `executeTests` / `confirmTestsFail`** (AC6) — the defining act of a RED phase, asserting the new tests fail against the not-yet-written implementation, is absent. There is no dispatch to `testing-pipeline`.
- **No commit** — the docstring (line 40, "commit to PR branch") and the parent comment ("committed to PR branch", `SingleIssueCycleWorkflow.cs:412`) both promise a commit; the workflow performs none.
- **`tenantId` is dropped** (P0 SaaS isolation) — verified: `grep tenantId` returns **nothing** in this file, while `PlanGenerationWorkflow` threads it (lines 51/77/96). The parent passes `tenantId` (`SingleIssueCycleWorkflow.cs:426`); Init never reads it.
- **`conventions` is dropped** — parent passes `conventions` (`SingleIssueCycleWorkflow.cs:425`); Init never reads it and `generateTests` never forwards it.
- **No DCB events** (AC7) — verified: no `event`/`Dcb`/`Emit`/`Record…Event` reference in the file. Neither success nor give-up is in the audit trail.
- **Empty-fallback give-up** — on give-up it emits `testCasesJson:"[]"`, the same shape a "0 tests" success would take.

---

## 3. Current capabilities (what it does today)

- **Init** (`SetVariable "Init"`, lines 61-76): reads `repository`, `branchName`, `tasksJson` (default `"[]"`), `contextIds` (default `"[]"`), and optional `maxRetries` (default 2). **Does NOT read `tenantId`** or **`conventions`** (the parent passes both; both are dropped).
- **Generate Tests** (`DispatchWorkflow("llm-call")`, lines 81-101): `role=tester`, `action=write-tests`, `enableTools=true`; passes `variables { tasksJson, contextIds, repository, branchName, validationErrors }`. **Mediation-correct** (engine never calls a provider) **but tenant-blind** (no `tenantId` key → system-scope prompt/convention resolution + platform BYOK credential).
- **Extract & Validate** (`SetVariable "ExtractValidate"`, lines 107-177): pulls `llmResponse`; extracts outermost `[...]` array, else outermost `{...}` object; parses JSON; if object with `testCases`/`tests`, normalizes to the inner array; validates **only** that the array is present and non-empty (a non-empty array of empty objects passes), or the object has a recognized key. Sets `testsValid` + `validationErrors`.
- **Tests Valid?** (`FlowDecision` on `testsValid`, lines 183-185): True → `OutTestCases` (`SetOutput testCasesJson`) → `Finish`.
- **Retry loop** (`IncrRetry` → `CanRetry` on `retryCount < maxRetries`, lines 190-200): on invalid, increment then — if budget remains — loop **back to Generate Tests with the validation errors now in the prompt variables** (informed regeneration). Exhausted → `SetErrorOutputs`.
- **Outputs:** valid → `SetOutput testCasesJson`. Give-up → `Sequence` emitting `testCasesJson:"[]"` + `error = validationErrors`. Both reach a single `Finish`.

**Not present:** test-file materialization, syntax validation, test execution / RED-fail confirmation, commit, `tenantId`/`conventions` threading, DCB events, capture of the `llm-call` `success`/cost/tokens/provider outputs, distinction between "LLM failed" and "tests invalid."

---

## 4. Intended full scope (with citations)

1. **It is the RED phase of the TDD loop (Story 2.5).** `docs/stories/epic-2/story-2-5/2-5-*.md` user story: "write failing tests first (TDD red phase)." Its `ITestFirstGenerator` contract is `generateTests → validateTestSyntax → executeTests → confirmTestsFail → organizeTestFiles`. AC1 "generates **test files**", AC2 "tests written to fail initially", AC3 "follows project conventions and testing framework", AC4 "cover edge cases, error conditions, happy paths", AC5 "validates test **syntax and structure**", AC6 "test execution **confirms tests fail as expected**", AC7 "test generation and execution **logged to event trail**." The current workflow satisfies (a sub-part of) AC2/AC4 implicitly via the prompt, and nothing else materially.
2. **The TDD RED gate is the load-bearing step.** `docs/architecture.md` (14-step loop / red-green-refactor) and the sibling `TddWorkflow` show the canonical RED machine: `WriteTests → ValidateTestSyntax → (guard) → DispatchWorkflow("testing-pipeline") → CheckTestsFailActivity` with a "TestsPass-when-they-should-fail" rewrite loop (`TddWorkflow.cs:99-204`). A complete `test-case-creation` either (a) does the same — write files, validate syntax, run via `testing-pipeline`, assert RED, commit — or (b) is explicitly redefined as a *spec-only pre-pass* whose output is actually consumed by the downstream executor. Today it is neither: it produces specs that are then discarded.
3. **Its output must actually be consumed (no dead steps).** `SingleIssueCycleWorkflow.cs` wires `createTestCases.Result → subResult` (line 429) but the per-task TDD loop (lines 434-516) feeds `ExecuteAgentActivity` only `currentTaskJson` (line 504) and never reads `subResult` until line 606 (`code-review`). A complete design feeds the validated `testCasesJson` into the per-task executor (as test scaffolding/context) so the generated tests are not thrown away — otherwise this whole step is wasted LLM spend.
4. **Tenant-scoped resolution is mandatory in SaaS (the "two scoping models" rule).** `CLAUDE.md` §"Universal rule for any tenant-aware feature": every feature must answer who owns it in single-user **and** SaaS mode; prompt resolution (Epic 27) and BYOK (32-3/32-16) are keyed by `tenantId`. `PlanGenerationWorkflow` threads it (`PlanGenerationWorkflow.cs:51/77/96`); this workflow does not. With `tenantId` empty, `ResolveConventionsActivity` resolves **system** conventions (`ResolveConventionsActivity.cs` logs `source = system` when tenantId is empty) and `ResolvePromptFromRegistryActivity` resolves the **system** prompt; a BYOK tenant's call is keyed/billed to the **platform**. This violates `MEMORY` `feedback_resolution_no_empty_fallback` (resolution is tenant→system→error) by collapsing the tenant tier entirely.
5. **Audit trail / DCB events (architecture mandate + AC7).** `docs/architecture.md` Logging Requirements + `CLAUDE.md` §"Emitting Events for Audit Trail": every operation emits a DCB event (`AGGREGATE.ACTION.STATUS`). Story 2.5 AC7 explicitly requires "test generation and execution logged to event trail." A complete RED step emits **`TEST.GENERATION.SUCCESS`** (tags `issueId/issueNumber/tenantId/provider`; data `testCaseCount/retryCount/costUsd/tokensUsed/durationMs`) and **`TEST.GENERATION.FAILED`** (data `reason: invalid-after-retries | llm-failure`, `validationErrors`, `retryCount`); if it runs tests, also **`TEST.RED.CONFIRMED`** / **`TEST.RED.UNEXPECTED_PASS`**. Today: zero events.
6. **Cost / usage metering must not be discarded.** `LlmCallWorkflow` returns `success`, `providerUsed`, `costUsd`, `tokensUsed`, `workflowOutput` (`LlmCallWorkflow.cs:583-681`); the revised agent architecture (`2026-06-20-epic-32-revised-agent-architecture.md` §0 rule 2(e)) makes metering a first-class output, consumed by Epic 36 analytics + 32-9 usage events. This workflow reads only `llmResponse` (line 115) and discards the rest, so the test-generation step is invisible to cost analytics.
7. **No-false-success / no-silent-failure.** Project rule (`MEMORY` `feedback_resolution_no_empty_fallback`; `CLAUDE.md`). Give-up emits `testCasesJson:"[]"` — an empty fallback indistinguishable from "0 tests" — and a hard `llm-call` failure (sub-workflow `success=false`, empty `llmResponse`) currently degrades into the **same** "Empty test cases output" validation error, burning retries re-prompting a dead provider chain rather than surfacing the fault on a distinct edge.
8. **Mediated, tenant/BYOK-correct LLM path (Epic 32).** §1 of the pivot spec: a STEP MUST NEVER call an external provider directly. The mediation half is **honored** (dispatch to `llm-call`); the tenant/BYOK half is **not** (no `tenantId`).
9. **Conventions / framework fidelity (AC3).** Tests must "follow project conventions and testing framework." `llm-call` resolves conventions from the store via `ResolveConventionsActivity`, but only when given the correct `tenantId`/`action`; the parent's `conventions` passthrough is dropped here, so self-hosted `.tamma/config.json` users and the empty-action passthrough path lose their convention string.

---

## 5. Missing capabilities (gap to complete)

| # | Missing capability | Priority | Depends on |
|---|---|---|---|
| 1 | **Thread `tenantId` end-to-end.** Read `ctx.GetInput<string>("tenantId")` in Init and add `["tenantId"] = tenantId.Get(ctx)` to the `generateTests` `llm-call` Input (exactly as `PlanGenerationWorkflow.cs:77/96`). Without it, SaaS tenants silently get **system** prompts/conventions and the **platform** BYOK credential — tenant-isolation + billing defect. | **P0** | 32-3/32-16 (already implemented; just not wired here) |
| 2 | **Consume the output (kill the orphan).** The generated `testCasesJson` is captured into `subResult` and then discarded by the parent before the TDD loop runs. Either (a) feed the validated test cases into `ExecuteAgentActivity`/`tdd-cycle` as RED scaffolding/context so they are actually used, or (b) have *this* workflow materialize+run+commit the tests. As-is the step is pure wasted LLM spend. | **P0** | parent `SingleIssueCycleWorkflow` change |
| 3 | **Emit DCB events.** `TEST.GENERATION.SUCCESS` on valid (tags `issueId/issueNumber/tenantId/provider`; data `testCaseCount/retryCount/costUsd/tokensUsed/durationMs`) and `TEST.GENERATION.FAILED` on give-up/fault (data `reason`, `validationErrors`, `retryCount`). Required by architecture + Story 2.5 AC7. Today: none. | **P0** | none (reuse codebase event-emit pattern) |
| 4 | **Distinguish "LLM call failed" from "tests invalid."** Read the sub-workflow `success` output; on `success=false` (all providers failed / circuit-open / budget exhausted), take a **distinct terminal-fault edge** — do not consume a validation retry re-prompting a dead chain — and surface a fault `error` so the parent can route to needs-human, not "bad tests." | **P0** | 32-5 (`llm-call` already returns `success`/diagnostics) |
| 5 | **Stop the empty-fallback false-success.** On give-up/fault, do not emit a parseable `testCasesJson:"[]"` as the sole signal. Add an explicit `testGenerationFailed=true` + non-empty `error`, and have the parent gate the RED/TDD path on it (don't silently proceed with no tests). | **P0** | parent `SingleIssueCycleWorkflow` change |
| 6 | **Validate test *syntax/structure* (Story 2.5 AC5).** Beyond JSON well-formedness, validate the emitted test code parses/compiles (mirror `ValidateTestSyntaxActivity` used by `TddWorkflow`), and validate per-test-case shape (`name`, `targetFile`, `testType`, `code`/body) — reject `[{}]`. Feed specific errors into the retry. | **P1** | none (activity exists: `Tamma.Activities.TDD.ValidateTestSyntaxActivity`) |
| 7 | **Materialize test files + run them to confirm RED (Story 2.5 AC1/AC6).** If this workflow owns the RED gate (vs. delegating to `tdd-cycle`), write the test files to the branch, dispatch `testing-pipeline`, assert the new tests **fail** (`CheckTestsFailActivity`-style guard), and only then commit. If the loop's downstream `tdd-cycle` is intended to own this, redefine this workflow's scope to "spec pre-pass" and resolve via #2 — but do not leave the docstring's "commit to PR branch" promise unfulfilled. | **P1** | testing-pipeline (`testing-pipeline` workflow) + Epic 38 mediation for git ops |
| 8 | **Stop discarding cost/tokens/provider.** Capture `providerUsed`, `costUsd`, `tokensUsed`, `workflowOutput` from the `llm-call` result; include them in the success DCB event and surface as workflow outputs so the cycle/analytics can meter the RED step. | **P1** | Epic 36 (analytics) / 32-9 (usage events); sub-workflow already returns the values |
| 9 | **Thread `conventions` (AC3).** Read `ctx.GetInput<string>("conventions")` in Init and forward it to `llm-call` for parity with the empty-action passthrough path and self-hosted `.tamma/config.json` users. | **P2** | none |
| 10 | **Coverage / completeness check vs. tasks.** Validate that the generated test cases actually cover the input `tasksJson` (≥1 test per task, edge/error/happy-path per AC4) rather than just "array non-empty," feeding specific gaps back into the retry. | **P2** | depends on #6 (per-case shape) |
| 11 | **Idempotency / regeneration guard.** A re-run (cycle replay, task-review `needsChanges` loop) regenerates from scratch with no correlation; emit a stable `testSetId` (issue+revision) and tag events with it. | **P3** | DCB events (#3) |

---

## 6. Ordered build-out spec (to reach complete + robust)

Ordered so prerequisites land first. All changes honor: **steps never call providers directly** (keep dispatching `llm-call`; route any git/test ops via activities/`testing-pipeline`/Epic-38 mediation, never inline provider calls), **tenant→system→error** (never empty/plain), **no false-success / no silent failure**, **emit DCB events**.

1. **Wire `tenantId` + `conventions` (P0 #1, P2 #9).** In `Init` add `tenantId` and `conventions` string variables (default `""`) and read them from input (`ctx.GetInput<string>("tenantId")`, `ctx.GetInput<string>("conventions")`). In `generateTests` Input add `["tenantId"] = tenantId.Get(ctx)` and `["conventions"] = conventions.Get(ctx)`. *Outcome:* SaaS tenants resolve their own `write-tests` prompt/conventions + BYOK credential. *Verify:* `llm-call`'s `ResolveConventionsActivity` logs `source=<tenantId>`, not `system`.

2. **Capture the mediated-call result fully (P0 #4 / P1 #8).** In `ExtractValidate`, after reading `llmResponse`, also read `success` (bool), `providerUsed`, `costUsd`, `tokensUsed` from `llmResult` into new variables (`llmSucceeded`, `providerUsed`, `costUsd`, `tokensUsed`). These feed the new fault branch and the success event.

3. **Add the LLM-fault branch (P0 #4).** Insert a `FlowDecision "LlmSucceeded"` immediately after `generateTests`, before `ExtractValidate`: `ctx => llmSucceeded.Get(ctx)`.
   - **True** → `ExtractValidate` (existing path).
   - **False** → new `SetVariable "MarkLlmFault"` setting `validationErrors = $"LLM call failed (provider chain exhausted): {providerUsed}"` + a `llmFault=true` flag → go straight to the failure-output sequence (step 6), **skipping the validation-retry loop** (don't re-prompt a dead chain).

4. **Tighten validation to per-test-case shape + syntax (P1 #6, P2 #10).** Replace the "array non-empty" check with a shared validator: require each test case to have `name`, `targetFile`, `testType`, and a non-empty test body; require ≥1 test case per input task (parse `tasksJson` for the expected set) and presence of edge/error/happy-path coverage (AC4). Then add a `ValidateTestSyntaxActivity` step (reuse `Tamma.Activities.TDD.ValidateTestSyntaxActivity`) so emitted test code actually parses; on syntax failure, append errors to `validationErrors` (best-effort/skip when no compiler on PATH, as `TddWorkflow` does). Accumulate specific messages so the retry feedback is actionable.

5. **Resolve the orphan + (optionally) own the RED gate (P0 #2, P1 #7).** Decide and implement one:
   - **(a) Spec pre-pass:** surface the validated `testCasesJson` as a real output and change `SingleIssueCycleWorkflow` to thread it into the per-task `ExecuteAgentActivity` (e.g. as `AgentConfigJson`/context) so the downstream `tdd-cycle` writes *these* tests instead of regenerating. *Outcome:* the LLM spend is used.
   - **(b) Full RED owner:** after validation, materialize test files to the branch (file-write activity), dispatch `testing-pipeline`, assert RED via a `CheckTestsFail`-style guard (route an unexpected pass back through the rewrite loop), and commit (via the commit activity / Epic-38 git mediation). Update the docstring to match whichever is chosen — today it promises a commit it never performs.

6. **Replace the empty-fallback failure output (P0 #5).** Change the give-up/fault `Sequence` to emit a non-deceptive contract: keep `error = validationErrors` (now specific) and **add** `SetOutput "testGenerationFailed" = true`. *Parent change (separate report — SingleIssueCycle):* gate the RED/TDD path on `testGenerationFailed`; route a true to needs-human/abort instead of silently proceeding with no tests.

7. **Emit DCB events (P0 #3).** Add an event-emit activity on each terminal edge:
   - Valid edge (before `Finish`): **`TEST.GENERATION.SUCCESS`** — tags `{ issueId/issueNumber, tenantId, provider: providerUsed }`, data `{ testCaseCount, retryCount, costUsd, tokensUsed, durationMs }`. If step 5(b) ran tests: also **`TEST.RED.CONFIRMED`** (or **`TEST.RED.UNEXPECTED_PASS`**).
   - Give-up edge: **`TEST.GENERATION.FAILED`** — data `{ reason: "invalid-after-retries", validationErrors, retryCount }`.
   - LLM-fault edge (step 3): **`TEST.GENERATION.FAILED`** — data `{ reason: "llm-failure", provider: providerUsed, validationErrors }`.
   Include a stable `testSetId` tag for correlation (#11).

8. **Surface metering outputs (P1 #8).** On the valid edge add `SetOutput`s for `providerUsed`, `costUsd`, `tokensUsed` so the cycle and Epic-36 analytics can meter the RED step (parity with the `llm-call` output contract).

9. **(P3 #11) Idempotency tag.** Compute `testSetId = $"{issueNumber}-rev{revisionNumber}"` (thread `revisionNumber` from the parent) and tag all events + the success output with it, so regenerations and cycle replays correlate cleanly.

---

## 7. Cross-workflow note

`TestCaseCreationWorkflow`, `TaskCreationWorkflow`, and `TaskReviewWorkflow` **all** drop `tenantId` (verified: `grep tenantId` returns nothing in any of the three), whereas `PlanGenerationWorkflow` threads it. The `tenantId`-drop (gap #1) is a **family defect** across the post-plan creation/review workflows — fix consistently across the three with the same Init/`llm-call`-Input pattern. The DCB-event (#3), LLM-fault-distinction (#4), empty-fallback (#5), and metering (#8) gaps are likewise shared and should reuse the same shared helper/event pattern once built. The **orphaned-output** gap (#2) is specific to this workflow's wiring in `SingleIssueCycleWorkflow` and is the single most wasteful issue — the step currently spends an LLM call whose result is thrown away.
