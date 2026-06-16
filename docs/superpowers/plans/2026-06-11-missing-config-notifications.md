# Missing-Config Notifications Epic

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan story-by-story. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every story writes tests
> before implementation.

**Goal:** Tamma notifies — instead of silently degrading or only logging — when system-level or
tenant-level configuration required by a feature is missing. Concretely: a taxonomy-valid
`(role, action)` with no prompt/convention system default, a tenant whose expected override is
absent, an unconfigured provider chain, or a mode-required platform setting (e.g.
`Tamma:TenantSharedSecret`, `Cranl:EncryptionKey`) that is unset. Each gap becomes (a) a DCB
event, (b) a deduplicated persisted record, (c) an alert through the existing Story 5.6 alert
pipeline, and (d) a visible surface on the admin/tenant dashboard.

**Seed note:** `~/.claude/projects/-home-meywd-tamma/memory/project_missing_config_notifications_epic.md`
— planned follow-up to Epic 27; resolution is strictly tenant → system → error (NEVER empty/plain
fallback), so a missing config row is a hard runtime error; the user wants proactive notification
of gaps rather than discovering them when a workflow throws.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine),
React/Vite dashboard in `packages/dashboard` (Vitest). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`).

---

## Non-goals (YAGNI guard)

- NO change to resolution semantics. `tenant → system → TammaError` stays exactly as-is
  (`feedback_resolution_no_empty_fallback`). Detection is a fire-and-forget side effect that must
  never swallow, delay, or alter the throw.
- NO new delivery channels. Email/Slack/PagerDuty/webhook delivery already exists in the alert
  pipeline (`Services/Alerts/Channels/`); once the new built-in rules are linked to a channel by an
  admin, delivery is free. No channel-linking UI in this epic (that is alert-pipeline Wave C.3
  scope).
- NO TypeScript-side (`packages/api`) detection. The prompt/convention stores, provider chains, and
  provisioning config all live in the C# app; the engine's Elsa activities resolve via HTTP
  callbacks to the central API, so server-side detection covers them with zero engine changes.
- NO per-user notification preferences / mute lists. Acknowledge on the gap record is the v1 mute.
- NO blocking startup validation. The scanner reports gaps; it does not fail the host (existing
  fail-fast paths like `AddTammaSecretReveal` stay as they are).

---

## Current-state findings (verified 2026-06-10, repo @ main 98cfb1c2)

### Where missing-config errors arise today

| Site | Behaviour today |
|---|---|
| `src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — `NoPromptError(...)` (~line 671), thrown from `ResolveForUser` (~173) and tenant resolve (~389) | Throws `TammaError("PROMPT.RESOLVE.NO_DEFAULT", ..., severity High)` with role/action/userId/tenantId context. **No event emitted, no alert** — only the exception. |
| `src/Tamma.Api/Services/Conventions/ConventionStore.cs` — `NoConventionError(...)` (~line 295), thrown from `ResolveAsync` (~138) | Throws `TammaError("CONVENTION_NOT_FOUND", ..., severity High)`. Same: exception only. |
| `src/Tamma.Api/Endpoints/PromptEndpoints.cs` (~181, ~420) and `ConventionStoreEndpoints.cs` (~86, 142, 229, 457) | Catch `TammaError` → translate to 404. The gap is invisible past the HTTP response. |
| `src/Tamma.Activities/LlmCall/ResolvePromptFromRegistryActivity.cs` (engine) | Calls central `POST /api/prompts/{role}/{action}/render`; on 404/error throws `TammaError("LLM.PROMPT.RESOLVE.NO_ROW" / "...REGISTRY_UNAVAILABLE")` and emits `LLM.PROMPT.RESOLVE.FAILED` — but only into the **transient** `tamma:events` workflow property + logs (see below). |
| `src/Tamma.Activities/Context/ResolveConventionsActivity.cs` (engine) | Same pattern via `POST /api/conventions/resolve`; `LLM.CONVENTIONS.RESOLVE.FAILED` transient event. |
| `src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` (~line 86) | "No provider chain configured for role/action" → returns an ErrorMessage result. Log-level surface only. |
| `src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs` (~111–140) | Missing `Cranl:EncryptionKey` in non-prod → HKDF-from-ApiKey **with only a logged warning**; prod requires it. |
| `src/Tamma.Api/Services/Provisioning/CranlProvisioningWorkflow.cs` (~355–380) | Missing `Tamma:TenantSharedSecret` → `LogError` and the engine env file is **silently written without** `TAMMA_SHARED_SECRET`. |
| `src/Tamma.Api/Program.cs` (~425–452) | Secret-store / reveal wiring is conditional on connection strings; misconfiguration either fails fast or silently skips registration. |

