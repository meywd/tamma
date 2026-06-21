# Managed LLM Execution — deep dive (provider duality · resilience · streaming · tools/MCP/RAG/cache · interactive question-back)

**Date:** 2026-06-20 · **Companion to:** `2026-06-20-epic-32-revised-agent-architecture.md` (§3 the call-LLM endpoint) · **Status:** design detail, grounded in a 3-track code audit

> The user's clarification: *"llm calls are not simple API calls… two types of providers (API call and harness/SDK), error handling, retrying, RAG, cache, tools, MCPs, plugins, backoff, logging, monitoring… it should be a stream, not just a call, and if it asks a question back then depending on context something should respond — the orch? a team of agents?"* This doc designs the `POST /api/v1/llm/call` endpoint as a **managed agent execution layer**, not a proxy. Every claim below is file-referenced against the real tree.

---

## 1. Provider duality — two hierarchies, but only one runs server-side

| | API providers | Harness / SDK providers |
|---|---|---|
| Interface | `ILLMProvider`/`IAIProvider` (`packages/providers/src/types.ts:162,311`), `type:'llm-api'` | `IAgentProvider`/`ICLIAgentProvider` (`agent-types.ts:23`, `types.ts:182`), `type:'cli-agent'` |
| Execution | Tamma owns request/tool-loop/streaming/retries; HTTP to provider | Provider owns its **own** loop/auth/streaming/retries: `ClaudeAgentProvider` `spawn('claude', -p --output-format stream-json)` (`claude-agent-provider.ts:136,280`); `OpenCodeProvider` SDK session (`opencode-provider.ts:229`) |
| Cost | per-token via `IProviderPricingService` | aggregate `costUsd` only (`AgentTaskResult.costUsd`, `claude-agent-provider.ts:342`) — no token split |
| In the C# engine today | **the only path** | **rejected** — `HttpProviderClient.NonHttpProviders = {claude-code, opencode, zen-mcp, …}` throws `ProviderNotSupportedException` (`HttpProviderClient.cs:59-86`) |

**Design.** `ManagedAgent.RunAsync` branches on the resolved provider's `AuthModel` (`api-key`|`cli-token` — the new `Provider` entity field, main spec §4.2):
- **API-provider path** = the endpoint's real job (gate → resolve agent → resolve credential BYOK→platform → render → **Tamma's own tool loop server-side** → meter). **The only path in SaaS.**
- **Harness-provider path** = single-user **local** affordance. Per main spec §5.3 these are *exempt* from `/llm/call` mediation (they spawn a local process, hold their own auth, run their own loop — routing them through the endpoint adds a hop with no security benefit). In SaaS the 32-4 gate makes them structurally unreachable (`400 SAAS_PROVIDER_NOT_ALLOWED`). So the **endpoint stays API-provider-only**; harness execution is a deployment-mode-gated local path that never traverses `/llm/call`. This keeps "SaaS has exactly one execution path" literally true.

> Consequence: there is **no C# harness adapter today**. Single-user harness execution needs either a retained TS execution path or a future ported C# CLI/MCP adapter — a deferred single-user story, not a blocker for the SaaS endpoint.

---

## 2. Resilience — what moves server-side, what stays at the workflow boundary

All resilience primitives already exist; the move relocates the *call*, not the machinery. (`LlmCallWorkflow.cs`, `Tamma.Api/Services/Providers/*`.)

| Concern | Today | After 32-5 |
|---|---|---|
| Credential resolution | engine `ConfigPlatformProviderCredentialResolver` (`ElsaServer/Program.cs:277`) | **→ API** `DefaultProviderCredentialResolver` (BYOK→platform); delete engine registration (engine holds no key) |
| Provider HTTP + tool loop | `CallLlmInlineActivity.AgenticToolLoop` (`:443`) in-engine | **→ API** `InlineToolLoopRunner` with request-scoped key |
| Circuit breaker | read from workflow var; state already in API (`CircuitBreakerService`→`provider_health`, per-tenant) | **authoritative state already API-side**; endpoint records success/failure |
| Provider-chain advance | `ForEach<provider>` in workflow (`:360`) | **stays at workflow boundary** (thin step called once per provider per attempt) — minimal blast radius. (Richer `ProviderChainResolver` exists API-side but is currently bypassed — open decision to adopt) |
| Retry (429/502/503/504) | `RetryCheck` reads `LastDiagnostic.HttpStatusCode` (`:781`); **NO backoff delay today** | **stays at workflow boundary** — *only works if the endpoint returns `HTTP 200 + success:false + preserved httpStatusCode`* for expected provider failures (the load-bearing §2.4 contract; a raw 5xx would be nulled by `TammaApiClient.PostAsync`) |
| Budget | `CheckBudgetActivity` (API-first, fail-closed) | **→ endpoint gate** (server-side, before the call) |
| Concurrency | counts Elsa `Running` `llm-call` instances (`CheckLlmConcurrencyActivity`) | **open decision** — counting workflow instances is meaningless once the call runs in the API; a server-side per-tenant limiter is the more correct home |
| Metering | post-call cost (partly engine) | **→ endpoint**, emitted from API where `IEventRepository`+cabinet live |

