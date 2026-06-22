# Workflow Audit — Agent / LLM-Execution workflows (2026-06-22)

Cluster: the pivot's core blast radius. Auditor read each `*Workflow.cs` plus the activities it
composes, and cross-checked against the pivot design of record
(`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`, §0 locked rules, §1
audit table, §2 `call-LLM` endpoint, §5 phasing). Branch `docs/workflow-structural-audit`.

**Key pivot fact used throughout:** the 9 known in-engine direct-LLM callers
(`CallLlmActivity`, `CallLlmInlineActivity`, `ClaudeAnalysisActivity`, `WriteTestsActivity`,
`WriteImplementationActivity`, `AnalyzeCodeActivity`, `ApplyRefactoringActivity`,
`ApplyReviewFixesActivity`, `AIDiagnosisActivity`) each still hold an in-engine keyed path
(`_httpClientFactory.CreateClient("anthropic")` → `PostAsJsonAsync("/v1/messages")` reading
`Anthropic:ApiKey`, or `CallLlmInlineActivity` resolving the credential itself). Verified present at
HEAD. Per §2.5 / §5.2, these are removed/repointed to `POST /api/v1/llm/call` by **story 32-5**.
Findings about a workflow needing rework only because it composes one of these are marked
**Depends on: 32-5 caller-cutover** — the *workflow graph* is fine; the *activity* is the violator.
Per the brief, no recommendation touches `LlmCallWorkflow`'s retry / provider-chain / circuit-breaker
boundary (32-5 preserves it).

## Summary
- **LlmCallWorkflow** — NEEDS-WORK — 1 P0, 2 P1, 2 P2 (centralized LLM path; the chokepoint `CallLlmInlineActivity` is the worst in-engine key holder)
- **TddWorkflow** — NEEDS-WORK — 4 P0, 1 P1, 1 P2 (4 of its activities call the LLM directly, bypassing the `llm-call` path entirely)
- **TddWithDebugRetryWorkflow** — GOOD — 0 P0, 0 P1, 1 P2 (pure dispatch orchestrator; no direct LLM/API; sound loop)
- **CodeReviewWorkflow** — NEEDS-WORK — 1 P0, 1 P1, 1 P2 (PR create/merge git activities are co-hosting violations; no LLM step)
- **ReviewFixWorkflow** — NEEDS-WORK — 2 P0, 1 P1, 1 P2 (correctly dispatches `llm-call`, but `ApplyReviewFixesActivity` carries a 2nd direct-LLM path + git co-hosting)
- **DebuggingWorkflow** — NEEDS-WORK — 1 P0, 1 P1, 1 P2 (`AIDiagnosisActivity` direct-LLM with NO simulated fallback; ApplyFix correctly routes via `llm-call`)
- **BlockerDiagnosisWorkflow** — NEEDS-WORK — 0 P0, 2 P1, 2 P2 (all LLM via `llm-call` — compliant; but escalation never resolves and signal collectors are git co-hosting)
- **AdlOrchestratorWorkflow** — GOOD — 0 P0, 1 P1, 1 P2 (orchestration only; downstream cycles hold the git/agent-dispatch violations)

**Totals: P0 = 9, P1 = 9, P2 = 10.**

---

## LlmCallWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`)
- **Purpose / owner story:** The universal LLM building block — provider chain + circuit breaker +
  retry + budget + 6-level prompt resolution + tool loop. Owner **Story 7-2** (`done`). Still
  central and needed; it is the path every other workflow should funnel through. **It IS the
  `call-LLM` mediation boundary** that 32-5 keeps.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P0] Architecture/pivot — The retry-loop body invokes `CallLlmInlineActivity` (`CallLlm`) in-engine
    at `LlmCallWorkflow.cs:885-899`. Per §1.2/§1.3 this activity is the **primary in-engine key
    holder** (resolves the credential + `PostAsync(".../v1/messages")` inside the engine process).
    The workflow graph stays as-is (32-5 preserves the chain/retry/CB boundary); the activity becomes
    a ~80-line `TammaApiClient.CallLlmAsync` shim that returns `LastDiagnostic`/`LastResponse`
    unchanged. **Fix:** repoint `CallLlmInlineActivity` to `POST /api/v1/llm/call`; do NOT change this
    workflow's loop structure. **Depends on: 32-5 caller-cutover.**
  - [P1] Architecture/pivot — Provider chain still resolved from a hard-coded default
    `["anthropic","openai","openrouter"]` (`:326`, `:369`) + caller/DB chain, with no notion of the
    new **persona / per-tenant enablement** model (rules 4–6). Post-pivot the agent/persona resolves
    provider+model server-side in `call-LLM`; the chain here should be sourced from the resolved
    persona/agent, not a literal. **Fix:** once 32-5 lands, have the request carry `persona`/`agentId`
    + `role` and let the endpoint resolve provider+model; treat the local chain as a transitional
    fallback only. **Depends on: 32-5, 32-2 (enablement), 32-15/32-16.**
  - [P1] Architecture/pivot — `ResolveAgentConfigActivity` (`:298`) resolves the agent's
    system prompt/chain in-engine. Under the locked model the **persona's prompts come from Epic 27**
    and config resolution + enablement gate move into `call-LLM` (§2.6 step 2). This in-engine resolve
    becomes redundant/duplicative once mediation lands. **Fix:** after 32-5, drop the in-engine agent
    resolve and let the endpoint own it (keep only input marshalling). **Depends on: 32-5.**
  - [P2] Structural — `SetupBudget` (`:284-294`) parses `BudgetCapUsd` from `inputVar` (the legacy
    `InputJson`); on the new typed-input path `inputVar` is `""`, so `ParseInput` returns a default and
    the cap is silently `0` (treated as "no cap"). Not a regression of the pivot, but a real latent gap.
    **Fix:** read `budgetCapUsd` from the typed inputs too (mirror how `tenantId`/`action` are read in
    `InitInputs`).
  - [P2] Event emission — This workflow emits no DCB `AGGREGATE.ACTION.STATUS` events itself; diagnostics
    are accumulated into `DiagnosticsListJson` and returned as output. Per §2.6 the
    `AGENT.RUN.*` / `AGENT.CREDENTIAL_RESOLVED.*` / `AGENT.PROVIDER.GATED` events are emitted from
    `Tamma.Api` (correct home). **Fix:** none in the engine — confirm the endpoint emits them; note it
    here so the enhancement phase doesn't add engine-side emission. **Depends on: 32-5 / 32-9.**
- **Depends on:** 32-5 caller-cutover (primary); 32-2/32-15/32-16 for persona+enablement sourcing.

## TddWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWorkflow.cs`)
- **Purpose / owner story:** Red-green-refactor cycle for one task. Owner **Story 7-8** (`done`); RED
  phase = 2-5, GREEN = 2-6. Still needed. Structurally the richest workflow in the cluster and largely
  sound, BUT it reaches the LLM the wrong way.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P0] Architecture/pivot — `WriteTestsActivity` (`:99`) is invoked **as a direct activity, not via
    `llm-call`**, and that activity has an in-engine keyed fallback
    (`TDD/WriteTestsActivity.cs:110` → `CreateClient("anthropic")` → `:213 PostAsJsonAsync("/v1/messages")`).
    **Fix:** delete the activity's direct keyed fallback and route its LLM call through `call-LLM`
    (its engine-callback branch terminates at the mediated path). **Depends on: 32-5 caller-cutover.**
  - [P0] Architecture/pivot — `WriteImplementationActivity` (`:209`) — same pattern
    (`TDD/WriteImplementationActivity.cs:177/188`). **Fix:** as above. **Depends on: 32-5.**
  - [P0] Architecture/pivot — `AnalyzeCodeActivity` (`:271`) — same pattern
    (`TDD/AnalyzeCodeActivity.cs:178/189`). **Fix:** as above. **Depends on: 32-5.**
  - [P0] Architecture/pivot — `ApplyRefactoringActivity` (`:301`) — same pattern
    (`TDD/ApplyRefactoringActivity.cs:173/184`). **Fix:** as above. **Depends on: 32-5.**
  - [P1] Per-mode/tenant scoping — Unlike `TddWithDebugRetryWorkflow` (which threads `tenantId` into the
    cycle), `TddWorkflow` itself never captures/threads a `tenantId`; its 4 LLM activities therefore
    can't BYOK-resolve per tenant once mediated. **Fix:** capture `tenantId` from input (mirror the
    other INIT assigns at `:83-89`) and pass it to the LLM activities so `call-LLM` resolves the
    tenant's credential. **Depends on: 32-3 BYOK + 32-5.**
  - [P2] Structural — `dispatchTestsRefactor`'s `RevertRefactoring` path (`:598-599`) reverts then
    commits unconditionally; on revert there is no re-run/verify before `commitChanges`. Minor —
    reverted state should equal the green GREEN-phase state — but worth a guard comment. **Fix:**
    document the invariant or add a post-revert verify; low priority.
