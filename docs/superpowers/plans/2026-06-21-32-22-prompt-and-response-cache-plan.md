# Story 32-22 — Prompt + Response Cache (provider prompt cache + gated response cache)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Add two server-side cache layers inside the managed-LLM run built by 32-5, both composed in
`Tamma.Api` so the engine sees an unchanged buffered request/response:
1. **Provider prompt cache** — mark the stable prompt prefix (Epic-27 system prompt + persona/agent
   config + RAG/conventions block) with Anthropic `cache_control: ephemeral`; parse the returned
   `cache_creation_input_tokens`/`cache_read_input_tokens` and price them at 34-11's reserved
   nullable `CacheReadUsdPer1M`/`CacheWriteUsdPer1M` columns.
2. **Optional response cache** keyed `(tenantId, agentVersion, renderedPromptHash, model, toolset)`,
   applied **strictly after** gate+budget so a hit still meters + audits (a `cache_hit` usage event),
   never bypassing billing. Tool-loop runs are **not** cacheable (documented policy + predicate).

Resolves the deep-dive **§7.4** open decision with recommended defaults: prompt cache ON for
Anthropic; response cache OFF for tool-loop runs, ON for single-turn deterministic renders
(`temperature<=0`), globally OFF until a tenant/operator opts in.

**Story file:** `docs/stories/epic-32/story-32-22/32-22-prompt-and-response-cache.md`
**Design specs:** `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` (§4 cache,
§6.3, §7.4) · `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§4 the
Provider/`ProviderModelPrice` cost entity reserving the cache-rate columns)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api` + activities
`Tamma.Activities` + engine `Tamma.ElsaServer`). Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/`
and `apps/tamma-elsa/tests/Tamma.Activities.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no
wrapper). **`packages/api` is DELETED — all C#.**

---

## Non-goals (YAGNI guard)

- **NO distributed cache backend (Redis/etc.).** The response cache is a tenant-schema table
  (`t_<hex>.agent_response_cache`) + an in-process LRU snapshot. A distributed backend is a later
  optimization, not this story.
- **NO semantic / embedding-similarity cache.** Exact `renderedPromptHash` match only.
- **NO non-Anthropic prompt cache.** Provider prompt caching is Anthropic-specific; for every other
  provider the prompt-cache layer is a no-op. (The response cache is provider-agnostic.)
- **NO markup / billing decision.** Cache tokens feed the **provider cost basis** only
  (`IProviderPricingService`); whether a `cache_hit` bills the tenant is 34-5/35. This story only
  guarantees the hit is **recorded + audited**.
- **NO change to 32-5's compose order, the `LlmCallRequest`/`LlmCallResponse` contract semantics, or
  the `LlmCallWorkflow.cs` boundary.** This story is **additive**: a cache lookup spliced after
  gate+budget, cache-token usage fields added to the meter + wire usage (defaulting to 0/false).
- **NO new control-plane table.** The response cache is tenant-schema; the cache-rate columns belong
  to 34-11's `ProviderModelPrice`. → no `Program.cs` DROP-list entry, no `ControlPlaneDbContextModelTests` edit.

---

## Prerequisites (must be landed first)

- **32-5** — `ManagedAgent.RunAsync` (the compose order), `IInlineToolLoopRunner`/`InlineToolLoopResult`,
  `LlmCallResponse`/`UsageDto`, the buffered request/response. **This story modifies these files.**
- **34-11** — `ProviderModelPrice` with reserved nullable `CacheReadUsdPer1M`/`CacheWriteUsdPer1M`,
  `DbProviderPricingService : IProviderPricingService` (`Compute`/`IsKnown`). **This story adds the
  additive `ComputeWithCache` path.**
- **32-9** — the usage emitter, extended here with cache-token splits + the `cache_hit` flavour.
- **Epic 27** — the prompt store producing the cacheable stable prefix + the rendered prompt hashed
  into the response-cache key.

If any prerequisite isn't landed, code to its interface and gate behind it (use fakes in tests).

---

## Architecture (where the two layers splice in)