Fail-closed posture is preserved end-to-end (credential-unavailable / breaker-open / budget-exhausted all **deny**, never call with an empty key) and surfaces as typed `success:false` codes.

---

## 3. Streaming — buffered for the engine, SSE for humans

**Correction to a prior assumption:** the current call does **not** stream tokens — `CallAnthropicMultiTurn`/`CallOpenAiMultiTurn` do a single blocking `PostAsync` + `ReadFromJsonAsync` (`CallLlmInlineActivity.cs:889,920,927`). "Streaming" today = tool-loop progress events, and even those are inert: `ToolLoopEventEmitter` → `IToolLoopEventSink` is wired only to `NullToolLoopEventSink` (events dropped), gated behind `EnableStreaming`. The seam exists; no live sink.

**Design — two response modes off one route, selected by `Accept`:**
- `application/json` → **buffered** (the engine step's default). The endpoint runs the tool loop to completion server-side and returns one `LlmCallResponse`. **The workflow stays request/response**, so `ForEach`/`RetryCheck`/`SkipIfSucceeded` are byte-for-byte unchanged. The thin `CallLlmInlineActivity` calls `TammaApiClient.CallLlmAsync(...)`, gets the result, writes the same `LastDiagnostic`/`LastResponse`/`ToolLoop*` variables.
- `text/event-stream` → **SSE** for human-facing clients (dashboard, `tamma` CLI). Wire a **real `IToolLoopEventSink` that writes each event as an SSE frame** (turning the inert seam live), reusing the existing SSE infra (`AdminTenantEventsSseEndpoint`/`EngineEndpoints` — `Response.ContentType=text/event-stream`, `FlushAsync`, `X-Accel-Buffering: no`, heartbeats). Frames: `token`, `tool_call`, `tool_result`, `question`, `answer`, `final`; correlated by `correlationId` (= workflow instance id, already threaded).

**Why the engine doesn't need SSE:** Elsa activities are durable-checkpointed request/response; holding an open socket across `MaxSteps` turns fights persistence + the `ForEach`-per-provider boundary. So: **engine = buffered; dashboard = SSE.** Provider token streaming (`stream:true`) can run *inside the runner* in the API to cut TTFB / allow mid-turn cancel, collapsed to a buffered turn result before the buffered response returns — invisible to the step. **Recommended:** a separate `GET /api/v1/llm/runs/{correlationId}/stream` tap fed by an in-process bus, so human observers are decoupled from the engine's buffered call.

---

## 4. Tools / MCP / plugins / cache / RAG / observability — all server-side

All of this moves into `Tamma.Api`, composed by `ManagedAgent.RunAsync`. The engine holds no key, runs no tool, opens no provider socket.

- **Tool loop** — extract `AgenticToolLoop` verbatim into `IInlineToolLoopRunner` (32-5 AC3); the registry/validator/parallel-executor/compactor/sanitizer all DI-registered in the API. **Delete** the engine-side tool/sanitizer/http registrations so the engine can't run tools. Local tools (`FileRead`/`ShellExecute`/`GitOperations`) execute against the tenant's sandbox *from inside the managed run*.
- **MCP + plugins** — **net-new for C#** (today only in TS `packages/mcp-client`; C# `ProviderSession.cs:87` says "MCP transport not yet ported"). The runner's tool catalog = built-in executors ∪ **MCP server tools** ∪ plugin tools, unioned through one `IToolExecutorRegistry.GetAllowed(allowlist)`, intersected with the agent's allowed-tool set (32-2), every invocation through the `ToolHookRegistry` pre/post sanitization hooks + `IContentSanitizer`/`RedactSecrets`. **Open:** port `mcp-client` to C# vs host the TS client as an API-managed sidecar vs a .NET MCP SDK; and MCP-server-per-tenant config + credentials (Epic 29 cabinet, enablement-gated like agents).
- **Cache** — two server-side layers (both new): (1) **provider prompt cache** — Anthropic `cache_control: ephemeral` on the stable prefix (system prompt + persona config + RAG/conventions block); the returned `cache_read`/`cache_write` token counts feed the meter (the `ProviderModelPrice` entity reserves nullable cache-rate columns). (2) optional **response cache** keyed by `(tenantId, agentVersion, renderedPromptHash, model, toolset)` — strictly **after** gate/budget so a hit still meters/audits (or records a `cache_hit` zero-cost usage event); tool-loop runs are likely *not* cacheable (non-deterministic side effects).
- **RAG** — keep `AssembleContextActivity`/`FetchSimilarPatternsActivity` as **pre-call workflow stages** that build `AssembledContext` and pass it as prompt variables in the `LlmCallRequest` (the existing `{{conventions}}`/variables merge is the template). The endpoint renders Epic 27 prompts (persona) or the custom agent's own prompts and merges the RAG block. **Open:** optionally move last-mile retrieval (`IntelligenceHttpClient`) into the managed run so RAG is also gated/metered — default keeps it a pre-call step.
- **Observability** — emitted from the API: a new OTel meter (`tamma.llm.call`/`.tokens`/`.cost`/`.toolloop.turns`), the server-authored `ProviderAttemptDiagnostic` (carrying `CredentialSource`/`BillingMode`), and DCB events `AGENT.RUN.STARTED/SUCCESS/FAILED`. `RecordDiagnosticsInlineActivity` collapses — the API writes the usage record directly.

---

## 5. Interactive question-back — who answers, and how the run pauses/resumes

**The gap (confirmed):** the tool loop **cannot ask a question back today** — the registry has 6 tools (`git_operations`/`shell_execute`/`file_read`/`file_write`/`run_tests`/`search_code`), none of them `ask_user`; the loop just ends the turn on a non-`ToolUse` stop reason, so a question becomes the run's final "answer" with nothing answering it. But the repo has **every primitive** to fix it.

### 5.1 The agent signals a question via a first-class `request_input` tool (a tool call, not a stop reason)
```jsonc
// tool: request_input — the agent's ONLY way to ask back
{ "question": "…", "kind": "fact"|"decision"|"judgment"|"approval",
  "options": [...] | null, "schema": {} | null,
  "blocking": true, "default_assumption": "…"|null, "confidence": 0.0 }
```
A tool call (not a parsed `end_turn`) means the answer returns as a **tool-result message**, so the model resumes its own reasoning with the answer in-context — no new conversation-shaping code; it flows through the existing validate→execute→append cycle. Executed server-side inside `/llm/call`, so **the step still never calls a provider**.

### 5.2 `IQuestionRouter` routes by `kind` + context (escalating cost/latency)
| `kind` | Answerer | Mechanism | Latency class |
|---|---|---|---|
| `fact` (system already knows) | **Orchestrator / workflow state** | synchronous lookup vs workflow vars + Epic-27 conventions + issue/PR context — **zero LLM, zero human** | in-stream (sub-second) |
| `judgment` (design/trade-off) | **Agent team / panel (32-7)** | `RunAgentPanelActivity`+`AggregatePanelActivity`, tenant-scoped, budget-clamped | in-stream / short in-process signal (seconds–min) |
| `decision` w/ closed options + confident default | **Orchestrator policy**, fallback panel | policy first, panel second | in-stream |
| `approval` / irreversible (merge/deploy/spend/schema) | **Human-in-the-loop** | durable Elsa bookmark + signal | **workflow-suspend** (hours–days) |

**The decision of *who answers* is a server-side `QuestionRoutingPolicy`** keyed `(principal, role, action, kind)` (tenant→system→error, fail-loud, never empty), with inputs: `kind`, **reversibility/blast-radius of the pending action**, the run's **autonomy level** (ADL limits config), and **budget**. `blocking:false` ⇒ orchestrator may auto-answer with `default_assumption` and record it as an audited assumption, never pausing.

**Security (load-bearing):** the model's `kind`/`blocking` are **hints**; the server **re-derives** the human-gate decision from the pending action's reversibility (which the orchestrator owns). The model can *raise* a question but **cannot downgrade** its routing below what the blast radius mandates — so a misclassifying/adversarial model can't tag a `merge`-approval as a `fact` to dodge human gating.

### 5.3 The pause/resume boundary — the core tension
A streaming HTTP request **cannot** stay open for an hours-long human answer. Split by latency class:
- **Fast (orchestrator-fact, agent-panel)** → resolved **inside the same `/llm/call` invocation**, on the server, bounded by an `inStreamAnswerTimeout` (e.g. 90s, tenant-tunable) with SSE heartbeats holding the connection. The turn never leaves the stream. Reuses the **`WebhookSignalRegistry` TCS** fast-signal model.
- **Slow (human)** → the turn ends and returns `success:false`, `failureCode = "INPUT_REQUIRED"` + the question + accrued `usage` (rides the existing fail-closed envelope; key never leaked). The thin step **does not retry** — it routes the *workflow* into a new **`WaitForAgentQuestionActivity`** (modeled byte-for-byte on `EscalateToSeniorActivity`): notify the human, `CreateBookmark("agent-question-{correlationId}")`, **suspend durably**. The human answers via `POST /api/v1/agents/questions/{correlationId}/answer` → `ElsaWorkflowService.SendSignalAsync`/resume → the workflow **re-invokes `/llm/call`** with the prior messages + the human answer re-primed as the `request_input` tool result, so the model resumes where it paused. The endpoint stays **stateless across the human gap** (conversation rehydrated from the request or the action-trail by `correlationId`).

**Boundary rule:** the durable wait lives in the **workflow** (Elsa bookmark), never in an HTTP connection. Fast answers resolve in-stream; slow answers cross engine→bookmark→signal→re-call. Cost: one extra LLM call to re-prime after the human gap — the only correct shape given a streaming request can't survive an hours-long wait.

**Audit:** `AGENT.QUESTION.RAISED` / `.ANSWERED` (tagged `answerer ∈ {orchestrator,panel,human}`) / `.ASSUMED` emitted from the API where the tenant store lives — full trail, time-travel-debuggable.

---

## 6. What this means for the stories

The endpoint is a **build-out**, not just a relocation. New/grown scope beyond the main re-plan:

1. **32-5 grows** — it owns: the buffered endpoint + `InlineToolLoopRunner` extraction + the resilience relocation (credential/breaker-record/budget/metering) + the buffered/SSE response modes + the live `IToolLoopEventSink`.
2. **NEW "MCP & plugin tool sourcing (C#)"** — port/host the MCP client + plugin tools into the API tool catalog with hooks + per-tenant MCP config (cabinet creds, enablement-gated). (Resolves the `ProviderSession.cs:87` gap; ties to Epic 6/9.)
3. **NEW "Prompt + response cache"** — Anthropic prompt-cache prefix + optional gated response cache + cache-rate metering columns.
4. **NEW "Streaming run tap"** — `GET /api/v1/llm/runs/{correlationId}/stream` SSE + the live sink for dashboard/CLI.
5. **NEW "Interactive question-back"** (Epic 32, depends on 32-5 + 32-7 panels) — the `request_input` tool + `IQuestionRouter` + `QuestionRoutingPolicy` + `WaitForAgentQuestionActivity` + the reversibility classifier + the question DCB events. **The single most novel piece.**
6. **DEFERRED single-user "C# harness/CLI adapter"** — port a `claude-code`/`opencode`/MCP harness path for single-user local execution (today TS-only; C# rejects it). Not needed for SaaS.

---

## 7. Consolidated open decisions (need a human call)

1. **Concurrency limiter** — server-side per-tenant limiter vs engine instance gate (current counts Elsa instances → meaningless after the move).
2. **Provider-chain ownership** — keep `ForEach` at the workflow (min blast radius) vs fold into the endpoint via the richer (currently-bypassed) `ProviderChainResolver` (CB-aware, half-open tail, budget-marked).
3. **MCP strategy** — port `mcp-client` to C# vs TS sidecar vs .NET MCP SDK; + per-tenant MCP config/credentials.
4. **Response-cache policy** — cacheable at all for tool-loop runs? key shape; metering of cache hits.
5. **In-stream timeout → escalation** for `judgment` — promote to human bookmark vs proceed-on-assumption vs fail (default is contentious vs the 70% autonomy target).
6. **Conversation-state rehydration across the human gap** — workflow variable vs action-trail (32-6) keyed by `correlationId` (leaning action-trail).
7. **`request_input` budgeting** — panel-answer tokens charged to the asking agent's budget vs a separate "clarification" line (affects 32-9/34-5).
8. **Harness execution in single-user** — retain a TS path vs port a C# adapter.
9. **HTTP-status fidelity guardrail** — a test/analyzer enforcing the endpoint returns `success:false`+`httpStatusCode` (not a raw 5xx) so `RetryCheck`/breaker keep working.
