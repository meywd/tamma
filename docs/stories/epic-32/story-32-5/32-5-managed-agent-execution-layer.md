# Story 32-5: Call-LLM Endpoint + Managed Execution (`POST /api/v1/llm/call`)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform engineer building agent-driven workflows on the Tamma engine**,
I want a single internal `POST /api/v1/llm/call` endpoint in `Tamma.Api` that holds the credential, gates the call, runs the agentic tool loop server-side, meters cost, and returns a structured key-free result — and a thin `CallLlmInlineActivity` shim in the engine that delegates to it,
So that **a workflow step NEVER calls an external provider directly** (the engine holds no provider key), SaaS has exactly **one** managed execution path, and every run carries a stable agent identity, a `credentialSource`, a metered cost basis, and a DCB audit trail — while the existing provider-chain/retry/circuit-breaker machinery in `LlmCallWorkflow.cs` keeps working byte-for-byte.

## Priority

P0 — This is the **lynchpin** of the Epic 32 architecture pivot (sequence step **F**). It is the integration point where steps stop calling providers: it relocates credential resolution, the provider HTTP call, the tool loop, and metering from the Elsa engine into `Tamma.Api`, deletes the engine's credential-resolver wiring shipped by 32-3, and is the producer of the run dataset that 32-6 (action trail), 32-8 (outcome capture) and 32-9 (usage/cost emission) consume. Nothing else in the pivot can land cleanly until the mediated endpoint exists.

## Context

### What exists today (the violation)

Agent-driven steps dispatch an LLM call through `CallLlmActivity` → `CallLlmInlineActivity` (~1592 LOC, 22 parent workflows). That inline activity **reads a provider key inside the engine process** (via `IProviderCredentialResolver` → `config.ApiKey`), builds the Anthropic/OpenAI request body, performs the external `PostAsync(.../v1/messages` & `/v1/chat/completions`), and runs a complete agentic tool loop (`AgenticToolLoop`): sanitize → multi-turn call → tool-call validation → sequential/parallel tool execution → tool-output sanitization + secret redaction → context compaction → token accounting. It is the **worst rule-1 violator** in the audit (design §1.2): the only step that, in any deploy topology, puts a live external key in the engine. `CallLlmActivity` is worse still — it reads `Anthropic:ApiKey` directly. Eight further activities (`ClaudeAnalysisActivity`, `WriteTests`/`WriteImplementation`/`AnalyzeCode`/`ApplyRefactoring`/`ApplyReviewFixes`, `AIDiagnosis`) each carry a **direct keyed LLM fallback** behind an engine-callback branch — **nine in-engine direct-LLM callers total**.

The retry / provider-chain / circuit-breaker machinery, by contrast, is **correct and stays put**: `LlmCallWorkflow.cs`'s `BuildRetryLoop` → `ForEach<provider>` invokes the step once per provider per attempt; `RetryCheck` reads `LastDiagnostic.HttpStatusCode` (429/502/503/504/0 → retry); `SkipIfSucceeded` and the circuit breaker advance the chain. That boundary is durable-checkpointed and must not move.

### What this story does (the locked model, rules 1 & 2)

This story builds the **`call-LLM` mediation endpoint** (design §2) and the managed execution layer behind it:

- A new `POST /api/v1/llm/call` endpoint (`Tamma.Api/Endpoints/LlmCallEndpoints.cs`), internal/engine-only, authenticated on the **same plane** as the other `TammaApiClient` callbacks (Bearer `Tamma:ApiToken` via `TammaEngineAuthHandler` + `X-Tenant-Id`), delegating to **`IManagedAgent.RunAsync`** in `Tamma.Api/Services/Agents/`.
- `ManagedAgent.RunAsync` composes the locked rule-2 sequence **inside the API**: gate (32-4) → resolve agent + per-tenant enablement (32-18/32-16) → resolve credential BYOK→platform via the cabinet-backed `DefaultProviderCredentialResolver` (32-3) → render prompt (Epic 27 for personas / own prompts for custom agents — 32-15/32-17) → provider call via the extracted `IInlineToolLoopRunner` (AC3, moved **verbatim** from `CallLlmInlineActivity.AgenticToolLoop`, run with a request-scoped key) → meter (`IProviderPricingService.Compute` cost basis from the 34-11 Provider entity; 34-5 markup when platform / none when byok; 32-9 usage event) → return `text` + `usage` + `credentialSource` (never the key).
- `CallLlmInlineActivity` collapses from ~1592 lines to a **~80-line thin client** that owns no provider logic, no key, no HTTP-to-provider, and no tool loop. It sends an `LlmCallRequest` via a **new `TammaApiClient.CallLlmAsync(...)`**, receives an `LlmCallResponse`, and writes the **same workflow variables it writes today** (`LastDiagnostic`, `LastResponse`, `ToolLoopTokens`/`Turns`/`Exhausted`) so `LlmCallWorkflow.cs` is unchanged.
- The engine's credential resolver (`ConfigPlatformProviderCredentialResolver` + `AddEngineProviderCredentialResolution` in `ElsaServer/Program.cs`, shipped by 32-3) is **deleted from the call path** — the engine no longer resolves keys at all. The provider-side deps that the activity injected (`IHttpClientFactory`, `IContentSanitizer`, `IToolExecutorRegistry`, `IToolCallValidator`, `ContextCompactor`, `ToolLoopEventEmitter`, `ParallelToolExecutor`, `IProviderCredentialResolver`) are removed from the engine and injected in the API process instead.

> **Architecture note — `IManagedAgent` is distinct from CLI providers.** `IManagedAgent` is the customization layer *above* the LLM API (provider + model + prompt + tools + budget). It is **not** an `ICLIAgentProvider`. Per the deep-dive (§1), the endpoint is **API-provider-only**: harness/CLI providers spawn a local process, hold their own auth, and run their own loop — they are single-user-local and exempt from `/llm/call` (design §5.3), and in SaaS the 32-4 gate makes them structurally unreachable (`400 SAAS_PROVIDER_NOT_ALLOWED`). So SaaS has exactly **one** execution path: this endpoint.

