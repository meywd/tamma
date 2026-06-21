# Story 32-22: Prompt + Response Cache (provider prompt cache + gated response cache)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform operator paying per-token for managed LLM execution**,
I want two server-side cache layers composed inside the managed run — (1) an **Anthropic provider prompt cache** that marks the stable prompt prefix (system prompt + persona config + RAG/conventions block) `cache_control: ephemeral` so repeated runs re-read the prefix cheaply, and (2) an **optional response cache** keyed by the rendered run identity that returns a prior completion **without re-calling the provider** — both wired so the returned `cache_read`/`cache_write` token counts meter against the cache-rate columns reserved on `ProviderModelPrice` (34-11), and so a response-cache hit **still runs gate → budget → metering → audit** (never bypassing billing),
So that **stable-prefix and deterministic single-turn runs cost less** while every call remains gated, metered, and auditable — the engine sees an unchanged buffered request/response and the API key never leaves `Tamma.Api`.

## Priority

P2 — A **cost-reduction layer**, not a correctness prerequisite. It rides entirely inside the `ManagedAgent.RunAsync` / `IInlineToolLoopRunner` composition built by **32-5** and consumes the cache-rate columns + `IProviderPricingService` seam reserved by **34-11**. Both layers are **off-by-default-safe**: with the prompt cache disabled the run behaves exactly as 32-5 ships it, and with the response cache disabled every call is a live metered run. Sequenced **after** 32-5 (the endpoint/runner) and 34-11 (the cache-rate cost columns); does not block any sibling pivot story.

## Context

### Where this sits (deep-dive §4 "Cache")

The managed-LLM deep dive (§4) specifies **two server-side cache layers, both new**, composed inside `ManagedAgent.RunAsync` / `IInlineToolLoopRunner` in `Tamma.Api`:

> **Cache** — two server-side layers (both new): (1) **provider prompt cache** — Anthropic `cache_control: ephemeral` on the stable prefix (system prompt + persona config + RAG/conventions block); the returned `cache_read`/`cache_write` token counts feed the meter (the `ProviderModelPrice` entity reserves nullable cache-rate columns). (2) optional **response cache** keyed by `(tenantId, agentVersion, renderedPromptHash, model, toolset)` — strictly **after** gate/budget so a hit still meters/audits (or records a `cache_hit` zero-cost usage event); tool-loop runs are likely *not* cacheable (non-deterministic side effects).

This story builds both layers and resolves the deep-dive **§7.4 open decision** (response-cache policy) with a recommended default.

### What exists after 32-5 / 34-11 (the seams this story extends)

- **32-5** ships `ManagedAgent.RunAsync` with the locked rule-2 compose order: gate (32-4) → resolve agent + enablement (32-18/32-16) → resolve credential BYOK→platform (32-3) → **render prompt (Epic 27)** → **provider call via `IInlineToolLoopRunner`** (request-scoped key, server-side) → **meter (`IProviderPricingService.Compute` + 32-9 usage event)** → return. The rendered prompt's **stable prefix** (Epic-27 system prompt + persona/agent config + RAG/conventions block assembled by the pre-call `AssembleContextActivity`) is exactly the span the prompt cache marks.
- **34-11** promotes the cost rate sheet to the `ProviderModelPrice` control-plane entity behind the unchanged `IProviderPricingService` seam and **reserves two nullable columns** — `CacheReadUsdPer1M (decimal?)` and `CacheWriteUsdPer1M (decimal?)` — explicitly "reserved" for exactly this story. `IProviderPricingService.Compute(provider, model?, in, out)` does **not** yet account for cache tokens; this story adds the cache-aware cost path.
- **32-9** is the usage/cost emitter; this story extends the usage record with `cache_read`/`cache_write` token splits and emits a `cache_hit` flavour for response-cache hits.
- **Epic 27** owns the prompt store — the resolved system prompt + persona/agent config that form the cacheable prefix.

### What this story does NOT do (out of scope — referenced, not built)

- **No streaming.** The SSE response mode / live `IToolLoopEventSink` / run tap are the "Streaming run tap" follow-on. This story is buffered request/response only; the engine is unaffected.
- **No markup math.** Cache-token cost feeds the **provider cost basis** only (`IProviderPricingService`); the 34-5 markup engine derives sell price downstream. This story does not price the sell side.
- **No new control-plane table.** The response cache is a **tenant-schema** store (`t_<hex>`), not a control-plane table — so it does **not** enter `Program.cs`'s startup-reset DROP list and does **not** touch `ControlPlaneDbContextModelTests`. The cache-rate columns live on `ProviderModelPrice`, owned by 34-11.
- **No cross-provider prompt cache.** Provider prompt caching is Anthropic-specific (`cache_control: ephemeral`); for non-Anthropic providers the prompt-cache layer is a no-op (the `cacheable?` predicate returns false), and the response cache is provider-agnostic.