```
ManagedAgent.RunAsync (32-5 order — cache splices are additive):
  1. gate (32-4)                                   unchanged
  2. resolve agent + enablement (32-18/32-16)      unchanged
  3. resolve credential BYOK->platform (32-3)      unchanged
  4. render prompt (Epic 27)  + budget guard       unchanged
  ── RESPONSE CACHE (Layer 2) splices in HERE: after gate+budget, before the call ──
  4b. if ResponseCacheOptions.Enabled
        && IResponseCacheabilityPolicy.IsCacheable(req, resolved):     # tool-loop => false
          key = (tenantId, agentVersion, Sha256(renderedPrompt), model, sortedToolset)
          if IResponseCache.TryGet(tenantId, key) => HIT:
              emit metered cache_hit usage (32-9, providerCostUsd=0)   # still meters + audits
              emit AGENT.RUN.SUCCESS { cacheHit:true }
              return AgentRunResult.FromCache(hit)                     # provider NOT called
  5. loop  IInlineToolLoopRunner (request-scoped key)                  # cache MISS -> live call
     ── PROMPT CACHE (Layer 1) lives INSIDE the runner: Anthropic cache_control on the stable prefix;
        parse cache_creation/cache_read usage -> InlineToolLoopResult.CacheWrite/ReadTokens
  6. costBasis = IProviderPricingService.ComputeWithCache(..., cacheReadTokens, cacheWriteTokens)
     emit usage (32-9) { CacheReadTokens, CacheWriteTokens, CacheHit=false }
  7. if IsCacheable: IResponseCache.Set(tenantId, key, {text, usage}, Ttl)   # write-through, errors swallowed
  8. emit AGENT.RUN.SUCCESS | FAILED
  9. return AgentRunResult
```

**Layer 1 (prompt cache)** is inside `IInlineToolLoopRunner` (one place that builds the provider
request). **Layer 2 (response cache)** is in `ManagedAgent.RunAsync` (the only place that owns
gate+budget order). They share only the meter.

---

## Task breakdown

### T1 — Prompt-cache prefix marking + usage parse in the runner (AC1–AC3) — TDD

- [ ] **Test first** (`Tamma.Activities.Tests/LlmCall/PromptCachePrefixTests.cs`): a fake provider
      transport captures the outgoing Anthropic request body; assert `cache_control:{type:ephemeral}`
      is on the system-prompt block + the RAG/conventions block, **not** on the volatile user prompt;
      assert a breakpoint at the stable/volatile boundary. Non-Anthropic provider → no `cache_control`.
      `PromptCacheOptions.Enabled=false` → byte-for-byte the 32-5 baseline request.
- [ ] **Test first**: a fake Anthropic response with `cache_creation_input_tokens=N`,
      `cache_read_input_tokens=M` → `InlineToolLoopResult.CacheWriteTokens==N`, `CacheReadTokens==M`.
- [ ] Add `PromptCacheOptions` (`Enabled` default true, `MinPrefixTokens` default 1024).
- [ ] Modify `InlineToolLoopResult` to add `CacheReadTokens`/`CacheWriteTokens` (default 0).
- [ ] In `InlineToolLoopRunner`: when alias-normalized provider == `anthropic` &&
      `PromptCacheOptions.Enabled` && prefix estimate >= `MinPrefixTokens`, mark the stable-prefix
      blocks `cache_control:ephemeral`; parse the cache-usage fields from the response.
- [ ] **Research gate:** re-confirm the latest Anthropic prompt-cache API shape (field names, the
      1024-token minimum, cache-write/cache-read pricing multipliers) against current Anthropic docs
      before implementing — never from memory. Isolate the parse behind `PromptCacheOptions`.

### T2 — Cache-aware cost path over 34-11's seam (AC3) — TDD

- [ ] **Test first** (`Tamma.Api.Tests/Providers/CacheAwarePricingTests.cs`): seed a `ProviderModelPrice`
      row with `CacheReadUsdPer1M`/`CacheWriteUsdPer1M` populated → `ComputeWithCache` prices cache
      tokens at those rates additively over `Compute`; a row with **null** cache columns → cache
      tokens add **zero** surcharge (reported, not priced), no throw; assert plain `Compute` output is
      **unchanged** from 34-11.
- [ ] Add `ComputeWithCache(provider, model?, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens)`
      to `DbProviderPricingService` (additive — `Compute` + null-safe cache surcharge). Keep the
      `IProviderPricingService` interface stable; expose the cache path on the concrete impl (or a
      sibling resolver) so downstream consumers are unaffected.

### T3 — Response cache store + key + cacheability policy (AC4, AC7, AC8) — TDD

