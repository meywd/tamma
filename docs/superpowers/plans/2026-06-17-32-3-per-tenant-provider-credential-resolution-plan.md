# Story 32-3 — Per-Tenant Provider Credential Resolution (BYOK → platform) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement phase-by-phase. Steps use checkbox
> (`- [ ]`) syntax. Project is test-first (TDD) — write the failing test before the implementation in
> every phase.

**Story:** `docs/stories/epic-32/story-32-3/32-3-per-tenant-provider-credential-resolution.md`
**Epic:** 32 — `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
**Date:** 2026-06-17

**Goal:** Replace the global-env provider-key lookup in `CallLlmInlineActivity` with a single
`IProviderCredentialResolver` seam that, per call, resolves a tenant's **own** provider API key from
the Epic 29 secret cabinet (`SecretScope.Tenant`, `SecretPurpose.ApiKey`, keyed by provider) and
falls back to the platform-provided key only when the tenant has none — **fail-closed** in SaaS when
neither is allowed. Record `credentialSource` (`byok | platform`) per call (for 32-9 / 34 / 35),
expose a tenant-admin BYOK management API, and never let a raw key reach an event, diagnostic, or log.
This story is the **canonical owner** of BYOK key wiring into the LLM call path.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (`Tamma.Api`, `Tamma.Data`,
`Tamma.Activities`, `Tamma.ElsaServer`, `Tamma.Core`). Tests: xUnit under
`apps/tamma-elsa/tests/` (docker-bound suites via `sg docker -c "dotnet test ..."`; build needs no
wrapper — see memory `reference_dotnet_test_docker.md`).

---

## Non-goals (YAGNI guard)

- **NO pricing/markup engine** — that is 34-5. This story only *emits* `credentialSource`; it does not
  compute cost differently for BYOK vs platform.
- **NO new provider implementations** (Epic 1-10). Resolution targets the providers
  `CallLlmInlineActivity` already supports (`anthropic`, `openai`/OpenAI-compatible, `openrouter`).
- **NO per-user BYOK layer.** BYOK is tenant-scoped only (single-user: the sole user == the
  tenant-equivalent). Mirrors Prompt Store "no per-user override in SaaS".
- **NO OpenBao / KMS work** — the cabinet's backend is whatever Epic 29 wired (env-KEK Postgres
  backend today); this story consumes `ISecretStoreBackend` as-is. Story 28-13 is out of scope.
- **NO change to provider *selection*** (`ProviderChainResolver` keeps choosing which provider). This
  story resolves the chosen provider's *key*.
- **NO removal of the Story 29-9 env-var fallback** for the platform key — we *reuse*
  `IRuntimeSecretResolver`, inheriting whatever fallback state Epic 29 has configured.
- **NO TypeScript-side work** — `packages/api` is deleted; everything is C# `apps/tamma-elsa`.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### The gap — direct env-key reads, tenant-agnostic

`apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` → `LoadProviderConfig`
(lines **~1398–1443**) reads provider keys straight from process config — one key for all tenants:

```csharp
"anthropic" => new LlmProviderConfig { ApiKey = _configuration?["Anthropic:ApiKey"] ?? "", ... },
"openai"    => new LlmProviderConfig { ApiKey = _configuration?["OpenAI:ApiKey"] ?? "", ... },
"openrouter"=> new LlmProviderConfig { ApiKey = _configuration?["OpenRouter:ApiKey"] ?? "", ... },
```

The same `config.ApiKey` flows into `CallAnthropicMessages`/`CallAnthropicMultiTurn` (`x-api-key`
header, lines ~853, ~1273) and `CallOpenAiCompatible`/`CallOpenAiMultiTurn` (`Authorization: Bearer`,
lines ~891, ~1334). This is the Epic 32 design Risk "Global provider keys today".

### Tenant context is threaded into the workflow — but not into this activity

`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`:
- line ~70: `var tenantIdVar = builder.WithVariable<string>("TenantId", "");`
- line ~153: `tenantIdVar.Set(context, context.GetInput<string>("tenantId") ?? "");`
- lines ~208, ~276: `TenantId = new Input<string>(ctx => tenantIdVar.Get(ctx))` is passed to the
  **prompt/convention** activities — proving the wiring pattern. `CallLlmInlineActivity` is
  instantiated in the same workflow (lines ~176/211 of `Tamma.ElsaServer/Program.cs` reference it) but
  **does not receive `TenantId`**. This is the integration seam (Phase 3).

### Epic 29 secret cabinet — the primitives to build on (all verified present)

| Primitive | Path | Use |
|---|---|---|
| `ISecretStore` (create/get/rotate/retire/version) | `Tamma.Api/Services/Secrets/ISecretStore.cs` | BYOK create/rotate/remove + metadata lookup. **Never returns plaintext** (its own doc-comment "Plaintext rule"). |
| `ISecretStoreBackend.GetVersionPlaintextAsync(secretId, versionNumber)` | `Tamma.Api/Services/Secrets/ISecretStoreBackend.cs` | The ONLY runtime plaintext read path. |
| `SecretRef.ForTenant(tenantId, name)` / `SecretScope.Tenant` | `…/Secrets/SecretRef.cs`, `SecretScope.cs` | Tenant-scoped ref; ctor enforces non-null tenantId. |
| `SecretPurpose.ApiKey` | `…/Secrets/SecretPurpose.cs` | Purpose for provider keys. |
| `IRuntimeSecretResolver.GetAsync("anthropic/api-key")` + `Invalidate` + `DefaultCacheTtl=60s` | `…/Secrets/Stopgap/IRuntimeSecretResolver.cs`, `RuntimeSecretResolver.cs` | **Prior art + platform-key source of truth.** Already does cabinet→backend→cache→config-fallback, but **platform-scoped only** (`s.Scope == "platform"`, `StopgapSecretMap.Platform`). We model the tenant resolver on it and delegate the platform leg to it. |
| `StopgapSecretMap.PlatformAnthropicApiKey = "anthropic/api-key"` (+ GitHub, Cranl, …) | `…/Secrets/Stopgap/StopgapSecretMap.cs` | Canonical platform cabinet names. No OpenAI/OpenRouter entry yet — add platform cabinet names for them in `ProviderCabinetNames` (Phase 1) or extend the stopgap map if a platform OpenAI key needs migrating. |

**Key architectural fact:** `RuntimeSecretResolver` is exactly the shape we want, restricted to
platform scope. 32-3 = its tenant-scoped sibling + BYOK→platform precedence + fail-closed.

### DCB + error + endpoint patterns (to mirror)

- **Events:** `Tamma.Data/Entities/DomainEvent.cs` (`Id, Type, TenantId, Tags, Metadata, Data,
  CreatedAt, SequenceNumber`) appended via `Tamma.Data/Repositories/IEventRepository.AppendAsync`.
  Emission exemplar: `AgentEndpoints.UpdateConfig` (`AGENT_CONFIG.UPDATED.SUCCESS`, JSON-serialized
  Tags/Metadata/Data). Type follows `AGGREGATE.ACTION.STATUS`.
- **Typed error:** `Tamma.Core/TammaError.cs` — `new TammaError(code, message, context, retryable,
  severity)`; `PROVIDER_CREDENTIAL_UNAVAILABLE` joins `PROMPT.RESOLVE.NO_DEFAULT` etc. as a
  fail-loud code (project rule `feedback_resolution_no_empty_fallback`: never silent empty fallback).
- **Tenant context / RBAC:** `Tamma.Data/ITenantContext.cs` (`Guid? TenantId`);
  `AgentEndpoints.UpdateConfig` shows the tenant-context guard (400/short-circuit) and
  `principal.GetUserId()`; Prompt Store RBAC (CLAUDE.md) is the owner/admin-vs-member precedent.
- **Mode:** `Tamma.Api/Services/PromptStore/TammaMode.cs` `ITammaModeProvider` (SingleUser | SaaS) —
  drives the single-user-always-fallback vs SaaS-fail-closed branch.

### Per-mode ownership (mandatory two-scoping-model answer, CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Who owns a BYOK key? | The sole user (resolve with `tenantId == null` → platform/local; no separate BYOK layer needed — their config IS the platform config). | The tenant (`SecretScope.Tenant`, keyed by `tenantId`); `tenant_owner`/`tenant_admin` manage, members read metadata. |
| Resolution order | platform/local key (env→cabinet via `IRuntimeSecretResolver`). | tenant BYOK cabinet key → platform-provided key (gated). |
| Fail-closed? | Only if even the platform key is unset (already broken today) → loud error. | YES when no BYOK + fallback disabled → `PROVIDER_CREDENTIAL_UNAVAILABLE` + `AGENT.CREDENTIAL.DENIED`. |
| Who sees `credentialSource`? | the user. | the tenant (in diagnostics/action-trail); platform admin sees aggregate, never the key. |
| Mode source | `ITammaModeProvider` | same |

---

## Phases (TDD — failing test first in every phase)

### Phase 1 — `ProviderCabinetNames` + `IPlatformFallbackPolicy` (pure, no I/O)

**Approach:** Pure helpers so the resolver's name-mapping and fallback gating are unit-testable
without a DB. `ProviderCabinetNames.Byok("anthropic") => "provider/anthropic/api-key"`;
`.Platform("anthropic") => StopgapSecretMap.PlatformAnthropicApiKey`. Allowlist via existing
`ProviderAllowlist` (already used in `CallLlmInlineActivity.LoadProviderConfig`).
`ConfigPlatformFallbackPolicy`: single-user ⇒ true; SaaS ⇒ true unless
`Providers:PlatformFallbackDisabled` (global) or `Providers:<provider>:PlatformFallbackDisabled`
(per-provider) is set.

**Files:** new `Services/Providers/ProviderCabinetNames.cs`, `IPlatformFallbackPolicy.cs`,
`ConfigPlatformFallbackPolicy.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Providers/ProviderCabinetNamesTests.cs`,
`ConfigPlatformFallbackPolicyTests.cs` — name mapping for anthropic/openai/openrouter; unknown
provider rejected; single-user always true; SaaS true by default, false when disabled globally /
per-provider.

- [ ] Write failing tests for name mapping + fallback policy matrix.
- [ ] Implement `ProviderCabinetNames`, `IPlatformFallbackPolicy`, `ConfigPlatformFallbackPolicy`.
- [ ] Green; no direct config reads outside the policy.

### Phase 2 — `IProviderCredentialResolver` + `DefaultProviderCredentialResolver` (core)

**Approach:** Implement the BYOK→platform algorithm from the story Technical Design.
- BYOK leg: `ISecretStore.GetAsync(SecretRef.ForTenant(tid, ByokName))` for metadata →
  `ISecretStoreBackend.GetVersionPlaintextAsync(meta.Id, meta.ActiveVersionNumber)` for bytes (mirror
  `RuntimeSecretResolver.TryReadCabinetAsync`, incl. the catch-and-degrade-to-absent behaviour).
- Platform leg: `IRuntimeSecretResolver.GetAsync(ProviderCabinetNames.Platform(provider))`.
- Cache: `ConcurrentDictionary<(Guid,string), CacheEntry>`, TTL `RuntimeSecretResolver.DefaultCacheTtl`;
  `Invalidate((tenant,provider))`.
- Emit `AGENT.CREDENTIAL_RESOLVED.SUCCESS` / `AGENT.CREDENTIAL.DENIED` via `IEventRepository`
  (tag-only projection, **never** `ApiKey`). Fail-closed → `TammaError("PROVIDER_CREDENTIAL_UNAVAILABLE")`.
- `ProviderCredential.ToTag()` is the ONLY thing handed to events/diagnostics.

**Files:** new `Services/Providers/IProviderCredentialResolver.cs` (+ `ProviderCredential`,
`CredentialSource`), `DefaultProviderCredentialResolver.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Providers/ProviderCredentialResolverTests.cs` with
`InMemorySecretStoreBackend` + fakes for `ISecretStore`, `IRuntimeSecretResolver`, `IEventRepository`,
`ITammaModeProvider`, `IPlatformFallbackPolicy`:
- BYOK present → tenant key, `source=byok`, ref + version correct, one `RESOLVED.SUCCESS`.
- BYOK absent + platform present + allowed → `source=platform`.
- SaaS + BYOK absent + fallback disabled → `PROVIDER_CREDENTIAL_UNAVAILABLE` + `DENIED`, no SUCCESS.
- single-user + platform present → `source=platform`; single-user + platform unset → loud throw.
- **Tenant isolation:** tenant A BYOK seeded; resolve A→A's key, resolve B→platform (never A's).
- **Redaction:** sentinel BYOK key never in any emitted `DomainEvent` Tags/Data or log.
- Cabinet probe throws → treated as BYOK-absent, WARN, proceeds to fallback.

- [ ] Write failing resolver tests (all AC13 cases + isolation + redaction).
- [ ] Implement resolver + cache + events + fail-closed.
- [ ] Green.

### Phase 3 — Wire resolver into `CallLlmInlineActivity` + workflow + diagnostics

**Approach:**
- Add `Input<string?> TenantIdProp` to `CallLlmInlineActivity`; parse to `Guid?` (empty → null).
- Add `IProviderCredentialResolver?` ctor dep (null-tolerant; extend `[JsonConstructor]` chain).
- Replace `ApiKey` population: keep `LoadProviderConfig` for BaseUrl/Model/Timeout; add
  `LoadProviderConfigWithKeyAsync(provider, tenantId, ctx)` that calls the resolver and sets
  `cfg.ApiKey`. Call it in both single-turn and tool-loop entry points (lines ~298, ~123).
- Set `ctx.SetVariable("CredentialSource", …)` and add `ProviderAttemptDiagnostic.CredentialSource`
  (`LlmCallModels.cs`) — already serialized into `LastDiagnostic`.
- Catch `TammaError("PROVIDER_CREDENTIAL_UNAVAILABLE")` inside the existing per-attempt try/catch so
  the chain can advance; if the whole chain has no usable credential, surface the failure (loud).
- `LlmCallWorkflow`: pass `TenantId = new Input<string>(ctx => tenantIdVar.Get(ctx))` to the
  `CallLlmInlineActivity` step (same as prompt/convention steps).
- Register resolver + invalidator in `Tamma.ElsaServer/Program.cs`.

**Tests (first):** `tests/Tamma.Activities.Tests/LlmCall/CallLlmInlineCredentialTests.cs` (mock
`IHttpClientFactory`): with `TenantIdProp` set + BYOK seeded → outbound header carries BYOK key,
`CredentialSource=byok` on diagnostic; empty tenant → platform key, `source=platform`; resolver
denial → diagnostic `Succeeded=false` with the typed error, no header sent. Existing single-turn +
tool-loop sanitization tests stay green (AC12).

- [ ] Write failing activity-credential tests.
- [ ] Add `TenantIdProp` + resolver dep; drop direct env-key read; wire diagnostics.
- [ ] Thread `TenantId` in `LlmCallWorkflow`; register in `Program.cs`.
- [ ] Green incl. existing CallLlmInline tests; add a guard test asserting `LoadProviderConfig`
      returns empty `ApiKey`.

### Phase 4 — BYOK management API + RBAC + cache invalidation on mutate

**Approach:** New `ProviderCredentialEndpoints` (or extend `AgentEndpoints`):
```
POST   /api/v1/agents/providers/{provider}/credential          → ISecretStore.CreateAsync (reveal-once, 29-3)
POST   /api/v1/agents/providers/{provider}/credential/rotate   → ISecretStore.RotateAsync
DELETE /api/v1/agents/providers/{provider}/credential          → retire active version
GET    /api/v1/agents/providers                                 → metadata list (NO key)
```
- RBAC: tenant_owner/tenant_admin only (member → 403; cross-tenant → 404); tenant-context guard like
  `AgentEndpoints.UpdateConfig`.
- Every mutation → `resolver.Invalidate(tid, provider)`; cabinet audit pipeline records the change;
  response never echoes the raw key (create returns the reveal-once token only).
- `ProviderCredentialCacheInvalidator`: subscribe/handle `SECRET.ROTATE.ACTIVATED`; if rotated ref is
  a `provider/*/api-key` tenant ref, evict `(tenantId, provider)`.
- DI in `Tamma.Api/Program.cs` + `ProviderCredentialServiceCollectionExtensions`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/ProviderCredentialEndpointsTests.cs` — RBAC matrix
(owner/admin create/rotate/delete; member 403; cross-tenant 404); create→list shows metadata only, no
key; whitespace key rejected; mutation invalidates cache (resolver re-reads). Invalidator test:
`SECRET.ROTATE.ACTIVATED` for a matching ref evicts the cache entry.

- [ ] Write failing endpoint RBAC + invalidation tests.
- [ ] Implement endpoints, DI, invalidator.
- [ ] Green.

### Phase 5 — Hardening, redaction sweep, full-suite + docker run

**Approach:** Cross-cutting verification.
- Redaction: end-to-end sentinel test (Phase 2 + Phase 3 combined) — sentinel key in HTTP header
  ONLY; absent from events, diagnostics, exceptions, logs.
- Edge: unknown provider; cabinet down (degrade, not leak); TTL expiry re-read; backward-compat byte
  check for platform path.
- Run `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"` (or per-project) — full suite green.
- Confirm `has-pending-model-changes` is clean (this story adds **no** EF migration — BYOK keys live
  in the existing cabinet tables; only verify nothing inadvertently changed the model).

- [ ] End-to-end redaction test green.
- [ ] Edge-case tests green.
- [ ] Full C# suite green via docker wrapper; no model drift.

---

## Sequencing & dependencies

Phase 1 → Phase 2 (needs P1 helpers) → Phase 3 (needs P2 resolver) → Phase 4 (needs P2 resolver;
parallel-safe with P3) → Phase 5 (needs all). Hard external prerequisite: **Epic 29** primitives
(verified present). 32-1 (agent entity) supplies the provider attribute but is not strictly required
to land the resolver — the resolver keys off `(tenantId, providerName)` which already flows from the
workflow.

## Risks

- **Raw-key leakage** (Critical): tag-only `ToTag()`, `Data` excludes `ApiKey`, dedicated end-to-end
  redaction test (Phase 5). Audit every new `LogInformation`/event-`Data` line during review.
- **`ISecretStore` plaintext-rule violation:** it deliberately never returns plaintext — must read
  via `ISecretStoreBackend.GetVersionPlaintextAsync` (Phase 2). A reviewer should reject any
  `ISecretStore`-to-plaintext shortcut.
- **Cross-tenant cache collision** (High): cache keyed by `(tenantId, provider)`; isolation test
  (Phase 2). Never cache platform key under a tenant key.
- **Stale key after rotation** (Medium): TTL + `Invalidate` on mutate + `SECRET.ROTATE.ACTIVATED`
  handler; rotation test (Phase 2/4).
- **Behaviour change for platform-key tenants** (Medium): AC12 byte-identical platform path; keep all
  existing `CallLlmInlineActivity` tests green (Phase 3).
- **`_configuration["…:ApiKey"]` regressing back in** (Medium): resolver is the only key source; add
  the `LoadProviderConfig` empty-`ApiKey` guard test (Phase 3).
- **Elsa activity hydration** (Low): null-tolerant ctor + `[JsonConstructor]`; Program-level wiring
  test confirms the resolver is actually injected at runtime.
- **Platform cabinet name absent for OpenAI/OpenRouter** (Low): `StopgapSecretMap` only ships
  `anthropic/api-key` today; `ProviderCabinetNames.Platform` defines the others (and the platform key
  may simply be unset → fail-closed/loud, which is correct).

## Acceptance criteria (plan-level — maps to story ACs)

- [ ] `IProviderCredentialResolver` resolves BYOK-then-platform per `(tenantId, provider)` (story AC1, AC2).
- [ ] `CallLlmInlineActivity` no longer reads `_configuration["<Provider>:ApiKey"]`; resolves via the
      resolver with the workflow's tenant context (AC3); guard test proves it.
- [ ] `credentialSource` (`byok | platform`) on the diagnostic/action-trail; never the key (AC4).
- [ ] BYOK read/written only through the cabinet; raw key never in events/diagnostics/logs — redaction
      test green (AC5).
- [ ] Fail-closed in SaaS (`PROVIDER_CREDENTIAL_UNAVAILABLE` + `AGENT.CREDENTIAL.DENIED`); single-user
      falls back (AC6); fallback gated by `IPlatformFallbackPolicy` (AC6.1).
- [ ] Tenant-admin BYOK register/rotate/remove API, owner/admin-gated, member 403, cross-tenant 404 (AC7).
- [ ] `AGENT.CREDENTIAL_RESOLVED.SUCCESS` + `AGENT.CREDENTIAL.DENIED` DCB events, no secret (AC8).
- [ ] Cache TTL + invalidate-on-rotate; rotation returns new key (AC9).
- [ ] Per-mode ownership explicit; no per-user BYOK (AC10); tenant isolation proven (AC11).
- [ ] Backward-compatible platform path (AC12); all unit tests (AC13) green; full C# suite green.
