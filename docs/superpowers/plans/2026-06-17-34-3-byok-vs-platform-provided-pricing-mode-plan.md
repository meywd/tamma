# Story 34-3 — BYOK vs Platform-Provided Pricing Mode (per-provider mode selection)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Let each tenant choose, per provider, between **BYOK** (their API key lives in the Epic 29
secret cabinet → billed a flat platform/seat fee, no token markup) and **platform-provided** (Tamma
supplies the key from global config → usage billed at cost+margin). Persist the mode per
`(tenant, provider)` on the control plane via the `TenantProviderBilling` entity + the BYOK/platform
toggle endpoints, and surface the mode on the cost record so the markup engine (34-5) keys off it.
SaaS BYOK is API-key-only; CLI/token agent providers stay single-user/self-hosted.

> **Boundary (canonical ownership):** Provider-key resolution from the Epic 29 cabinet into the LLM call
> path is owned by **Story 32-3** (the `IProviderCredentialResolver` seam, exposing the resolved key AND
> a `ProviderCredential.Source` discriminator of `byok|platform`). SaaS provider-auth eligibility is
> owned by **Story 32-4** (`IProviderAuthRegistry.IsSaaSEligible`). This story **CONSUMES** both: it
> persists the per-`(tenant, provider)` mode 32-3 reads, writes/retires the BYOK secret on the toggle
> flows, and calls 32-4 for the CLI-provider 422 gate. It does **NOT** define its own key resolver,
> read the cabinet at call time, or modify `CallLlmActivity`.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API `Tamma.Api`,
control-plane data `Tamma.Data`, Elsa engine activities `Tamma.Activities`, shared enums `Tamma.Core`).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` and `…/tests/Tamma.Activities.Tests/` (xUnit;
docker-bound suites run via `sg docker -c "dotnet test …"`; build needs no wrapper).

---

## Non-goals (YAGNI guard)

- **NO cost→price markup math.** This story records `BillingMode`; applying the margin is Story 34-5.
- **NO billing/charging.** Stripe meters/invoices are Epic 35. No Stripe dependency here.
- **NO own key resolver.** Provider-key resolution is **owned by Story 32-3**
  (`IProviderCredentialResolver` + `ProviderCredential.Source`). This story does NOT define
  `IProviderKeyResolver`/`ProviderKeyResolver`/`ProviderKeyResolution`, does NOT read the cabinet at
  call time, and does NOT add an internal key-resolution callback endpoint.
- **NO `CallLlmActivity` changes.** The LLM-call-path wiring (removing the direct
  `_configuration["…:ApiKey"]` reads, the cabinet read) is 32-3's. This story leaves the activity alone.
- **NO re-wiring the secret cabinet.** This story uses `ISecretStore.CreateAsync`/`RetireVersionAsync`
  only to write/retire the BYOK secret on the toggle flows (so 32-3 can later read it); it does not
  change the cabinet or its runtime read path (Epic 29 / 32-3).
- **NO reinventing the CLI-provider rejection.** The 422 gate calls Story 32-4's
  `IProviderAuthRegistry.IsSaaSEligible`; this story does not duplicate the allowlist or the message logic.
- **NO per-user BYOK layer in SaaS.** Mode is owned by the tenant (tenant_owner/tenant_admin), mirroring
  the prompt-store "no per-user override in SaaS" rule.
- **NO new provider implementations.** Provider selection stays in the Epic 32 chain (`AgentConfig`);
  this story only persists the mode for whatever provider the chain picks.
- **NO CLI/token-agent BYOK in SaaS.** Those (`packages/providers` CLI agents) remain single-user; a
  SaaS BYOK request for one is a hard 422.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Story 32-3 credential seam (canonical owner — CONSUME, don't duplicate)

- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderCredentialResolver.cs` (Story 32-3) is the
  **single owner** of provider-key resolution: `ResolveAsync(tenantId?, providerName)` → `ProviderCredential
  { ApiKey, Source ∈ {Byok, Platform}, SecretRefStorageKey?, VersionNumber? }`, plus
  `Invalidate(tenantId, providerName)`. It reads the tenant BYOK cabinet key
  (`SecretRef.ForTenant(tenantId, "provider/<name>/api-key")`) first, then the platform key; a missing
  BYOK secret throws (no silent platform fallback). 32-3 also owns the `CallLlmActivity` /
  `CallLlmInlineActivity` wiring that replaces the direct `_configuration["…:ApiKey"]` reads. **This
  story does NOT touch any of that** — it persists the mode 32-3 reads and calls `Invalidate` on toggle.