**Key gap:** activity `*.FAILED` events from `TammaEventEmitter.EmitFailure`
(`src/Tamma.Activities/Core/TammaActivity.cs` ~88–128) go to
`WorkflowExecutionContext.TransientProperties["tamma:events"]` + logs only — **nothing flushes
them to the DCB `DomainEvents` store**, so the alert rule evaluator can never see workflow-side
resolution failures. Central-API-side detection (this epic) closes that hole because every engine
resolution round-trips through the central endpoints.

### Existing notification infrastructure (Story 5.6, Waves C.1–C.2 — substantial, reuse it)

- **Write side:** `IAlertSink.RaiseAsync(AlertPayload)` →
  `src/Tamma.Api/Services/Alerts/PostgresAlertSink.cs`. Severities critical/warning/info; alert
  rows + per-channel `alert_delivery_attempts`; emits `ALERT.RAISED` DCB events.
  `TokenBucketAlertRateLimiter` per RuleId.
- **Rule engine:** `src/Tamma.Api/Services/Alerts/Rules/AlertRuleEvaluator.cs` — background poller
  over `ControlPlaneDbContext.DomainEvents` + `PlatformEvents`, matches `alert_rules` by
  `EventType` + JSON predicate (`always`, `count_gte`), in-process throttle keyed
  `(ruleId, tenantId)` honoring `ThrottleSeconds`. Built-ins seeded idempotently by
  `BuiltInAlertRuleSeeder` from `BuiltInAlertRules.All` (5 rules today: BUDGET.EXHAUSTED,
  AGENT.DISPATCH.FAILED, WORKFLOW.RETRY_EXCEEDED, PLATFORM.API.UNHEALTHY, SECRET.ROTATION.FAILED).
- **Delivery:** `NotificationDispatcher` background service + channels
  (`Channels/EmailAlertChannel.cs`, Slack, PagerDuty, Webhook) with retry/backoff. Email transports
  exist separately too (`Services/Email/ResendEmailService.cs`, `SmtpEmailService.cs`).
- **API:** `src/Tamma.Api/Endpoints/AlertEndpoints.cs` — `/api/v1/admin/alerts/*` +
  `/api/v1/admin/alert-channels/*` (OwnerAccess) and tenant-scoped
  `/api/v1/orgs/{tenantId}/alerts/*` (list/detail/ack/resolve). `AlertRuleEndpoints.cs` for rules.
- **Dashboard:** **no alert/notification UI exists yet** in `packages/dashboard` (admin tabs:
  users, tenants, api-keys, health, links, audit-log — `src/pages/admin/AdminLayout.tsx`). SSE
  exists only for admin tenant events (`Endpoints/Admin/AdminTenantEventsSseEndpoint.cs`).
- **Event store:** `IEventRepository.AppendAsync(DomainEvent)`
  (`src/Tamma.Data/Repositories/EventRepository.cs`); CP-resident today; per Story 28-1 audit the
  evaluator migrates to per-tenant fan-out later with no rule-engine changes.

### Mode + taxonomy seams