### Explicitly out of scope (follow-on stories — referenced, not implemented here)

- **Response mode is buffered (`application/json`) ONLY.** The SSE response mode, the live `IToolLoopEventSink`, and the `GET /api/v1/llm/runs/{correlationId}/stream` run tap are deferred to the **"Streaming run tap"** follow-on (deep-dive §3, §6.4).
- **MCP / plugin tool sourcing (C#)** — the runner uses only the existing built-in `IToolExecutorRegistry` catalog; per-tenant MCP servers/plugins are the **"MCP & plugin tool sourcing"** follow-on (deep-dive §4, §6.2).
- **Prompt + response cache** — Anthropic prompt-cache prefix and the optional gated response cache are the **"Prompt + response cache"** follow-on (deep-dive §4, §6.3).
- **Interactive question-back** — the `request_input` tool + `IQuestionRouter` + `WaitForAgentQuestionActivity` are the **"Interactive question-back"** follow-on (deep-dive §5, §6.5).
- **C# harness/CLI adapter** for single-user local harness execution (deep-dive §6.6) — deferred single-user story.
- **Non-LLM step mediation** (git/agent-dispatch/Slack) — **Epic 38** (design §5).
- **The Provider Cost Price-Book entity** is **34-11** (a hard prerequisite consumed via `IProviderPricingService`); this story does not build it.

## Acceptance Criteria

1. **The endpoint exists.** `POST /api/v1/llm/call` is served by a new `Tamma.Api/Endpoints/LlmCallEndpoints.cs`, internal/engine-only, authenticated by **Bearer `Tamma:ApiToken` (via `TammaEngineAuthHandler`) + `X-Tenant-Id`** — the same plane as the existing `TammaApiClient` callbacks (agent-resolve / budget / diagnostics / provider-session). A missing/invalid bearer → **HTTP 401**. The handler binds an `LlmCallRequest`, derives `tenantId` from `X-Tenant-Id` when the body omits it, and delegates to `IManagedAgent.RunAsync`.

2. **`LlmCallRequest` / `LlmCallResponse` match the design (§2.2/§2.3).** The request record carries `{ tenantId?, agentId?, persona?, role, action?, phase?, prompt, variables, model?, tools?, enableToolLoop, toolLoopConfig?, params{maxTokens,temperature,budgetCapUsd}, correlationId }`. The success response (HTTP 200) carries `{ success:true, text, usage{promptTokens,completionTokens,totalTokens,toolLoopTokens,toolLoopTurns,toolLoopExhausted}, credentialSource("byok"|"platform"), providerUsed, modelUsed, cost{providerCostUsd,priceUsd,currency}, toolCalls[], agentId, agentVersion, role, correlationId, durationMs }`. **`credentialSource` is returned; the API key is NEVER in the response body, logs, or events.**

3. **`ManagedAgent.RunAsync` composition order is exactly the locked rule-2 sequence (design §2.6), all inside `Tamma.Api`:** (1) **gate** — 32-4 `ISaaSProviderGate.InspectAsync` + SaaS auth/entitlement; (2) **resolve agent config + per-tenant enablement** — 32-2/32-18 resolver applying the 32-16 `TenantAgentEnablement` gate → `Provider` + `Model` + `AgentId`/`AgentVersion` + allowed tools; (3) **resolve credential BYOK→platform** — the cabinet-backed `DefaultProviderCredentialResolver` (32-3) yielding `{ ApiKey, Source }`; (4) **render prompt** — Epic 27 `(principal, role, action)` for personas (32-15) / the custom agent's own prompts for custom agents (32-17), tenant→system→**error** (never empty/plain); (5) **provider call** — the extracted `IInlineToolLoopRunner` makes the external HTTPS call **inside `Tamma.Api`** with the request-scoped key; (6) **meter** — `IProviderPricingService.Compute` cost basis + 34-5 markup (platform) / none (byok) + 32-9 usage event; (7) **return**.

4. **The agentic tool loop is reused, NOT forked (AC3 of the original story, preserved).** `CallLlmInlineActivity.AgenticToolLoop(...)` and its private helpers (multi-turn builders/parsers, `LoadProviderConfig`, sanitize/redact calls, compaction) are **extracted verbatim** into `InlineToolLoopRunner : IInlineToolLoopRunner` under `Tamma.Activities/LlmCall/`. `Tamma.Api` references `Tamma.Activities`, so the runner is shared (no second copy). The provider HTTP call executes in the API process where the key is resolved; the engine activity never touches the runner. The existing **`CallLlmInlineActivitySanitizationTests` must pass unchanged** as the regression net proving no behaviour drift. The sanitizer/registry/validator/compactor/parallel-executor are DI-registered **in the API**.

5. **The thin-client cutover (design §2.5).** `CallLlmInlineActivity` is reduced to a ~80-line shim that: (a) maps its current `Input<>` props (`InputJsonProp`, `ProviderNameProp`, `SystemPromptProp`, `ToolsJsonProp`, `AttemptNumberProp`, `EnableToolLoopProp`, `ToolLoopConfigJsonProp`, `TenantIdProp`) into an `LlmCallRequest`; (b) sends it via the **new** `TammaApiClient.CallLlmAsync(LlmCallRequest, tenantId, ct)` (following the existing `PostAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` pattern); (c) maps the `LlmCallResponse` back into the **same workflow variables it writes today** — `LastDiagnostic` (a `ProviderAttemptDiagnostic` carrying `CredentialSource`, `HttpStatusCode`, token counts), `LastResponse` (a `NormalizedLlmResponse`), and `ToolLoopTokens`/`ToolLoopTurns`/`ToolLoopExhausted`. The shim owns **no** provider logic, key, HTTP-to-provider, or tool loop. `enableToolLoop` + `toolLoopConfig` are passed through to the endpoint, not executed locally.

6. **The workflow boundary is byte-for-byte unchanged.** `LlmCallWorkflow.cs`'s `BuildRetryLoop` → `ForEach<provider>` / `RetryCheck` / `SkipIfSucceeded` / circuit-breaker are not modified. The step is still invoked **once per provider per attempt**; provider-chain advance and retry stay at the workflow boundary. This works **only because** the endpoint honours AC7's status-preservation contract.

7. **Error / gating semantics — the load-bearing contract (design §2.4), fail-closed.** The endpoint always returns a **typed, key-free body**:
   - **HTTP 200 + `success:false`** for *expected execution failures*, with `httpStatusCode` **preserved** (e.g. 429/502/503/504/0) so the engine's `RetryCheck` + circuit breaker keep working. `failureCode ∈ { PROVIDER_ERROR, PROVIDER_CREDENTIAL_UNAVAILABLE, BUDGET_EXCEEDED, LOOP_EXHAUSTED }`, plus a key-free `failureReason`, `credentialSource`, `providerUsed`, and `usage` accrued before failure.
   - **HTTP 400 `SAAS_PROVIDER_NOT_ALLOWED`** when 32-4's `ISaaSProviderGate` denies a CLI-token provider in SaaS.
   - **HTTP 403** when SaaS auth/entitlement (32-4) rejects the tenant.
   - **HTTP 401** when the engine bearer is absent/invalid.
   - **Fail-closed:** if the credential, gate, or budget cannot be evaluated, **deny** — never call the provider with an empty/wrong key (consistent with `feedback_resolution_no_empty_fallback`). `PROVIDER_CREDENTIAL_UNAVAILABLE` stays `retryable:false, severity:High`. A raw 5xx must never leak (it would be nulled by `TammaApiClient.PostAsync`, breaking `RetryCheck`).

8. **DCB events from the API.** `AGENT.RUN.STARTED` (one, before the loop) and exactly one terminal `AGENT.RUN.SUCCESS` or `AGENT.RUN.FAILED` are emitted from **`Tamma.Api`** via the tenant `IEventRepository` (where the tenant store + cabinet live), tagged `{ agentId, version, provider, model, role, correlationId, credentialSource, tenantId }`; `AGENT.RUN.FAILED` additionally tags `failureCode`. Credential/gate decisions also surface as `AGENT.CREDENTIAL_RESOLVED.SUCCESS|DENIED` / `AGENT.PROVIDER.GATED` where applicable.

9. **The nine in-engine direct-LLM callers stop calling providers.** `CallLlmActivity` becomes a thin client the same way as `CallLlmInlineActivity` **or is deleted** in favour of the inline path (it is the most severe violator). The TDD/ADL/Debug/Mentorship activities' **direct keyed LLM fallback is deleted** — `ClaudeAnalysisActivity`, `WriteTestsActivity`, `WriteImplementationActivity`, `AnalyzeCodeActivity`, `ApplyRefactoringActivity`, `ApplyReviewFixesActivity`, `AIDiagnosisActivity` route through `call-LLM` (their engine-callback mode terminates at the mediated path). The engine's `ConfigPlatformProviderCredentialResolver` + `AddEngineProviderCredentialResolution` registration are removed from `ElsaServer/Program.cs`.

10. **`AgentRunResult` is preserved and is the producer record.** `ManagedAgent.RunAsync` internally produces an `AgentRunResult` (the typed record from the original 32-5: `AgentId, Version, Provider, Model, Role, InputTokens, OutputTokens, CostUsd, DurationMs, Success, ToolCalls, CorrelationId, CredentialSource, ResponseText, FailureCode?, FailureReason?`), which the endpoint projects to `LlmCallResponse`. Cost stays at the **provider cost basis** (`IProviderPricingService.Compute`); markup is 34-5, not this story. The same shape is returned whether the run succeeded, failed, was budget-exceeded, credential-unavailable, or gate-denied — **failures never lose the run record**.

11. **Tests cover the endpoint + composition + cutover.** Endpoint auth (401 missing bearer, 403 entitlement, 400 SAAS_PROVIDER_NOT_ALLOWED); composition order + happy path (every response field populated, exactly one terminal DCB event); each typed failure (`PROVIDER_ERROR` with preserved `httpStatusCode`, `PROVIDER_CREDENTIAL_UNAVAILABLE`, `BUDGET_EXCEEDED`, `LOOP_EXHAUSTED`); BYOK vs platform `credentialSource` + markup-on-platform-only; the thin shim maps `LlmCallResponse` → the same `LastDiagnostic`/`LastResponse`/`ToolLoop*` variables; and `CallLlmInlineActivitySanitizationTests` passes unchanged (no-fork proof).

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Endpoints/
  LlmCallEndpoints.cs              # NEW — POST /api/v1/llm/call; engine-only auth; delegates to IManagedAgent

apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  IManagedAgent.cs                 # KEEP — the managed execution contract
  ManagedAgent.cs                  # KEEP/REWORK — composes the rule-2 sequence (gate→resolve→cred→render→loop→meter)
  ManagedAgentRequest.cs           # KEEP — internal input record (maps from LlmCallRequest)
  AgentRunResult.cs                # KEEP — internal structured outcome (projected to LlmCallResponse)
  AgentRunEventTypes.cs            # KEEP — AGENT.RUN.STARTED/SUCCESS/FAILED constants
  LlmCallRequest.cs                # NEW — wire request record (design §2.2)
  LlmCallResponse.cs               # NEW — wire response record (design §2.3) + UsageDto/CostDto/ToolCallDto

apps/tamma-elsa/src/Tamma.Activities/LlmCall/
  IInlineToolLoopRunner.cs         # NEW — the extracted, reusable tool-loop seam (interface)
  InlineToolLoopRunner.cs          # NEW — the loop body, moved VERBATIM from CallLlmInlineActivity
  CallLlmInlineActivity.cs         # GUT — ~1592 → ~80-line thin client; no key/HTTP/loop; calls TammaApiClient.CallLlmAsync
  CallLlmActivity.cs               # GUT or DELETE — thin client, or removed in favour of the inline path

apps/tamma-elsa/src/Tamma.Activities/<TDD|ADL|Debug|AI>/
  WriteTestsActivity.cs … AIDiagnosisActivity.cs   # MODIFY — delete the direct keyed LLM fallback; route through call-LLM

apps/tamma-elsa/src/Tamma.ElsaServer/
  Program.cs                       # MODIFY — DELETE ConfigPlatformProviderCredentialResolver + AddEngineProviderCredentialResolution

apps/tamma-elsa/src/Tamma.Api/Clients/  (or wherever TammaApiClient lives)
  TammaApiClient.cs                # MODIFY — add CallLlmAsync(LlmCallRequest, tenantId, ct) (PostAsync<T> + AddTenantHeader + RecordHealthAsync)
```

### The endpoint (`LlmCallEndpoints.cs`)

```csharp
// POST /api/v1/llm/call — internal, engine-only.
// Auth: [Authorize] on the engine-token scheme (TammaEngineAuthHandler) + X-Tenant-Id.
app.MapPost("/api/v1/llm/call", async (
        LlmCallRequest request,
        HttpContext http,
        IManagedAgent managed,
        ILlmCallResponseMapper mapper,
        CancellationToken ct) =>
{
    // Bearer validated by the engine auth scheme; missing/invalid -> 401 before this runs.
    var tenantId = request.TenantId ?? ResolveTenant(http);     // from X-Tenant-Id when body omits it

    var run = await managed.RunAsync(ManagedAgentRequest.From(request, tenantId), ct);

    // Map AgentRunResult -> LlmCallResponse, applying the §2.4 status discipline:
    return mapper.ToHttpResult(run);   // 200 success | 200 success:false (+httpStatusCode) | 400 | 403
})
.RequireAuthorization(EngineAuthPolicy)   // same plane as the other TammaApiClient callbacks
.WithName("CallLlm");
```

The mapper enforces AC7: gate denial (CLI in SaaS) → **400 SAAS_PROVIDER_NOT_ALLOWED**; entitlement rejection → **403**; expected execution failures → **200 `success:false`** with the preserved `httpStatusCode`; success → **200**. It NEVER returns a raw 5xx (that would null the engine's `LastDiagnostic.HttpStatusCode`).

### `LlmCallRequest` / `LlmCallResponse` (the wire contract — design §2.2/§2.3)

```csharp
public sealed record LlmCallRequest
{
    public Guid? TenantId { get; init; }            // null => single-user/platform; also from X-Tenant-Id
    public Guid? AgentId { get; init; }             // explicit custom/persona agent; else resolved by role
    public string? Persona { get; init; }           // system persona name (claude/gemini/codegpt)
    public required string Role { get; init; }      // one of the 8 valid roles (drives Epic 27 prompt resolution)
    public string? Action { get; init; }            // role+action prompt key (Epic 27)
    public string? Phase { get; init; }             // workflow phase for ResolveForPhaseAsync (32-2)
    public required string Prompt { get; init; }    // task/user prompt
    public Dictionary<string, object?> Variables { get; init; } = new();
    public string? Model { get; init; }             // optional override (clamped to persona/agent allowance)
    public IReadOnlyList<string>? Tools { get; init; }
    public bool EnableToolLoop { get; init; }
    public ToolLoopConfig? ToolLoopConfig { get; init; }
    public LlmCallParams Params { get; init; } = new();   // { MaxTokens=4096, Temperature=0.7, BudgetCapUsd=0 }
    public required string CorrelationId { get; init; }   // workflow instance id
}

public sealed record LlmCallResponse
{
    public required bool Success { get; init; }
    public string? Text { get; init; }
    public UsageDto Usage { get; init; } = new();          // prompt/completion/total + toolLoopTokens/Turns/Exhausted
    public string? CredentialSource { get; init; }         // "byok" | "platform" — NEVER the key
    public string? ProviderUsed { get; init; }
    public string? ModelUsed { get; init; }
    public CostDto Cost { get; init; } = new();            // { ProviderCostUsd, PriceUsd, Currency }
    public IReadOnlyList<ToolCallDto> ToolCalls { get; init; } = Array.Empty<ToolCallDto>();
    public Guid? AgentId { get; init; }
    public int AgentVersion { get; init; }
    public string? Role { get; init; }
    public required string CorrelationId { get; init; }
    public long DurationMs { get; init; }

    // failure-only (Success == false):
    public string? FailureCode { get; init; }              // PROVIDER_ERROR | PROVIDER_CREDENTIAL_UNAVAILABLE | BUDGET_EXCEEDED | LOOP_EXHAUSTED
    public string? FailureReason { get; init; }            // key-free
    public int? HttpStatusCode { get; init; }              // preserved so RetryCheck/circuit-breaker work
}
```

### `ManagedAgent.RunAsync` composition (inside `Tamma.Api`)

```
0. req = ManagedAgentRequest.From(LlmCallRequest, tenantId)
1. gate       = await _saasGate.InspectAsync(mode, req.Persona/agentId, req.Provider?)        // 32-4
                  -> CLI-token provider in SaaS  => 400 SAAS_PROVIDER_NOT_ALLOWED
                  -> entitlement reject          => 403
1b. budget    = await _budgetGuard.CheckAsync(tenantId, req.Params.BudgetCapUsd, ct)          // server-side, the existing CheckBudgetActivity logic, fail-closed
                  -> over budget / cannot evaluate => 200 success:false BUDGET_EXCEEDED (deny, never call provider)
                  (this is the named owner of the budget gate; 32-22's response cache lookup runs strictly AFTER this step)
2. resolved   = await _resolver.ResolveForPhaseAsync(tenantId, phase, role, agentId, overrides) // 32-2/32-18
                  -> applies 32-16 TenantAgentEnablement gate (persona not enabled => denied)
                  -> ResolvedAgentConfig { Provider, Model, Temperature, MaxTokens, TokenBudget, Tools, AgentId, Version }
3. credential = await _credentialResolver.ResolveAsync(tenantId, resolved.Provider)            // 32-3 cabinet, BYOK->platform
                  -> { ApiKey, BaseUrl, Source }; if null => 200 success:false PROVIDER_CREDENTIAL_UNAVAILABLE (retryable:false)
4. prompt     = await _promptRenderer.RenderAsync(principal, role, action, resolved, variables) // Epic 27 (persona) / agent prompts (custom)
                  -> tenant -> system -> ERROR (never empty/plain)
5. emit AGENT.RUN.STARTED { agentId, version, provider, model, role, correlationId, credentialSource, tenantId }
6. loop       = await _toolLoop.RunAsync(provider=resolved.Provider, providerConfig with credential.ApiKey,
                     model=resolved.Model, systemPrompt=prompt.System, userPrompt=merged,
                     tools=resolved.Tools, enableToolLoop, toolLoopConfig, correlationId, ct)   // IInlineToolLoopRunner (server-side, request-scoped key)
                  -> provider error          => 200 success:false PROVIDER_ERROR + preserved httpStatusCode
                  -> exhausted, no response  => 200 success:false LOOP_EXHAUSTED
7. costBasis  = _pricing.Compute(resolved.Provider, resolved.Model, loop.InputTokens, loop.OutputTokens)  // 34-11 entity via IProviderPricingService
   price      = credentialSource == "platform" ? _markup.Apply(costBasis, ...) : 0   // 34-5; byok => token price 0
   emit usage (32-9) { CostUsd=costBasis, PlatformBilledUsd=price, BillingMode=credentialSource }
8. emit AGENT.RUN.SUCCESS | AGENT.RUN.FAILED  (exactly one terminal event)
9. return AgentRunResult -> projected to LlmCallResponse
```

The request-scoped key is set on the provider request header and dropped after the call (32-3 AC5); it is never logged, returned, or persisted.

### Reuse, not fork — the extracted runner (AC4)

The `AgenticToolLoop` body moves **verbatim** into `InlineToolLoopRunner : IInlineToolLoopRunner`:

```csharp
public interface IInlineToolLoopRunner
{
    Task<InlineToolLoopResult> RunAsync(
        string provider, LlmProviderConfig providerConfig, string model,
        string systemPrompt, string userPrompt, int maxTokens, double temperature,
        IReadOnlyList<ResolvedTool>? tools, bool enableToolLoop, ToolLoopConfig loopConfig,
        string correlationId, CancellationToken ct);
}
// InlineToolLoopResult { NormalizedLlmResponse Response, int InputTokens, int OutputTokens,
//                        int Turns, bool Exhausted, IReadOnlyList<ToolCallSummary> ToolCalls }
```

`Tamma.Api` references `Tamma.Activities`, so the runner is shared with **no fork**. The DI registrations for `IContentSanitizer`, `IToolExecutorRegistry`, `IToolCallValidator`, `ContextCompactor`, `ParallelToolExecutor` move to the **API** process; they are removed from the engine. `CallLlmInlineActivitySanitizationTests` is the unchanged regression net.

### The thin `CallLlmInlineActivity` shim (AC5)

```csharp
// ~80 lines: map props -> LlmCallRequest -> TammaApiClient.CallLlmAsync -> write the SAME variables.
var req = new LlmCallRequest {
    TenantId = tenantId, Role = role, Persona = providerName /* or agentId */,
    Prompt = userPrompt, Variables = vars, Tools = tools,
    EnableToolLoop = enableToolLoop, ToolLoopConfig = toolLoopConfig,
    Params = new(){ MaxTokens = maxTokens, Temperature = temperature },
    CorrelationId = context.WorkflowExecutionContext.Id
};
var resp = await _api.CallLlmAsync(req, tenantId, ct);   // NEW client method (PostAsync<T> + AddTenantHeader + RecordHealthAsync)

context.SetVariable("LastDiagnostic", new ProviderAttemptDiagnostic {
    CredentialSource = resp.CredentialSource, HttpStatusCode = resp.HttpStatusCode ?? (resp.Success ? 200 : 0),
    InputTokens = resp.Usage.PromptTokens, OutputTokens = resp.Usage.CompletionTokens, Success = resp.Success });
context.SetVariable("LastResponse", NormalizedLlmResponse.From(resp));
context.SetVariable("ToolLoopTokens", resp.Usage.ToolLoopTokens);
context.SetVariable("ToolLoopTurns",  resp.Usage.ToolLoopTurns);
context.SetVariable("ToolLoopExhausted", resp.Usage.ToolLoopExhausted);
```

`LlmCallWorkflow.cs`'s `ForEach<provider>` invokes this once per provider per attempt; `RetryCheck` reads `LastDiagnostic.HttpStatusCode`; the chain advances exactly as today (AC6).

### Cost basis (producer record only)

```csharp
// 34-11 Provider entity behind the unchanged IProviderPricingService seam:
costBasisUsd = _pricing.Compute(resolved.Provider, resolved.Model, loop.InputTokens, loop.OutputTokens);
```

`AgentRunResult.CostUsd` / `LlmCallResponse.cost.providerCostUsd` is the **raw provider cost basis**. The 34-5 markup engine derives `priceUsd` (markup when `platform`, 0 token-price when `byok` — rule 7); 32-9 emits the usage record; 35/36 consume it. No markup math lives in this story.

## Dependencies

**Internal (hard prerequisites):**

- **34-11** (Provider Cost Price-Book) — supplies the cost basis behind `IProviderPricingService.Compute`. Consumed at compose step 7. (Sequence A.)
- **32-15** (Persona reframe + seeding) — named cross-role personas with explicit `model`; Epic-27 prompt wiring. (Sequence B.)
- **32-16** (Per-tenant agent/persona enablement) — `TenantAgentEnablement` gate applied during resolution. (Sequence C.)
- **32-17** (Custom-agent prompts) — `ConfigJson.prompts` + resolver prompt-source branch (custom agent). (Sequence D.)
- **32-18** (Agent registry enablement gate + Epic-27 prompt source) — the amended `IAgentRegistryService`/resolver this story calls at step 2. (Sequence E.)
- **32-4** (REWRITE — SaaS provider gate inside the endpoint) — `ISaaSProviderGate.InspectAsync`, the gate stage (step 1). (Co-stage of F.)
- **32-3** (BYOK credential resolution) — the **cabinet-backed `DefaultProviderCredentialResolver`** invoked at step 3 (this story **deletes** 32-3's engine-side `ConfigPlatformProviderCredentialResolver` wiring).
- **Epic 27** (prompt/convention render) — tenant→system→error resolution at step 4 (never empty/plain).
- **Epic 29** (cabinet) — `ITenantProviderKeyReader` / runtime secret resolver reached by 32-3.
- **Epic 9** (unified agent API) — the engine↔API callback convention (`TammaApiClient`, `TammaEngineAuthHandler`); confirm per story (the endpoint builds against the C# `CallLlm*` seam).

**Consumers (downstream, not blockers):**

- **32-6** (action trail) — consumes `AGENT.RUN.*` + the run record.
- **32-8** (outcome capture & bug taxonomy) — consumes the run outcome.
- **32-9** (usage & cost metering) — emits the usage record from the metered fields.
- **34-5** (markup), **35** (billing), **36** (analytics) — consume `credentialSource` + cost.

**Follow-ons (referenced, separate stories):** Streaming run tap; MCP & plugin tool sourcing (C#); Prompt + response cache; Interactive question-back; C# harness/CLI adapter (single-user); Epic 38 non-LLM mediation.

**External:** none new (reuses the existing provider HTTP stack — now in the API process).

## Testing Strategy

1. **Endpoint auth.** Missing/invalid bearer → 401; valid bearer + `X-Tenant-Id` → request bound, `tenantId` derived from header when body omits it.
2. **Endpoint gating (AC7).** CLI-token provider in SaaS → 400 `SAAS_PROVIDER_NOT_ALLOWED`; entitlement reject → 403; both via fakes of `ISaaSProviderGate`.
3. **Composition order + happy path (AC2/AC3).** Mock gate/resolver/credential/prompt/loop/pricing; assert collaborators called once in order; assert **every** `LlmCallResponse` field populated; exactly one `AGENT.RUN.STARTED` + one terminal event.
4. **`PROVIDER_ERROR` status preservation (AC7/AC6).** Loop returns an unsuccessful `NormalizedLlmResponse` with `httpStatusCode=429` → endpoint returns **200 `success:false`** with `httpStatusCode=429` preserved; assert a raw 5xx is never produced.
5. **`PROVIDER_CREDENTIAL_UNAVAILABLE`.** Credential resolver returns null → 200 `success:false`, `retryable:false`, provider never called.
6. **`BUDGET_EXCEEDED`.** Budget guard over-budget → 200 `success:false`; loop never invoked.
7. **`LOOP_EXHAUSTED`.** Loop returns `exhausted=true` with no usable response → 200 `success:false`, accrued tokens preserved.
8. **`credentialSource` + markup branch.** BYOK → `credentialSource="byok"`, `cost.priceUsd==0`; platform → `"platform"`, `priceUsd==markup(costBasis)`; `cost.providerCostUsd` identical in both.
9. **Thin-shim mapping (AC5/AC6).** Given an `LlmCallResponse`, `CallLlmInlineActivity` writes `LastDiagnostic`/`LastResponse`/`ToolLoopTokens`/`Turns`/`Exhausted` identical to the legacy variables; a minimal `LlmCallWorkflow` `ForEach`/`RetryCheck` run advances the chain unchanged.
10. **No-fork proof (AC4).** `CallLlmInlineActivitySanitizationTests` passes **unchanged** after the loop is extracted into `InlineToolLoopRunner`; `grep` confirms exactly one tool-loop implementation.
11. **Cutover (AC9).** `ClaudeAnalysisActivity` / `WriteTests` / … / `AIDiagnosis` no longer contain a direct keyed LLM call (assert via test + grep for `Anthropic:ApiKey` / `/v1/messages` under `Tamma.Activities` → zero non-`TammaApiClient` hits); `ElsaServer/Program.cs` no longer registers `AddEngineProviderCredentialResolution`.
12. **Credential safety.** Assert the API key never appears in any `LlmCallResponse`, log line, or DCB event payload.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

8-10 days (the lynchpin: endpoint + records + the verbatim loop extraction + the resilience relocation + the thin-client cutover of nine activities + the engine-wiring deletion).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/LlmCallEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/LlmCallRequest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/LlmCallResponse.cs` | Create (+ UsageDto/CostDto/ToolCallDto) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IManagedAgent.cs` | Keep |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` | Create/Rework (rule-2 composition + endpoint projection) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgentRequest.cs` | Keep (add `From(LlmCallRequest, tenantId)`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRunResult.cs` | Keep |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRunEventTypes.cs` | Keep |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/IInlineToolLoopRunner.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/InlineToolLoopRunner.cs` | Create (loop extracted verbatim) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` | Gut → ~80-line thin client |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` | Gut (thin client) or Delete |
| `apps/tamma-elsa/src/Tamma.Activities/AI/ClaudeAnalysisActivity.cs` | Modify (delete direct keyed fallback) |
| `apps/tamma-elsa/src/Tamma.Activities/TDD/WriteTestsActivity.cs` | Modify (delete direct keyed fallback) |
| `apps/tamma-elsa/src/Tamma.Activities/TDD/WriteImplementationActivity.cs` | Modify (delete direct keyed fallback) |
| `apps/tamma-elsa/src/Tamma.Activities/TDD/AnalyzeCodeActivity.cs` | Modify (delete direct keyed fallback) |
| `apps/tamma-elsa/src/Tamma.Activities/TDD/ApplyRefactoringActivity.cs` | Modify (delete direct keyed fallback) |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/ApplyReviewFixesActivity.cs` | Modify (delete direct keyed fallback) |
| `apps/tamma-elsa/src/Tamma.Activities/Debug/AIDiagnosisActivity.cs` | Modify (delete direct keyed fallback) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Modify (delete `ConfigPlatformProviderCredentialResolver` + `AddEngineProviderCredentialResolution`) |
| `apps/tamma-elsa/src/Tamma.Api/Clients/TammaApiClient.cs` | Modify (add `CallLlmAsync(LlmCallRequest, tenantId, ct)`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map endpoint; register `IManagedAgent`, `IInlineToolLoopRunner`, sanitizer/registry/validator/compactor in the API) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/LlmCallEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ManagedAgentTests.cs` | Create/Extend |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/InlineToolLoopRunnerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/CallLlmInlineActivityThinClientTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`).
3. Read the design of record §1 (steps never call providers), §2 (the call-LLM endpoint) IN FULL, and the deep-dive §1–§5.
4. Reviewed `CallLlmInlineActivity.cs` (the loop you are extracting), `TammaApiClient` (the callback pattern), `LlmCallWorkflow.cs` (the `BuildRetryLoop`/`ForEach`/`RetryCheck` boundary you must NOT touch), and `ElsaServer/Program.cs:277` (the resolver registration you are deleting).
5. Confirmed 34-11 / 32-15 / 32-16 / 32-17 / 32-18 / 32-4 / 32-3 contracts are landed (sequence A–E + the gate) before wiring them.
6. Planned the TDD approach; remember the loop extraction is a **pure move** with `CallLlmInlineActivitySanitizationTests` as the net.

### Key Design Decisions

- **The endpoint is a managed execution layer, not a proxy (deep-dive §preamble).** It gates, resolves agent + credential, renders the prompt, runs the loop, and meters — all server-side. The step is a dumb shim.
- **Status preservation is load-bearing (AC6/AC7).** Provider failures return **200 `success:false` + preserved `httpStatusCode`**, never a raw 5xx, because `TammaApiClient.PostAsync` would null a raw 5xx body and break `RetryCheck`/circuit-breaker. This is THE reason the workflow boundary stays unchanged. Add the HTTP-status-fidelity guardrail test (deep-dive §7.9).
- **Provider-chain + retry stay at the workflow boundary (deep-dive §2).** The richer API-side `ProviderChainResolver` exists but is bypassed — folding the chain into the endpoint is an explicit **open decision**, deferred. Minimal blast radius: the step is called once per provider per attempt.
- **Reuse, don't fork (AC4).** `Tamma.Api` references `Tamma.Activities`, so the extracted `InlineToolLoopRunner` is shared verbatim — the provider HTTP call runs in the API where the key is resolved; the engine activity never touches the runner. If the sanitization tests need edits, the extraction is wrong — stop and redo it as a move.
- **Fail-closed, never empty (AC7).** Credential/gate/budget that cannot be evaluated → deny; `PROVIDER_CREDENTIAL_UNAVAILABLE` stays `retryable:false, severity:High`. Consistent with `feedback_resolution_no_empty_fallback` — prompt and credential resolution are tenant→system→error.
- **Buffered only (this story).** SSE / live `IToolLoopEventSink` / the run tap are deferred — the engine is durable-checkpointed request/response and does not need a held-open socket (deep-dive §3).
- **DCB events from the API.** Emitted where the tenant `IEventRepository` + cabinet live, not from the engine's optional sink. Performance/action data is ALWAYS tenant-scoped (design ownership rule).
- **No new control-plane table.** This story adds no CP table → no entry in `Program.cs`'s startup-reset DROP list, and no `ControlPlaneDbContextModelTests` edit. (`TenantAgentEnablement` / `Provider` / `ProviderModelPrice` tables are owned by 32-16 / 34-11 respectively.)

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal of a `call-LLM` request? | The sole user (keyed by `UserId`; `TenantId` may be null). | The tenant (keyed by `TenantId` from `X-Tenant-Id`). No per-user layer. |
| Which execution backends are reachable? | LLM-API path **and** local harness/CLI providers (exempt, local, never traverse `/llm/call` — design §5.3). | **Only** the LLM-API path via `/llm/call`. CLI-token providers → 400 `SAAS_PROVIDER_NOT_ALLOWED` (32-4 gate). |
| Whose credential does a run use? | The sole user's BYOK key → else platform default; resolved by 32-3 in the API. | The tenant's BYOK key (Epic 29 cabinet) → else platform-provided (metered + 34-5 markup). `credentialSource` records which. |
| Whose agent/persona enablement applies? | The sole user's enabled set (`TenantAgentEnablement` keyed by `UserId`). | The tenant's enabled set (keyed by `TenantId`; members can't enable/disable). |
| Where do `AGENT.RUN.*` events land? | The user's (sole) tenant event store; `TenantId` may be the implicit user scope. | The tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`; `TenantId` set. Never cross-tenant. |
| Who owns the run's performance/cost data? | The user. | The tenant — platform admin sees none of it (design ownership rule). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Endpoint returns a raw 5xx → `RetryCheck`/breaker silently break (AC6/AC7) | Critical | The mapper always returns 200 `success:false` + preserved `httpStatusCode` for expected provider failures; HTTP-status-fidelity guardrail test (deep-dive §7.9); 400/403/401 only for gate/entitlement/auth. |
| Loop-extraction drifts behaviour (AC4) | High | Pure verbatim move; `CallLlmInlineActivitySanitizationTests` unchanged as the regression net; `grep` proves one tool-loop copy. |
| A failure path throws and drops the run record (AC10) | High | Wrap each compose step → `AgentRunResult{Success=false}` + one terminal `AGENT.RUN.FAILED`; "exactly one terminal event per run" test. |
| Engine still holds a key after cutover (AC9) | High | Delete `ConfigPlatformProviderCredentialResolver` + `AddEngineProviderCredentialResolution`; grep `Tamma.Activities` for `Anthropic:ApiKey` / `/v1/messages` / non-`TammaApiClient` `PostAsync` → zero. |
| Thin shim writes different variables → workflow breaks (AC5/AC6) | High | Map `LlmCallResponse` → the exact `LastDiagnostic`/`LastResponse`/`ToolLoop*` shapes; a minimal `LlmCallWorkflow` `ForEach`/`RetryCheck` integration test. |
| Cost attribution wrong (BYOK vs platform) | Medium | `credentialSource` from 32-3 copied verbatim; markup only when `platform`; `providerCostUsd` identical both branches; both-branch test. |
| Depends on sequence A–E + 32-4/32-3 not yet landed | Medium | Code to the interfaces; gate behind A–E; use fakes in tests until they land. |
| Co-hosting hides the violation (design §1.1) | Medium | The shim calls over the wire via `TammaApiClient`, never resolves an injected vendor service — verified the moment the engine runs as per-tenant dedicated compute (Cranl). |

### Success Metrics

- [ ] `grep` over `Tamma.Activities` finds **zero** direct provider HTTP calls / `Anthropic:ApiKey` reads (all nine callers cut over).
- [ ] `ElsaServer/Program.cs` registers **no** provider credential resolver (engine holds no LLM key).
- [ ] 100% of mediated runs produce one `AGENT.RUN.STARTED` + exactly one terminal `AGENT.RUN.*`, and a metered usage record.
- [ ] Zero forks of the tool loop / sanitizer / compactor (single source confirmed by grep).
- [ ] `LlmCallWorkflow.cs` diff is empty (the workflow boundary is byte-for-byte unchanged).

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1 steps-never-call-providers; §2 the call-LLM endpoint; §5.2 phasing)
- Managed-LLM deep dive: `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§1 provider duality; §2 resilience relocation; §3 buffered/SSE; §4 tools/MCP/cache/RAG)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (sequence step F)
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-5-call-llm-endpoint-and-managed-execution-plan.md`
- Sibling stories: `story-32-3/` (credential resolver), `story-32-4/` (SaaS gate), `story-32-15/` (persona reframe), `story-32-16/` (enablement), `story-32-17/` (custom-agent prompts), `story-32-18/` (registry enablement gate + Epic-27 prompt source), `story-32-6/`, `story-32-8/`, `story-32-9/`; `docs/stories/epic-34/story-34-11/` (Provider Cost Price-Book)
- Reused code: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` (the loop being extracted), `TammaApiClient`, `LlmCallWorkflow.cs`

## Logging Requirements

- **INFO**: call-LLM received (correlationId, role, persona/agentId, tenantId — **never the prompt body verbatim if it may contain secrets**); managed run started (agentId, version, provider, model, role, credentialSource); run completed (success, durationMs, inputTokens, outputTokens, providerCostUsd, toolCalls); gate decision (allow/deny + mode).
- **DEBUG**: composition step boundaries (gate → resolve → credential → render → loop → meter), rendered-prompt token estimate, tool-loop turns.
- **WARN**: typed failure paths (`PROVIDER_ERROR` + `httpStatusCode`, `PROVIDER_CREDENTIAL_UNAVAILABLE`, `BUDGET_EXCEEDED`, `LOOP_EXHAUSTED`) and gate denials (400/403) with `failureCode` + `correlationId`.
- **ERROR**: contract violations (null request), DCB append failure (the run still returns its result; the append failure is logged, not swallowed), and any attempt to return a raw 5xx (guardrail).
- **Structured context**: `{ agentId, version, provider, model, role, correlationId, tenantId, credentialSource }` where applicable.
- **Credential safety (LOAD-BEARING)**: NEVER log, return, or persist the resolved API key, `BaseUrl` auth, or raw provider headers. `credentialSource` (the label `byok`/`platform`) is safe; the key is not. The `LlmCallResponse` body, all DCB event payloads, and the action trail are key-free by contract.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation — `IManagedAgent` orchestration seam over the inline path. | Claude |
| 2026-06-21 | 2.0.0   | **Rewrite to the call-LLM endpoint model** (architecture pivot, sequence F). Reframed from an in-engine orchestration seam to `POST /api/v1/llm/call` in `Tamma.Api`: thin-client cutover of `CallLlmInlineActivity`/`CallLlmActivity` + the eight other direct-LLM callers; `IInlineToolLoopRunner` extraction now executes in the API with a request-scoped key; resilience relocation (credential/breaker-record/budget/metering moves server-side); deletion of the engine's `ConfigPlatformProviderCredentialResolver`/`AddEngineProviderCredentialResolution`; `LlmCallRequest`/`LlmCallResponse` wire contract with the load-bearing 200-`success:false`+`httpStatusCode` semantics; buffered-only (SSE/MCP/cache/question-back scoped out to follow-ons). `AgentRunResult`/reuse-not-fork/typed-failures-never-lost preserved. | Claude |