## Acceptance Criteria

1. **Provider prompt cache — Anthropic only, on the stable prefix.** When the resolved provider is Anthropic (alias-normalized — `anthropic`/`anthropic-claude`/`claude` → `anthropic`), `IInlineToolLoopRunner` adds `cache_control: { type: "ephemeral" }` to the **stable prefix** of the request: the Epic-27 system prompt block + the persona/agent config block + the RAG/conventions block (`{{conventions}}` + assembled-context variables). The volatile task/user prompt is **never** marked cacheable. The cache breakpoint is placed at the boundary between the stable prefix and the volatile suffix. For all non-Anthropic providers this is a **no-op** (request unchanged).

2. **Prompt-cache config + default.** A new `PromptCacheOptions` (config-bound) controls the layer: `Enabled` (default **true** for Anthropic), and a `MinPrefixTokens` floor (default 1024 — below it, marking a breakpoint is not worthwhile). A persona/agent may opt out via its config. With `Enabled=false` the run is byte-for-byte the 32-5 baseline request.

3. **Cache-token metering wired to 34-11's reserved columns.** The Anthropic response's usage fields `cache_creation_input_tokens` (→ `cacheWriteTokens`) and `cache_read_input_tokens` (→ `cacheReadTokens`) are parsed and threaded through `InlineToolLoopResult` → `AgentRunResult` → the 32-9 usage record. A **new cache-aware cost path** on `IProviderPricingService` (`ComputeWithCache(provider, model?, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens)` or an additive overload) prices `cacheReadTokens` at `CacheReadUsdPer1M` and `cacheWriteTokens` at `CacheWriteUsdPer1M` when those columns are non-null, falling back to the plain input rate when a column is null (so an unpopulated cache rate never overcharges or throws). The plain `Compute(...)` from 34-11 is **unchanged**; cache pricing is additive.

4. **Response cache — keyed exactly per the design.** An optional response cache stores a completed run's `text` + `usage` keyed by `(tenantId, agentVersion, renderedPromptHash, model, toolset)` where `renderedPromptHash` is a SHA-256 of the fully-rendered prompt (system + merged variables + user prompt, post-Epic-27 render), `model` is the resolved model, and `toolset` is the canonical-sorted allowed-tool set. The cache is a tenant-schema store (`t_<hex>.agent_response_cache`), per-tenant (never cross-tenant — a key from tenant A can never hit tenant B's entry). Entries carry a TTL (`ResponseCacheOptions.Ttl`, default 24h) and are bounded (LRU / count cap per tenant).

5. **The response cache is applied STRICTLY AFTER gate + budget — never bypassing billing or audit.** The cache lookup happens **after** compose steps 1 (gate/32-4) and the budget guard, and **before** the provider call. On a hit, the run **still**: passes the gate, passes the budget check, **emits a metered usage event** (`cache_hit` flavour) and the terminal `AGENT.RUN.SUCCESS` DCB event. A cache hit **never** skips gating, entitlement, budget, metering, or audit. The provider is not called; the cached `text`/`usage` is returned.

6. **Cache-hit usage is metered at zero provider cost but fully recorded.** A response-cache hit records a usage event with `cacheHit=true`, `providerCostUsd=0` (no provider tokens were spent), the cached token counts preserved for reporting, and `BillingMode` from `credentialSource`. Whether a hit is billed to the tenant at all is a **34-5/35 policy decision** (default: a cache hit is a zero-cost-basis usage event → zero sell price); this story only guarantees the hit is **recorded and audited**, never silent.

7. **Tool-loop runs are NOT response-cached (documented policy + predicate).** A `IResponseCacheabilityPolicy.IsCacheable(req, resolved)` predicate returns **false** when `enableToolLoop == true` (tool calls have non-deterministic side effects — file writes, shell, git — that a cached replay would skip), when `temperature > 0` beyond a configurable determinism threshold (default: cacheable only at `temperature <= 0`), or when the agent/persona opts out. Single-turn deterministic renders (`enableToolLoop==false`, `temperature<=threshold`) are cacheable. The predicate is the single documented gate; its reasons are logged.