- **Depends on:** 32-5 caller-cutover (4 activities); 32-3 for tenant-scoped BYOK.

## TddWithDebugRetryWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs`)
- **Purpose / owner story:** Wraps `tdd-cycle` with up to N debug retries, dispatching `debugging`
  between attempts. Owner **Story 13-1** (`done`). Pure orchestrator — it composes only
  `DispatchWorkflow` + `SetVariable` + `FlowDecision`; it holds NO activity that touches a provider.
- **Health:** GOOD
- **Findings:**
  - [P2] Naming/convention — `using Tamma.Api.Services.Agents;` is absent here (it doesn't need the
    enums) — fine. No issues; the loop (`tddCycle → tddSuccess → tddDebugGuard → increment → dispatch
    debugging → loop`) terminates correctly via `maxRetries` and both finish branches are wired
    (`:223-238`). One nit: the success-finish sets `errorMessage=""` (`:176`) while the doc-comment
    says outputs are `{success, errorMessage}` — consistent, fine. **Fix:** none required; this is the
    reference shape for "engine holds no keys" orchestration. Its compliance is inherited entirely from
    the child workflows (`tdd-cycle`, `debugging`), so its real exposure is whatever those carry.
- **Depends on:** (transitively) 32-5 via `tdd-cycle` and `debugging`.

## CodeReviewWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs`)
- **Purpose / owner story:** Full PR lifecycle — create → request review → monitor (bookmark) →
  approve/merge OR changes→guidance→wait→re-request (max 5) → escalate. Owner **Story 7-4** (`done`).
  No LLM step at all — it is a git-platform + bookmark workflow. Still needed.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P0] Architecture/pivot — `CreatePRActivity` (`:65`), `MergeAndCompleteReviewActivity` (`:194`),
    `RequestReviewActivity`/`ReRequestReviewActivity`, `EscalateReviewActivity` reach the git platform
    through `IGitHubIntegrationService` co-hosted in the engine (§1.2 Class A,
    "VIOLATION-by-co-hosting" — a mis-scoped token = cross-tenant write/merge once the engine isn't
    co-hosted with `Tamma.Api`). **Fix:** route PR create/merge/issue-status through the Class-A git
    endpoints (`POST /api/v1/git/{repo}/pull-requests`, `PUT .../{n}/merge`, …) via `TammaApiClient`.
    **Depends on: Epic 38 (non-LLM step mediation, §5.1 Class A).**
  - [P1] Structural — `MonitorReview`'s `"Commented"` outcome loops straight back to `MonitorReview`
    (`:306`) with no iteration bound or backoff; a stream of `Commented` webhooks can spin the bookmark
    re-arm indefinitely. The 24h timeout caps wall-clock but not the loop count. **Fix:** add a
    comment-iteration guard or coalesce `Commented` into the existing 24h timeout window.
  - [P2] Naming/convention — Most activities here lack `.SetDisplayText` symmetry with other workflows?
    No — they all set it. The real nit: `failedEnd` emits a generic `"Code review failed"` error
    (`:243`) even for the `prCreatedCheck=False` path, losing the cause. **Fix:** carry the
    `CreatePRActivity` failure reason into the output.
- **Depends on:** Epic 38 (Class A git mediation).

## ReviewFixWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ReviewFixWorkflow.cs`)
- **Purpose / owner story:** Analyze PR review comments → generate fixes via LLM → apply. Owner
  **Story 2-18** (`in-progress` — "ReviewFixes de-stubbed; …ACs still unmet"). Still needed.
  Notably it does TWO things to reach the LLM: it correctly dispatches `llm-call` (`:49-62`), then
  feeds the result into `ApplyReviewFixesActivity` which *itself* can call the LLM again directly.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P0] Architecture/pivot — `ApplyReviewFixesActivity` (`:65`) carries the in-engine keyed fallback
    (`ADL/ApplyReviewFixesActivity.cs:120 CallLlm` → `:222 PostAsJsonAsync("/v1/messages")` reading
    `Anthropic:ApiKey`). This is a 2nd, un-mediated LLM path **in addition to** the compliant `llm-call`
    dispatch the workflow already does. **Fix:** delete the activity's direct keyed fallback; the
    activity should consume the `LlmFixResponse` it's already given (from the `llm-call` dispatch) and
    only apply edits — or, if it must call the model, route via `call-LLM`. **Depends on: 32-5.**
  - [P0] Architecture/pivot — `AnalyzeReviewActivity` (`:35`) reads PR review comments via
    `IGitHubIntegrationService` co-hosted in the engine (§1.2 Class A, AnalyzeReview =
    "VIOLATION-by-co-hosting"). **Fix:** route via `GET /api/v1/git/{repo}/pull-requests/{n}/comments`.
    **Depends on: Epic 38 (Class A).**
  - [P1] Structural — The `hasActionable=False` branch jumps straight to `outputSuccess` (`:120`) and
    reports `success=true` with `fixesApplied=false`. That's defensible (nothing to do), but the
    workflow emits the same `success=true` regardless of whether `ApplyFixes` actually succeeded —
    `fixesAppliedVar` is reported but never gates `success`. **Fix:** when `hasActionable=true` but
    `fixesApplied=false`, surface `success=false` (or a distinct `partial` reason) so the audit trail
    doesn't read a failed apply as success (fail-closed, not silent-success).
  - [P2] Naming/convention — `using Tamma.Api.Services.Agents;` (`:14`) pulls in `AgentRole`/`AgentAction`
    which actually live in `Tamma.Core/Agents/` under that legacy namespace (Story 27-19). Harmless (no
    `Tamma.Api` project ref — ElsaServer references only `Tamma.Activities`), but the `using` reads like
    an engine→API layering breach. **Fix:** consider renaming the namespace to `Tamma.Core.Agents`
    (cluster-wide; tracked under 27-19's TODO).
- **Depends on:** 32-5 (ApplyReviewFixes direct path); Epic 38 (AnalyzeReview git read).

## DebuggingWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs`)
- **Purpose / owner story:** Systematic AI debugging — classify → parallel context gather (fork/join) →
  AIDiagnosis → hypothesis loop (max 5: select → apply fix → run tests → resolve/refine). Owner
  **Story 7-9** (`done`). Still needed. Structurally strong: real `FlowFork`/`FlowJoin(WaitAll)`,
  bounded loop, escalation sink.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P0] Architecture/pivot — `AIDiagnosisActivity` (`:288`) is invoked directly and reaches the LLM
    in-engine; per §1.2 it is the **only** direct-LLM caller with **NO simulated fallback** —
    engine-callback then **direct `/v1/messages`** (`Debug/AIDiagnosisActivity.cs:242 CreateClient("anthropic")`
    → `:253 PostAsJsonAsync("/v1/messages")`). **Fix:** delete the direct path; route via `call-LLM`.
    Note the `applyFix` step (`:373-387`) already correctly dispatches `llm-call` — only the diagnosis
    step is the violator, so the fix is localized to the activity. **Depends on: 32-5.**
  - [P1] Structural — The `selectHypothesis`/`hasHypothesis=False` branch (`:636`) routes to
    `compileReport` (escalate) which is correct, but the loop bound is enforced **inside**
    `SelectHypothesisActivity` (via `CurrentIteration`/`MaxIterations`) rather than by a workflow-level
    guard. If `SelectHypothesis` ever returns a non-null hypothesis past `MaxIterations`, the loop
    (`incrementIteration → selectHypothesis`, `:665`) has no independent flowchart stop. **Fix:** add a
    flowchart `FlowDecision(currentIteration > maxIterations) → compileReport` before re-select, so loop
    termination is guaranteed by the graph, not only the activity.
  - [P2] Event emission — Resolution/escalation are surfaced only via `SetOutput` + `WriteLine`
    (`:465-468`, `:542-545`); no `DEBUG.RESOLVED.*` / `DEBUG.ESCALATED.*` DCB event is emitted from the
    workflow. `RecordResolutionActivity` may persist internally — confirm — but the audit trail would
    benefit from explicit terminal events. **Fix:** verify `RecordResolutionActivity`/`CompileDebugReport`
    emit DCB events; if not, add them.
- **Depends on:** 32-5 (AIDiagnosis direct path).

## BlockerDiagnosisWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs`)
- **Purpose / owner story:** Diagnose a junior's blocker (parallel signals → AI diagnosis → progressive
  Hint→Guidance→Assistance→Escalation). Owner **Story 7-7** (`done`). Still needed. **Best pivot
  citizen of the cluster: every LLM call already goes through `llm-call`** (`:181`, `:402`, `:509`,
  `:616`) with `AgentRole`/`AgentAction` `.ToWire()` keys — no in-engine LLM key.
