# Story 37-10: Sensitive-Action Audit Emission Coverage — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan step-by-step. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every step writes the
> assertion before the emission code.

**Goal:** Close the emission gaps so the Story 37-1 sensitive-action **catalog** is actually fed.
Instrument the action sites that today mutate sensitive state without an audit event — BYOK/provider
keys, billing/plan/subscription, auth/login + token refresh + API-key auth, agent actions, persona/
config edits, data export — mapping each onto the 37-1 catalog and routing each event to the correct
scope (tenant `domain_events` vs control-plane `platform_events`). Reuse the existing single-source
emitters (`SECRET.*`, `IMPERSONATION.*`, `TENANT.MEMBER_*`, `AUTH.REFRESH_REUSE_DETECTED`) by mapping
them into the catalog — never by re-emitting.

**Story file:** `docs/stories/epic-37/story-37-10/37-10-sensitive-action-audit-emission-coverage.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`). **`packages/api` is DELETED — never a target.**

**Required reading before code:** `docs/guides/BEFORE_YOU_CODE.md`, Story 37-1 (catalog +
`audit_records` projection), this story file.

---

## Non-goals (YAGNI guard)

- **NO new catalog model.** Story 37-1 owns `SensitiveActionCatalog` + the `audit_records` projection.
  37-10 only **adds entries** to the catalog and **wires emissions**. If 37-1 is not merged, this plan
  is blocked on it (see Dependencies).
- **NO re-emission of already-audited actions.** `SECRET.*`, `IMPERSONATION.*`, `TENANT.MEMBER_*`,
  `AUTH.REFRESH_REUSE_DETECTED` keep their one emission; 37-10 maps them into the catalog and tests
  the single emission. Adding a second event for these is a defect, not a feature.
- **NO change to auth/billing/secret behaviour.** Login still logs in, a secret write still writes,
  a plan change still changes the plan. Emission is a side effect that must never alter, delay, or
  roll back the action (never-throws contract, mirrors `ISecretAccessAuditor`).
- **NO new delivery / alerting.** This story feeds the audit trail; alert rules over these event types
  are a separate concern (Story 5.6 / missing-config alert pipeline already exists if needed).
- **NO secret material in events.** Metadata only — provider, mode, version number, key prefix.

---

## Current-state findings (verified 2026-06-17, repo @ main)

### Already-emitting sites (reuse — map into catalog, do NOT re-emit)

| Site | Event(s) today | Emission mechanism |
|---|---|---|
| `src/Tamma.Api/Services/Secrets/Query/SecretQueryService.cs` (~195), `Reveal/SecretRevealService.cs`, `Stopgap/StopgapSecretMigrator.cs` | `SECRET.WRITE`, `SECRET.ROTATE.*`, `SECRET.REVEAL`, `SECRET.VERSION.REVOKED`, `SECRET.MIGRATED.*` (`SecretAuditEventTypes`) | `ISecretAccessAuditor.EmitAsync` |
| `src/Tamma.Api/Endpoints/OrgEndpoints.cs` (~227, 278, 367, 507, 618) | `TENANT.MEMBER_ROLE_CHANGED/REMOVED/INVITED/INVITE_RESENT/JOINED.SUCCESS` | `EmitTenantEvent(IEventRepository, ...)` → `domain_events` |
| Impersonation | `IMPERSONATION.STARTED` / `IMPERSONATION.ENDED` | DomainEvent/PlatformEvent |
| `src/Tamma.Api/Endpoints/AuthEndpoints.cs` (~1123) | `AUTH.REFRESH_REUSE_DETECTED` | `PlatformEvent` |

### Silent sites (NEW emission needed)

