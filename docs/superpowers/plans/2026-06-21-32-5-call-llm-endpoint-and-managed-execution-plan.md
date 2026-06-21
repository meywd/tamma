# Story 32-5 (v2) — Call-LLM Endpoint + Managed Execution (`POST /api/v1/llm/call`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax. Project is test-first (TDD) — every task writes tests before
> implementation. Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group
> is stale; plain `dotnet build` needs no wrapper).

**Date:** 2026-06-21 · **Supersedes** `2026-06-17-32-5-managed-agent-execution-layer-plan.md` (the
pre-pivot in-engine-seam plan; left in place for history).

**Goal:** Build the **`call-LLM` mediation endpoint** (the lynchpin, sequence step **F**). After this
story a workflow STEP never calls an external provider: `POST /api/v1/llm/call` in `Tamma.Api` holds
the credential, gates, runs the agentic tool loop server-side, meters cost, and returns a structured
**key-free** `LlmCallResponse`. `CallLlmInlineActivity` collapses to a ~80-line thin client over
`TammaApiClient.CallLlmAsync`; the eight other in-engine direct-LLM callers cut over; the engine's
`ConfigPlatformProviderCredentialResolver` wiring (shipped by 32-3) is deleted. The retry /
provider-chain / circuit-breaker boundary in `LlmCallWorkflow.cs` stays **byte-for-byte unchanged**.