- `src/Tamma.Api/Services/PromptStore/TammaMode.cs` — process-wide `ITammaModeProvider`
  (SingleUser | SaaS; detection per CLAUDE.md "Operating Modes").
- Taxonomy: `src/Tamma.Core/Agents/RolePhaseMap.cs` (`EligibleActions` frozen role×action matrix),
  `src/Tamma.Core/Agents/AgentAction.cs`. Prompt system defaults in code:
  `src/Tamma.Api/Auth/SystemPrompts.cs` (`RoleActionTemplates`, `RoleSystemPrompts`). Convention
  system defaults DB-seeded from `src/Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs` via
  `ConventionStoreSeeder` (insert-missing-only).

---

## Architecture

**Detection → record → event → alert → surface**, reusing the alert pipeline end-to-end:

1. **`IMissingConfigRecorder`** (new, `src/Tamma.Api/Services/MissingConfig/`) — the single
   write-side seam. `RecordAsync(MissingConfigGap gap, CancellationToken)` is fire-and-forget-safe
   (never throws to callers; callers invoke it immediately before throwing their `TammaError`).
2. **`config_gaps` table** (CP DB) — the deduplicated registry. Unique on
   `(scope, tenant_id, user_id, domain, config_key)` (NULLS NOT DISTINCT, mirroring
   `prompt_overrides`). Columns: scope (`system|tenant|user`), domain
   (`prompt|convention|provider-chain|platform-config|secret`), config_key (e.g. `qa:write-tests`
   or `Tamma:TenantSharedSecret`), status (`open|acknowledged|resolved`), severity, message,
   first_seen, last_seen, hit_count, resolved_at. **Dedup rule:** insert-if-absent emits the event
   + raises the alert; an existing `open`/`acknowledged` row just bumps `last_seen`/`hit_count` —
   a hot workflow loop hitting the same gap 1000×/min produces ONE alert, not 1000.
3. **DCB events** (AGGREGATE.ACTION.STATUS): `CONFIG.MISSING.DETECTED` on first detection,
   `CONFIG.MISSING.RESOLVED` on resolution. Tags: `scope`, `domain`, `configKey`, `tenantId`,
   `mode`, `source` (`runtime|scanner`). Appended via `IEventRepository` (CP store — exactly what
   `AlertRuleEvaluator` polls).
4. **Built-in alert rules** `config-missing-tenant` (warning) and `config-missing-system`
   (critical) on `CONFIG.MISSING.DETECTED` with `ThrottleSeconds` belt-and-suspenders on top of
   registry dedup. Delivery channels come free once linked.
5. **Gap scanner** (`MissingConfigScanner` BackgroundService, startup + every 6h +
   on-demand) — *proactive* detection (find gaps before a workflow throws) and *reconciliation*
   (auto-resolve open gaps whose config now exists, e.g. after a PUT override or seeder reset).
   Checks: taxonomy completeness (every `RolePhaseMap.EligibleActions` cell has a
   `SystemPrompts.GetRoleAction` template AND an enabled convention system default), and
   mode-required platform config (SaaS: `Tamma:TenantSharedSecret`, `ConnectionStrings:ControlPlane`;
   Cranl enabled ⇒ `Cranl:EncryptionKey` in production; etc.).
6. **Surfaces:** admin endpoints `/api/v1/admin/config-gaps` (+ ack/resolve/rescan), tenant
   endpoints `/api/v1/orgs/{tenantId}/config-gaps`; dashboard admin tab + tenant settings banner.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a **system-scope** gap (missing seed, missing platform config)? | The sole user — it's their instance; gap shows in their (only) feed. | Platform owner ONLY (`OwnerAccess`). Never exposed to tenants — a missing seed or `Cranl:EncryptionKey` is an operator concern and would leak platform internals. |
