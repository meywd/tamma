# Story 37-10: Sensitive-Action Audit Emission Coverage (BYOK, Billing/Plan, Auth/Login, Agent Actions)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **platform owner / compliance reviewer**,
I want every sensitive action — a tenant API key being set/rotated/removed, a plan or subscription
change, a login success or failure, a membership/role change, a persona/config edit, an agent action,
a data export — to emit a curated DCB audit event that lands in the correct scope (tenant vs
control-plane),
So that the Story 37-1 catalog is actually fed and the audit trail is complete enough to stand up to
a SOC2 / GDPR review instead of being silently full of holes.

## Priority

P0 - The 37-1 taxonomy/catalog is worthless if the action sites never emit. This story closes the
emission gaps that make the audit product trustworthy.

## Context & Boundary

Story 37-1 defines the **catalog** of sensitive-action event types and the projection that lands them
into `audit_records`. This story (37-10) is the **wiring** story: it instruments the action sites that
today mutate sensitive state **without** an audit event, mapping each onto the 37-1 catalog.

Target architecture is the C# app **`apps/tamma-elsa`**. The TypeScript `packages/api` tree is
DELETED and is never a target — the Epic 20 billing/metering path that lived there is re-targeted to
the C# billing/plan surface (`Plan` / `BudgetConfig` + the admin plan endpoint).

**Reuse, do not re-emit.** Some sites already have a single source of truth:
- Secret cabinet — `ISecretAccessAuditor.EmitAsync` with `SecretAuditEventTypes.*`
  (`SECRET.WRITE` / `SECRET.ROTATE.*` / `SECRET.REVEAL` / `SECRET.VERSION.REVOKED`), wired through
  `SecretQueryService` / `SecretRevealService` / `StopgapSecretMigrator`.
- Membership / RBAC — `OrgEndpoints.EmitTenantEvent(...)` already emits
  `TENANT.MEMBER_ROLE_CHANGED.SUCCESS`, `TENANT.MEMBER_REMOVED.SUCCESS`,
  `TENANT.MEMBER_INVITED.SUCCESS`, `TENANT.MEMBER_INVITE_RESENT.SUCCESS`,
  `TENANT.MEMBER_JOINED.SUCCESS`.
- Impersonation — `IMPERSONATION.STARTED` / `IMPERSONATION.ENDED` already exist.
- Refresh-token reuse — `AUTH.REFRESH_REUSE_DETECTED` already emits a `PlatformEvent`.

For these, this story **maps the existing event type into the 37-1 catalog** (so it surfaces in
`audit_records`) and adds a coverage test asserting the single emission — it does NOT add a second
emission at the same site (no double-emission).

The genuinely **silent** sites — BYOK provider-key set/rotate/remove, billing/plan/subscription
mutations, login success/failure + token refresh + API-key auth, agent-action `agent_id` tagging,
persona/config edits, data export — get **new** emissions wired here.

## Acceptance Criteria

### Catalog mapping (no double-emission)

1. The 37-1 `SensitiveActionCatalog` (NEW from 37-1, `apps/tamma-elsa/src/Tamma.Core/Audit/`) is
   extended to include catalog entries for the event types reused from existing emitters — `SECRET.*`,
   `IMPERSONATION.*`, `TENANT.MEMBER_*`, `AUTH.REFRESH_REUSE_DETECTED` — so they project into
   `audit_records` **without** any change to their emission sites. A test asserts each is in the
   catalog and that the existing site still emits exactly one event (no duplicate added).

### BYOK / provider keys

2. Tenant BYOK provider-key changes emit `BYOK.PROVIDER_KEY.SET`, `BYOK.PROVIDER_KEY.ROTATED`,
   `BYOK.PROVIDER_KEY.REMOVED` at the secret-cabinet write path for `SecretPurpose.ApiKey`
   tenant-scoped secrets (32-3 credential resolution). The emission carries tags `provider`,
   `tenantId`, `actor` (user id), and `mode` (`byok` vs `platform-provided`) — aligned with the
   BYOK-per-tenant constraint. The underlying `SECRET.WRITE`/`SECRET.ROTATE.*` event remains the
   cabinet's source of truth; the `BYOK.*` event is the **catalog-facing curated** event derived from
   it (one cabinet write → one `SECRET.*` + one `BYOK.*`, not two cabinet writes).