- 32-3 propagates the resolved `ProviderCredential.Source` into `ProviderAttemptDiagnostic.CredentialSource`
  / the `credentialSource` diagnostic tag — the value this story persists as `ProviderDiagnostic.BillingMode`.

### Story 32-4 SaaS auth gating (consume for the 422)

- `apps/tamma-elsa/src/Tamma.Activities/Security/IProviderAuthRegistry.cs` (Story 32-4) —
  `IsSaaSEligible(providerName)` returns `true` only for API-key providers (CLI/token providers like
  `claude-code`/`opencode` and unknown providers → `false`, fail-closed). This is the single source for
  the BYOK-registration 422 gate; this story calls it and maps `false` → 422.

### Secret cabinet seam (Epic 29 — write/retire only on toggle)

- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretStore.cs` — `CreateAsync`/`GetAsync`/
  `RotateAsync`/`RetireVersionAsync`; plaintext only flows out-of-band to a rotation handler (the
  HTTP-visible read API never returns plaintext). This story uses `CreateAsync` (enable) and
  `RetireVersionAsync` (disable) ONLY; the runtime read is 32-3's.
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretRef.cs` — `ForTenant(tenantId, name)` /
  `ForPlatform(name)`; constructor enforces the tenant-id/scope invariant.
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretScope.cs` (`Platform`/`Tenant`),
  `SecretPurpose.cs` (`ApiKey` is the canonical purpose for an external provider key, lines **24–29**),
  `SecretRequests.cs` (`CreateSecretRequest` with `InitialPlaintext`).
- The cabinet name written on enable MUST be `provider/{providerKey}/api-key` (scope `Tenant`) to match
  the name 32-3's resolver reads — see `StopgapSecretMap` only for the platform-side names that 32-3
  (not this story) consumes.

### Control-plane data + events

- `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` + `TammaModelConfiguration.cs` (single
  source for entity config per Epic 28 convention). Migrations under
  `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` — current state is a **single collapsed
  baseline** `20260609205701_InitialControlPlane(.Designer).cs` + `ControlPlaneDbContextModelSnapshot.cs`,
  so a new entity is a clean additive `dotnet ef migrations add`.
- `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` — the per-call cost record
  (`ProviderKey`, `InputTokens`, `OutputTokens`, `Cost`, `TenantId`, `Model`, …). Needs a `BillingMode`
  column, populated from 32-3's `ProviderCredential.Source` (the `credentialSource` diagnostic tag).
  Written at three sites: `ProviderEndpoints.IngestDiagnostic` (POST `/api/providers/diagnostics`,
  `Program.cs` line **1821**), `ProviderSessionService.cs` (lines **122**, **158**),
  `LlmProxyService.cs` (line **223**).
- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` — `Type`, `TenantId`, `Tags` (JSON),
  `Metadata`, `Data`, `SequenceNumber`. Appended via
  `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` `AppendAsync(DomainEvent)` — used
  here only for `PRICING.BYOK.ENABLED/DISABLED` on the toggle flows.

### Mode + RBAC seams

- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — process-wide
  `ITammaModeProvider` (`SingleUser`/`SaaS`).
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` auth policies: `SettingsManage` = `settings:manage`
  **owner-only** (line **1001**); `PromptManage` = `prompts:manage`, **tenant_owner OR tenant_admin**
  (lines **1012–1016**, with the explanatory comment **1006–1011** that `SettingsManage` would 403 every
  tenant_admin); `MemberAccess` = any authenticated user (line **991**); `PlatformOwnerAccess` =
  platform_admin (line **986**). Tenant-scoped endpoints follow `/api/v1/orgs/{tenantId}/…`.
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` (chain selection, line
  **67**) and `IProviderPricingService` (`Services/Providers/IProviderPricingService.cs`) exist — this
  story's mode selection complements, not replaces, these; key resolution stays in 32-3's
  `IProviderCredentialResolver` (same `Services/Providers/` folder).

**Decision flagged for the implementer:** the spec says BYOK management uses `SettingsManage`, but that
policy is owner-only in this codebase and would 403 tenant admins. Follow the `PromptManage`/`ConventionManage`
precedent: add a dedicated **`PricingManage`** policy (`pricing:manage`, owner+admin) and use it on the
BYOK mutation routes. Document `SettingsManage` as the spec's label.

---

## Architecture

**Control-plane mode registry + toggle endpoints + cost-record tag (key resolution stays in 32-3):**

1. **`TenantProviderBilling`** (CP table) — one `active` row per `(tenant, provider)`; `Mode` +
   `SecretName` + audit. Partial unique index + CHECKs (mode/status/secret-xor). This is the per-(tenant,
   provider) mode that 32-3's resolver reads.
2. **`MetricBillingMode`** (`Tamma.Core/Enums`) — `Platform|Byok`; shared with 34-1/34-5/metering.
3. **Key resolution = CONSUME Story 32-3** — `IProviderCredentialResolver.ResolveAsync` returns the key
   + `ProviderCredential.Source`. This story does NOT add a resolver; it persists the mode 32-3 reads and
   calls `Invalidate(tenantId, providerKey)` on every toggle.
4. **No LLM-call-path wiring here** — `CallLlmActivity` is wired to the resolver by Story 32-3, not this
   story. No internal key-resolution callback endpoint is added.
5. **BYOK lifecycle** — tenant endpoints store/retire the cabinet secret (`provider/{p}/api-key`, scope
   `Tenant`, purpose `ApiKey`) + flip the row + invalidate 32-3's cache; emit
   `PRICING.BYOK.ENABLED/DISABLED` DCB events.
6. **Cost tag** — `ProviderDiagnostic.BillingMode` populated on ingest from 32-3's
   `ProviderCredential.Source`; tagged on the cost DCB event so the 34-5 markup engine keys off it.
7. **CLI-provider 422** — the BYOK registration gate calls Story 32-4's
   `IProviderAuthRegistry.IsSaaSEligible`.

### Per-mode ownership (mandatory two-scoping answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Who owns the `(provider, mode)` choice? | the sole user (`tenantId` null) | the tenant (`tenant_id`) |
| Default | platform (no BYOK row → 32-3 resolves platform) | platform until a BYOK row exists |
| Who manages BYOK? | the user (no RBAC) | `tenant_owner`/`tenant_admin` via `PricingManage`; `member` → 403 |
| Cabinet scope | no per-tenant cabinet row needed | `SecretRef.ForTenant(tenantId, "provider/{p}/api-key")` |
| Cross-tenant | n/a | route `tenantId` ≠ caller's tenant → 404 |
| Key resolution at call time | owned by 32-3 | owned by 32-3 |
| Mode source | `ITammaModeProvider` (process-stable) | same |

---

## Task breakdown

### T1: `MetricBillingMode` enum + `TenantProviderBilling` entity + migration (core)

**Scope:** Shared enum, CP entity, model config, additive migration. No endpoints yet.