- **Health:** NEEDS-WORK
- **Findings:**
  - [P1] Structural (control flow) — Progressive resolution is 4 sequential `If` activities connected
    unconditionally (`hintLevel → guidanceLevel → assistanceLevel → escalationLevel → setOutput`,
    `:354-364`). Each level's `If` guards on `!isResolved`, so when an early level resolves, later
    levels no-op — correct. BUT the **Escalation** level's `EscalateToSenior` bookmark wait
    (`:722-734`) does NOT set `isResolved` on senior resolution; `setOutput` therefore reports
    `Status = Escalated` even when the senior resolved the blocker (`:303-305` keys solely off
    `isResolved`, which escalation never flips). **Fix:** have the escalation body set `isResolved`
    from the `EscalateToSenior` outcome (resolved vs rejected) so the output reflects a senior-resolved
    blocker rather than always reporting `Escalated`.
  - [P1] Architecture/pivot — Signal collectors `CollectGitActivityActivity` (`:112`) and
    `CollectCIStatusActivity` (`:120`) read git/CI state; to the extent they hit the git platform
    directly (co-hosted service) they fall under §1.2 Class A "VIOLATION-by-co-hosting". `ClassifyBlocker`
    is local (no LLM — verified). **Fix:** if the collectors touch the git platform, route via the
    Class-A git read endpoints. **Depends on: Epic 38 (Class A).**
  - [P2] Structural — `DetermineStartLevel` (`:222-233`) computes a start level ("Guidance" for skill
    ≤2 to skip Hint) but the Hint `If` already guards on `currentLevel == "Hint"` (`:470`), so the
    Guidance level still runs for low-skill juniors via its own `Set Level: Guidance` — works, but the
    `determineStartLevel` write is partially redundant with the per-level `Set Level` writes. **Fix:**
    consolidate level transitions in one place to avoid the two-source-of-truth on `currentLevel`.
  - [P2] Naming/convention — `BuildDiagnosisPrompt` builds a large prompt string in the workflow file
    (`:767-824`); per the pivot, prompt bodies should come from the Epic 27 store (`role`+`action`),
    not be hand-assembled in the engine. The signals summary is data (fine to pass as a variable), but
    the classification instructions ("Classify into one of: …", "Return JSON with…") are prompt content
    that belongs in the prompt store. **Fix:** move the instruction scaffold to the Epic 27
    `senior-developer`+`resolve-blocker` template; pass only the signal data as `{{variables}}`.
    **Depends on: Epic 27 template + 32-5 prompt sourcing.**
- **Depends on:** Epic 38 (signal collectors, if git-bound); Epic 27 (prompt sourcing).

## AdlOrchestratorWorkflow  (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs`)
- **Purpose / owner story:** Top-level loop — load config → select work item → triage/limits →
  dispatch single-issue cycle (fire&forget) → cooldown → re-dispatch itself. Owner cluster **Epic 2**
  (2-1 issue selection `done`, 2-11 auto-next `done`). Still needed. Holds NO LLM step and no direct
  external call in the workflow itself — it's pure dispatch/scheduling.
- **Health:** GOOD
- **Findings:**
  - [P1] Architecture/pivot — `SelectWorkItemActivity` (`:79`) and `DispatchTriageActivity` (`:94`)
    read GitHub issues; to the extent they hit the git platform directly (co-hosted
    `IGitHubIntegrationService`) they are §1.2 Class A "VIOLATION-by-co-hosting". The orchestrator graph
    is sound (every outcome wired: `NothingFound`/`NeedsTriage`/`Selected` and `Stop`/`Continue`, all
    funnel to cooldown → re-dispatch → finish). **Fix:** route issue reads/selection through the
    Class-A git endpoints. **Depends on: Epic 38 (Class A).**
  - [P2] Structural — Self-re-dispatch via `DispatchAdlActivity` (`:161`) is an intentional tail-loop
    (each instance dispatches the next and finishes), which is sound for Elsa but makes the run history
    a chain of single-iteration instances. Not a defect; note for observability so the dashboard
    stitches the chain. **Fix:** none required; document the chaining for the event-trail UI.