3. Platform-provided fallback toggles (a tenant flips between BYOK and platform-provided keys for a
   provider) emit `BILLING.BYOK_MODE_CHANGED` with old/new mode in `data`, tagged `provider`,
   `tenantId`, `actor`.

### Billing / plan / subscription

4. Plan changes on the C# billing surface emit `BILLING.PLAN_CHANGED` from the admin plan-update path
   (`AdminTenantsEndpoints` `PLAN.UPDATED` site) — the existing `PLAN.UPDATED` event is mapped into
   the catalog as `BILLING.PLAN_CHANGED` (or re-typed; see Technical Design) carrying `tenantId`,
   `actor`, and `{ oldPlanSlug, newPlanSlug }` in `data`, **replacing** the silent Epic 20 TS path.

5. Subscription lifecycle mutations emit `SUBSCRIPTION.CREATED`, `SUBSCRIPTION.UPDATED`,
   `SUBSCRIPTION.CANCELED` at the subscription state-change site, tagged `tenantId`, `actor`, with
   plan/period in `data`. (If a subscription entity does not yet exist on the C# surface, these are
   wired at the `Plan` + `BudgetConfig` mutation sites that stand in for subscription state — see
   Technical Design / Dependencies.)

### Auth / login

6. The login endpoint emits `AUTH.LOGIN.SUCCESS` on a successful authentication and
   `AUTH.LOGIN.FAILURE` (with a machine-readable `reason` — `bad_credentials`, `locked_out`,
   `unverified_email`, etc.) on failure, both carrying `ip` and `user_agent` tags plus `userId`
   (success) / `email` (failure, redaction-safe). Lockout-triggered failures still emit
   `AUTH.LOGIN.FAILURE` with `reason=locked_out`.

7. Token refresh emits `AUTH.TOKEN.REFRESHED` (tagged `userId`, `tenantId`, `ip`); the existing
   `AUTH.REFRESH_REUSE_DETECTED` path is untouched and only mapped into the catalog (AC1).

8. API-key authentication emits `AUTH.APIKEY.USED` from `ApiKeyAuthHandler` on a successful ticket,
   tagged `tenantId`, `apiKeyPrefix` (NOT the key), `scope`, `ip`. Emission is rate-aware: a hot
   per-request auth loop is de-duplicated (sampled/throttled) so the audit trail is not flooded — see
   Technical Design.

### Agent actions

9. Agent actions (Epic 32) carry `agent_id` in their DCB event tags, and the catalog includes
   `AGENT.ACTION.*` so per-tenant agent action trails appear in `audit_records` scoped to the tenant.
   Existing `AGENT.DISPATCH.*` / `AGENT.RESULTS.*` / `AGENT_CONFIG.UPDATED.SUCCESS` events gain an
   `agentId` tag where one is in scope and are mapped into the catalog.

### Persona / config + data export

10. Persona/config changes emit a catalog event — `AGENT_CONFIG.UPDATED.SUCCESS` (already emitted by
    `AgentEndpoints`) is mapped into the catalog and gains `agentId`; persona edits at the
    prompt-store / convention-store admin write sites emit `CONFIG.PERSONA_CHANGED` tagged with
    `scope` (system/tenant), `role`, `action`, `actor`, `tenantId`.

11. Data export actions (audit/DSAR export endpoints) emit `DATA.EXPORT.REQUESTED` /
    `DATA.EXPORT.COMPLETED` tagged `tenantId`, `actor`, `exportType`, with row/record counts in
    `data` (never the exported payload).

### Scope routing + safety + coverage