**Files:**
- New: `src/Tamma.Core/Enums/MetricBillingMode.cs` (`Platform`, `Byok`; with a `ToToken()`/`Parse`
  helper producing `"platform"`/`"byok"`).
- New: `src/Tamma.Data/Entities/TenantProviderBilling.cs` (shape per story Technical Design).
- Modify: `src/Tamma.Data/ControlPlaneDbContext.cs` (`DbSet<TenantProviderBilling>`),
  `src/Tamma.Data/TammaModelConfiguration.cs` (table name `tenant_provider_billing`, CHECK
  `ck_tpb_mode`/`ck_tpb_status`/`ck_tpb_secret_xor`, partial unique index
  `ux_tpb_active_provider` on `(TenantId, ProviderKey)` filtered `status='active'`, FK → tenants).
- Migration: `dotnet ef migrations add AddTenantProviderBilling -c ControlPlaneDbContext`
  (additive — new table, not a baseline CHECK edit). Then run `has-pending-model-changes` → none.

**Tests (first):** `tests/Tamma.Api.Tests/Pricing/TenantProviderBillingModelTests.cs` (extend the
existing `Epic28/ControlPlaneDbContextModelTests.cs` pattern) — model builds; the partial unique index
rejects a second active row for the same `(tenant, provider)`; secret-xor CHECK rejects byok-without-secret
and platform-with-secret; enum round-trips token ↔ value.

**Done when:** migration applies + rolls back cleanly; `has-pending-model-changes` clean; suite green.

### T2: BYOK lifecycle service + endpoints + CLI guard + RBAC

**Scope:** `TenantProviderBillingService` (enable/disable/get-mode) and `PricingEndpoints`. CLI-provider
guard via 32-4. `PricingManage` policy. 32-3 cache invalidation on toggle. NO key resolver, NO internal
key-resolution callback endpoint.

**Files:**
- New: `src/Tamma.Api/Services/Pricing/TenantProviderBillingService.cs` — `EnableByokAsync(tenantId,
  provider, apiKey, actor)` (store cabinet secret via `ISecretStore.CreateAsync` purpose `ApiKey` scope
  `Tenant` name `provider/{provider}/api-key` — the name 32-3 reads; upsert row byok/active/secretName;
  emit `PRICING.BYOK.ENABLED`; call `IProviderCredentialResolver.Invalidate(tenantId, provider)`);
  `DisableByokAsync` (retire secret via `RetireVersionAsync`; flip row to platform, `SecretName=null`;
  emit `PRICING.BYOK.DISABLED`; invalidate); `GetModeAsync` (mode + `keySet`).
- New: `src/Tamma.Api/Services/Pricing/PricingEventTypes.cs` (`PRICING.BYOK.ENABLED`,
  `PRICING.BYOK.DISABLED` only — key-resolution events belong to 32-3).
- New: `src/Tamma.Api/Endpoints/PricingEndpoints.cs` — tenant routes only (list/get/POST byok/DELETE
  byok). Mirror `AlertEndpoints.cs` structure. NO internal callback route.
- Modify: `src/Tamma.Api/Program.cs` — register `TenantProviderBillingService` (scoped) via a
  `PricingServiceCollectionExtensions` (mirror `AlertServiceCollectionExtensions`); add `PricingManage`
  policy (`pricing:manage`, owner+admin like `PromptManage`); map the routes (`PricingManage` on
  mutations, `MemberAccess` on reads). Add `pricing:manage` to the role-permission grant table next to
  `prompts:manage`.
- CLI-provider guard: call **Story 32-4's `IProviderAuthRegistry.IsSaaSEligible(provider)`**; `false`
  (CLI/token or unknown) → 422 `"CLI providers are single-user only"`. Do NOT reimplement the allowlist.

