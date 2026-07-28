# Story 46-1: Persisted model selection — `provider_settings`, the four-step resolver, defaults refresh

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform owner (platform default) or tenant admin (tenant override)**,
I want my model choice saved in the database and honoured by every LLM call path that today falls
back to a config or code default,
So that a model picked in the UI survives restarts and redeploys, takes effect without either, and
an install that never touches the UI behaves exactly as it does today.

## Priority

P0 — this is the "without code updates" half of the product requirement. 46-0 shows the latest
models; this story makes choosing one stick.

## Architectural Context (READ FIRST)

### Where "the default model" currently comes from (every consumer, verified)

| # | Consumer | Site | Today's resolution |
|---|---|---|---|
| 1 | `InlineToolLoopRunner.LoadProviderConfig` | `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs:1099-1151` | `LlmProviders:{key}:DefaultModel` if the section exists (`:1118-1127` — note the EARLY RETURN); else descriptor default, with an `Anthropic:Model` special case for anthropic only (`:1141-1143`) |
| 2 | `IInlineToolLoopRunner.GetDefaultModel` | `InlineToolLoopRunner.cs:1198-1201`, interface at `IInlineToolLoopRunner.cs:98` | delegates to #1; **sync** |
| 3 | `ManagedAgent` provider-override fallback | `ManagedAgent.cs:929` | calls #2; **sync** |
| 4 | `LlmProxyService` | `LlmProxyService.cs:30,98` | hardcoded `DefaultModel = "claude-sonnet-4.5"` const when the request names no model |
| 5 | Provider-chain entries | `ProviderChainResolver.cs:321-328` (`ProviderHandle.Model` nullable) | a chain entry may omit `model`; consumers of a null `ProviderHandle.Model` fall through to #2's resolution — **implementation task: audit every `ProviderHandle` consumer and confirm the null-model path routes through `GetDefaultModel`, and fix any that bypasses it** |

The story rewires #1 (which transitively fixes #2/#3/#5) and #4. `DefaultAgentConfig.DefaultModel`
(`DefaultAgentConfig.cs:23`) is agent-role configuration, not provider default — out of scope (epic
Out of scope).

### The precedence (epic decision, binding)

For `(provider, tenantId?)`:

1. **Tenant override** — `provider_settings` row keyed by the tenant (SaaS) or sole user
   (single-user), when a tenant/user context exists on the call;
2. **Platform DB override** — `provider_settings` platform row;
3. **`LlmProviders:{key}:DefaultModel`** configuration (and, for anthropic, the legacy
   `Anthropic:Model` — kept, slotted at this same config step);
4. **Descriptor `DefaultModel`** (`ProviderCatalog`).

Empty-string sentinel rule: a DB row's model is always non-empty (validated on write); config's
`""` continues to mean "no config opinion" exactly as `LoadProviderConfig` treats it today.

### The sync-read constraint — the real design problem in this story

`LoadProviderConfig` is **synchronous** and has synchronous public callers
(`GetDefaultModel` → `ManagedAgent.cs:929`). A per-call `await db` is not available there without
breaking the `IInlineToolLoopRunner` interface. The store therefore exposes a **cached snapshot
with sync reads**: `IProviderSettingsStore.TryGetModel(string providerKey, Guid? tenantId)` reading
an in-memory snapshot that is (a) invalidated synchronously on every write through the settings
endpoints (same-process — the API serves both the endpoints and the runner), and (b) refreshed
lazily with a short TTL (60 s, matching `DefaultProviderCredentialResolver.DefaultCacheTtl`) to
cover multi-process deployments where another API instance took the write. Consequence to state
honestly in the XML docs: **in a multi-instance deployment a UI change may take up to 60 s to be
honoured by other instances.** That is well within "no redeploy" and matches the existing BYOK
cache posture (`DefaultProviderCredentialResolver.cs:39-40`).

### Scoping (CLAUDE.md universal rule — answered per mode)

- **single-user:** the sole user owns both layers. Platform row: written via the admin routes
  (`PlatformOwnerAccess` resolves to them). Override row: keyed `user_id`, written via the tenant
  routes. Same person, two knobs; the resolver order makes their override win, which is harmless
  since both are theirs.
- **SaaS:** platform row is the platform owner's (`PlatformOwnerAccess`); tenant rows are keyed
  `tenant_id`, written by `tenant_owner`/`tenant_admin` through the `AgentManage`-gated tenant
  routes (the same policy already guarding BYOK writes — `ProviderCredentialEndpoints.cs:32-34`);
  members read the resolved value only.