8. **Recommended defaults (resolving deep-dive §7.4).** Ship: **prompt cache ON** for Anthropic (AC1/AC2); **response cache OFF for tool-loop runs** (AC7), **ON for single-turn deterministic renders** (`enableToolLoop==false && temperature<=0`); response-cache default **disabled globally** until a tenant/operator opts in via `ResponseCacheOptions.Enabled` (conservative — a stale or wrong cached answer is worse than a re-spend). All four knobs (`PromptCacheOptions.Enabled`, `ResponseCacheOptions.Enabled`, `Ttl`, the determinism threshold) are config-bound and per-tenant-overridable where the platform supports it.

9. **Buffered-only; engine unaffected.** Both layers live entirely inside `Tamma.Api`. The `CallLlmInlineActivity` thin client, the `LlmCallRequest`/`LlmCallResponse` wire contract, `LlmCallWorkflow.cs`'s `ForEach`/`RetryCheck`/circuit-breaker boundary, and the engine's buffered request/response are **byte-for-byte unchanged**. The `LlmCallResponse.usage` gains `cacheReadTokens`/`cacheWriteTokens`/`cacheHit` fields (additive, defaulting to 0/false), so a workflow that ignores them is unaffected.

10. **Fail-open on cache failure, fail-closed on gate/budget (no-empty-fallback respected).** A response-cache **read** error (store unavailable, deserialize failure) → **fall through to a live provider call** (the cache is an optimization, never a correctness dependency) and log a WARN — but the gate/budget/credential resolution upstream remain **fail-closed** (an unevaluable gate/budget/credential still denies, per `feedback_resolution_no_empty_fallback`; the cache fail-open applies ONLY to the cache layer itself, never to gating). A cache **write** error never fails the run.

11. **Credential safety.** Neither cache layer ever stores, logs, or keys on the provider API key. The `renderedPromptHash` is a hash, not the prompt plaintext; the cache value stores completion text + token counts only. No key, no `BaseUrl` auth, no provider header appears in any cache entry, log line, or DCB event.

12. **Tests cover both layers + the policy + the metering.** Prompt-cache prefix marking (Anthropic adds `cache_control` to the stable prefix only / non-Anthropic no-op / disabled = baseline request); cache-token parse → meter wiring (cache_read/write tokens priced at the reserved columns, null-column fallback, plain `Compute` unchanged); response-cache hit path (after gate+budget, emits metered `cache_hit` usage + terminal DCB event, provider not called); cacheability predicate (tool-loop → not cacheable, temperature>threshold → not cacheable, single-turn deterministic → cacheable); per-tenant isolation (tenant A key never hits tenant B); fail-open on cache read error → live call; credential-never-in-cache; the `LlmCallResponse.usage` additive fields default to 0/false.

## Acceptance Criteria — non-goals (YAGNI guard)

- No distributed cache backend selection (Redis/etc.) — the response cache is a tenant-schema table with an in-process snapshot; a distributed backend is a later optimization.
- No semantic/embedding-similarity cache — exact `renderedPromptHash` match only.
- No prompt-cache support for non-Anthropic providers (their APIs differ; out of scope until a provider needs it).

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  ManagedAgent.cs                       # MODIFY (32-5) — compose the response-cache lookup AFTER gate+budget, BEFORE the loop; thread cache-token usage into the meter
  ResponseCacheOptions.cs               # NEW — { Enabled(false), Ttl(24h), MaxEntriesPerTenant, DeterminismTemperatureThreshold(0) }
  PromptCacheOptions.cs                 # NEW — { Enabled(true), MinPrefixTokens(1024) }
  IResponseCache.cs                     # NEW — TryGet / Set, keyed by ResponseCacheKey, tenant-scoped
  ResponseCache.cs                      # NEW — tenant-schema store (t_<hex>.agent_response_cache) + in-process LRU snapshot
  ResponseCacheKey.cs                   # NEW — record { TenantId, AgentVersion, RenderedPromptHash, Model, Toolset }
  IResponseCacheabilityPolicy.cs        # NEW — IsCacheable(req, resolved) -> (bool, reason)
  ResponseCacheabilityPolicy.cs         # NEW — tool-loop / temperature / opt-out gate (AC7/AC8)

apps/tamma-elsa/src/Tamma.Activities/LlmCall/
  IInlineToolLoopRunner.cs              # MODIFY (32-5) — InlineToolLoopResult gains CacheReadTokens / CacheWriteTokens
  InlineToolLoopRunner.cs               # MODIFY (32-5) — Anthropic prompt-cache prefix marking (AC1/AC2); parse cache_creation/cache_read usage (AC3)

apps/tamma-elsa/src/Tamma.Api/Services/Providers/
  IProviderPricingService.cs            # UNCHANGED seam (34-11) — Compute / IsKnown stay; add the additive cache path on the impl
  DbProviderPricingService.cs           # MODIFY (34-11) — ComputeWithCache(...) prices CacheReadUsdPer1M / CacheWriteUsdPer1M; null-column fallback

apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  LlmCallResponse.cs                    # MODIFY (32-5) — UsageDto gains CacheReadTokens / CacheWriteTokens / CacheHit (additive)

apps/tamma-elsa/src/Tamma.ElsaServer/migrations  (per-tenant EfTenantDbMigrator)
  <timestamp>_AddAgentResponseCache.cs  # NEW — t_<hex>.agent_response_cache (tenant-schema, NOT control-plane)

apps/tamma-elsa/src/Tamma.Api/Program.cs
  Program.cs                            # MODIFY — register IResponseCache, IResponseCacheabilityPolicy, bind PromptCacheOptions/ResponseCacheOptions
```

### Layer 1 — provider prompt cache (Anthropic `cache_control: ephemeral`)

`InlineToolLoopRunner` builds the Anthropic request. When `PromptCacheOptions.Enabled` and the alias-normalized provider is `anthropic` and the prefix exceeds `MinPrefixTokens`, the stable-prefix blocks are marked:

```jsonc
// Anthropic /v1/messages — cache breakpoint at the stable/volatile boundary
{
  "model": "claude-sonnet-4-20250514",
  "system": [
    { "type": "text", "text": "<Epic-27 system prompt + persona config>",
      "cache_control": { "type": "ephemeral" } }     // STABLE PREFIX — cached
  ],
  "messages": [
    { "role": "user", "content": [
      { "type": "text", "text": "<RAG/conventions block>",
        "cache_control": { "type": "ephemeral" } },   // STABLE PREFIX — cached
      { "type": "text", "text": "<volatile task/user prompt>" }   // NOT cached
    ]}
  ]
}
```

The response usage is parsed:

```csharp
// Anthropic response.usage:
//   input_tokens, output_tokens,
//   cache_creation_input_tokens  -> cacheWriteTokens   (first-write of the prefix)
//   cache_read_input_tokens      -> cacheReadTokens     (subsequent cheap re-reads)
public sealed record InlineToolLoopResult(
    NormalizedLlmResponse Response,
    int InputTokens, int OutputTokens,
    int CacheReadTokens, int CacheWriteTokens,   // NEW — threaded to the meter
    int Turns, bool Exhausted,
    IReadOnlyList<ToolCallSummary> ToolCalls);
```

> **Note (research before implementing):** Anthropic's prompt-cache API shape (`cache_control` placement, the `cache_creation_input_tokens`/`cache_read_input_tokens` usage fields, 1024-token minimums, cache-write vs cache-read pricing multipliers) must be re-confirmed against the **latest** Anthropic docs at implementation time — do not assume the field names/limits from memory.

### Cache-aware cost (additive over 34-11's seam)

```csharp
// IProviderPricingService stays unchanged (34-11). The DB impl gains an additive cache path:
public decimal ComputeWithCache(
    string provider, string? model,
    int inputTokens, int outputTokens,
    int cacheReadTokens, int cacheWriteTokens)
{
    var baseCost = Compute(provider, model, inputTokens, outputTokens); // 34-11, unchanged
    var price = ResolveActiveRow(provider, model);                       // ProviderModelPrice row
    var cacheReadCost  = price?.CacheReadUsdPer1M  is { } r ? cacheReadTokens  / 1_000_000m * r
                                                            : 0m;        // null column => no cache surcharge
    var cacheWriteCost = price?.CacheWriteUsdPer1M is { } w ? cacheWriteTokens / 1_000_000m * w
                                                            : 0m;
    return baseCost + cacheReadCost + cacheWriteCost;
}
```

When a cache-rate column is null (34-11 ships them nullable/unseeded), the cache tokens contribute **zero** surcharge — they are still **reported** in the usage record, just not yet priced. Populating those columns later (admin write, 34-11 `PUT .../prices`) starts pricing them with no code change.

### Layer 2 — response cache (composed in `ManagedAgent.RunAsync`)

```
ManagedAgent.RunAsync (32-5 order, with the cache spliced in):
1. gate           (32-4)                                       # unchanged
2. resolve agent + enablement (32-18/32-16)                    # unchanged
3. resolve credential BYOK->platform (32-3)                    # unchanged
4. render prompt  (Epic 27)                                    # unchanged -> produces the rendered prompt
   budget guard   (fail-closed)                                # unchanged
   --- response cache splices in HERE: after gate+budget, before the call ---