| Site | Today | Add |
|---|---|---|
| Secret cabinet, `SecretPurpose.ApiKey` + `SecretScope.Tenant` writes | `SECRET.WRITE`/`ROTATE` only | curated `BYOK.PROVIDER_KEY.SET/ROTATED/REMOVED` + `BILLING.BYOK_MODE_CHANGED` |
| `AuthEndpoints.Login` (~543) | no login audit event | `AUTH.LOGIN.SUCCESS` / `AUTH.LOGIN.FAILURE(reason)` |
| `AuthEndpoints.Refresh` (~653) | reuse event only | `AUTH.TOKEN.REFRESHED` |
| `Auth/ApiKeyAuthHandler.cs` `BuildSuccessTicket` (~526) | no auth-usage event | `AUTH.APIKEY.USED` (throttled) |
| `Endpoints/Admin/AdminTenantsEndpoints.cs` (~629) | `PLAN.UPDATED` | re-type → `BILLING.PLAN_CHANGED`; `SUBSCRIPTION.*` |
| `Endpoints/AgentEndpoints.cs` (~93) + `AGENT.DISPATCH.*`/`AGENT.RESULTS.*` | `AGENT_CONFIG.UPDATED.SUCCESS` (no `agentId`) | add `agentId` tag; catalog `AGENT.ACTION.*` |
| Prompt-store / convention-store admin write endpoints | persona/config edits unaudited | `CONFIG.PERSONA_CHANGED` |
| Audit/DSAR export endpoint(s) (37-1/37-x) | — | `DATA.EXPORT.REQUESTED/COMPLETED` |

### Emission primitives (reuse)

- `IEventRepository.AppendAsync(DomainEvent)` — `src/Tamma.Data/Repositories/IEventRepository.cs`.
  `DomainEvent` has `Type`, `TenantId`, `Tags` (JSON), `Metadata` (JSON), `Data` (JSON), `CreatedAt`.
- `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent)` —
  `src/Tamma.Data/Abstractions/IPlatformEventPublisher.cs`. Platform/control-plane scope.
- Established shape: `OrgEndpoints.EmitTenantEvent` (domain), `AuthEndpoints.BuildRefreshReuseEvent`
  (platform). Metadata convention: `{"workflowVersion":"1.0.0","eventSource":"system"}`.
- `ITammaModeProvider` (`src/Tamma.Api/Services/PromptStore/TammaMode.cs`) — process-stable mode.
- `ApiKeyAuthHandler` resolves services from an injected `IServiceProvider` — so it can resolve the
  emitter without a ctor change.

### NOT present yet (from 37-1 — prerequisite)

- `src/Tamma.Core/Audit/SensitiveActionCatalog.cs` — does not exist at HEAD; comes from Story 37-1.
- No `audit_records` table/projection yet. **This plan is blocked until 37-1 lands those.**

---

## Architecture

**One emitter, one scope-routing decision, catalog-validated.**

```
call site ──► ISensitiveActionEmitter.EmitAsync(SensitiveAction{ Type, TenantId, Actor, Tags, Data })
                         │  (Type must be in SensitiveActionCatalog — else log+drop, never throw)
                         ▼
            TenantId != null ──► IEventRepository.AppendAsync(DomainEvent)        (tenant scope)
            TenantId == null ──► IPlatformEventPublisher.AppendAndPublish(...)    (control-plane)
                         │
                         ▼
            Story 37-1 projection ──► audit_records  (correct scope)
```

- The emitter is the single place that decides tenant vs platform, runs the catalog typo-guard, and
  runs the defensive redaction strip. Never throws to the caller.
- Curated `BYOK.*` events are derived **alongside** the existing `SECRET.*` emission (a decorator on
  `ISecretAccessAuditor`, or an emitter call in the secret facade gated on `ApiKey`+`Tenant`), so one
  cabinet write yields one `SECRET.*` + one `BYOK.*` — never a second secret write.

### Per-mode ownership (mandatory two-scoping-model answer)

| Action class | single-user | SaaS |
|---|---|---|
| BYOK, plan/subscription, persona/config (tenant), tenant agent action, tenant export | Sole user's personal tenant → `TenantId` set, lands in their feed | Tenant → `TenantId` set, `domain_events`, visible to tenant_owner/admin |
| Platform-owner login, impersonation, system-scope persona/config, API-key auth at platform edge | Sole user (one principal) | Platform owner → `platform_events`, `TenantId` null, never exposed to tenants |
| Login failure | User's feed | Platform-scoped (no trusted tenant yet); email + reason only |