**Story file:** `docs/stories/epic-32/story-32-5/32-5-managed-agent-execution-layer.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`
(§1, §2, §5.2) · **Deep dive:** `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md`
(§1–§5)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa`. Two processes: `Tamma.ElsaServer` (engine, no
secrets) + `Tamma.Api` (holds creds/gates/meters). Engine→API over HTTP via `TammaApiClient` (Bearer
`Tamma:ApiToken` via `TammaEngineAuthHandler` + `X-Tenant-Id`). `Tamma.Api` references
`Tamma.Activities`, so the extracted tool-loop runner is shared verbatim. **`packages/api` is DELETED
— there is no TypeScript path; all of this is C#.**

---

## Non-goals (YAGNI guard)

- **Buffered (`application/json`) response ONLY.** No SSE response mode, no live `IToolLoopEventSink`,
  no `GET /api/v1/llm/runs/{correlationId}/stream` → follow-on **"Streaming run tap"** (deep-dive §3).
- **NO MCP / plugin tool sourcing.** The runner uses the existing built-in `IToolExecutorRegistry`
  catalog only → follow-on **"MCP & plugin tool sourcing (C#)"** (deep-dive §4).
- **NO prompt/response cache** → follow-on **"Prompt + response cache"** (deep-dive §4).
- **NO interactive question-back** (`request_input`/`IQuestionRouter`/`WaitForAgentQuestionActivity`)
  → follow-on **"Interactive question-back"** (deep-dive §5).
- **NO new tool loop/sanitizer/validator/compactor.** AC4 is "reuse, don't fork": EXTRACT the existing
  `AgenticToolLoop` verbatim and call it from the API. No reimplementation.
- **NO markup / invoicing / analytics.** `providerCostUsd` = raw `IProviderPricingService.Compute`
  cost basis. Markup is 34-5; invoicing 35; analytics 36. This story is a producer.
- **NO Provider Cost Price-Book entity** — that's 34-11 (consumed via the unchanged seam).
- **NO change to `LlmCallWorkflow.cs`.** The `BuildRetryLoop`/`ForEach<provider>`/`RetryCheck`/
  `SkipIfSucceeded`/circuit-breaker boundary must not move; its diff is empty.
- **NO new control-plane table** → no `Program.cs` DROP-list edit, no `ControlPlaneDbContextModelTests`
  edit (those belong to 32-16 / 34-11).
- **NO non-LLM mediation** (git/agent-dispatch/Slack) — Epic 38.

---

## Current-state findings (from the design audit, repo @ `feat/exec-wave-02`)

| Seam | Where it is today | What 32-5 does |
|---|---|---|
| **Agentic tool loop** | `Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — private `AgenticToolLoop(...)` (~line 443): multi-turn Anthropic/OpenAI calls (`CallAnthropicMultiTurn`/`CallOpenAiMultiTurn`, single blocking `PostAsync` + `ReadFromJsonAsync`), `IToolCallValidator`, `IToolExecutorRegistry`, `ParallelToolExecutor`, `ContextCompactor`, `ToolLoopEventEmitter`, token accounting; sanitize via `IContentSanitizer` + `ToolOutputHelper.RedactSecrets`. | **Extract** verbatim into `IInlineToolLoopRunner`/`InlineToolLoopRunner`; run **in the API** with the request-scoped key. |
| **Credential resolution** | engine `ConfigPlatformProviderCredentialResolver` registered via `AddEngineProviderCredentialResolution` (`ElsaServer/Program.cs:~277`); plus the cabinet-backed `DefaultProviderCredentialResolver` in `Tamma.Api/Services/Providers/`. | **DELETE** the engine registration; `ManagedAgent` invokes `DefaultProviderCredentialResolver` (BYOK→platform) in the API. |
| **Provider HTTP call** | `CallLlmInlineActivity.CallAnthropicMultiTurn`/`CallOpenAiMultiTurn` (`:889,920,927`) — `PostAsync(.../v1/messages` & `/v1/chat/completions`); also `CallLlmActivity` reads `Anthropic:ApiKey` directly. | **Move** into the runner, executed in the API; the activity becomes a thin client; `CallLlmActivity` gutted or deleted. |
| **Retry / provider-chain / breaker** | `LlmCallWorkflow.cs` — `BuildRetryLoop` → `ForEach<provider>`; `RetryCheck` reads `LastDiagnostic.HttpStatusCode` (429/502/503/504/0); `SkipIfSucceeded`; circuit breaker. | **Unchanged.** Works only because the endpoint returns 200 `success:false` + preserved `httpStatusCode`. |
| **Budget guard** | `Tamma.Activities/LlmCall/CheckBudgetActivity.cs` (API-first, fail-closed). | Budget check moves into the endpoint gate (server-side, before the call). |
| **Cost basis** | `Tamma.Api/Services/Providers/IProviderPricingService.Compute(provider, model, in, out)`. | Used at meter step; backed by 34-11's Provider entity (seam unchanged). |
| **DCB events** | `Tamma.Data/Repositories/IEventRepository.AppendAsync` (tenant-scoped). | `AGENT.RUN.STARTED/SUCCESS/FAILED` emitted from the **API**. |
| **Engine→API client** | `TammaApiClient` — `PostAsync<T>` + `AddTenantHeader` + `RecordHealthAsync`; Bearer via `TammaEngineAuthHandler` + `X-Tenant-Id`. Routes agent-resolve / budget / diagnostics / provider-session. | **Add** `CallLlmAsync(LlmCallRequest, tenantId, ct)`. |
| **Nine direct-LLM callers** | `CallLlmInlineActivity`, `CallLlmActivity`, `ClaudeAnalysisActivity`, `WriteTests`/`WriteImplementation`/`AnalyzeCode`/`ApplyRefactoring` (TDD), `ApplyReviewFixes` (ADL), `AIDiagnosis` (Debug). | All cut over / direct fallback deleted → route through `call-LLM`. |

**Key insight:** the genuinely new code is the *endpoint* (`LlmCallEndpoints`), the *wire records*
(`LlmCallRequest`/`LlmCallResponse`), the `TammaApiClient.CallLlmAsync` method, the *thin shims*, and
the *extraction* of the existing loop into a runner. The composition shell (`ManagedAgent`),
`AgentRunResult`, and the failure semantics are kept from v1. The hard part is the **status-preservation
contract** and the **verbatim move**.

---

## Architecture