| Who owns a **tenant/user-scope** gap (expected override absent, tenant provider chain unconfigured)? | The sole user (`user_id`-keyed, `tenant_id` NULL — same XOR as `prompt_overrides`). | The tenant: visible to `tenant_owner`/`tenant_admin` via `/api/v1/orgs/{tenantId}/config-gaps`; `member` users get read-only list, 403 on ack/resolve (mirrors prompt-store RBAC). |
| Who can acknowledge/resolve? | The user. | System-scope: platform owner. Tenant-scope: tenant_owner/tenant_admin (or platform owner). |
| Where do alerts fan out? | Platform feed (TenantId null) = the user's feed; `AlertPayload.TenantId` null. | Tenant-scope gaps raise with `TenantId` set → tenant-scoped channels + tenant alert feed; system-scope raise with `TenantId` null → admin feed only. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — already process-stable. | same |

---

## Story breakdown

### MCN-1: `config_gaps` registry + `IMissingConfigRecorder` + DCB events (core)

**Scope:** New entity/table, recorder service with dedup + event emission + alert raise. No
call-site wiring yet.

**Files:**
- New: `src/Tamma.Data/Entities/ConfigGap.cs`; DbSet + model config in
  `src/Tamma.Data/ControlPlaneDbContext.cs` / `TammaModelConfiguration.cs` (CHECK constraints on
  scope/domain/status; `UNIQUE NULLS NOT DISTINCT (scope, tenant_id, user_id, domain, config_key)`;
  principal XOR check: tenant_id/user_id never both set). Additive EF migration under
  `src/Tamma.Data/Migrations/ControlPlane/` (normal `dotnet ef migrations add` — new table, not a
  baseline CHECK edit; still run `has-pending-model-changes` → none).
- New: `src/Tamma.Api/Services/MissingConfig/IMissingConfigRecorder.cs`,
  `MissingConfigRecorder.cs`, `MissingConfigGap.cs` (record type + `ConfigGapScope`/`ConfigGapDomain`
  constants), `MissingConfigEventTypes.cs` (`CONFIG.MISSING.DETECTED`, `CONFIG.MISSING.RESOLVED`).
- New: `src/Tamma.Api/Extensions/MissingConfigServiceCollectionExtensions.cs`; wire in
  `src/Tamma.Api/Program.cs` (mirror `AlertServiceCollectionExtensions` pattern).

**Tests (first):** `tests/Tamma.Api.Tests/MissingConfig/MissingConfigRecorderTests.cs` —
first record inserts row + appends `CONFIG.MISSING.DETECTED` + calls `IAlertSink.RaiseAsync` once;
duplicate record bumps `hit_count`/`last_seen`, NO second event/alert; acknowledged row stays
deduped; resolved row re-detected → reopens + new DETECTED event; recorder never throws (DB down
→ logged, swallowed); severity mapping; XOR/CHECK violations rejected.

**Acceptance criteria:**
- [ ] Recording the same gap N times concurrently yields exactly 1 open row, 1 DETECTED event, 1 alert (race handled via unique-index catch + retry-as-update).
- [ ] `RecordAsync` never propagates an exception to the caller.
- [ ] `ResolveAsync(scopeKey)` flips status, stamps `resolved_at`, emits `CONFIG.MISSING.RESOLVED`.
- [ ] Full suite stays green; migration applies + rolls back cleanly.

### MCN-2: Wire runtime detection points (prompt, convention, provider chain)

**Scope:** Call `IMissingConfigRecorder.RecordAsync` at the existing fail-loud sites, immediately
before the throw/error-return. Resolution behaviour is byte-for-byte unchanged.

**Files:**
- Modify: `src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` — at both `NoPromptError`
  throw sites (user path ~173, tenant path ~389): record gap
  (domain `prompt`, config_key `"{role}:{action}"`, scope user/tenant per which id is set —
  but note: a missing **system default** for a taxonomy-valid pair is scope `system`; derive scope
  from whether the pair is taxonomy-valid, per the doc-comment on `NoPromptError` ~664–670).
