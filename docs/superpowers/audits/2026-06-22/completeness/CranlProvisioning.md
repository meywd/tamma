# Completeness Audit — CranlProvisioningWorkflow (V1)

**Date:** 2026-06-22
**Target:** `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlProvisioningWorkflow.cs`
**Type:** Plain orchestrator class (state-machine walker), NOT an Elsa `WorkflowBase`. It is named
"Workflow" and drives the per-tenant Cranl provisioning long-running flow on the platform task queue.

---

## Purpose & Owner

**Purpose:** Walk a single tenant through Cranl-backed infrastructure provisioning — create Cranl
project → database → poll-until-running → capture+encrypt the `DATABASE_URL` → create the engine
application → push env → deploy → poll-until-running → capture the app domain → mark `ready`. Also
owns the reverse teardown (`DeprovisionAsync`: app → db → project). Invoked off the platform task
queue by `TenantProvisioningTaskHandler` (so the multi-minute polling never pins a request thread).

**Owner:** Epic 28 (Cranl per-tenant provisioning baseline — audit `cranl/001`, Doc 02 §3) →
**superseded by Epic 30** (Pluggable Tenant Infrastructure Provisioning). Story 30-3 explicitly marks
this V1 surface `[Obsolete("Use ITenantInfrastructureProvider (V2) instead. Removed in Wave C.")]`.

**Production status:** This V1 path is what production still rides. The admin endpoints
(`AdminEndpoints.cs:428-482` — `ProvisionTenant` / `GetTenantProvisioning` / `DeprovisionTenant`)
inject the V1 `ITenantProvisioner` (`CranlTenantProvisioner`), which enqueues a `provisioning.tenant`
task → `TenantProvisioningTaskHandler` → `CranlProvisioningWorkflow.ProvisionAsync`. The V2 successor
`ProvisionTenantV2Workflow` is fully written and DI-registered but **has zero production callers**
(the cutover is the unstarted Phase A of `docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md`).

---

## Maturity: **partial**

It is more than a thin happy-path skeleton — it is a genuinely resumable, 9-step state machine with
real polling, timeouts, encryption-at-rest of the connection string, idempotent step-skipping
("do I already have what this step produces?"), a structured failure path, teardown, and an
"unexpected error" catch-all that flips the row to `failed` with a truncated diagnostic. That is well
past "stub" or "thin."

But it is **NOT complete** against its own product/architecture intent for two decisive reasons, both
of which the codebase itself already proves are the intended bar:

1. **Zero DCB/audit events.** The flow emits only `_logger.LogInformation` lines and writes a
   `tenants.provisioning_state` string column. It appends **nothing** to the platform event stream.
   This directly violates the project's first-class rule ("All system actions are captured as
   immutable events… 100% audit trail", CLAUDE.md §Event Sourcing; "Every operation must emit events
   for audit trail"). A canonical event vocabulary for exactly this flow already exists
   (`TenantLifecycleEvents` — Story 28-5 — `TENANT.PROVISIONING_REQUESTED`,
   `TENANT.PROVISION.STEP_STARTED/COMPLETED/FAILED`, `TENANT.PROVISIONED.SUCCESS`,
   `TENANT.PROVISION.FAILED`) and the V2 successor emits all of it via `IPlatformEventPublisher`.
   V1 ignores the catalogue entirely.

2. **No saga compensation.** When step 7 (app deploy poll) times out, the workflow flips the row to
   `failed` and returns — but it leaves the already-created Cranl **project + database + application**
   live in the vendor account (orphaned cloud resources, ongoing cost). There is no reverse-step
   cleanup on a mid-flow failure. The V2 workflow is an 8-step compensating saga precisely to close
   this; V1 has only a forward path plus a separate, manually-triggered `DeprovisionAsync`.

Because the core walk works end-to-end and is resumable, this is **partial**, not **thin**. The gap
is the audit-trail contract, the orphan-resource safety net, and the unified-tenancy reconciliation —
all of which are formally owned by the V2 rewrite that is meant to replace this file wholesale.

---

## Current Capabilities

- 9-step forward provisioning walk: create project → create database → poll db until `running`
  (5-min timeout) → capture + AES-GCM-encrypt the connection string onto `cranl_database_url_encrypted`
  → create app → push env (`PUT environment`) → trigger deploy → poll app until `running`/`done`
  (10-min timeout) → fetch + persist default domain → mark `ready`.
- **Resumability** — every create step is guarded by "do I already have the resource id?" so a worker
  that died mid-flow resumes on re-enqueue without leaking duplicate resources; the connection-string
  step is guarded by the encrypted-column presence.