```
ENGINE (Tamma.ElsaServer — holds NO key)              API (Tamma.Api — holds the key, gates, meters)
─────────────────────────────────────────            ─────────────────────────────────────────────
LlmCallWorkflow.cs  (UNCHANGED)
  BuildRetryLoop → ForEach<provider>
     │  (once per provider per attempt)
     ▼
CallLlmInlineActivity  (~80-line THIN CLIENT)
  map Input<> props → LlmCallRequest
     │  TammaApiClient.CallLlmAsync(req, tenantId)  ───────►  POST /api/v1/llm/call  (LlmCallEndpoints)
     │  (Bearer Tamma:ApiToken + X-Tenant-Id)                      │  401 if bearer bad
     │                                                             ▼
     │                                              IManagedAgent.RunAsync (ManagedAgent):
     │                                                1 gate (32-4 ISaaSProviderGate)      → 400/403
     │                                                2 resolve agent + enablement (32-18/16)
     │                                                3 credential BYOK→platform (32-3 cabinet)
     │                                                4 render prompt (Epic 27 / custom-agent 32-17)
     │                                                5 emit AGENT.RUN.STARTED
     │                                                6 InlineToolLoopRunner (server-side, scoped key)
     │                                                7 meter (34-11 cost + 34-5 markup + 32-9 usage)
     │                                                8 one terminal AGENT.RUN.SUCCESS|FAILED
     ◄── LlmCallResponse (key-free) ──────────────────9 project AgentRunResult → LlmCallResponse
  write LastDiagnostic / LastResponse / ToolLoop*           (200 | 200 success:false +httpStatusCode | 400 | 403)
  (SAME variables as today)
     ▼
RetryCheck reads LastDiagnostic.HttpStatusCode  (UNCHANGED — chain advances exactly as today)
```

Per-mode (CLAUDE.md two-scoping rule): single-user principal = sole user (`UserId`), LLM-API + local
harness backends, user's BYOK→platform key, events in the user store; SaaS principal = tenant
(`X-Tenant-Id`), LLM-API path only (CLI → 400), tenant BYOK→platform, events in `t_<hex>`, never
cross-tenant. Mode from `ITammaModeProvider`.

---

## Task breakdown

Order: **T1** (wire records) → **T2** (extract loop, pure refactor) → **T3** (`ManagedAgent`
rule-2 composition + projection) → **T4** (endpoint + auth + status mapper) → **T5**
(`TammaApiClient.CallLlmAsync` + thin shim cutover) → **T6** (delete engine resolver + cut over the
8 other callers) → **T7** (mode/RBAC/credential-safety + the status-fidelity guardrail). T1 ∥ T2 are
parallel-safe; T3 needs both; T4 needs T3; T5 needs T4; T6 needs T5.

### T1 — Wire records (`LlmCallRequest`, `LlmCallResponse`) + keep the internal records

**Scope:** the §2.2/§2.3 wire contract + the `ManagedAgentRequest.From(LlmCallRequest, tenantId)` and
`AgentRunResult → LlmCallResponse` projection shapes. No behaviour.

**Files (new):** `Tamma.Api/Services/Agents/LlmCallRequest.cs`,
`Tamma.Api/Services/Agents/LlmCallResponse.cs` (+ `UsageDto`/`CostDto`/`ToolCallDto`/`LlmCallParams`).
**Files (keep):** `IManagedAgent.cs`, `ManagedAgentRequest.cs` (add `From`), `AgentRunResult.cs`,
`AgentRunEventTypes.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/LlmCallContractTests.cs` — required fields enforced;
`Success=false` always carries `failureCode`+`failureReason`+`httpStatusCode`; success carries none;
`credentialSource ∈ {byok,platform}`; the key field does not exist on the response type.

**Acceptance:**
- [ ] `LlmCallRequest`/`LlmCallResponse` match design §2.2/§2.3 field-for-field.
- [ ] `ManagedAgentRequest.From(...)` derives `tenantId` from the body or the header arg.
- [ ] Builds clean; no analyzer warnings; no API-key property anywhere on the response.

### T2 — Extract the agentic tool loop into a reusable runner (pure refactor, AC4)

**Scope:** Move `CallLlmInlineActivity.AgenticToolLoop(...)` + its private helpers
(`CallAnthropicMultiTurn`/`CallOpenAiMultiTurn`, body builders/parsers, `LoadProviderConfig`,
sanitize/redact, compaction) **verbatim** into `InlineToolLoopRunner : IInlineToolLoopRunner`. No
logic change. (At this task the activity still calls the runner *locally*; the cutover to the API is
T5/T6.)

**Files:** new `Tamma.Activities/LlmCall/IInlineToolLoopRunner.cs`,
`Tamma.Activities/LlmCall/InlineToolLoopRunner.cs`; modify `CallLlmInlineActivity.cs` to delegate; DI
registration. **Move the sanitizer/registry/validator/compactor/parallel-executor DI to the API**
(they stop being engine-registered — staged so the engine build stays green; final removal in T6).

