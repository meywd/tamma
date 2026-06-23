# Completeness Audit — ProvisionTenantV2Workflow (V2)

**Date:** 2026-06-22
**Target:** `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisionTenantV2Workflow.cs`
**Type:** Plain orchestrator class (state-machine walker / compensating saga), **not** an Elsa
`WorkflowBase`. It is named "Workflow" and drives the per-tenant, backend-pluggable infrastructure
provisioning flow on the platform task queue. (The header XML doc explains why it is a plain class and
not an Elsa workflow: the 30-1 ADR §5 locked the V2 namespace inside `Tamma.Api`, which
`Tamma.Activities` does not reference, so an Elsa activity calling V2 types would force a cross-project
refactor outside Story 30-2's scope. The pre-existing `CranlProvisioningWorkflow` uses the same
pattern.)

---

## Purpose & Owner

**Purpose:** Walk a single tenant through backend-agnostic infrastructure provisioning as a resumable
8-step compensating saga: ResolveProvider → Preflight → ReserveResources → ExecuteProvision →
PersistEndpoints → RegisterSecrets → InitialProbe → Activate. It resolves the correct backend from the
request's `ProviderKey` via `TenantProviderRegistry`, dispatches to that backend's
`ITenantInfrastructureProvider.ProvisionAsync`, polls `GetStatusAsync` until Ready or a timeout budget,
and on any failure past `ReserveResources` runs reverse-order compensations (provider `DeprovisionAsync`,
state rollback) to avoid orphaning cloud resources. Every step boundary and terminal outcome emits a DCB
platform event via `IPlatformEventPublisher`.