---

## Story breakdown

### S1: `ISensitiveActionEmitter` + catalog extension (core, no call-site wiring)

**Scope:** The single emission seam + the catalog entries. No site instrumentation yet.

**Files:**
- New: `src/Tamma.Api/Services/Audit/ISensitiveActionEmitter.cs`, `SensitiveActionEmitter.cs`,
  `SensitiveAction.cs` (record + tag/data dictionaries; scope derived from `TenantId`).
- Modify: `src/Tamma.Core/Audit/SensitiveActionCatalog.cs` (NEW from 37-1) — add catalog entries for
  reused types (`SECRET.*`, `IMPERSONATION.*`, `TENANT.MEMBER_*`, `AUTH.REFRESH_REUSE_DETECTED`) and
  new types (`BYOK.PROVIDER_KEY.SET/ROTATED/REMOVED`, `BILLING.PLAN_CHANGED`, `BILLING.BYOK_MODE_CHANGED`,
  `SUBSCRIPTION.CREATED/UPDATED/CANCELED`, `AUTH.LOGIN.SUCCESS/FAILURE`, `AUTH.TOKEN.REFRESHED`,
  `AUTH.APIKEY.USED`, `AGENT.ACTION.*`, `CONFIG.PERSONA_CHANGED`, `DATA.EXPORT.REQUESTED/COMPLETED`).
- New: `src/Tamma.Api/Extensions/AuditEmissionServiceCollectionExtensions.cs`; wire in `Program.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/SensitiveActionEmitterTests.cs` —
`TenantId` set → `IEventRepository.AppendAsync` called with that `TenantId`, `IPlatformEventPublisher`
NOT called; `TenantId` null → `IPlatformEventPublisher.AppendAndPublishAsync` called, repo NOT called;
un-cataloged `Type` → dropped + WARN, neither sink called; sink throws → swallowed, EmitAsync returns
without throwing; redaction guard strips a `tamma_sk_`-shaped value from `Data`; metadata shape
matches the repo convention.

**Acceptance criteria:**
- [ ] Every reused + new event type is in `SensitiveActionCatalog`.
- [ ] Scope routing: `TenantId` non-null → domain stream; null → platform stream.
- [ ] `EmitAsync` never throws to the caller; un-cataloged type is logged + dropped.
- [ ] Redaction guard removes secret-shaped values; full suite stays green.

### S2: BYOK / provider-key emission (depends S1)

**Scope:** Derive `BYOK.PROVIDER_KEY.*` + `BILLING.BYOK_MODE_CHANGED` from the secret-cabinet
`ApiKey`+`Tenant` write path, alongside the existing `SECRET.*` emission.

**Files:**
- Modify: secret facade / a decorator on `ISecretAccessAuditor` (`src/Tamma.Api/Services/Secrets/`)
  — on `SECRET.WRITE`/`ROTATE`/version-revoke for `Reference.Purpose == ApiKey &&
  Reference.Scope == Tenant`, call `ISensitiveActionEmitter` with `BYOK.PROVIDER_KEY.SET/ROTATED/REMOVED`,
  tags `provider`, `tenantId`, `actor`, `mode` (`byok`|`platform-provided`, from 32-3 resolution).
- BYOK-mode toggle site (provider config flip byok↔platform-provided) → `BILLING.BYOK_MODE_CHANGED`.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/ByokEmissionTests.cs` — a tenant ApiKey write emits
exactly one `SECRET.WRITE` AND one `BYOK.PROVIDER_KEY.SET` (not two cabinet writes); rotate →
`ROTATED`; revoke → `REMOVED`; a platform-scope or non-ApiKey secret write does NOT emit a `BYOK.*`;
redaction: stored `audit_record` carries provider/mode/version only, zero key bytes; mode toggle emits
`BILLING.BYOK_MODE_CHANGED` with old/new mode.

**Acceptance criteria:**
- [ ] One cabinet write → one `SECRET.*` + one `BYOK.*`; no double secret write.
- [ ] `BYOK.*` only for `ApiKey`+`Tenant` secrets; tagged provider/tenant/actor/mode.
- [ ] Redaction test passes (no key material in the audit record).

### S3: Auth/login emission (depends S1)

**Scope:** Login success/failure, token refresh, API-key auth.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/AuthEndpoints.cs` — `Login` emits `AUTH.LOGIN.SUCCESS` (success
  path ~644) / `AUTH.LOGIN.FAILURE(reason)` (all failure returns: bad creds, lockout via
  `ILoginLockoutService`, unverified email); `Refresh` emits `AUTH.TOKEN.REFRESHED` on success.
  `ip`/`user_agent` from `HttpContext`. Add `[FromServices] ISensitiveActionEmitter`.