**Seam shape:** as in the story Technical Design (`provider, providerConfig, model, systemPrompt,
userPrompt, maxTokens, temperature, tools, enableToolLoop, loopConfig, correlationId, ct →
InlineToolLoopResult { Response, InputTokens, OutputTokens, Turns, Exhausted, ToolCalls }`).

**Tests (first):** `tests/Tamma.Activities.Tests/LlmCall/InlineToolLoopRunnerTests.cs` (port the
existing loop assertions); **and `CallLlmInlineActivitySanitizationTests` MUST pass unchanged** —
the regression net proving no drift.

**Acceptance:**
- [ ] `CallLlmInlineActivity` delegates to the runner; tool-loop outputs (token totals, turns,
      exhausted, sanitized output) byte-for-byte identical.
- [ ] `grep -rn "AgenticToolLoop\|class InlineToolLoopRunner" apps/tamma-elsa/src` → exactly one loop.
- [ ] Activities suite green via `sg docker -c "dotnet test .../Tamma.Activities.Tests/ --filter ...LlmCall"`.

### T3 — `ManagedAgent` rule-2 composition + projection (AC3, AC10)

**Scope:** `ManagedAgent.RunAsync` composes gate → resolve+enablement → credential → render → STARTED
→ runner → meter → terminal event, builds `AgentRunResult`, and projects it to `LlmCallResponse`. All
in the API.

**Files:** `Tamma.Api/Services/Agents/ManagedAgent.cs` (create/rework),
`Tamma.Api/Services/Agents/ILlmCallResponseMapper.cs` + impl (AgentRunResult → LlmCallResponse +
HTTP-result decision).