4b. if ResponseCacheOptions.Enabled
       && _cacheability.IsCacheable(req, resolved).Cacheable:    # AC7 predicate (tool-loop => false)
       key = ResponseCacheKey(tenantId, resolved.AgentVersion,
                              Sha256(renderedPrompt), resolved.Model, SortedToolset)
       if _cache.TryGet(tenantId, key) is { } hit:
           emit usage (32-9) { CacheHit=true, ProviderCostUsd=0, tokens=hit.Usage, BillingMode=credentialSource }   # AC5/AC6
           emit AGENT.RUN.SUCCESS { ..., cacheHit:true }
           return AgentRunResult.FromCache(hit)                  # provider NOT called
5. loop           IInlineToolLoopRunner (request-scoped key)    # cache MISS -> live call
6. costBasis      _pricing.ComputeWithCache(...)                # AC3 cache-aware
   emit usage (32-9) { ..., CacheReadTokens, CacheWriteTokens, CacheHit=false }
7. if IsCacheable: _cache.Set(tenantId, key, { text, usage }, Ttl)   # write-through (errors swallowed, AC10)
8. emit AGENT.RUN.SUCCESS | FAILED
9. return AgentRunResult
```

The lookup is **strictly after** gate (step 1) and budget (step 4) so a hit cannot dodge gating/billing. The write-through (step 7) is best-effort — a write failure logs WARN and the run still returns.

### Tenant-schema cache store

```sql
-- t_<hex>.agent_response_cache  (per-tenant schema — owned by EfTenantDbMigrator, NOT control-plane)
CREATE TABLE agent_response_cache (
  cache_key          TEXT PRIMARY KEY,        -- Sha256 of the ResponseCacheKey tuple
  agent_version      INTEGER NOT NULL,
  rendered_prompt_hash TEXT NOT NULL,
  model              TEXT NOT NULL,
  toolset            TEXT NOT NULL,           -- canonical-sorted allowed-tool set
  response_text      TEXT NOT NULL,
  usage_json         JSONB NOT NULL,          -- token counts (key-free)
  created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at         TIMESTAMPTZ NOT NULL
);
CREATE INDEX idx_agent_response_cache_expires ON agent_response_cache (expires_at);
```

This is a **tenant-schema** table created by the per-tenant `EfTenantDbMigrator` — it does **NOT** go in `Program.cs`'s startup-reset public-schema DROP list, and it does **NOT** alter `ControlPlaneDbContextModelTests`. No `tenantId` column is needed: the schema **is** the tenant boundary (per-tenant isolation, AC4).

### Cacheability policy

```csharp
public interface IResponseCacheabilityPolicy
{
    (bool Cacheable, string Reason) IsCacheable(ManagedAgentRequest req, ResolvedAgentConfig resolved);
}
// ResponseCacheabilityPolicy:
//   enableToolLoop == true               -> (false, "tool-loop: non-deterministic side effects")
//   temperature  >  threshold (def 0)    -> (false, "non-deterministic temperature")
//   agent/persona opted out              -> (false, "agent opt-out")
//   else                                 -> (true,  "single-turn deterministic")
```

## Dependencies

**Internal (hard prerequisites):**

- **32-5** (Call-LLM endpoint + managed execution) — supplies `ManagedAgent.RunAsync`, `IInlineToolLoopRunner`/`InlineToolLoopResult`, `LlmCallResponse`/`UsageDto`, the buffered request/response, and the compose order this story splices into. (Sequence F.)
- **34-11** (Provider Cost Price-Book) — supplies the `ProviderModelPrice` entity with the reserved nullable `CacheReadUsdPer1M`/`CacheWriteUsdPer1M` columns and the `IProviderPricingService` seam the cache-aware cost path extends. (Sequence A.)
- **32-9** (usage & cost metering) — the usage emitter this story extends with cache-token splits + the `cache_hit` flavour.
- **Epic 27** (prompt/convention render) — produces the stable prompt prefix (system prompt + persona config + RAG/conventions block) that the prompt cache marks and that the `renderedPromptHash` covers.

**Consumers (downstream, not blockers):**

- **34-5** (markup) / **35** (billing) — decide whether/how a `cache_hit` usage event bills the tenant (default: zero cost basis → zero sell).
- **36** (analytics) — reports cache-hit rate, cache-read/write token splits, and cache-driven cost savings from the metered fields.

**External:** Anthropic Messages API prompt-caching feature (`cache_control: ephemeral`, the `cache_creation_input_tokens`/`cache_read_input_tokens` usage fields) — re-confirm the latest API shape at implementation time.

## Testing Strategy

1. **Prompt-cache prefix marking (AC1/AC2).** Anthropic provider + `Enabled=true` → `cache_control:{type:ephemeral}` appears on the system-prompt block and the RAG/conventions block, **not** on the volatile user prompt; a fake captures the outgoing request body and asserts the breakpoint position.
2. **Non-Anthropic no-op + disabled baseline (AC1/AC2).** OpenAI provider → request has no `cache_control`; Anthropic + `Enabled=false` → request is byte-for-byte the 32-5 baseline.
3. **Cache-token parse → meter (AC3).** A fake Anthropic response with `cache_creation_input_tokens`/`cache_read_input_tokens` → `InlineToolLoopResult.CacheWriteTokens`/`CacheReadTokens` populated → `ComputeWithCache` prices them at `CacheReadUsdPer1M`/`CacheWriteUsdPer1M`; null columns → zero surcharge (still reported); plain `Compute` output unchanged from 34-11.
4. **Response-cache hit after gate+budget (AC5/AC6).** Seed a cache entry; a matching request → gate + budget evaluated, provider runner **never invoked** (spy), a `cache_hit` usage event emitted (`providerCostUsd=0`, tokens preserved), exactly one `AGENT.RUN.SUCCESS` (`cacheHit:true`).
5. **Cacheability predicate (AC7/AC8).** `enableToolLoop=true` → not cacheable (no `Set`, no lookup-hit path); `temperature>threshold` → not cacheable; `enableToolLoop=false, temperature=0` → cacheable; opt-out → not cacheable; reasons logged.
6. **Per-tenant isolation (AC4).** Same `(agentVersion, promptHash, model, toolset)` for tenant A and tenant B → A's entry never returned to B (separate `t_<hex>` schemas).
7. **Fail-open on cache read error (AC10).** Cache store throws on `TryGet` → run falls through to a live provider call, WARN logged, result correct; gate/budget remain fail-closed (an unevaluable budget still denies).
8. **Write-through best-effort (AC10).** `Set` throws → run still returns its result; WARN logged.
9. **Additive wire fields (AC9).** `LlmCallResponse.usage` gains `cacheReadTokens`/`cacheWriteTokens`/`cacheHit` defaulting to 0/0/false; a workflow ignoring them is unaffected; `LlmCallWorkflow.cs` diff is empty.
10. **Credential safety (AC11).** Assert no API key / `BaseUrl` auth / provider header appears in any cache entry, the `usage_json` payload, any log line, or DCB event; the cache key is a hash, never the prompt plaintext.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

4-6 days (two cache layers + the cache-aware cost path + the tenant-schema migration + the cacheability policy + the metering wiring + tests). Smaller than 32-5 because it rides the existing compose order and seams rather than building them.

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/PromptCacheOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResponseCacheOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IResponseCache.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResponseCache.cs` | Create (tenant-schema store + in-process LRU) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResponseCacheKey.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IResponseCacheabilityPolicy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResponseCacheabilityPolicy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` | Modify (splice cache lookup after gate+budget; cache-aware meter) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/LlmCallResponse.cs` | Modify (UsageDto += CacheReadTokens/CacheWriteTokens/CacheHit) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/IInlineToolLoopRunner.cs` | Modify (InlineToolLoopResult += cache tokens) |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/InlineToolLoopRunner.cs` | Modify (Anthropic prefix marking + cache-usage parse) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/DbProviderPricingService.cs` | Modify (ComputeWithCache additive path) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/.../<timestamp>_AddAgentResponseCache.cs` | Create (tenant-schema migration via EfTenantDbMigrator) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register cache + policy; bind options) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ResponseCacheTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ResponseCacheabilityPolicyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ManagedAgentCacheCompositionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/PromptCachePrefixTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/CacheAwarePricingTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`).
3. Read the managed-LLM deep dive §4 (cache), §6 item 3, and §7 item 4 (the open decision this story resolves) IN FULL, plus 32-5 (the compose order/seams you splice into) and 34-11 (the reserved cache-rate columns).
4. Re-confirmed the **latest** Anthropic prompt-cache API shape (`cache_control` placement, `cache_creation_input_tokens`/`cache_read_input_tokens` usage fields, the 1024-token minimum, cache-write/cache-read pricing multipliers) against current Anthropic docs — never from memory.
5. Confirmed 32-5 and 34-11 are landed (their seams — `ManagedAgent.RunAsync`, `IInlineToolLoopRunner`, `ProviderModelPrice` cache columns, `IProviderPricingService`) before wiring.
6. Planned the TDD approach: the response-cache splice is an **additive composition** around the 32-5 order — assert the lookup runs after gate+budget and a hit still meters/audits.

### Key Design Decisions

- **Two independent layers, independently disable-able.** Prompt cache (Layer 1, Anthropic-only, ON by default) reduces the cost of a *single live call*; response cache (Layer 2, provider-agnostic, OFF by default) avoids the call entirely for deterministic repeats. They share nothing but the meter; either can ship/disable without the other.
- **Response cache is after gate + budget, never before (AC5).** This is the load-bearing rule from the deep-dive §4: a cache hit must still pass gating and **emit a metered, audited usage event**. A cache that bypassed billing would be a compliance hole (silent unbilled runs) and an audit gap (runs with no DCB trail). The hit is zero *provider* cost, not zero *record*.
- **Tool-loop runs are not cacheable (AC7) — resolving deep-dive §7.4.** Tool calls have non-deterministic side effects (file writes, shell, git); replaying a cached completion would skip the side effects, corrupting state. The recommended default: **response cache OFF for tool-loop runs, ON for single-turn deterministic renders (`temperature<=0`); prompt cache always ON for Anthropic.** The cacheability predicate is the single documented gate; its decision is logged.
- **Cache-rate columns are nullable and may be unseeded (34-11).** The cache-aware cost path treats a null column as a zero surcharge (cache tokens reported but unpriced) so an unpopulated rate never overcharges or throws; populating the column later (admin `PUT .../prices`) starts pricing with no code change.
- **Cache fail-open, gate fail-closed (AC10).** The cache is an *optimization* — a read/write error falls through to a live metered call. This does **not** weaken the upstream fail-closed posture: an unevaluable gate/budget/credential still denies (`feedback_resolution_no_empty_fallback`). The fail-open boundary is the cache layer only.
- **No new control-plane table.** The response cache is tenant-schema (`t_<hex>.agent_response_cache`), owned by the per-tenant `EfTenantDbMigrator` — so it does **not** enter `Program.cs`'s startup-reset DROP list and does **not** edit `ControlPlaneDbContextModelTests`. The cache-rate columns live on `ProviderModelPrice` (34-11's control-plane entity, its DROP-list/CP-test obligations, not this story's).
- **Buffered-only; engine unaffected.** Both layers are server-side inside `Tamma.Api`; the engine sees an unchanged buffered request/response. The additive `LlmCallResponse.usage` fields default to 0/false so the engine and any workflow ignoring them are byte-for-byte unaffected.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the response-cache entries? | The sole user (the entries live in the user's sole tenant schema; keyed `TenantId` may be the implicit user scope). | The tenant — entries live in the tenant's `t_<hex>.agent_response_cache`. No per-user cache layer. |
| Can a cache entry cross principals? | N/A (one user). | **Never** — the cache key includes `TenantId` and the store is the tenant schema; tenant A's entry can never hit tenant B (AC4). |
| Who configures prompt/response cache on/off + TTL? | The sole user (their settings own `PromptCacheOptions`/`ResponseCacheOptions`). | The tenant (`tenant_owner`/`tenant_admin`); members cannot toggle. Platform ships the conservative defaults (prompt ON / response OFF). |
| Whose cost do cache-read/write tokens reduce? | The sole user's provider cost basis (their BYOK or platform key). | The tenant's provider cost basis (`credentialSource` decides BYOK vs platform + 34-5 markup). |
| Where does a `cache_hit` usage event land? | The user's (sole) tenant event store. | The tenant's `t_<hex>` event store via the tenant-scoped emitter (32-9). Never cross-tenant. |
| Who reads cache-hit-rate analytics? | The user. | The tenant — platform admin sees none of the tenant's cache/cost data (design ownership rule). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| A cache hit silently bypasses billing/audit (compliance hole) | Critical | AC5 — the lookup is strictly after gate+budget; a hit emits a metered `cache_hit` usage event + terminal DCB event; test asserts the meter/audit fire on a hit (provider never called). |
| A tool-loop run is cached → replay skips side effects, corrupting state | Critical | AC7 — `IResponseCacheabilityPolicy` returns false for `enableToolLoop==true`; default response-cache OFF for tool-loop runs; predicate test. |
| Cross-tenant cache leak | Critical | AC4 — key includes `TenantId`; the store IS the tenant schema (`t_<hex>`); per-tenant-isolation test (A's key never hits B). |
| Stale cached answer returned after a prompt/agent change | High | The key includes `agentVersion` + `renderedPromptHash` + `model` + `toolset`, so any change to the agent, prompt render, model, or tools is a cache miss; TTL bounds staleness; response cache defaults OFF. |
| Cache-rate columns unseeded → cache tokens mispriced | Medium | Null column → zero surcharge (reported, not priced); never overcharges/throws; admin populates later with no code change (34-11). |
| Cache store failure breaks a run | Medium | AC10 — fail-open: read/write errors fall through to a live metered call; WARN logged; gate/budget stay fail-closed. |
| Anthropic prompt-cache API drift (field names/limits) | Medium | Re-confirm the latest Anthropic docs at implementation time (Dev Notes step 4); the parse is isolated in `InlineToolLoopRunner` behind `PromptCacheOptions`. |
| New tenant-schema table forgotten in DROP list | Low | It's a tenant-schema (`t_<hex>`) table owned by `EfTenantDbMigrator` — it deliberately does NOT go in the public-schema DROP list; Dev Notes calls this out. |

### Success Metrics

- [ ] Anthropic runs mark `cache_control:ephemeral` on the stable prefix only; non-Anthropic runs are unchanged.
- [ ] `cache_read`/`cache_write` token counts appear in 100% of Anthropic usage records and price at the reserved columns when populated.
- [ ] 100% of response-cache hits emit a metered `cache_hit` usage event + a terminal `AGENT.RUN.SUCCESS` (zero silent/unbilled hits).
- [ ] Zero tool-loop runs are response-cached (predicate enforced).
- [ ] Zero cross-tenant cache hits; zero API keys in any cache entry/log/event.
- [ ] `LlmCallWorkflow.cs` diff is empty (engine unaffected).

## Related

- Managed-LLM deep dive: `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§4 cache — the two layers; §6 item 3 — "Prompt + response cache" scope; §7 item 4 — the response-cache-policy open decision this story resolves)
- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§4 the Provider/`ProviderModelPrice` cost entity reserving the cache-rate columns)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-22-prompt-and-response-cache-plan.md`
- Sibling stories: `story-32-5/` (call-LLM endpoint + managed execution — the compose order/seams this splices into), `story-32-9/` (usage & cost metering — the emitter extended with cache tokens + `cache_hit`); `docs/stories/epic-34/story-34-11/` (Provider Cost Price-Book — the reserved `CacheReadUsdPer1M`/`CacheWriteUsdPer1M` columns + `IProviderPricingService`); `story-32-15/`/`story-32-17/` (persona/custom-agent prompts that form the cacheable prefix); Epic 27 (prompt store)
- Reused code: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs`, `apps/tamma-elsa/src/Tamma.Activities/LlmCall/InlineToolLoopRunner.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Providers/DbProviderPricingService.cs`