- Modify: `src/Tamma.Api/Auth/ApiKeyAuthHandler.cs` — `BuildSuccessTicket` (~526) emits
  `AUTH.APIKEY.USED` (throttled per `(apiKeyId, time bucket)` using the existing throttle pattern),
  tags `tenantId`, `apiKeyPrefix`, `scope`, `ip`. Resolve emitter from injected `IServiceProvider`.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuthEmissionTests.cs` — successful login →
`AUTH.LOGIN.SUCCESS` (userId, ip, userAgent, no password); each failure reason →
`AUTH.LOGIN.FAILURE` with that `reason` + email, no password; lockout → `reason=locked_out`; refresh
→ `AUTH.TOKEN.REFRESHED`; reuse path still emits ONLY `AUTH.REFRESH_REUSE_DETECTED` (no duplicate);
100 API-key auths in one bucket → one (or sampled-N) `AUTH.APIKEY.USED`; scope: login success
tenant/platform per resolved active tenant, login failure platform-scoped.

**Acceptance criteria:**
- [ ] Login success + every failure reason emit the right catalog code with ip/user_agent.
- [ ] Token refresh emits `AUTH.TOKEN.REFRESHED`; reuse path unchanged (no double).
- [ ] `AUTH.APIKEY.USED` is throttled (no per-request flood); prefix only, never the key.

### S4: Billing / plan / subscription (depends S1)

**Scope:** Re-type plan-update to the catalog; add subscription lifecycle events.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` (~629) — `PLAN.UPDATED` →
  `BILLING.PLAN_CHANGED` (keep `PLAN.UPDATED` as a legacy catalog alias if dashboards depend on it),
  tags `tenantId`, `actor`, data `{ oldPlanSlug, newPlanSlug }`. Subscription create/update/cancel →
  `SUBSCRIPTION.*`. If no subscription entity exists, wire at the `Plan`/`BudgetConfig` change site
  that stands in for subscription state and note the gap in Dev Notes.

**Tests (first):** extend `tests/Tamma.Api.Tests/` billing/admin tests — plan change emits
`BILLING.PLAN_CHANGED` with old/new slug (no card data); subscription mutations emit the matching
`SUBSCRIPTION.*`; events land tenant-scoped.

**Acceptance criteria:**
- [ ] Plan change emits `BILLING.PLAN_CHANGED`; the silent Epic 20 TS path is replaced.
- [ ] Subscription mutations emit `SUBSCRIPTION.CREATED/UPDATED/CANCELED`, tenant-scoped, no PII.

### S5: Agent actions + persona/config + data export (depends S1)