12. Every emission lands in the **correct scope**: tenant-scoped actions (BYOK, plan/subscription,
    membership, persona/config, tenant agent actions, tenant data export) append to the tenant's
    `domain_events` stream via `IEventRepository` with `TenantId` set; control-plane / platform-owner
    actions (platform-owner login, API-key auth at the platform edge, system-scope persona/config,
    impersonation) append to `platform_events` via `IPlatformEventPublisher` with `TenantId` null.
    Per-mode: in single-user mode the sole user's actions land in their (only) feed.

13. All new emissions are **redaction-safe** — no key material, no plaintext secret, no card/payment
    data, no full API key (prefix only). A test feeds a BYOK key-set and asserts the stored
    `audit_record` carries only metadata (provider, mode, version number) and zero key bytes.

14. **No double-emission**: existing `SECRET.*` / `IMPERSONATION.*` / `TENANT.MEMBER_*` /
    `AUTH.REFRESH_REUSE_DETECTED` remain the single source for their actions and are mapped into the
    catalog rather than re-emitted; a coverage test asserts exactly one event per action at each
    reused site.

15. A **coverage test** enumerates the sensitive-action sites and, per site (BYOK, billing/plan,
    subscription, login success, login failure, token refresh, API-key auth, agent action,
    persona/config, data export), asserts: the emitted catalog code matches the 37-1 taxonomy, the
    required tags (`actor`, `tenantId`/none, plus site-specific) are present, and the 37-1 projection
    lands the event into `audit_records` in the correct scope.

## Technical Design

### Verified current state (repo @ main, 2026-06-17)

| Site | Today | This story |
|---|---|---|
| Secret cabinet (`SecretQueryService`, `SecretRevealService`, `StopgapSecretMigrator`) | Emits `SECRET.*` via `ISecretAccessAuditor.EmitAsync` (`SecretAuditEventTypes`) | Map `SECRET.*` into catalog; derive curated `BYOK.PROVIDER_KEY.*` for `ApiKey`+Tenant writes |
| `OrgEndpoints.EmitTenantEvent` | Emits `TENANT.MEMBER_*.SUCCESS` to `domain_events` | Map into catalog (no new emission) |
| Impersonation | `IMPERSONATION.STARTED` / `ENDED` | Map into catalog |
| `AuthEndpoints.Login` | No login audit event | Emit `AUTH.LOGIN.SUCCESS` / `AUTH.LOGIN.FAILURE` |
| `AuthEndpoints.Refresh` | `AUTH.REFRESH_REUSE_DETECTED` only (PlatformEvent) | Add `AUTH.TOKEN.REFRESHED`; map reuse event into catalog |
| `ApiKeyAuthHandler.BuildSuccessTicket` | No auth-usage event | Emit `AUTH.APIKEY.USED` (throttled) |
| `AdminTenantsEndpoints` plan path | Emits `PLAN.UPDATED` | Re-type/alias to `BILLING.PLAN_CHANGED`; add subscription events |
| `AgentEndpoints` config path | `AGENT_CONFIG.UPDATED.SUCCESS` (has `tenantId`/`userId`, no `agentId`) | Add `agentId` tag; map into catalog |
| `AGENT.DISPATCH.*` / `AGENT.RESULTS.*` | Emitted by dispatch path | Add `agentId` tag; catalog `AGENT.ACTION.*` |
| Audit/DSAR export endpoints (37-x) | (added in 37-1/37-x) | Emit `DATA.EXPORT.REQUESTED` / `COMPLETED` |

### Emission helper

Introduce a single, thin `ISensitiveActionEmitter` (NEW,
`apps/tamma-elsa/src/Tamma.Api/Services/Audit/`) so every call site emits the same shape and the
scope-routing decision lives in one place:

```csharp
public interface ISensitiveActionEmitter
{
    /// Append a curated sensitive-action event. Routes to the tenant
    /// domain_events stream (TenantId set) via IEventRepository, or to
    /// platform_events (TenantId null) via IPlatformEventPublisher.
    /// Never throws to the caller — an audit-sink outage must not roll
    /// back the action that already happened (mirrors ISecretAccessAuditor).
    Task EmitAsync(SensitiveAction action, CancellationToken ct = default);
}

public sealed record SensitiveAction(
    string Type,                       // must be a SensitiveActionCatalog code (37-1)
    Guid? TenantId,                    // null => platform/control-plane scope
    Guid? ActorUserId,
    IReadOnlyDictionary<string, string?> Tags,   // provider, mode, ip, userAgent, ...
    IReadOnlyDictionary<string, object?> Data);  // redaction-safe payload only
```

- **Validation:** `EmitAsync` rejects (logs + drops, never throws) a `Type` that is not in
  `SensitiveActionCatalog` so a typo can't silently create an un-cataloged event.
- **Scope routing:** `TenantId != null` → `IEventRepository.AppendAsync(DomainEvent{ TenantId, ... })`
  (the established `OrgEndpoints.EmitTenantEvent` pattern); `TenantId == null` →
  `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent{ ... })`.
- **Metadata** follows the repo convention: `{"workflowVersion":"1.0.0","eventSource":"system"}`.
- **Redaction:** `Tags`/`Data` are caller-supplied; the emitter additionally runs a guard that strips
  known-secret keys (`key`, `apiKey`, `token`, `password`, `secret`, `connectionString`, `card`,
  `cardNumber`) defensively, and asserts no value matches the `tamma_sk_` / `sk-` / PEM prefixes.

### Per-mode / per-tenant placement

| Question | single-user | SaaS |
|---|---|---|
| Who owns a tenant-scope sensitive action (BYOK, plan, persona, tenant agent action, export)? | The sole user — `TenantId` is their personal tenant; lands in their feed. | The tenant — `TenantId` set; lands in the tenant's `domain_events` (visible to `tenant_owner`/`tenant_admin`). |
| Who owns a control-plane action (platform-owner login, impersonation, system-scope persona/config)? | The user (only one principal). | Platform owner — `platform_events`, `TenantId` null, never exposed to tenants. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable | same |

### Site wiring notes

- **BYOK** (AC2/AC3): the curated `BYOK.PROVIDER_KEY.*` event is derived **alongside** the existing
  `SECRET.*` emission — the natural seam is a decorator/observer on `ISecretAccessAuditor` (or a call
  to `ISensitiveActionEmitter` inside the secret facade) gated on
  `Reference.Purpose == ApiKey && Reference.Scope == Tenant`. `provider` is read from the secret's
  consumer ref / metadata; `mode` is `byok` for a tenant-scoped key and `platform-provided` when the
  tenant resolves the platform key (32-3 credential resolution decides which). One cabinet write
  produces one `SECRET.WRITE` AND one `BYOK.PROVIDER_KEY.SET` (catalog-facing) — they are NOT two
  writes.
- **Auth** (AC6-8): `AuthEndpoints.Login` and `Refresh` resolve `ISensitiveActionEmitter` as a
  `[FromServices]` parameter. `ApiKeyAuthHandler` resolves it from its injected `IServiceProvider`
  (the handler already resolves services that way). `ip`/`user_agent` come from
  `HttpContext.Connection.RemoteIpAddress` / `User-Agent` header. `AUTH.LOGIN.SUCCESS` is tenant- or
  platform-scoped per the resolved active tenant; `AUTH.LOGIN.FAILURE` is platform-scoped (no trusted
  tenant yet) and carries only the submitted email + reason (redaction-safe).
- **API-key auth throttle** (AC8): `AUTH.APIKEY.USED` must not emit on every request. Throttle keyed
  on `(apiKeyId, coarse time bucket)` (e.g. one event per key per N minutes) using the existing
  in-process throttle pattern (`TokenBucketAlertRateLimiter` / `AlertRuleEvaluator` throttle) so a
  busy key produces a heartbeat, not a flood.