## Logging Requirements

- **INFO**: prompt-cache marked (correlationId, provider=anthropic, prefixTokensEstimate); response-cache hit (correlationId, agentVersion, model — **never the prompt plaintext or hash-preimage**); response-cache write-through (correlationId, ttlSeconds); cache-aware cost computed (providerCostUsd incl. cache surcharge, cacheReadTokens, cacheWriteTokens).
- **DEBUG**: cacheability decision (`cacheable`, `reason` — tool-loop / temperature / opt-out / single-turn-deterministic); cache key components (agentVersion, model, toolset, **hash only — never the prompt plaintext**); prompt-cache breakpoint position.
- **WARN**: response-cache read error → fell through to live call (correlationId, error class — no payload); write-through error; cache-rate column null while cache tokens > 0 (cost under-reported until the column is seeded).
- **ERROR**: cacheability predicate threw (run proceeds uncached — never blocks the call); usage/DCB append failure on a cache hit (the hit still returns its result; the append failure is logged, not swallowed).
- **Structured context**: `{ correlationId, tenantId, agentVersion, provider, model, cacheHit, cacheReadTokens, cacheWriteTokens }` where applicable.
- **Credential safety (LOAD-BEARING)**: NEVER log, store, or key on the provider API key, `BaseUrl` auth, or raw provider headers. The cache key is a **hash** of the rendered prompt, never the plaintext; the cache value stores completion text + token counts only. No cache entry, `usage_json` payload, log line, or DCB event payload contains a key.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — two server-side cache layers (Anthropic `cache_control:ephemeral` prompt cache on the stable prefix + optional gated response cache) composed inside 32-5's `ManagedAgent.RunAsync`/`IInlineToolLoopRunner`; cache-token metering wired to 34-11's reserved `CacheReadUsdPer1M`/`CacheWriteUsdPer1M` columns; response cache applied strictly after gate+budget (a hit still meters/audits via a `cache_hit` usage event); tool-loop runs documented as non-cacheable via `IResponseCacheabilityPolicy`; resolves deep-dive §7.4 with recommended defaults (prompt cache ON for Anthropic; response cache OFF for tool-loop, ON for single-turn deterministic, globally OFF until opted in). Buffered-only; engine unaffected. | Claude |