- **Polling** with bounded deadlines, a 5s interval, transient-error tolerance
  (`CranlApiException.IsRetryable` → log + retry), and explicit `error`-state short-circuits.
- **Encryption at rest** of the tenant `DATABASE_URL` via `TenantSecretProtector` (AES-GCM).
- **Env assembly** (`BuildEnvironmentTextAsync`) with cabinet-backed shared-secret resolution
  (Story 29-10) and a legacy-config fallback; logs an error (no silent success) when the shared
  secret is missing in fail-fast mode.
- **Failure path** — `CranlApiException` and unexpected `Exception` both flip the row to `failed`
  with a truncated, structured `provisioning_detail`; `OperationCanceledException` deliberately does
  NOT mark failed (lets the next poll resume) — correct shutdown handling.
- **Teardown** (`DeprovisionAsync`) in safe order (app → db → project), with `SafeDeleteAsync`
  treating 404 as success, then clears the `cranl_*` columns and marks `deprovisioned`.
- Has a dedicated test (`CranlProvisioningWorkflowTests.cs`).

---

## Intended Full Scope (with citations)

The "complete" bar for this flow is set by three converging sources, all in-repo:

1. **Project audit-trail contract** — `CLAUDE.md` §"Event Sourcing (DCB Pattern)" and §"Emitting
   Events for Audit Trail": *every* operation appends an immutable `DomainEvent`/platform event with
   typed `tags` + `metadata`. A provisioning flow that touches external infra and money MUST be fully
   auditable / time-travel-replayable.

2. **Story 28-5 event catalogue** — `Tamma.Activities/TenantLifecycle/TenantLifecycleEvents.cs`
   already declares the exact `TENANT.PROVISION.*` / `TENANT.PROVISIONED.SUCCESS` /
   `TENANT.PROVISION.FAILED` types this flow is supposed to emit for the 28-11 dashboards.