**Storage: control-plane resident** (epic D3a): the resolver runs on hot egress paths that carry a
`tenantId` but no tenant `DbContext`, so all rows — platform and tenant — live in one CP table
behind one cache. XOR principal pattern borrowed from `prompt_overrides` (see
`AgentRoleSelection.cs:10-23` for the documented pattern; note that entity chose tenant-schema
residency for reasons that do not apply here, as D3a records).

## Acceptance Criteria

1. **Entity + migration.** `Tamma.Data/Entities/ProviderSetting.cs`:
   `Id`, `TenantId?`, `UserId?`, `Scope` (`"platform" | "principal"`), `ProviderKey` (canonical,
   never an alias), `DefaultModel` (non-empty), `Enabled` (bool, default true — platform rows only;
   principal rows always true and the endpoint rejects attempts to set it), `UpdatedAt`,
   `UpdatedBy?`. Constraints: platform rows have `TenantId IS NULL AND UserId IS NULL`; principal
   rows satisfy the XOR (`ck_provider_settings_principal_xor`, mirroring
   `ck_prompt_overrides_principal_xor`); `UNIQUE NULLS NOT DISTINCT (TenantId, UserId, ProviderKey)`.
   ControlPlane migration under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/`
   (naming per the existing `20260703191700_AddTenantProviderBilling` convention).

2. **`IProviderSettingsStore` / `ProviderSettingsStore`** in `Services/Providers/`: sync
   `TryGetModel(providerKey, tenantId?)` and `IsEnabled(providerKey)` snapshot reads; async
   `SetPlatformModelAsync`, `SetPrincipalModelAsync`, `RemoveXxxAsync`, `SetEnabledAsync` writes
   that update the DB and invalidate the snapshot; TTL-based lazy refresh (60 s, `TimeProvider`).
   The multi-instance staleness bound is XML-doc'd. The extension point for any future scoping
   change is a doc comment, not speculative code.

3. **`LoadProviderConfig` honours the four-step precedence.**
   `InlineToolLoopRunner.cs:1099-1151` is restructured so `DefaultModel` resolves as
   store-tenant → store-platform → config (`LlmProviders` section, `Anthropic:Model` legacy case) →
   descriptor — including when the config section EXISTS (the current early return at `:1119-1127`
   must not bypass the store). `BaseUrl`/`TimeoutSeconds` resolution is unchanged. The tenant leg
   uses the tenant id already flowing through `LoadProviderConfigWithKeyAsync` (`:1166-1190`);
   the sync `GetDefaultModel(provider)` overload resolves platform-scope only, and gains a
   `GetDefaultModel(provider, tenantId?)` overload on `IInlineToolLoopRunner` so `ManagedAgent`
   (`ManagedAgent.cs:929`) can pass its tenant context. Callers without tenant context keep
   today's behaviour + the platform DB layer.

4. **`LlmProxyService` reads the store.** `LlmProxyService.cs:98` becomes: request model →
   store (tenant, then platform, for the canonical `anthropic` key) → the existing const as final
   fallback. The const stays as the last-resort safety net but is corrected as part of AC7 if the
   live list disproves it.

5. **Settings mutation routes.**
   - Platform (extends 46-0's `ProviderAdminEndpoints.cs`, `PlatformOwnerAccess`):
     `PUT /api/admin/providers/{key}/settings` body `{ defaultModel?, enabled? }`;
     `DELETE /api/admin/providers/{key}/settings` (removes the platform row → falls back to
     config/descriptor). `GET /api/admin/providers` (46-0) now reports
     `source: "platform-db" | "config" | "descriptor"` and `enabled`.
   - Tenant (extends `ProviderCredentialEndpoints.cs` surface, `AgentManage` for writes,
     member-readable GETs):
     `GET /api/v1/agents/providers/models` → the tenant-facing roster 46-3 renders — one row per
     **enabled** HTTP provider: `key`, `displayName`, `modelsSupported`, resolved `model` +
     `source`, `hasOverride`, `byokKeyPresent` (metadata only — reuses the `ListProviders`
     cabinet query, `ProviderCredentialEndpoints.cs:52-77`); disabled providers are simply
     absent (tenants never see the platform's off switch);
     `GET /api/v1/agents/providers/{provider}/model` → resolved model +
     `source: "tenant-override" | "platform-db" | "config" | "descriptor"` + `override?`;
     `PUT /api/v1/agents/providers/{provider}/model` body `{ model }`;
     `DELETE /api/v1/agents/providers/{provider}/model`. Alias normalization + unknown → 404 via
     the `NormalizeProvider` shape (`ProviderCredentialEndpoints.cs:333-352`). Member PUT/DELETE →
     403. Validation: model non-empty, ≤ 256 chars, no whitespace-only, no control characters.
     *(Amended 2026-07-28, conformance review: the tenant roster row AND the per-provider model
     GET gained an additive `fallbackModel` field post-story — the model the tenant would resolve
     to WITHOUT its override (skip-principal resolution: platform DB → config → descriptor),
     computed server-side so 46-3's reset confirm can name it without the client restating
     precedence. See
     `.dev/bugs/2026-07-27-tenant-surface-cannot-name-platform-default-under-override.md`.)*
     *(Amended 2026-07-28, conformance review — shipped disabled-provider semantics on the
     tenant surface, deliberately asymmetric: the per-provider GETs (`…/models`, `…/model`)
     answer for a platform-disabled provider with the SAME 404 shape as an unknown provider
     (review F11, never-enumerate — matching the roster, where it is simply absent); PUT returns
     409 `provider_disabled` (the off switch wins); DELETE of an existing override is
     deliberately ALLOWED, so a tenant can clean up an orphaned override row while the provider
     is disabled. The asymmetry is intentional.)*

6. **Pricing warning (epic D3b).** Both PUT responses carry
   `pricingKnown: bool` — false when `IProviderPricingService` has no rate for
   `(provider, model)` — plus a human-readable `warning` string when false. Nothing is blocked
   (open question 1 in the epic README records the possible SaaS hard-block).

7. **Defaults refresh — the rot task (one-time, at implementation).** Using 46-0's live lists (or
   curl where keys exist): verify every non-empty descriptor `DefaultModel`
   (`ProviderCatalog.cs:37,55,66,125,213,227`) and the `LlmProxyService` const + price-table keys
   (`LlmProxyService.cs:30,57-63`) are ids the provider accepts **today**; refresh any that are not
   (the known suspects: openrouter's `anthropic/claude-sonnet-4-20250514` snapshot slug; the
   dot-formed `claude-sonnet-4.5` family). Every change lands with the verification evidence in
   the PR description and updates `appsettings.json`'s `LlmProviders` examples
   (`Tamma.ElsaServer/appsettings.json:64-89`) to match. Golden-request tests that pin model
   strings are updated in the same commit.

8. **Audit events.** Every settings mutation emits a DCB event through `ISensitiveActionEmitter`
   with a new catalog entry `ProviderSettingsChanged = "PROVIDER.SETTINGS_CHANGED.SUCCESS"`
   (`SensitiveActionCatalog.cs` — follow the `ProviderKeyChanged` pattern at `:78,205`, category
   `AuditCategory.Byok` is wrong here; use the closest configuration-change category the catalog
   offers, or add one the way the file's other groups do). Tags: provider, scope
   (platform/tenant/user), operation (set/removed/enabled/disabled); data: old → new model.
   Never any key material.

9. **Tests** (NUnit, `Tamma.Api.Tests`): precedence matrix (16 cases: each layer present/absent ×
   tenant context present/absent); early-return regression (config section exists AND platform row
   exists → DB wins); snapshot invalidation on write; TTL refresh; `LlmProxyService` pickup;
   endpoint RBAC per the epic's RBAC table (member 403 on writes, both modes' read paths);
   pricing-warning field; alias normalization on all new routes; audit emission per mutation;
   migration round-trip (Testcontainers, matching the existing migration test conventions).

## Dependencies

- **Blocked by: nothing hard.** 46-0 lands first by preference (shared
  `ProviderAdminEndpoints.cs`); the defaults-refresh task (AC7) is easiest with 46-0's endpoint
  but can use curl.
- **Blocks:** 46-2, 46-3 (both UIs bind the settings routes and `source` provenance).
- **Coordination:** none outside this epic. No other in-flight epic edits
  `InlineToolLoopRunner.LoadProviderConfig` (checked against epics 43-45 plans, 2026-07-27).

## Out of Scope

- Per-tenant enable/disable (platform-level flag only; epic Out of scope).
- Enforcing `Enabled` on the egress path beyond what the allowlist already does — this story
  persists the flag and reports it; a disabled provider is hidden/greyed by the UIs and rejected
  by the settings routes, but wiring `Enabled=false` into `ProviderAllowlist`/chain selection is
  the finding's Phase-2 allowlist-inversion work and carries its own blast radius. The flag exists
  now so Phase 2 has data to enforce.
- Any UI (46-2, 46-3).
- Chain-entry model editing (`llm-provider-chains` config stays as is; a chain entry that names a
  model still wins over everything — it is an explicit per-call choice, not a default).

## Estimated Effort

4 days

## Change Log

| Date       | Version | Changes                                                          | Author |
| ---------- | ------- | ---------------------------------------------------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation                                           | Claude |
| 2026-07-27 | 1.1.0   | Tenant override layer added; precedence extended to four steps (PO decision) | Claude |