**Tests (first):** `tests/Tamma.Api.Tests/Pricing/PricingEndpointsTests.cs` (docker-bound) +
`TenantProviderBillingServiceTests.cs` —
enable stores cabinet row + DB row + event + invalidates 32-3 cache, returns `keySet:true`, never echoes
the key; disable retires + reverts + event + invalidates; one-active-row (second enable updates); RBAC
matrix (member 403, admin/owner 200, platform-owner 200); cross-tenant route 404; tenant A can't read
tenant B; a provider where `IsSaaSEligible` is `false` → 422; single-user mode passes all gates.
Stub `IProviderCredentialResolver` and `IProviderAuthRegistry`.

**Done when:** RBAC + isolation + 422 + reveal-once + cache-invalidation all asserted.

### T3: `ProviderDiagnostic.BillingMode` migration + default

**Scope:** Additive column (default `'platform'`).

**Files:**
- Modify: `src/Tamma.Data/Entities/ProviderDiagnostic.cs` (`public string BillingMode { get; set; } =
  "platform";`), `TammaModelConfiguration.cs` (default value).
- Migration: `dotnet ef migrations add AddProviderDiagnosticBillingMode` (additive column,
  `defaultValue: "platform"`). `has-pending-model-changes` → none.

> Note: `provider_diagnostics` may be tenant-DB-resident (per-tenant `TenantDbContext`) vs CP — verify
> which context owns `ProviderDiagnostic` before generating the migration; add the column to the owning
> context's migration set. (If both, mirror in both — check `ControlPlaneDbContext` vs `TenantDbContext`
> DbSets first.)

**Tests (first):** model test asserts default `platform`; the T4 ingest test asserts persistence.

**Done when:** column present, default applied, suite green.

### T4: Populate `ProviderDiagnostic.BillingMode` from 32-3's `ProviderCredential.Source`

**Scope:** Set the cost-record `BillingMode` from 32-3's credential source on the diagnostic
ingest/record path and tag the cost DCB event. NO `CallLlmActivity` changes.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/ProviderEndpoints.cs` (+ `ProviderSessionService.cs`,
  `LlmProxyService.cs`) — set `ProviderDiagnostic.BillingMode` from the `credentialSource` 32-3 already
  surfaces on the diagnostic/attempt, and tag the cost DCB event with `mode`. (32-3 owns producing
  `credentialSource`; this story persists it as the `BillingMode` column the 34-5 markup engine reads.)

**Tests (first):** extend `tests/Tamma.Api.Tests/ProviderSession/` — a diagnostic carrying
`credentialSource=byok` persists `BillingMode=byok`; default `platform` when absent; the cost DCB event
carries the `mode` tag.

**Done when:** `BillingMode` persists from `ProviderCredential.Source`; default `platform` holds.

---

## Sequencing & dependencies

```
T1 (enum + entity + migration)
  ├─> T2 (mode service + endpoints + 32-4 guard + RBAC)
  └─> T3 (diagnostic column) ──> T4 (populate BillingMode from 32-3 source + cost tag)