- **Billing** (AC4/5): re-type the `PLAN.UPDATED` emission in `AdminTenantsEndpoints` to
  `BILLING.PLAN_CHANGED` (keep `PLAN.UPDATED` as a legacy alias in the catalog if dashboards depend on
  it). Subscription events wire at the subscription-state mutation; if no subscription entity exists
  yet, wire at the `Plan`/`BudgetConfig` change sites that represent subscription state and document
  the gap in Dev Notes.
- **Agent** (AC9/10): thread `agentId` into the `Tags` of `AGENT.DISPATCH.*` / `AGENT.RESULTS.*` /
  `AGENT_CONFIG.UPDATED.SUCCESS`. The 32-6 trail expects `agent_id` on every agent DCB event so
  per-tenant agent action trails are reconstructable.
- **Export** (AC11): emit at the audit/DSAR export endpoint(s) introduced by 37-1/37-x — paired
  `DATA.EXPORT.REQUESTED` (on accept) and `DATA.EXPORT.COMPLETED` (on finish) with counts only.

## Dependencies

- **Prerequisite**: Story 37-1 (sensitive-action **catalog** + `audit_records` projection — this
  story extends the catalog and asserts the projection lands events). `SensitiveActionCatalog.cs` is
  NEW from 37-1.
- **Prerequisite / parallel**: Epic 29 (secret cabinet — `ISecretAccessAuditor`, `SecretQueryService`,
  `SecretPurpose.ApiKey`) for the BYOK write path.
- **Prerequisite / parallel**: Epic 32 — 32-3 (credential resolution: decides byok vs
  platform-provided) and 32-6 (agent action trail: `agent_id` tagging).