**Collaborators (constructor-injected; fakes in tests):** `ISaaSProviderGate` (32-4),
`IAgentResolverService`/`IManagedAgentResolver` (32-2/32-18, applies 32-16 enablement),
`DefaultProviderCredentialResolver`/`IProviderCredentialResolver` (32-3), prompt renderer (Epic 27 /
32-17), `IInlineToolLoopRunner` (T2), budget guard (reuse `CheckBudgetActivity` logic behind a small
interface), `IProviderPricingService` (34-11), markup engine (34-5), usage emitter (32-9),
`IEventRepository`, `ITammaModeProvider`, `ILogger<ManagedAgent>`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/ManagedAgentTests.cs` — happy path (every
`LlmCallResponse` field populated; collaborators called once in order; `providerCostUsd ==
_pricing.Compute(...)`; `credentialSource` copied); BYOK → `priceUsd==0`, platform →
`priceUsd==markup(costBasis)`; exactly one `AGENT.RUN.STARTED` + one terminal event.

**Acceptance:**
- [ ] Happy-path `RunAsync` → `AgentRunResult{Success=true}`, projected to a full `LlmCallResponse`.
- [ ] Markup applied only when `credentialSource=="platform"`; `providerCostUsd` identical both branches.

### T4 — The endpoint + auth + the status mapper (AC1, AC7)

**Scope:** `POST /api/v1/llm/call` (engine-only auth) → `IManagedAgent.RunAsync` →
`ILlmCallResponseMapper.ToHttpResult`. The mapper enforces §2.4: 200 success / 200 `success:false` +
preserved `httpStatusCode` / 400 `SAAS_PROVIDER_NOT_ALLOWED` / 403 entitlement / 401 bad bearer.
**Never a raw 5xx.**

**Files:** new `Tamma.Api/Endpoints/LlmCallEndpoints.cs`; modify `Tamma.Api/Program.cs` (map the
endpoint under the engine-auth policy; register `IManagedAgent`, `IInlineToolLoopRunner`, the mapper,
and the sanitizer/registry/validator/compactor in the API).

**Tests (first):** `tests/Tamma.Api.Tests/Endpoints/LlmCallEndpointsTests.cs` (via
`WebApplicationFactory`) — 401 missing/invalid bearer; `tenantId` derived from `X-Tenant-Id`; 400
SAAS_PROVIDER_NOT_ALLOWED (gate denies CLI in SaaS); 403 entitlement; 200 success; **200 `success:false`
+ `httpStatusCode=429` for a provider failure** (assert no raw 5xx ever returned).

**Acceptance:**
- [ ] All five HTTP outcomes covered; expected provider failures are 200 `success:false` with the
      `httpStatusCode` preserved.
- [ ] Endpoint resolves the whole DI chain at host startup.

### T5 — `TammaApiClient.CallLlmAsync` + the thin `CallLlmInlineActivity` shim (AC5, AC6)

**Scope:** Add `CallLlmAsync(LlmCallRequest, tenantId, ct)` to `TammaApiClient` (existing
`PostAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` pattern). Gut `CallLlmInlineActivity` to a
~80-line shim: map `Input<>` props → `LlmCallRequest`, call `CallLlmAsync`, write the **same**
`LastDiagnostic`/`LastResponse`/`ToolLoopTokens`/`Turns`/`Exhausted` variables. Pass
`enableToolLoop`+`toolLoopConfig` through (not executed locally).

**Files:** modify `TammaApiClient.cs`, `CallLlmInlineActivity.cs`.

**Tests (first):** `tests/Tamma.Activities.Tests/LlmCall/CallLlmInlineActivityThinClientTests.cs` —
given an `LlmCallResponse`, the shim writes variables identical to the legacy shapes; a minimal
`LlmCallWorkflow` `ForEach`/`RetryCheck` integration test advances the chain unchanged for a
`success:false`+`httpStatusCode=429` response.

**Acceptance:**
- [ ] The shim owns no key/HTTP-to-provider/tool loop; it calls `CallLlmAsync` only.
- [ ] `LlmCallWorkflow.cs` is unmodified (empty diff); `RetryCheck`/`SkipIfSucceeded`/breaker advance
      exactly as before.

### T6 — Delete the engine resolver + cut over the eight other callers (AC9)

**Scope:** Remove `ConfigPlatformProviderCredentialResolver` + `AddEngineProviderCredentialResolution`
from `ElsaServer/Program.cs`; remove the now-unused provider-side DI from the engine
(`IHttpClientFactory` provider use, `IContentSanitizer`, `IToolExecutorRegistry`, `IToolCallValidator`,
`ContextCompactor`, `ToolLoopEventEmitter`, `ParallelToolExecutor`, `IProviderCredentialResolver`).
Gut `CallLlmActivity` to a thin client **or delete it**. Delete the direct keyed LLM fallback from
`ClaudeAnalysisActivity`, `WriteTests`/`WriteImplementation`/`AnalyzeCode`/`ApplyRefactoring`,
`ApplyReviewFixes`, `AIDiagnosis` — route through `call-LLM` (engine-callback mode terminates at the
mediated path).

**Files:** modify `ElsaServer/Program.cs`, `CallLlmActivity.cs` (or delete), and the seven TDD/ADL/
Debug/Mentorship activities.

**Tests (first):** `tests/Tamma.Activities.Tests/.../NoDirectLlmCallTests.cs` — assert (and grep) that
under `Tamma.Activities` there is **zero** `Anthropic:ApiKey` read, **zero** `/v1/messages`/
`/v1/chat/completions` `PostAsync`, and **zero** non-`TammaApiClient` provider HTTP call; assert
`ElsaServer/Program.cs` registers no provider credential resolver.

**Acceptance:**
- [ ] Nine in-engine direct-LLM callers eliminated; engine holds no LLM key.
- [ ] Engine + API build green; full suite green.

### T7 — Mode/RBAC, credential safety & the status-fidelity guardrail (AC7, AC8, AC11)

**Scope:** Prove the two-scoping ownership, the key never leaks, and the load-bearing
status-preservation contract holds.

**Files:** extend `ManagedAgentTests` + `LlmCallEndpointsTests`; add
`tests/Tamma.Api.Tests/Endpoints/LlmCallStatusFidelityTests.cs`.

**Tests (first):**
- SaaS + CLI-token provider → 400 SAAS_PROVIDER_NOT_ALLOWED; single-user CLI → not reached by the
  endpoint (local, exempt).
- BYOK → `credentialSource="byok"`, no markup; platform → `"platform"`, markup; `providerCostUsd` same.
- `AGENT.RUN.*` tags `{ agentId, version, provider, model, role, correlationId, credentialSource,
  tenantId }`; FAILED adds `failureCode`; events land in the tenant-scoped repo; two tenants → no leak.
- **Credential safety:** the API key appears in no `LlmCallResponse`, no log line, no DCB payload.
- **Status fidelity guardrail (deep-dive §7.9):** for every expected provider failure the endpoint
  returns 200 `success:false` + a non-null `httpStatusCode`, never a raw 5xx.

**Acceptance:**
- [ ] Mode matrix passes; SaaS never reaches a CLI provider via `/llm/call`.
- [ ] Key-leak assertions hold; status-fidelity guardrail green.

---

## Story order & dependencies

External prereqs (sequence A–E + the gate): **34-11** (cost price-book), **32-15** (persona
reframe), **32-16** (enablement), **32-17** (custom-agent prompts), **32-18** (registry enablement
gate + Epic-27 prompt source), **32-4** (SaaS gate), **32-3** (cabinet credential resolver — this
story deletes its engine wiring), **Epic 27** (prompt render), **Epic 29** (cabinet). Code to the
interfaces; use fakes until landed. Internal: T1 ∥ T2 → T3 → T4 → T5 → T6 → T7. Downstream consumers
(32-6/8/9, 34-5, 35, 36) depend on this; they are NOT blockers.

EF note: this story adds **no** migration / CP table (the loop relocation + endpoint are code-only).
`TenantAgentEnablement` and `Provider`/`ProviderModelPrice` migrations belong to 32-16 / 34-11; this
plan amends no migration snapshot.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Agents|FullyQualifiedName~Endpoints"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/ --filter FullyQualifiedName~LlmCall"
# AC4 no-fork:
grep -rn "AgenticToolLoop\|class InlineToolLoopRunner" apps/tamma-elsa/src
# AC9 engine holds no key:
grep -rn "Anthropic:ApiKey\|/v1/messages\|/v1/chat/completions" apps/tamma-elsa/src/Tamma.Activities
grep -rn "AddEngineProviderCredentialResolution\|ConfigPlatformProviderCredentialResolver" apps/tamma-elsa/src/Tamma.ElsaServer
# AC6 workflow boundary unchanged:
git diff --stat apps/tamma-elsa/src/Tamma.ElsaServer/.../LlmCallWorkflow.cs   # expect: empty
```