- **Depends on:** Epic 38 (Class A git for issue selection/triage). The biggest LLM/agent exposure lives
  in the dispatched single-issue cycle (which composes TDD/CodeReview/Debugging), not here.

---

## Cross-cutting observations (patterns shared across this cluster)

1. **Two distinct ways these workflows reach the LLM — only one is compliant.**
   - *Compliant (route via `llm-call` sub-workflow):* BlockerDiagnosis (all 4 levels + diagnosis),
     ReviewFix's `generateFixes`, Debugging's `applyFix`. These need **no graph change** — they
     inherit mediation the moment `CallLlmInlineActivity` (inside `llm-call`) is repointed by 32-5.
   - *Non-compliant (invoke a direct-LLM activity as a graph node):* TddWorkflow (×4), Debugging's
     `AIDiagnosis`, ReviewFix's `ApplyReviewFixes`. These compose one of the 9 known direct callers as
     a first-class activity, so the in-engine key path runs regardless of `llm-call`. **The fix is in
     the activity (delete the keyed fallback / route through `call-LLM`), not the workflow graph** —
     all are **Depends on: 32-5 caller-cutover**.
   - Net: of the 9 known direct callers, **8 appear in this cluster** (`CallLlmInlineActivity` via
     `llm-call`; `WriteTests`/`WriteImplementation`/`AnalyzeCode`/`ApplyRefactoring` via Tdd;
     `ApplyReviewFixes` via ReviewFix; `AIDiagnosis` via Debugging). Only `CallLlmActivity` and
     `ClaudeAnalysisActivity` are not composed here (`ClaudeAnalysisActivity` is the Mentorship
     cluster).

2. **Git-platform co-hosting is the cluster's second systemic violation.** CodeReview (create/merge),
   ReviewFix (AnalyzeReview), BlockerDiagnosis (signal collectors), and AdlOrchestrator (issue select /
   triage) all reach the git platform through an in-engine co-hosted `IGitHubIntegrationService`. Per
   §1.3/§5.1 this is **Epic 38 Class A** (`/api/v1/git/...`), a follow-up epic — but it's the
   highest-blast-radius non-LLM violation (cross-tenant write/merge once the engine isn't co-hosted).
   None of these can be closed by 32-5; they all wait on Epic 38.

3. **`LlmCallWorkflow` is the single chokepoint — keep its boundary, repoint its one activity.** Every
   compliant LLM path terminates in `CallLlmInlineActivity` inside `LlmCallWorkflow`. Repointing that
   one activity to `POST /api/v1/llm/call` (32-5) mediates the entire compliant set at once. Per the
   brief, the chain/retry/circuit-breaker/budget loop in `LlmCallWorkflow.cs` is preserved — no graph
   surgery there.

4. **Persona / per-tenant enablement / provider-cost entities are not yet reflected anywhere in this
   cluster.** Workflows pass `agentRole`/`action` (Epic 27 keys) but no `persona`/`agentId`/
   enablement-aware selection; the provider chain is still a hard-coded literal default in
   `LlmCallWorkflow`. This is expected pre-32-5/32-15/32-16 and is correctly deferred to those stories;
   the engine should NOT try to model personas/enablement itself.

5. **`tenantId` threading is inconsistent.** `LlmCallWorkflow` and `TddWithDebugRetryWorkflow` thread
   `tenantId`; `TddWorkflow` does not (so its direct LLM activities can't BYOK-resolve per tenant once
   mediated). When the cutover happens, every workflow that ultimately reaches `call-LLM` must carry
   `tenantId` end-to-end for BYOK→platform credential resolution (rule 7). **Depends on: 32-3 + 32-5.**

6. **Fail-closed vs silent-success nits.** ReviewFix reports `success=true` even when an apply did
   nothing/failed; BlockerDiagnosis reports `Escalated` even on senior-resolution. Neither is a security
   bug, but both blur the audit trail. Align with `feedback_resolution_no_empty_fallback` — terminal
   outputs should reflect the true outcome, never a default-to-success.

7. **`using Tamma.Api.Services.Agents;` is a benign-but-misleading convention.** The `AgentRole`/
   `AgentAction` enums live in `Tamma.Core/Agents/` under that legacy namespace (Story 27-19);
   ElsaServer references only `Tamma.Activities` (verified — no `Tamma.Api` project ref). The `using`
   *looks* like an engine→API layering breach but isn't. Rename to `Tamma.Core.Agents` cluster-wide
   (27-19 TODO) to remove the smell.