```

- **T1** is the only hard prerequisite for everything.
- **T2** and **T3** are parallel-safe after T1.
- **T2** consumes Story 32-3 (`IProviderCredentialResolver.Invalidate`) and Story 32-4
  (`IProviderAuthRegistry.IsSaaSEligible`) — both are external prerequisites, not built here.
- **T4** needs T3 (the column) and consumes 32-3's `credentialSource` on the diagnostic.

**External prerequisites:** Story **32-3** (provider-key resolution: `IProviderCredentialResolver` +
`ProviderCredential.Source`), Story **32-4** (`IProviderAuthRegistry.IsSaaSEligible`), Story 34-1
(shared enums + `PlanPrice.PricingMode`), Epic 29 cabinet (`ISecretStore` et al.). **Blocks:**
Story 34-5 (markup engine reads `BillingMode`).

---

## Risks + mitigations

- **Boundary violation: duplicating 32-3's key resolver or re-wiring `CallLlmActivity`.** *High.* This
  story persists the mode row + writes/retires the cabinet secret on toggle only; key reads and the
  LLM-call wiring stay in 32-3. Reviewer checklist item; no `IProviderKeyResolver`/internal callback in
  the file set.
- **Stale 32-3 credential cache after a mode toggle.** *Medium.* Call
  `IProviderCredentialResolver.Invalidate(tenantId, providerKey)` on every enable/disable; T2 asserts it.
- **`SecretName` mismatch between this story's write and 32-3's read.** *Medium.* Pin the shared name
  `provider/{providerKey}/api-key` (scope `Tenant`, purpose `ApiKey`); integration test asserts 32-3 can
  resolve the secret this story writes.
- **`SettingsManage` is owner-only and would 403 tenant admins.** *Medium.* Add the dedicated
  `PricingManage` (owner+admin) policy following the `PromptManage` precedent; document the spec's
  `SettingsManage` label.
- **Reinventing the CLI-provider rejection.** *Medium.* The 422 gate calls 32-4's
  `IProviderAuthRegistry.IsSaaSEligible`; do not duplicate the allowlist or message logic.
- **Migration discipline on the collapsed CP baseline.** *Medium.* Both changes are additive (new table,
  new column). Verify `has-pending-model-changes` reports none; mirror entity config solely in
  `TammaModelConfiguration.cs` (the established single source).
- **`ProviderKey` term overload (provider id vs Cranl backend label).** *Low.* The column is the provider
  identifier (`"anthropic"`), matching `ProviderDiagnostic.ProviderKey`; not the Cranl tenancy label.
  Documented in story Dev Notes.
- **Which context owns `ProviderDiagnostic`.** *Low/Medium.* Verify CP vs tenant DbContext before the
  T3 migration; add the column to the owning context (both if dual-resident).
- **Logging the supplied key.** *High.* Explicit no-secret logging rule (`provider`/`mode`/`tenantId`
  only); the POST body is never logged; reviewer checklist item.

---

## Acceptance criteria (mirrors the story)

- [ ] `TenantProviderBilling` entity + DbSet + partial-unique-index (one active row per
      `(tenant, provider)`) + CHECKs + additive migration; `has-pending-model-changes` clean.
- [ ] `MetricBillingMode` enum in `Tamma.Core/Enums` shared across pricing/metering layers.
- [ ] Key resolution CONSUMES Story 32-3's `IProviderCredentialResolver` / `ProviderCredential.Source`;
      this story defines NO key resolver and makes NO `CallLlmActivity` changes. The persisted mode is
      what 32-3 reads; platform is the default for single-user and rows-absent (32-3's resolution).
- [ ] BYOK enable stores the key in the cabinet (`SecretPurpose.ApiKey`, name `provider/{p}/api-key`) +
      flips the row to byok + invalidates 32-3's cache; disable reverts to platform + tombstones the
      secret ref + invalidates; both emit `PRICING.BYOK.ENABLED/DISABLED`.
- [ ] SaaS BYOK guard: a provider where `IProviderAuthRegistry.IsSaaSEligible` (Story 32-4) is `false`
      → 422 `"CLI providers are single-user only"`; this story does not reimplement the registry.
- [ ] `ProviderDiagnostic.BillingMode` (byok|platform) populated from 32-3's `ProviderCredential.Source`
      on the cost record + tagged on the DCB cost event so the 34-5 markup engine keys off it.
- [ ] RBAC: BYOK mutation reachable by tenant_owner/tenant_admin (via `PricingManage`); member → 403;
      single-user user has full control; reads available to any member.
- [ ] Per-tenant isolation: cross-tenant route → 404; cabinet `SecretRef.ForTenant` scoping; a tenant
      can't read another tenant's mode.
- [ ] Unit + integration tests cover all the above incl. tenant-isolation; no Stripe; `ISecretStore`,
      `IProviderCredentialResolver`, and `IProviderAuthRegistry` mocked. Full suite green.