## Risks

- **Raw 5xx leak breaks retry (T4, AC6/AC7) — CRITICAL.** A raw 5xx is nulled by
  `TammaApiClient.PostAsync` → `RetryCheck`/breaker silently stop working. Mitigation: the mapper
  always returns 200 `success:false` + preserved `httpStatusCode` for expected provider failures; the
  T7 status-fidelity guardrail enforces it; only gate/entitlement/auth use 400/403/401.
- **Extraction drift (T2, AC4) — HIGH.** The loop is large and security-critical (sanitization at
  every boundary). Mitigation: pure verbatim move, zero logic edits, `CallLlmInlineActivitySanitizationTests`
  unchanged. If those tests need edits, the extraction is wrong — stop and redo as a move.
- **Thin shim writes different variables (T5, AC5/AC6) — HIGH.** Map `LlmCallResponse` → the exact
  `LastDiagnostic`/`LastResponse`/`ToolLoop*` shapes; the minimal `ForEach`/`RetryCheck` integration
  test is the proof; keep `LlmCallWorkflow.cs` diff empty.
- **Engine still holds a key after cutover (T6, AC9) — HIGH.** Grep gates in the verification block +
  the `NoDirectLlmCallTests`; delete the resolver registration and the provider-side DI.
- **Lost run records (T3, AC10) — HIGH.** Each compose step → typed `AgentRunResult{Success=false}` +
  one terminal `AGENT.RUN.FAILED`; "exactly one terminal event per run" invariant test; only a null
  request may throw.
- **Cost mis-attribution (T3) — MEDIUM.** `credentialSource` copied from 32-3, never re-derived;
  markup only when `platform`; both-branch test; `providerCostUsd` from the unchanged seam.
- **Dependency timing (A–E + 32-4/32-3) — MEDIUM.** Interfaces + fakes; this story is the integrator,
  not the owner, of those seams.
- **Co-hosting masks the violation (design §1.1) — MEDIUM.** The shim calls over the wire via
  `TammaApiClient`, never resolves an injected vendor service — the rule holds the moment the engine
  runs as per-tenant dedicated compute (Cranl).