- [ ] **Test first** (`Tamma.Api.Tests/Agents/ResponseCacheabilityPolicyTests.cs`): `enableToolLoop=true`
      → not cacheable (reason "tool-loop"); `temperature>threshold` → not cacheable; opt-out → not
      cacheable; `enableToolLoop=false, temperature=0` → cacheable; reasons returned + logged.
- [ ] **Test first** (`Tamma.Api.Tests/Agents/ResponseCacheTests.cs`): `Set` then `TryGet` round-trips
      `text`+`usage`; TTL expiry → miss; per-tenant isolation — same key tuple for tenant A and B,
      A's entry never returned to B (separate `t_<hex>` schemas); `TryGet` on a missing key → miss.
- [ ] Add `ResponseCacheKey` (record), `ResponseCacheabilityPolicy` (the predicate), `ResponseCacheOptions`
      (`Enabled` default false, `Ttl` default 24h, `MaxEntriesPerTenant`, `DeterminismTemperatureThreshold` default 0).
- [ ] Add `IResponseCache` + `ResponseCache` (tenant-schema `agent_response_cache` store + in-process
      LRU snapshot, bounded per tenant, TTL-evicting).
- [ ] **Tenant-schema migration** (`EfTenantDbMigrator`): `<timestamp>_AddAgentResponseCache.cs` creates
      `t_<hex>.agent_response_cache` (`cache_key` PK, `agent_version`, `rendered_prompt_hash`, `model`,
      `toolset`, `response_text`, `usage_json` JSONB, `created_at`, `expires_at` + an `expires_at` index).
      **NOTE:** tenant-schema — does **NOT** go in `Program.cs`'s public-schema DROP list; does **NOT**
      touch `ControlPlaneDbContextModelTests` (the `EfTenantDbMigrator` owns `t_<hex>` tables).

### T4 — Splice the response cache into `ManagedAgent.RunAsync` (AC5, AC6, AC9, AC10) — TDD

- [ ] **Test first** (`Tamma.Api.Tests/Agents/ManagedAgentCacheCompositionTests.cs`):
      - **Hit after gate+budget:** seed an entry; a matching request → gate + budget evaluated, the
        `IInlineToolLoopRunner` (spy) **never invoked**, a metered `cache_hit` usage event emitted
        (`providerCostUsd=0`, tokens preserved), exactly one `AGENT.RUN.SUCCESS` (`cacheHit:true`).
      - **Miss → live call → write-through:** no entry → runner invoked, cache-aware cost computed,
        `cache_hit=false` usage emitted, `Set` called.
      - **Not cacheable (tool-loop):** `enableToolLoop=true` → no lookup, no `Set`, live call.
      - **Fail-open read error:** cache `TryGet` throws → falls through to a live metered call, WARN
        logged; **gate/budget stay fail-closed** (an unevaluable budget still denies).
      - **Write-through best-effort:** `Set` throws → run still returns its result; WARN logged.
- [ ] Splice the lookup into `ManagedAgent.RunAsync` **after** gate(step 1) + budget(step 4), **before**
      the runner. Compute `renderedPromptHash = Sha256(renderedPrompt)`; build `ResponseCacheKey`.
- [ ] On a hit: emit the metered `cache_hit` usage event (32-9) + `AGENT.RUN.SUCCESS`; return
      `AgentRunResult.FromCache(hit)`. On a miss: live run, then `ComputeWithCache`, emit usage with
      cache tokens, write-through if cacheable.
- [ ] Wire the meter to `ComputeWithCache` and pass `CacheReadTokens`/`CacheWriteTokens` to 32-9.

### T5 — Additive wire/usage fields + DI registration (AC9) — TDD

- [ ] **Test first**: `LlmCallResponse.usage` gains `cacheReadTokens`/`cacheWriteTokens`/`cacheHit`
      defaulting to 0/0/false; a workflow ignoring them is unaffected; assert `LlmCallWorkflow.cs`
      diff is empty (no engine change).
- [ ] Add the three additive fields to `UsageDto`; project them from `AgentRunResult`.
- [ ] `Program.cs` (`Tamma.Api`): register `IResponseCache`, `IResponseCacheabilityPolicy`; bind
      `PromptCacheOptions`/`ResponseCacheOptions` (defaults: prompt ON, response OFF).

### T6 — Credential safety + recommended-default verification (AC8, AC11) — TDD