**Scope:** `agentId` tagging + catalog `AGENT.ACTION.*`; persona/config edit events; export events.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/AgentEndpoints.cs` (~93) — add `agentId` to
  `AGENT_CONFIG.UPDATED.SUCCESS` tags; map into catalog. Thread `agentId` into `AGENT.DISPATCH.*` /
  `AGENT.RESULTS.*` tags at their emission sites (32-6 trail).
- Modify: prompt-store / convention-store admin write endpoints — emit `CONFIG.PERSONA_CHANGED`
  (scope system/tenant, role, action, actor, tenantId) on persona/template edits.
- Modify: audit/DSAR export endpoint(s) (37-1/37-x) — emit `DATA.EXPORT.REQUESTED` on accept and
  `DATA.EXPORT.COMPLETED` on finish (counts only).

**Tests (first):** extend agent/prompt/convention/export tests — agent dispatch/results/config carry
`agentId`; per-tenant agent trail reconstructable from `audit_records`; persona edit emits
`CONFIG.PERSONA_CHANGED`; export emits paired `DATA.EXPORT.*` with counts, no payload bytes.

**Acceptance criteria:**
- [ ] Agent actions carry `agent_id`; `AGENT.ACTION.*` in catalog; tenant-scoped trail visible.
- [ ] Persona/config edits and data exports emit their catalog events, scope-correct, redaction-safe.

### S6: Coverage test + catalog mapping assertions (depends S1-S5)

**Scope:** The AC15 enumerated coverage test + the AC1/AC14 no-double-emission assertions.

**Files:**
- New: `tests/Tamma.Api.Tests/Audit/SensitiveActionCoverageTests.cs` — one parameterized case per
  site (BYOK set/rotate/remove, BYOK-mode, plan, subscription ×3, login success, login failure ×reasons,
  token refresh, API-key auth, agent dispatch+results, persona/config, data export): assert catalog
  code, required tags (`actor`, `tenantId`/null, site-specific), and that the 37-1 projection writes
  the `audit_record` to the correct scope.
- No-double-emission: membership role change → exactly one `TENANT.MEMBER_ROLE_CHANGED.SUCCESS`;
  secret write → exactly one `SECRET.WRITE` + one `BYOK.PROVIDER_KEY.SET`.
- Cross-tenant leakage: a tenant audit query never returns another tenant's sensitive rows.
- Mode matrix: single-user vs SaaS placement of a system-scope action.

**Acceptance criteria:**
- [ ] The coverage test enumerates every site and is green.
- [ ] No reused site double-emits; scope routing + redaction asserted end-to-end.
- [ ] `sg docker -c "dotnet test ..."` full suite green.

---

## Story order & dependencies

S1 → (S2, S3, S4, S5 in parallel) → S6. S1 is the only hard prerequisite for the wiring stories; S6
needs them all. **The whole plan is blocked on Story 37-1** (the `SensitiveActionCatalog` type and the
`audit_records` projection must exist first — verified absent at HEAD).

External deps: Epic 29 (secret cabinet — S2), Epic 32 / 32-3 / 32-6 (BYOK mode resolution + agent
trail — S2/S5), Epic 20 re-targeted to C# billing (S4), Epic 16/18 auth (S3), Epic 34/35 billing
surface (S4).

## Risks

- **37-1 not merged.** Hard blocker — `SensitiveActionCatalog` and `audit_records` don't exist yet.
  Confirm 37-1 is in before starting; if implementing the wave together, S1 depends on 37-1's S-final.
- **Double-emission.** The classic defect: adding a second event for an already-audited action, or
  re-writing a secret to derive a `BYOK.*` payload. Mitigation: the reuse-vs-new table is authoritative;
  S6 asserts exactly-one per reused site, and S2 asserts one cabinet write → one `SECRET.*` + one
  `BYOK.*`.
- **Wrong-scope leak.** A tenant event in `platform_events` (invisible to the tenant) or a
  platform-owner action in a tenant stream (leaks operator activity). Mitigation: one emitter makes the
  decision in one tested place; S6 has a cross-tenant leakage test.
- **API-key auth flood.** Emitting `AUTH.APIKEY.USED` on every request floods the trail. Mitigation:
  throttle keyed on `(apiKeyId, time bucket)`; S3 asserts a 100-request burst yields one/sampled-N.
- **Redaction regression.** A caller passes a raw key in `Data`. Mitigation: defensive strip in the
  emitter (belt-and-suspenders), plus the S2/S3/S4 redaction assertions that feed real-looking secrets
  and assert zero key bytes land.
- **Never-throws contract is load-bearing.** A broken event sink must not 500 a login or roll back a
  secret write. Mitigation: emitter swallows + logs (mirrors `ISecretAccessAuditor`); S1 tests the
  sink-throws path.
- **Subscription entity may not exist on the C# surface.** S4 falls back to `Plan`/`BudgetConfig`
  change sites and documents the gap rather than inventing a new entity in this story.