3. **Epic 30 — the formal successor design** (`docs/stories/epic-30/README.md` +
   `docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md` + the live
   `V2/ProvisionTenantV2Workflow.cs`). The intended-complete provisioning workflow is:
   - a **resumable compensating saga** (ResolveProvider → Preflight → ReserveResources →
     ExecuteProvision → PersistEndpoints → RegisterSecrets → InitialProbe → Activate), each step with
     a reverse compensation; failure past ReserveResources runs compensations in reverse to avoid
     orphan cloud resources (README "Risks": *"Provisioning half-failure leaves orphan cloud
     resources → saga pattern, each step has a compensation"*);
   - **preflight gating** — capability matrix (topology, region) + per-org quota cap, fail-fast with
     a typed `FailureReason` before any vendor call;
   - **vendor-agnostic** — driven through `ITenantInfrastructureProvider`/`TenantProviderRegistry`,
     not hard-coded to Cranl; `ProviderKey` is a label;
   - **unified-tenancy reconciliation** (plan §1.2) — a backend must register the minted hosting DB
     as a `tenant_databases` pool row and the tenant routes via the unified `EncryptedConnectionString`
     (`Search Path=t_<hex>` + per-tenant role), NOT the raw Cranl DB URL. V1 (and V2's
     `ResolveEndpointsAsync`) currently bypass this — the real open architectural work of Epic 30.
   - **full DCB emission** at every step boundary + terminal success/failure (V2 emits
     `TENANT.PROVISION.<STEP>.STARTED/COMPLETED/FAILED`, `TENANT.PROVISIONED.SUCCESS`,
     `TENANT.PROVISION.FAILED` via `IPlatformEventPublisher.AppendAndPublishAsync`).

Net: a "complete" version of this workflow is the V2 saga, wired to production, reconciled with the
unified pool, and fully event-sourced. The honest disposition for the V1 file specifically is
**replace, not extend** — but until the cutover lands, the V1 gaps are real production gaps.

---

## Missing Capabilities

| # | Capability | Priority | Depends on |
|---|---|---|---|
| 1 | **DCB/audit events** — emit `TENANT.PROVISION.STEP_STARTED/COMPLETED/FAILED` per step + terminal `TENANT.PROVISIONED.SUCCESS` / `TENANT.PROVISION.FAILED` (the Story 28-5 `TenantLifecycleEvents` catalogue) via `IPlatformEventPublisher`. Today: zero events; only logs + a state-string write. Violates the project audit-trail contract. | P0 | Story 30-2 (V2 already does this) / Story 28-5 catalogue |
| 2 | **Saga compensation on mid-flow failure** — on db-poll / deploy-poll timeout or API error, reverse-delete the resources already minted (app → db → project) instead of leaving orphans live + billing. | P0 | Story 30-2 / 30-9 |
| 3 | **Unified-tenancy reconciliation** — register the Cranl-minted DB into `tenant_databases` and route the tenant through the unified `EncryptedConnectionString` (`Search Path=t_<hex>` + per-tenant role), not the raw Cranl `DATABASE_URL`. Current path would bypass schema-per-tenant isolation. | P0 | Story 30-3 + parent unified-tenancy plan decision 3 |
| 4 | **Preflight capability/region/quota gate** — validate topology + region against a capability matrix and enforce per-org tenant quota BEFORE any vendor create call; fail-fast with a typed reason. V1 has no preflight. | P1 | Story 30-1 / 30-2 |
| 5 | **`TENANT.PROVISIONING_REQUESTED` on enqueue** — the `CranlTenantProvisioner.ProvisionAsync` enqueue step writes the `pending` state-string but emits no event, so the audit stream has no "request received" anchor. | P1 | Story 28-5 catalogue |
| 6 | **Migrate / schema bootstrap inside the provisioned DB** — V1 captures the Cranl DB URL but never creates the `t_<hex>` schema / per-tenant role / runs app migrations on it (that lives in the separate unified `TenantProvisioningService`, not this Cranl walk). The two provisioning planes are unreconciled. | P1 | Story 30-3 (reconcile with `TenantProvisioningService`) |
| 7 | **Deploy/app-failure diagnostics** — on `error` app status the flow logs and returns `false` with a generic `application_did_not_reach_running` detail; it does not fetch Cranl deploy logs or surface the vendor error reason into `provisioning_detail` for operators. | P2 | none |
| 8 | **Idempotent env re-push guard** — step 5 always re-`PUT`s the full env set on every (re)run, even on resume past it; harmless but not guarded like the create steps. | P2 | none |
| 9 | **Deprovision audit events + state event** — `DeprovisionAsync` emits no `TENANT.DELETE.*` / teardown events; teardown is invisible in the audit stream. | P2 | Story 28-5 catalogue |
| 10 | **Terminal-failure operator notification** — a `failed` provisioning today only surfaces via a `GET /provisioning` poll; no alert/notification is raised (cf. Story 5.6 alert pipeline). | P3 | Story 5.6 alert pipeline |
| 11 | **Retry/backoff on terminal-but-retryable failures** — failures are terminal-until-manual-reset; no automatic bounded retry for transient deploy failures. | P3 | none |

> **Note on disposition:** Items 1–6 are the substance of the V2 rewrite (`ProvisionTenantV2Workflow`),
> which already exists and already satisfies 1, 2 (partially — provider-level deprovision compensation),
> and 4. The correct "build-out" for this file is therefore the **Epic 30 Phase-A cutover** (point the
> admin endpoints at V2 + delete V1), plus closing the V2-side gaps (3, 6) that V2 itself defers. Build
> out V1 in place ONLY if the cutover is not being done.

---

## Ordered Build-out Spec

### Track A (recommended) — cut over to V2 and retire V1 (Epic 30 "Wave C" / Phase A)

This is the intended path; it makes items 1, 2, 4, 5 disappear for free and leaves only the genuine
open architecture (3, 6).

1. **Repoint admin endpoints to the V2 dispatcher.** In `AdminEndpoints.cs:428-482`, inject
   `ProvisionTenantV2Dispatcher` instead of the `[Obsolete]` `ITenantProvisioner`. Map the request's
   `region` + `customName` onto `ProvisionTenantV2TaskPayload` and resolve `ProviderKey` from
   `Cranl` config (`cranl`) vs the null seam. `GetTenantProvisioning` reads the same
   `tenants.provisioning_state`/`ProvisioningStatusSnapshot`; `DeprovisionTenant` enqueues the V2
   deprovision saga (Story 30-9) instead of `CranlProvisioningWorkflow.DeprovisionAsync`.
2. **Close the V2 unified-tenancy gap (item 3).** In `CranlTenantProviderV2.ProvisionAsync`, after
   the Cranl DB reports `running`, **register the hosting DB into `tenant_databases`** (via
   `ITenantDatabasePool` / `TenantPlacementService`) and run the unified
   `TenantProvisioningService` step engine (create `t_<hex>` schema + per-tenant role + migrate),
   so the tenant routes via `EncryptedConnectionString` not the raw Cranl URL. Change
   `ResolveEndpointsAsync` to return only the **engine URL** for dedicated-compute; DB routing stays
   on the unified path. Emit a `TENANT.PROVISION.REGISTER_POOL.STEP_COMPLETED` event for the new step.
3. **Wire `RegisterSecrets` (V2 step 6, currently a `deferred_to_30_3` no-op)** to push the shared
   secret + DATABASE_URL into the secret cabinet (`ISecretStore.CreateAsync`, Epic 29) with a
   `RetireVersionAsync`-per-secret compensation. Emit step events.
4. **Add a `TENANT.PROVISIONING_REQUESTED` event** at the dispatcher enqueue point (item 5).
5. **Delete the V1 surface** (`CranlProvisioningWorkflow`, `CranlTenantProvisioner`,
   `TenantProvisioningTaskHandler`, V1 types in `ProvisioningModels.cs` — keep the shared
   `ProvisioningState` enum), remove the `#pragma warning disable CS0618` registrations, and migrate
   `CranlProvisioningWorkflowTests` to V2.
6. **Add a deprovision saga** (Story 30-9) with full `TENANT.DELETE.*` events (item 9) and an
   operator alert on terminal `TENANT.PROVISION.FAILED` (item 10, via the Story 5.6 pipeline).

### Track B (fallback) — harden V1 in place (only if cutover is deferred)

Do this strictly if Track A is not being executed this cycle.

1. **Inject `IPlatformEventPublisher`** into `CranlProvisioningWorkflow` and emit at every transition:
   - On entry: `TENANT.PROVISIONING_REQUESTED` (tags: `tenantId`, `region`, `provider:cranl`).
   - Before/after each step: `TENANT.PROVISION.STEP_STARTED` / `STEP_COMPLETED` with a `step`
     tag (`create_project`, `create_database`, `poll_database`, `create_application`, `push_env`,
     `deploy`, `poll_application`, `fetch_domains`) and `data` (resource id, duration).
   - On the two timeout/`error` returns and both `catch` blocks: `TENANT.PROVISION.STEP_FAILED`
     + terminal `TENANT.PROVISION.FAILED` (data: the existing truncated `detail`/status code).
   - On success at step 9: `TENANT.PROVISIONED.SUCCESS` (data: `appUrl`, `databaseId`).
   - Metadata: `{"workflowVersion":"1.x","eventSource":"system"}`. Use the `TenantLifecycleEvents`
     constants — do not invent new strings.
2. **Add forward compensation.** Build a compensation list as resources are minted (project → db →
   app). On any `Failed` return or caught exception PAST step 1, run reverse-order best-effort
   deletes (reuse `SafeDeleteAsync`) wrapped in their own `TENANT.PROVISION.<step>_compensated`
   events; on a compensation failure emit a `COMPENSATION_FAILED` event and leave the row `failed`
   with an `orphans_possible` detail (operator-visible, never a silent/false success).
3. **Reconcile with the unified pool** (item 3/6) — same as Track A step 2 but inside the V1 walk:
   after the DB is `running`, register it into `tenant_databases` and run the unified schema/role/migrate
   step engine; persist the unified `EncryptedConnectionString` (not just `CranlDatabaseUrlEncrypted`).
4. **Add a preflight step** before step 1: validate region against `CranlOptions`/a capability set and
   enforce a per-org tenant quota; fail-fast with `TENANT.PROVISION.PREFLIGHT.STEP_FAILED` and a typed
   detail (`region_not_supported` / `org_quota_exceeded`) BEFORE any Cranl create call.
5. **Surface vendor failure detail** (item 7): on `error` db/app status, fetch the Cranl error/deploy
   reason and fold it into `provisioning_detail` instead of the generic `did_not_reach_running`.
6. **Guard the env push** (item 8): skip the `PUT environment` re-push on resume when the app is
   already past `app_deploying` and env is unchanged.
7. **Deprovision events** (item 9): emit `TENANT.DELETE.REQUESTED` / `STEP_*` / `TENANT.DELETED.SUCCESS`
   from `DeprovisionAsync`.
8. **Terminal alert** (item 10): raise an alert via the Story 5.6 notification pipeline on
   `TENANT.PROVISION.FAILED`.

**Cross-cutting rules honored:** no external-provider calls move into this class beyond the existing
Cranl client (and per the pivot, agent/LLM work is out of scope here — this is infra provisioning, not
the LLM mediation path); tenant→system→error resolution is preserved for the shared secret (already
fail-fast, never empty/plain); every failure edge flips the row to `failed` with a diagnostic and emits
an event (no silent failure / false success).

---

## One-line verdict

A genuinely functional, resumable Cranl provisioning state machine that is **partial**, not complete:
it is missing the project's mandatory DCB audit-trail emission (P0), saga compensation against orphan
cloud resources (P0), and unified-tenancy reconciliation (P0) — all of which are formally owned by the
already-written-but-uncut-over **V2 `ProvisionTenantV2Workflow`** (Epic 30). Recommended disposition:
**replace via the Epic 30 Phase-A cutover**, not extend in place.