- Modify: `src/Tamma.Api/Services/Conventions/ConventionStore.cs` — `ResolveAsync` throw site
  (~138), same scope derivation (domain `convention`).
- Modify: `src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` — "No provider chain
  configured" path (~86): domain `provider-chain`, config_key `"{role}:{action}"`, scope tenant
  (SaaS) / user (single-user).
- DI: these services gain an optional `IMissingConfigRecorder?` ctor param (null-tolerant so
  existing unit tests don't all need rework — but new tests assert it IS wired in Program.cs).

**Tests (first):** extend `tests/Tamma.Api.Tests/PromptStore/`, `tests/Tamma.Api.Tests/Conventions/`,
`tests/Tamma.Api.Tests/Providers/` — resolve-miss still throws the identical `TammaError`
(code/context/severity unchanged) AND records exactly one gap; resolve-hit records nothing;
recorder failure does not mask the `TammaError`. Endpoint-level test: engine-style
`POST /api/conventions/resolve` and `POST /api/prompts/{role}/{action}/render` 404s create gaps —
proving the engine activities (`ResolvePromptFromRegistryActivity`, `ResolveConventionsActivity`)
are covered server-side with no engine changes.

**Acceptance criteria:**
- [ ] Every `PROMPT.RESOLVE.NO_DEFAULT` / `CONVENTION_NOT_FOUND` / no-chain occurrence leaves a `config_gaps` row.
- [ ] Zero change to thrown error codes, messages, HTTP status mapping, or resolution order.
- [ ] Hot-loop test: 100 sequential misses on one pair → 1 open gap, 1 alert.

### MCN-3: Proactive gap scanner + reconciliation

**Scope:** `MissingConfigScanner` BackgroundService — startup scan, periodic (default 6h,
configurable `MissingConfig:ScanInterval`), and an injectable `ScanOnceAsync` for tests +
on-demand trigger (MCN-5). Two jobs: detect gaps proactively; auto-resolve open gaps whose config
now exists (covers PUT-override/seeder-reset healing without hooking every upsert path).

**Files:**
- New: `src/Tamma.Api/Services/MissingConfig/MissingConfigScanner.cs`,
  `MissingConfigScannerOptions.cs` (RunOnStartup gate mirroring `AlertRuleEvaluatorOptions` /
  `NotificationDispatcherOptions`), `IPlatformConfigRequirements.cs` +
  `PlatformConfigRequirements.cs` (pure, mode-aware required-key list:
  SaaS ⇒ `Tamma:TenantSharedSecret`, `ConnectionStrings:ControlPlane`; Cranl:ApiKey set +
  production ⇒ `Cranl:EncryptionKey`; extensible table — keep it data, not branches).
- Checks (pure helpers, DB-free where possible, mirroring `ConventionSeedSpecs` style):
  taxonomy × `SystemPrompts.RoleActionTemplates` (scope system, domain prompt); taxonomy ×
  enabled convention system defaults via `IConventionRepository` (scope system, domain
  convention); platform config (scope system, domain platform-config).
- Wire in `Program.cs` via the MCN-1 extension.

**Tests (first):** `tests/Tamma.Api.Tests/MissingConfig/MissingConfigScannerTests.cs` — seeded-
complete world → zero gaps; remove one convention system default → scanner opens exactly one
system gap; restore it → next scan resolves it + emits `CONFIG.MISSING.RESOLVED`; mode matrix
(SingleUser vs SaaS) drives which platform keys are required; scanner crash-isolated per tick
(one failing check doesn't kill the loop); `RunOnStartup=false` gates the loop for unrelated tests.

**Acceptance criteria:**
- [ ] Fresh deploy with intact seeds + complete config scans clean.
- [ ] A gap fixed via `PUT /api/prompts/...` or seeder reset is auto-resolved within one scan (or immediately via MCN-5 rescan).
- [ ] Scanner honours `ITammaModeProvider` — no SaaS-only keys flagged in single-user mode.

### MCN-4: Built-in alert rules for config gaps

**Scope:** Two new specs in `BuiltInAlertRules.All`: `config-missing-system` (severity critical,
EventType `CONFIG.MISSING.DETECTED`, predicate `{"op":"always"}` — registry dedup already gates
volume; ThrottleSeconds 300) and `config-missing-tenant` (severity warning, ThrottleSeconds 300).
Distinguish via predicate on the `scope` tag — extend `AlertRulePredicate` with a `tag_equals` op
ONLY if it doesn't already support tag matching (verify `AlertRulePredicate.cs` first; if
predicates can't read tags, ship one rule and let `AlertPayload.TenantId` null/non-null do the
feed split — the simpler option wins).

**Files:** modify `src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` (+ possibly
`AlertRulePredicate.cs`); seeder picks them up automatically (`BuiltInAlertRuleSeeder` is
idempotent insert-by-`built_in_key`).

**Tests (first):** extend `tests/Tamma.Api.Tests/Alerts/` — seeder creates the new rules;
evaluator fires on an appended `CONFIG.MISSING.DETECTED` event; tenant-scoped event → alert with
TenantId (tenant feed); system event → platform alert; throttle suppresses a burst.

**Acceptance criteria:**
- [ ] `CONFIG.MISSING.DETECTED` events produce alerts visible at `/api/v1/admin/alerts` (and `/api/v1/orgs/{id}/alerts` for tenant scope) with no manual rule setup.
- [ ] Built-ins ship with empty ChannelIds (no auto-spam) per existing convention.

### MCN-5: Config-gaps API endpoints

**Scope:** Read + lifecycle endpoints over the registry, per-mode RBAC.

```
GET   /api/v1/admin/config-gaps                 (OwnerAccess; filters: scope, domain, status, tenantId)
POST  /api/v1/admin/config-gaps/{id}/acknowledge
POST  /api/v1/admin/config-gaps/{id}/resolve     (manual override; scanner may reopen if still missing)
POST  /api/v1/admin/config-gaps/rescan           (202; triggers ScanOnceAsync via the platform task queue pattern)
GET   /api/v1/orgs/{tenantId}/config-gaps        (tenant members; tenant-scope rows ONLY — never system rows)
POST  /api/v1/orgs/{tenantId}/config-gaps/{id}/acknowledge  (tenant_owner/tenant_admin; member → 403)
```

**Files:** new `src/Tamma.Api/Endpoints/ConfigGapEndpoints.cs` (mirror `AlertEndpoints.cs`
structure: admin section + tenant section, paging defaults 50/500); map in `Program.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/MissingConfig/ConfigGapEndpointsTests.cs` — RBAC matrix
(owner / tenant_owner / tenant_admin / member / cross-tenant 404), tenant list never leaks
system-scope or other-tenant rows, ack emits no DETECTED/RESOLVED event (status-only), rescan
returns 202 and a subsequent GET reflects scan results, single-user mode: sole user sees
system + user rows in one list.

**Acceptance criteria:**
- [ ] Endpoint shape identical between modes; auth middleware decides scope (prompt-store API precedent).
- [ ] Acknowledged gaps stay deduped (no new alerts) but remain listed until resolved.

### MCN-6: Dashboard surfaces

**Scope:** Minimal visible surface — admin tab + tenant banner. No channel management UI.

**Files:**
- New: `packages/dashboard/src/services/admin/config-gaps-client.ts` (mirror
  `admin-api-client.ts` conventions).
- New: `packages/dashboard/src/pages/admin/ConfigGapsTab.tsx`; register in
  `packages/dashboard/src/pages/admin/AdminLayout.tsx` (`AdminTab` union + `TABS`). Columns:
  scope, domain, config key, severity, first/last seen, hit count, status; ack/resolve actions;
  open-count badge on the tab label.
- New: `packages/dashboard/src/components/config-gaps/ConfigGapsBanner.tsx` — tenant-facing
  banner rendered on `packages/dashboard/src/pages/settings/PromptsPage.tsx` and
  `ConventionsPage.tsx` when `GET /api/v1/orgs/{tenantId}/config-gaps?status=open` is non-empty,
  deep-linking to the affected (role, action) editor.

**Tests (first):** colocated Vitest + Testing Library (existing pattern, e.g.
`components/secrets/__tests__/`) — tab renders rows/states, ack button calls client + optimistic
update, banner hidden when zero gaps, member-role sees banner without ack button.

**Acceptance criteria:**
- [ ] Platform owner sees all gaps in admin panel; tenant admin sees only their tenant's gaps in settings.
- [ ] `pnpm test --filter @tamma/dashboard` green; no new lint errors.

### MCN-7 (stretch, explicitly deferrable): provisioning + secret-protector detection

**Scope:** Record gaps at the two silent-degrade provisioning sites:
`CranlProvisioningWorkflow` writing an engine env file without `TAMMA_SHARED_SECRET` (~364), and
`TenantSecretProtector` dev-only HKDF fallback (~125–140) when running outside Development.
Domain `secret`/`platform-config`, scope system. (MCN-3's static checks catch most of this at
scan time; this story adds the in-flight detection.) Defer if the wave runs long — the scanner
coverage is the 80%.

**Tests:** extend `tests/Tamma.Api.Tests/Provisioning/` — env-file build without secret records a
gap; protector fallback outside Development records a gap; behaviour otherwise unchanged.

---

## Story order & dependencies

MCN-1 → MCN-2 → MCN-3 → MCN-4 (parallel-safe with 3) → MCN-5 → MCN-6 → MCN-7 (optional).
MCN-1 is the only hard prerequisite for everything else; MCN-4/5 only need MCN-1.

## Risks

- **Alert noise / spam:** primary mitigation is registry dedup (one open row = one alert),
  secondary is rule ThrottleSeconds, tertiary is sink rate limiter. Watch the reopen path: a
  flapping gap (resolved by scanner, re-detected at runtime) re-alerts by design — if flapping is
  observed, add a reopen-cooldown column (cheap follow-up).
- **Recorder on the hot resolve path:** `RecordAsync` does a DB write on every miss. Misses are
  exceptional (fail-loud bugs), so volume should be near-zero — but the never-throw +
  short-timeout contract in MCN-1 is load-bearing; a broken CP DB must not turn a 404 into a hang.
- **Event-store topology shift (Story 28-1 / Epic 30):** `CONFIG.MISSING.*` events append to the
  CP `DomainEvents` table the evaluator polls today. When events move per-tenant, system-scope
  events must stay CP-resident — keep the recorder writing system-scope events via the CP
  `IEventRepository` explicitly so the migration only touches tenant-scope routing.
- **Scope derivation subtlety (MCN-2):** taxonomy-valid pair missing a system default = `system`
  scope (seed bug, platform owner's problem); taxonomy-valid pair where only the expected tenant
  override is absent cannot happen today (system default backstops it) — so runtime tenant-scope
  prompt/convention gaps only arise for provider chains. Get the derivation wrong and tenants get
  paged for platform bugs. The MCN-2 tests must pin this matrix.
- **Migration discipline:** Phase-0 collapsed-baseline rules apply to CHECK *edits* on existing
  tables; `config_gaps` is additive, but still verify `has-pending-model-changes` reports none
  after the migration, and mirror entity config in `TammaModelConfiguration.cs` only (the
  established single source).
- **Predicate capability unknown (MCN-4):** whether `AlertRulePredicate` can match event tags is
  unverified — story starts by reading `AlertRulePredicate.cs` and takes the simpler of the two
  designs described there.