**Owner:** Epic 30 (Pluggable Tenant Infrastructure Provisioning), **Story 30-2** ("Provisioning
workflow in Elsa — resumable, per-backend dispatch"). It is the formal successor to the V1
`CranlProvisioningWorkflow` (Epic 28). The submit/execute split is owned jointly with
`ProvisionTenantV2Dispatcher` (intake/202-accept) and `ProvisionTenantV2TaskHandler` (platform-queue
entry).

**Production status:** **DI-registered but ZERO production callers.** `ProvisioningServiceCollection
Extensions.cs:129-130` registers `ProvisionTenantV2Workflow` + `ProvisionTenantV2Dispatcher`, but the
admin endpoints (`AdminEndpoints.cs` ProvisionTenant / GetTenantProvisioning / DeprovisionTenant) still
inject the V1 `ITenantProvisioner` (`CranlTenantProvisioner`). The cutover is the unstarted Phase A of
`docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md`. (This matches the sibling
CranlProvisioning audit's finding and the project memory note "V2 provisioning surface already shipped …
but has ZERO production callers".)

---

## Maturity: **partial**

This is the strongest end of "partial" — it is emphatically **not** the user's "thin happy-path
skeleton" complaint. It is a real saga: 8 named steps, a per-step DCB event vocabulary
(`TENANT.PROVISION.<STEP>.STEP_STARTED/COMPLETED/FAILED`), a reverse-order compensation catalog,
structured kebab-case failure short-codes, a bounded poll loop with cancellation, idempotent
resumability, an `IllegalTenantState` guard, a graceful worker-shutdown path (don't compensate on
`OperationCanceledException` — let the reaper resume), and a terminal `TENANT.PROVISIONED.SUCCESS` /
`TENANT.PROVISION.FAILED`. Against the spec it satisfies AC1, AC2 (structure), AC3 (resumability), AC4
(reverse compensation + `CompensationFailed` halt), AC5 (per-step events), and AC9 (provider returns a
structured Failed snapshot, not an exception).

It is **not complete** for four decisive, code-evident reasons, all of which the workflow's own inline
comments admit as deferred:

1. **Two of the eight steps are no-ops / non-persisting.** `RegisterSecrets` (step 6) is an explicit
   `deferred_to_30_3` no-op — it emits STARTED/COMPLETED events but does **nothing**: no
   `ISecretStore.CreateAsync`, no `RetireVersionAsync` compensation (the compensation entry is
   `_ => Task.CompletedTask`). `PersistEndpoints` (step 5) captures `ProviderResourceIds` + `Endpoints`
   **in memory only** — the inline note says the `provider_resource_ids` JSONB + `provider_key` columns
   "land in 30-3," so a successful provision **persists no resource ids or endpoints to the tenant row**.
   The `Tenant` entity confirms this: it has only `ProvisioningState` / `ProvisioningDetail` /
   `ProvisioningUpdatedAt` — no `ProviderResourceIds` / `ProviderKey` POCO columns. AC2's
   PersistEndpoints requirement ("writes `provider_resource_ids` JSONB + updates `tenants.provider_key`")
   is therefore unmet.

2. **Quota enforcement (AC6) is a documented no-op.** Preflight reads `caps.MaxTenantsPerOrg` and
   `payload.InvokingOrgId` into `_ = …` discards with a comment "Quota check — skipped today because
   the per-org tenant count helper requires the `tenants.provider_key` column that lands in 30-3." So a
   provider that declares `MaxTenantsPerOrg` is **not** actually capped. The `OrgQuotaExceeded` failure
   code exists but is never emitted.

3. **Per-provider per-step timeout (AC7) is unimplemented.** AC7 requires
   `ProviderCapabilities.TimeoutSeconds` (Cranl 5 min, Hetzner 10 min, Cloudflare 60 s, BYO 30 s). That
   field does **not** exist on `ProviderCapabilities`; the workflow hardcodes a single
   `DefaultProbeTimeout = 30 min` for every backend and the class doc flags it as a "30-2 follow-up."
   ExecuteProvision itself has **no timeout** at all (it awaits the provider call unbounded except for
   the ambient `ct`).

4. **Unified-tenancy reconciliation is bypassed (the real open architecture).** The Cranl V2 provider
   (`CranlTenantProviderV2.ResolveEndpointsAsync` / `TryBuildEndpoints`) returns the **raw decrypted
   Cranl `DATABASE_URL`** as the tenant's `DatabaseUrl`. Per the project's unified schema-per-tenant
   model (CLAUDE.md §"Multi-tenant provisioning", unified-tenancy plan decision 3), every tenant must
   route through its `tenant_databases` pool row + an AES-GCM `EncryptedConnectionString`
   (`Search Path=t_<hex>` + per-tenant role), **not** the raw vendor URL. The workflow never registers
   the minted hosting DB into the pool nor runs the `t_<hex>` schema/role/migrate step engine
   (`TenantProvisioningService`). The two provisioning planes are unreconciled.

Because the core saga walks end-to-end, is resumable, and is fully event-sourced, this is **partial**,
not **thin**. The gap to "complete" is: real RegisterSecrets, real endpoint/resource-id persistence,
quota enforcement, per-provider timeouts, unified-pool reconciliation, and — above all — **being wired
to a production caller** (today it is dead code behind the V1 path).

---

## Current Capabilities

- **8-step compensating saga** with a typed step vocabulary and an explicit compensation catalog built
  forward as resources are reserved/minted; reverse-order execution on failure past `ReserveResources`.
- **Backend-agnostic dispatch** via `TenantProviderRegistry.TryGetProvider(providerKey)` →
  `ITenantInfrastructureProvider`; refuses the null seam (`NoProvisioningInThisMode`) and unknown keys
  (`ProviderNotRegistered`) before any vendor call.
- **Preflight gating (partial)** — topology check (`caps.SupportsTopology`) → `UnsupportedTopology`;
  region check against `caps.Regions` → `UnsupportedRegion`. (Quota check is a no-op — see gaps.)
- **`IllegalTenantState` guard** — refuses to run against `Ready` / `Deprovisioning` / `Deprovisioned`
  rows; `TenantNotFound` synthetic failure when the row is missing.
- **ExecuteProvision** — calls `provider.ProvisionAsync`; distinguishes a provider that *throws*
  (`ProviderUnexpectedException`, contract violation) from one that *returns* a structured Failed
  snapshot (surfaced verbatim, `FailureReason`/`Detail` preserved) — both run compensation
  (`DeprovisionAsync`, best-effort, idempotent).
- **InitialProbe** — bounded poll loop (`ProbeUntilReadyAsync`) at `DefaultProbeInterval` (5 s) up to
  the budget; short-circuits on provider-reported `Failed`; cancellable `Task.Delay` so worker shutdown
  unblocks fast; `ProbeTimeout` on budget exhaustion → compensation.
- **Activate** — flips the row to `Ready` and emits terminal `TENANT.PROVISIONED.SUCCESS`
  (`workflowVersion: 2.0.0`, tags: tenantId/providerKey/topology, data: resourceIdCount).
- **Full DCB emission** — every step boundary emits `TENANT.PROVISION.<STEP>.STEP_STARTED/COMPLETED/
  FAILED`; failures stamp the row `Failed` and emit `TENANT.PROVISION.FAILED`; compensations emit
  `…_compensated.STEP_COMPLETED/FAILED`. Tags include `tenantId`, `step`, `workflow:v2`.
- **Resumability** — `ExecuteAsync` inspects the persisted `provisioning_state` on every run; a
  re-fired task re-asks idempotent providers (ADR §4) rather than double-creating; worker-shutdown
  (`OperationCanceledException` + `ct.IsCancellationRequested`) deliberately does NOT compensate so the
  visibility-timeout reaper resumes.
- **Compensation-failure halt** — `CompensationFailed` is emitted and the saga halts (no auto-retry of
  compensation per AC4); operator-visible, "orphans may exist" is explicit — never a silent/false
  success.
- **Mode-aware intake** in the dispatcher (`ProvisionTenantV2Dispatcher`) — single-user/null-seam
  short-circuit, `202`-style synchronous snapshot, enqueue onto the **platform** queue (tenant DB
  doesn't exist yet), and a `ProvisionTenantV2TaskHandler` that dead-letters malformed payloads via
  `PlatformTaskTerminalException` but re-enqueues on unexpected workflow exceptions.

---

## Intended Full Scope (with citations)

The "complete" bar is set by the Story 30-2 spec, the Epic 30 README, the project audit-trail contract,
and the unified-tenancy model — all in-repo:

1. **Story 30-2 acceptance criteria** (`docs/stories/epic-30/30-2-provisioning-workflow-dispatch.md`):
   - **AC2** — the full 8-step saga, each step with a compensation, where **`PersistEndpointsActivity`
     writes `provider_resource_ids` JSONB + updates `tenants.provider_key` + stores endpoints in the
     routing cache (30-8)**, and **`RegisterSecretsActivity` creates the initial cabinet rows via
     `ISecretStore.CreateAsync` (Epic 29) with `RetireVersionAsync` compensation**.
   - **AC5** — events at each step PLUS **every step records duration** so 30-10's cost dashboard can
     attribute time (current events carry no duration).
   - **AC6** — **`PreflightActivity` enforces per-provider/per-org quotas** via
     `TenantRepository.CountByOrgAndProvider(orgId, providerKey)` and `caps.MaxTenantsPerOrg`,
     fail-fast before reserving resources.
   - **AC7** — **per-provider per-step timeouts** surfaced via `ProviderCapabilities.TimeoutSeconds`
     (Cranl 5 min, Hetzner 10 min, Cloudflare 60 s, BYO 30 s).
   - **AC8/AC9** — unit + integration tests: success path; provisioner throws → compensation; probe
     timeout → compensation; a fake `"test"` provider end-to-end verifying all events in order and the
     row reaching `Ready`.
   - **AC1 / migration** — replaces (or wraps) Epic 28's `CreateTenantWorkflow`; the cutover is
     feature-flagged (`Tenants:AsyncProvisioning:UseV2`) then V1 is deleted. (Implies a **production
     caller** is the terminal state of the story.)

2. **Project audit-trail contract** — CLAUDE.md §"Event Sourcing (DCB Pattern)" / §"Emitting Events for
   Audit Trail": every operation appends an immutable platform event with typed `tags` + `metadata`.
   The workflow satisfies the *presence* of events; AC5's **per-step duration** is the remaining gap.

3. **Epic 30 README** (`docs/stories/epic-30/README.md`) — the workflow is the saga that closes the
   "Provisioning half-failure leaves orphan cloud resources" risk (compensation per step) and is the
   dispatch core that the four backends (30-3 Cranl, 30-4 Hetzner, 30-5 Cloudflare, 30-6 BYO), routing
   (30-8), deprovisioning (30-9), and cost/quota (30-10) all hang off.

4. **Unified schema-per-tenant model** — CLAUDE.md §"Multi-tenant provisioning (Cranl)" + the unified
   tenancy plan: a backend that mints a hosting DB must **register it into the `tenant_databases` pool**
   and the tenant must route via the unified `EncryptedConnectionString` (`Search Path=t_<hex>` +
   per-tenant role), NOT the raw vendor `DATABASE_URL`. The provisioning saga is where this
   reconciliation must happen (a new pool-register + schema-bootstrap step, or a call into
   `TenantProvisioningService`).

Net: a complete version is this saga with steps 5/6 actually persisting + creating secrets, quota +
per-provider timeouts enforced, the minted DB reconciled into the unified pool, per-step durations
emitted, the AC8/AC9 test matrix green, and the admin endpoints repointed at it (V1 retired). The cross-
cutting LLM-mediation pivot (32-5 / Epic 38) does **not** apply here — this is infra provisioning, it
calls no AI/agent/git provider, so the "route via tamma-api call-LLM" rule is out of scope for this
workflow.

---

## Missing Capabilities

| # | Capability | Priority | Depends on |
|---|---|---|---|
| 1 | **PersistEndpoints actually persists.** Step 5 captures `ProviderResourceIds` + `Endpoints` in memory only; nothing is written to the tenant row. Add `tenants.provider_key` + `tenants.provider_resource_ids` (JSONB) columns to the `Tenant` POCO/EF model and write them; store endpoints into the 30-8 routing cache. Without this a successful provision leaves no durable record of what was minted. | P0 | Story 30-3 (column migration) / 30-8 (routing cache) |
| 2 | **RegisterSecrets is wired (currently a `deferred_to_30_3` no-op).** Call `ISecretStore.CreateAsync` (Epic 29) for the tenant DB connection + any provider-surfaced API keys; compensation = `RetireVersionAsync` per registered secret. Today step 6 does nothing and its compensation is `Task.CompletedTask`. | P0 | Epic 29 (`ISecretStore`) / Story 30-3 |
| 3 | **Unified-tenancy reconciliation.** Register the provider-minted hosting DB into the `tenant_databases` pool and route the tenant via the unified `EncryptedConnectionString` (`Search Path=t_<hex>` + per-tenant role) + run the `t_<hex>` schema/role/migrate step engine (`TenantProvisioningService`), instead of `CranlTenantProviderV2` returning the raw Cranl `DATABASE_URL`. The real open architecture of Epic 30. | P0 | Unified-tenancy plan decision 3 / `TenantPlacementService` / `TenantProvisioningService` |
| 4 | **Wire to a production caller (cutover).** Repoint `AdminEndpoints` ProvisionTenant/GetTenantProvisioning/DeprovisionTenant at `ProvisionTenantV2Dispatcher`; gate behind `Tenants:AsyncProvisioning:UseV2`; then retire V1. Today the entire V2 saga is dead code behind the V1 path. | P0 | Epic 30 Phase-A cutover (plan `2026-06-11-epic-30-pluggable-provisioning.md`) |
| 5 | **Per-org quota enforcement (AC6).** Replace the `_ = caps.MaxTenantsPerOrg; _ = payload.InvokingOrgId;` no-op with `TenantRepository.CountByOrgAndProvider(orgId, providerKey)` ≥ cap → emit `OrgQuotaExceeded` and fail-fast at Preflight before ReserveResources. | P1 | Story 30-3 (needs `provider_key` column) |
| 6 | **Per-provider per-step timeouts (AC7).** Add `ProviderCapabilities.TimeoutSeconds` (per-step dictionary) and bound BOTH `ExecuteProvision` (currently unbounded) and `InitialProbe` per provider (Cranl 5 min, Hetzner 10 min, Cloudflare 60 s, BYO 30 s) instead of one hardcoded 30-min probe budget. | P1 | Story 30-1 contract extension |
| 7 | **Per-step duration in events (AC5).** Stamp wall-clock `durationMs` (and a run-total) into each `STEP_COMPLETED`/`STEP_FAILED` event's `data` so 30-10's cost dashboard can attribute time. Today events carry only status/counts. | P1 | Story 30-10 (consumer) |
| 8 | **`TENANT.PROVISIONING_REQUESTED` anchor at intake.** The dispatcher flips the row to `Pending` and enqueues but emits no "request received" event — the audit stream has no intake anchor before the first step event. | P1 | Story 28-5 catalogue |
| 9 | **AC8/AC9 test matrix.** Verify in-repo coverage of: success path; provider throws → compensation; probe timeout → compensation; persistence rollback; and a fake `"test"`-keyed provider end-to-end asserting all events emit in order and the row reaches `Ready`. (Confirm/author — not located in this audit.) | P1 | none |
| 10 | **ExecuteProvision retry/backoff for transient provider errors.** A thrown provider exception currently goes straight to `ProviderUnexpectedException` + compensation; transient cloud-API 5xx/429s should get bounded retry-with-backoff before tearing everything down (CLAUDE.md §Retry Pattern). | P2 | none |
| 11 | **Coarse step-state persistence for finer resume.** Resume granularity is coarse (the column carries the v1 vocabulary); a worker that died between step 4 and 5 re-asks the provider but the saga doesn't record "I already executed step 4." Persist a per-step cursor so resume skips proven-complete steps deterministically. | P2 | Story 30-3 (state-column extension) |
| 12 | **Vendor failure-detail surfacing.** When a provider returns Failed, the saga surfaces `FailureReason`/`Detail` verbatim — good — but there is no enrichment hook to pull vendor deploy logs into the operator-visible detail (parity with the V1 audit's item 7). | P2 | none |
| 13 | **Operator notification on terminal `TENANT.PROVISION.FAILED`.** Failure is only discoverable via a `GET /provisioning` poll / the event stream; raise an alert through the Story 5.6 notification pipeline. | P3 | Story 5.6 alert pipeline |

---

## Ordered Build-out Spec

Goal: take the existing saga from "fully-shaped but partly-deferred and uncalled" to "production-wired,
durably persisted, unified-tenancy-reconciled, fully event-sourced." Order is dependency-driven.

### Phase 1 — Make a success durable (P0: items 1, 5, 11)

1. **Add the v2 persistence columns.** Migration + `Tenant` POCO: `ProviderKey TEXT NULL`,
   `ProviderResourceIds JSONB NULL` (and keep `FailureReason` shadow column). Backfill existing
   Cranl-backed rows (`UPDATE … SET provider_key = 'cranl'`). (Story 30-3 migration.)
2. **Persist in PersistEndpoints (step 5).** In the workflow's `persist_endpoints` block, after
   capturing `executeResult.ProviderResourceIds` / `Endpoints`, write them onto the tenant row
   (`tenant.ProviderKey = payload.ProviderKey`, `tenant.ProviderResourceIds = serialize(resourceIds)`)
   in a `TransitionAsync`-style save. Compensation: **clear those columns** (not just the in-memory
   captures). Emit `TENANT.PROVISION.PERSIST_ENDPOINTS.STEP_COMPLETED` with `resourceIdCount` +
   `hasEndpoints` (already present) PLUS a `persisted:true` flag.
3. **Enforce quota in Preflight (item 5).** Replace the `_ = caps.MaxTenantsPerOrg` discard with:
   `if (caps.MaxTenantsPerOrg is int cap && payload.InvokingOrgId is Guid org && await
   _tenantRepository.CountByOrgAndProviderAsync(org, payload.ProviderKey, ct) >= cap)` → emit
   `preflight.STEP_FAILED` (`failureReason = OrgQuotaExceeded`) and `StampFailureAsync(...,
   OrgQuotaExceeded, ...)` BEFORE ReserveResources. Inject `ITenantRepository`/the count helper.
4. **Persist a per-step cursor (item 11).** Extend the provisioning-state vocabulary (or add a
   `ProvisioningStepCursor` column) and write it at each `STEP_COMPLETED`; on resume, skip steps whose
   cursor is already past them (ResolveProvider/Preflight stay pure-replayable).

### Phase 2 — Close the two no-op steps (P0: items 2, 3)

5. **Wire RegisterSecrets (step 6).** Define a provider-surfaced secret set on the
   `ProvisioningResult`/provider (e.g. `executeResult.Secrets`). In the `register_secrets` block, for
   each: `await _secretStore.CreateAsync(tenantId, name, value, ct)`; record created versions; add a
   real compensation entry that calls `_secretStore.RetireVersionAsync(...)` per created version
   (replace the `_ => Task.CompletedTask` no-op). Emit `register_secrets.STEP_COMPLETED` with a
   `secretCount` (drop the `deferred_to_30_3` marker). tenant→system→error resolution applies to any
   secret value that itself resolves from config — never write an empty/plain secret; fail the step
   with a typed reason if a required secret is unresolved.
6. **Add a UnifiedPoolRegister step between ExecuteProvision and PersistEndpoints (item 3).** After the
   provider reports the hosting DB exists, (a) register the DB into `tenant_databases` via
   `TenantPlacementService`/`ITenantDatabasePool`, (b) run the unified `TenantProvisioningService` step
   engine to create the `t_<hex>` schema + per-tenant role + migrate, (c) persist the unified
   `EncryptedConnectionString` (`Search Path=t_<hex>`) as the tenant's routing connection. Change
   `CranlTenantProviderV2.ResolveEndpointsAsync` to return only the **engine URL** for dedicated-compute
   topologies — DB routing stays on the unified path, not the raw Cranl URL. Emit
   `TENANT.PROVISION.REGISTER_POOL.STEP_STARTED/COMPLETED/FAILED`; compensation: deregister the pool row
   + drop the `t_<hex>` schema/role.

### Phase 3 — Bound and time the saga (P1: items 6, 7, 10)

7. **Add `ProviderCapabilities.TimeoutSeconds` (item 6).** A per-step `IReadOnlyDictionary<string,int>`
   (keys: `execute_provision`, `initial_probe`). In the workflow, derive `ProbeTimeout` and an
   ExecuteProvision deadline from `caps.TimeoutSeconds` (fall back to the existing defaults when a
   provider doesn't declare one). Wrap the `provider.ProvisionAsync` await in a linked
   `CancellationTokenSource` bounded by the ExecuteProvision timeout → on timeout emit
   `execute_provision.STEP_FAILED` (`failureReason = ProbeTimeout`/a new `ExecuteTimeout` code) and run
   compensation.
8. **Stamp per-step duration (item 7).** Wrap each step in a `Stopwatch`/`_clock` delta and add
   `durationMs` (+ a running `elapsedMs`) to the `STEP_COMPLETED`/`STEP_FAILED` event `data` so 30-10
   can attribute time.
9. **Retry transient ExecuteProvision errors (item 10).** Classify the caught provider exception
   (transient cloud-API 5xx/429 vs terminal); apply bounded `retryWithBackoff` (CLAUDE.md pattern)
   before falling through to `ProviderUnexpectedException` + compensation.

### Phase 4 — Anchor the audit trail + cut over (P0/P1: items 4, 8, 13, 9)

10. **Emit `TENANT.PROVISIONING_REQUESTED` at dispatcher intake (item 8).** In
    `ProvisionTenantV2Dispatcher.DispatchAsync`, on the accept branch (after flip to `Pending`,
    before/with enqueue), append `TENANT.PROVISIONING_REQUESTED` (tags: tenantId, providerKey, topology,
    region) so the audit stream has a request anchor.
11. **Repoint admin endpoints at V2 (item 4 — the cutover).** Inject `ProvisionTenantV2Dispatcher` into
    `AdminEndpoints` ProvisionTenant; map `region`/`customName` onto `ProvisionTenantV2TaskPayload`;
    resolve `ProviderKey` from Cranl config (`cranl`) vs the null seam. `GetTenantProvisioning` reads the
    same `provisioning_state`/`ProvisioningStatusSnapshot`. `DeprovisionTenant` enqueues the V2
    deprovision saga (Story 30-9). Gate behind `Tenants:AsyncProvisioning:UseV2`; validate on staging;
    then delete the V1 surface (`CranlProvisioningWorkflow`, `CranlTenantProvisioner`,
    `TenantProvisioningTaskHandler`) and the `#pragma warning disable CS0618` registrations.
12. **Operator alert on terminal failure (item 13).** In/after `StampFailureAsync`, raise a notification
    via the Story 5.6 pipeline on `TENANT.PROVISION.FAILED` and especially `CompensationFailed`
    (orphans-possible → operator must intervene).
13. **Author/confirm the AC8/AC9 test matrix (item 9).** A fake `"test"`-keyed
    `ITenantInfrastructureProvider` driving: success → all 8 step events in order + row `Ready`; provider
    throws → reverse compensation runs + `ProviderUnexpectedException`; probe timeout → compensation +
    `ProbeTimeout`; persistence rollback survives; compensation-failure halt emits `CompensationFailed`.

**Cross-cutting rules honored:** no AI/agent/git external-provider calls live in this workflow (it is
infra provisioning — the 32-5 / Epic 38 LLM-mediation rule is out of scope here); tenant→system→error
resolution applies to any secret/connection-string resolution (never empty/plain — fail the step with a
typed reason); every failure edge flips the row to `Failed` with a structured `FailureReason` + emits a
DCB event (no silent failure, no false success); compensation halts visibly on its own failure with an
orphans-possible diagnostic.

---

## One-line verdict

A genuinely well-built, resumable, event-sourced 8-step compensating saga that is **partial**, not
complete: two of its eight steps (`RegisterSecrets`, `PersistEndpoints`) are deferred no-ops / non-
persisting, quota (AC6) and per-provider timeouts (AC7) are unimplemented, unified-tenancy
reconciliation is bypassed (raw Cranl DB URL), and — decisively — the whole saga has **zero production
callers** (admin endpoints still ride V1). Recommended disposition: **finish the deferred 30-3 work +
the Epic 30 Phase-A cutover**, not a rewrite.