- [ ] **Test first**: assert no API key / `BaseUrl` auth / provider header appears in any cache entry,
      the `usage_json` payload, any log line, or DCB event; assert the cache key/`rendered_prompt_hash`
      is a **hash**, never the prompt plaintext.
- [ ] **Test first**: defaults shipped match AC8 — `PromptCacheOptions.Enabled` true (Anthropic),
      `ResponseCacheOptions.Enabled` false, tool-loop never cached, single-turn deterministic cacheable.
- [ ] Audit logs: cache key components logged as **hash only**; cache hit logs `agentVersion`/`model`,
      never the prompt plaintext.

---

## Test list (consolidated)

1. Prompt-cache prefix marking — Anthropic stable prefix only / non-Anthropic no-op / disabled = baseline (T1).
2. Cache-usage parse — `cache_creation`/`cache_read` → `InlineToolLoopResult` fields (T1).
3. Cache-aware cost — priced at reserved columns / null-column zero surcharge / plain `Compute` unchanged (T2).
4. Cacheability predicate — tool-loop / temperature / opt-out / single-turn-deterministic + reasons (T3).
5. Response-cache round-trip + TTL expiry + **per-tenant isolation** (T3).
6. Hit after gate+budget — runner not invoked, metered `cache_hit` usage + terminal DCB event (T4).
7. Miss → live call → `ComputeWithCache` → write-through (T4).
8. Fail-open on cache read error → live call; gate/budget stay fail-closed; write-through best-effort (T4, T10).
9. Additive wire fields default 0/false; `LlmCallWorkflow.cs` diff empty (T5).
10. Credential safety — no key/auth/header in cache/usage/log/event; key is a hash (T6).
11. Recommended defaults shipped (T6).

---

## Verification

- [ ] `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"` green.
- [ ] `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests"` green.
- [ ] `dotnet build apps/tamma-elsa` clean (no wrapper needed for build).
- [ ] `grep` confirms no API key / provider header is ever written to `agent_response_cache` or logged.
- [ ] `grep` confirms the response cache is a tenant-schema table — **not** added to `Program.cs`'s
      public-schema DROP list and **not** in `ControlPlaneDbContextModelTests`.
- [ ] `LlmCallWorkflow.cs` diff is empty (engine untouched).
- [ ] Plain `Compute` parity from 34-11 still holds (cache pricing is purely additive).

---

## Risks

| Risk | Mitigation |
|------|-----------|
| A cache hit silently bypasses billing/audit | Lookup is **strictly after** gate+budget; a hit emits a metered `cache_hit` usage + terminal DCB event; T4 asserts both fire (provider never called). |
| Tool-loop run cached → replay skips side effects | `IResponseCacheabilityPolicy` returns false for `enableToolLoop==true`; response cache OFF for tool-loop by default; T3 predicate test. |
| Cross-tenant cache leak | Key includes `TenantId`; the store IS the tenant schema; T3 isolation test. |
| Stale cached answer after agent/prompt change | Key includes `agentVersion`+`renderedPromptHash`+`model`+`toolset` → any change is a miss; TTL bounds staleness; response cache defaults OFF. |
| Cache-rate columns unseeded → mispriced | Null column → zero surcharge (reported, not priced); never overcharges/throws; T2 null-column test. |
| Cache store failure breaks a run | Fail-open: read/write errors → live metered call; WARN; gate/budget stay fail-closed; T4 tests. |
| Anthropic API drift (field names/limits) | Re-confirm latest docs before T1; parse isolated behind `PromptCacheOptions`. |
| EF parallel-migration hazard | This story **amends/extends** the existing tenant-schema migration snapshot (one sequential snapshot — do not branch it); the `agent_response_cache` table is owned by `EfTenantDbMigrator`. |
| Forgotten DROP-list entry | Deliberate — tenant-schema (`t_<hex>`) table is NOT a public-schema table; called out in T3 + Verification. |

---

## Story order & dependencies

- **After 32-5** (the compose order + `IInlineToolLoopRunner` + `LlmCallResponse` this modifies) and
  **34-11** (the reserved cache-rate columns + `IProviderPricingService` seam).
- **Feeds 32-9** (cache-token splits + `cache_hit` usage), **34-5/35** (billing decision on a hit),
  **36** (cache-hit-rate / cost-savings analytics).
- Implemented **sequentially** w.r.t. other Epic-32/34 stories (one EF migration snapshot — extend,
  don't branch).