- **Re-targeted from**: Epic 20 (billing/plan/usage state — the silent TS `packages/api` path is
  replaced by the C# `Plan` / `BudgetConfig` + admin plan endpoint surface).
- **Related**: Epic 16 / 18 (auth, login, refresh, lockout — `AuthEndpoints`, `ApiKeyAuthHandler`,
  `LoginLockoutService`); Epic 34 / 35 (billing/plan surface).
- **Reuses**: `IEventRepository` / `IPlatformEventPublisher` (DCB/PlatformEvent emission),
  `OrgEndpoints.EmitTenantEvent` pattern, `ISecretAccessAuditor`.

## Testing Strategy

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`). TDD — write the assertion first.

1. **Catalog mapping**: every reused event type (`SECRET.*`, `IMPERSONATION.*`, `TENANT.MEMBER_*`,
   `AUTH.REFRESH_REUSE_DETECTED`) and every new type (`BYOK.*`, `BILLING.*`, `SUBSCRIPTION.*`,
   `AUTH.LOGIN.*`, `AUTH.TOKEN.REFRESHED`, `AUTH.APIKEY.USED`, `AGENT.ACTION.*`, `CONFIG.PERSONA_CHANGED`,
   `DATA.EXPORT.*`) is present in `SensitiveActionCatalog`.
2. **No double-emission**: a membership role change emits exactly one `TENANT.MEMBER_ROLE_CHANGED.SUCCESS`;
   a secret write emits exactly one `SECRET.WRITE` + one `BYOK.PROVIDER_KEY.SET` (not two of either).
3. **Per emission site** (the AC15 coverage test): BYOK set/rotate/remove, BYOK-mode toggle, plan
   change, subscription create/update/cancel, login success, login failure (each reason), token
   refresh, API-key auth, agent dispatch + results, persona/config edit, data export — assert catalog
   code, tags (`actor`, `tenantId`/null, site-specific), and that the 37-1 projection writes the
   `audit_record` to the correct scope.
4. **Redaction**: feed a BYOK key-set with a real-looking `tamma_sk_...` value; assert the stored
   `audit_record` `Tags`/`Data` contain zero key bytes (only provider/mode/version metadata). Same
   for plan change (no card data) and login (no password).
5. **Scope routing**: tenant-scoped action → `domain_events` row with `TenantId` set; platform action
   → `platform_events` row with `TenantId` null. Cross-tenant leakage test: a tenant audit query
   never returns another tenant's sensitive-action rows.
6. **Throttle**: 100 API-key auths in one bucket → one (or sampled-N) `AUTH.APIKEY.USED`, not 100.
7. **Never-throws**: emitter swallows a sink/DB failure (logged) and does NOT break the login,
   secret write, or plan update.
8. **Mode matrix**: single-user vs SaaS drives whether a system-scope action is platform-only or in
   the sole user's feed.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Audit/SensitiveActionCatalog.cs` | Modify (NEW from 37-1; add reused + new entries) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/ISensitiveActionEmitter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/SensitiveActionEmitter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/SensitiveAction.cs` | Create (record + scope-routing types) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/AuditEmissionServiceCollectionExtensions.cs` | Create (DI wiring; map in `Program.cs`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretAccessAuditor.cs` (or a decorator) | Modify (derive `BYOK.PROVIDER_KEY.*` for ApiKey+Tenant writes) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` | Modify (login success/failure, token refresh) |
| `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs` | Modify (`AUTH.APIKEY.USED`, throttled) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` | Modify (`BILLING.PLAN_CHANGED`, subscription events) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Modify (`agentId` tag, catalog mapping) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (catalog mapping only — no new emission) |
| Persona/config write sites (prompt-store / convention-store admin endpoints) | Modify (`CONFIG.PERSONA_CHANGED`) |
| Audit/DSAR export endpoint(s) (37-1/37-x) | Modify (`DATA.EXPORT.*`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register emitter) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/SensitiveActionEmitterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/SensitiveActionCoverageTests.cs` | Create (AC15 per-site coverage) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/ByokEmissionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuthEmissionTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions
3. Read the Story 37-1 catalog + `audit_records` projection so you extend (not fork) the catalog
4. Confirmed which existing events already cover a site BEFORE adding a new emission (the
   reuse-vs-new decision is the crux of this story; see the verified-current-state table)
5. Planned a TDD approach (Red-Green-Refactor)

### The one rule that prevents most of the bugs here

**Decide reuse-vs-new per site, write that down, and test it.** The biggest failure mode is
double-emission (a second event for an action that already audits) or, worse, a `BYOK.*` event that
re-writes the secret to derive its payload. The curated catalog event is derived **from** the existing
emission, side by side, never by re-doing the action.

### Scope routing is load-bearing for tenant isolation

A sensitive-action event written to the wrong stream is a compliance leak: a tenant-scope BYOK event
in `platform_events` is invisible to the tenant's own audit view; a platform-owner action in a tenant
`domain_events` stream leaks operator activity to the tenant. The single `ISensitiveActionEmitter`
exists specifically so this decision is made in one tested place, not duplicated at 12 call sites.

### Never-throws contract

Mirror `ISecretAccessAuditor`: an audit-sink outage must NOT roll back the action that already
happened. The emitter logs and swallows; it never turns a successful login or secret write into a 500.

### Redaction is asserted, not assumed

The AC13 test must feed a real-looking secret value through a BYOK key-set and assert zero key bytes
land in the stored `audit_record`. The defensive strip in the emitter is belt-and-suspenders on top of
callers passing only metadata.

## Logging Requirements

- **INFO**: Sensitive-action event emitted (`type`, `tenantId`/`platform`, `actor`), API-key-usage
  heartbeat emitted (sampled).
- **DEBUG**: Per-site emission detail (tags assembled), throttle suppressed an `AUTH.APIKEY.USED`.
- **WARN**: Emitter dropped an event because `Type` is not in the catalog (typo guard); redaction
  guard stripped an unexpected secret-shaped value (indicates a caller bug — fix the caller).
- **ERROR**: Emitter sink/DB write failed (logged + swallowed; the action is NOT rolled back).
- **Structured context**: `{ eventType, tenantId, actorUserId, scope }` where applicable.
- **Credential safety**: NEVER log key material, plaintext secrets, card data, or full API keys
  (prefix only).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
