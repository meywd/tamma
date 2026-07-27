# Epic 30: Pluggable Tenant Infrastructure Provisioning

**Status**: in-progress (Phase A / Wave C complete 2026-06-29; Phases B–E outstanding)
**Layer**: Layer 5 (validation + scale-out) — see
[`plans/epic-29-30-placement.md`](../plans/epic-29-30-placement.md)
**Depends on**: Epic 28 (tenant DbContext factory, tenant lifecycle
workflows), Epic 29 Story 29-6..29-8 (rotation workflow + handlers for
secret-push into provisioned infra)

## Execution status

### Phase A — Wave C: V1→V2 provisioner cutover (DONE 2026-06-29)

Commits `c25cd980`–`d69c42bb` on `feat/epic-30-phase-a-v1v2-cutover`
(plus Cranl wiring `c9f2c353`). Covers the three admin endpoints
(`POST/GET/POST /api/admin/tenants/{id}/provision|provisioning|deprovision`),
all now riding `ProvisionTenantV2Dispatcher` / `TenantProviderRegistry`.

**Delivered:**
- `c25cd980` — null-provider dispatch short-circuits to `Ready`
  (`shared_infrastructure_no_backend_configured`), matching V1
  `NullTenantProvisioner` semantics under the unified schema-per-tenant
  model (schema minted at creation; nothing to do).
- `ca4a3879` — V2 deprovision path: `ProvisioningOperation` payload
  discriminator; `DispatchDeprovisionAsync` on the dispatcher (null →
  `Deprovisioned` no-enqueue; real → `Deprovisioning` + enqueue);
  `DeprovisionAsync` on `ProvisionTenantV2Workflow`.
- `7678e794` — admin endpoints cut over to `ProvisionTenantV2Dispatcher`;
  V1 `ITenantProvisioner` injection removed.
- `d69c42bb` — V1 surface deleted: `ITenantProvisioner`, `NullTenantProvisioner`,
  `CranlTenantProvisioner`, `TenantProvisioningTaskHandler` removed; V1 test
  files deleted; `ProvisioningModels.cs` V1 records cleaned up.
- `c9f2c353` — **deviation from the 2026-06-11 plan** (which deferred Cranl
  to Phase B): `CranlProvisioningWorkflow` (the REST-walk engine) was KEPT
  and two new `IPlatformTaskHandler`s wired — `CranlProvisionPlatformTaskHandler`
  (`provisioning.tenant`) and `CranlDeprovisionPlatformTaskHandler`
  (`provisioning.tenant.deprovision`) — so the Cranl provision/deprovision
  paths complete end-to-end (project→db→app→Ready / app→db→project teardown)
  rather than timing out to Failed.

**Still deferred (Phase B / Story 30-3):**
- `RegisterSecrets` saga step — hard-blocked on Epic 29's `ISecretStore`
  (does not exist in code).
- Per-org quota enforcement (Story 30-3).
- Pool-row registration: Cranl provider must mint a `tenant_databases` row
  and drive `TenantMoveService` (the V2↔unified-model reconciliation; the
  V2↔unified-model routing fix so Cranl DB routing flows through the unified
  `EncryptedConnectionString` envelope rather than a raw Cranl URL).
- `provider_resource_ids`/`provider_key` column persistence; `SqlTenantProviderKeyLookup` activation.

**Ops note:** `PlatformTaskWorker.RunOnStartup` is `false` (unchanged) —
provisioning platform tasks drain only when that worker is enabled.

### Phases B–E — Outstanding

- **Phase B** — Pool-row reconciliation / unified-model routing fix (see
  `docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md` §3
  Phase B). Blocked on the Cranl-credential CREATEROLE analysis.
  **Known limitation (single-worker Cranl saga constraint):** `ProvisionTenantV2Workflow` block-polls for an inner `provisioning.tenant` platform task that `CranlTenantProviderV2` enqueues on the same queue. `PlatformTaskWorker` processes one task at a time per process — so on a single worker process the saga occupies the only slot and the inner task is never reserved, causing provision to time out to `Failed`. Requires ≥2 platform-worker processes until Phase B restructures the saga away from block-polling a same-queue inner task. Not reachable today (`PlatformTaskWorker.RunOnStartup=false`; Cranl opt-in; null path unaffected).
- **Phase C** — CHECK-constraint tightening (deviations 6–7 from the
  unified-tenancy plan).
- **Phase D** — Story 28-1 closeout: per-tenant Elsa runner decision.
- **Phase E** — Story 28-13 trigger review (OpenBao).

---

## Why this epic exists

The V1 `ITenantProvisioner` surface has now been retired (Phase A above).
Phases B–E generalise the provisioning plane further: one interface,
multiple backends, multiple topologies, per-tenant routing, and CHECK
tightening. The original motivation for coupling elimination:

Today (`pre-Phase A`) `ITenantProvisioner` had two implementations: `Null` (dev
fallback) and `Cranl`. Everything else about the tenant plane — the
connection string, the engine host, the DB topology — was Cranl-specific.
This coupled the platform to one vendor and made it impossible to
offer:

- **BYO** tenants (enterprise accounts on their own Postgres + their own
  Elsa runner; Tamma registers the endpoints and routes traffic but
  doesn't provision infra).
- **Hetzner Cloud** tenants (dedicated VPS per tenant for data-residency
  / performance customers).
- **Cloudflare Workers for Platforms** tenants (edge-deployed engine +
  D1 DB; lowest-cost tier — matches the research notes' observation
  that Workers for Platforms is the closest industry analogue).
- Hybrid topologies — a premium tenant on Hetzner for compute but
  connected to a customer-owned RDS instance for data.

User design intent (2026-04-20):

> Cranl and maybe other replacements — either vps based db servers, or
> cloudflare or any db provider — will allow tenant dbs to be created
> on the fly, physical or virtual servers, not just dbs per tenants.

This epic generalises the provisioning plane: one interface, multiple
backends, multiple topologies, selectable per tenant at onboarding.

## Scope

- **In-scope**: pluggable provisioning interface + topology model,
  workflow reshape to dispatch to a per-backend handler, four backend
  implementations (Cranl refactor, Hetzner, Cloudflare, BYO), onboarding
  UI, per-request routing, deprovisioning, cost/quota visibility.
- **Out-of-scope**: the secret-management surface for the new infra
  (owned by Epic 29 — each backend registers its rotation handler),
  billing integration (future Epic), region-failover + DR (future
  Epic).

## Story map

| # | Title | Est. hours | Depends on | Blocks |
|---|---|---|---|---|
| [30-1](./30-1-provisioner-interface-v2.md) | `ITenantInfrastructureProvider` v2 + `ProvisioningTopology` | 18 | 28-3 | 30-2 .. 30-10 |
| [30-2](./30-2-provisioning-workflow-dispatch.md) | Provisioning workflow in Elsa — resumable, per-backend dispatch | 22 | 30-1, 28-5 | 30-3 .. 30-10 |
| [30-3](./30-3-cranl-provider-refactor.md) | Cranl provider refactor to v2 interface | 14 | 30-1, 30-2 | — |
| [30-4](./30-4-hetzner-cloud-provider.md) | Hetzner Cloud provider (Cloud API + cloud-init) | 32 | 30-1, 30-2, 29-7 | 30-8, 30-9 |
| [30-5](./30-5-cloudflare-provider.md) | Cloudflare provider (D1 + Workers + KV) | 30 | 30-1, 30-2 | 30-8, 30-9 |
| [30-6](./30-6-byo-provider.md) | BYO provider (validate external DB + engine-registry hook) | 18 | 30-1, 30-2 | 30-8, 30-9 |
| [30-7](./30-7-onboarding-ui.md) | Admin UI — onboarding backend + topology picker | 24 | 30-1, 30-3, 30-4, 30-5, 30-6 | — |
| [30-8](./30-8-per-tenant-routing.md) | Per-tenant routing — resolve tenantId → provider+endpoints | 20 | 30-1, 30-3, 30-4, 30-5, 30-6, 19-6 | — |
| [30-9](./30-9-deprovisioning-workflow.md) | Deprovisioning saga — reverse each backend | 16 | 30-1, 30-2, 30-3, 30-4, 30-5, 30-6 | 30-10 |
| [30-10](./30-10-cost-quota-dashboard.md) | Cost + quota dashboard per tenant | 22 | 30-8 | — |
| [30-11](./story-30-11/30-11-tenant-offboarding-and-data-portability.md) | **Tenant offboarding & data portability — the customer-initiated exit** | 56 | 30-9 (soft), 37-7 (soft), 41-30 (soft) | — |
| **Total** | | **272** | | |

> **30-11 added 2026-07-27, closing a gap this epic's own 30-9 named and deferred.** 30-9 **AC6** reads
> *"Tenant-admin cannot self-deprovision; must contact platform admin (feature flagged for future
> self-service)"* — and no follow-up story was ever written for that flag. Verified today: **every**
> tenant-destruction path is platform-admin-only (`AdminTenantsEndpoints`'s `/actions/delete`,
> `/cancel-delete`, `/cleanup`); `grep -ri offboard` over the C# tree returns **zero hits**; there is
> **no tenant-scoped data export** anywhere (37-7 is a *data subject* DSAR, 37-4 is audit rows, 36-8 is
> analytics rollups — none exports a tenant's working data); and `Status = "suspended"` has a complete
> 402 response branch (`TenantStatusEvaluator.cs:38-39,76,186-195`) that **nothing in the codebase ever
> writes**. 30-11 is a **new workflow** (`tenant-offboarding`) because the trigger, the artifact and the
> lifecycle all differ from 30-9's: a tenant owner rather than a token-holding operator; a portability
> bundle produced *before* teardown; and a **day-scale, customer-cancellable** grace period rather than
> 30-9/`TenantDeleteRequestedTrigger`'s five-minute operator undo. It **composes** 30-9 rather than
> duplicating it — the terminal step dispatches `DeprovisionTenantWorkflow` and 30-11 writes no
> teardown logic at all (pinned by a structure test).
>
> Two things it deliberately does **not** take on, both still unowned and both recorded here so they
> stay visible: the **scheduled retention purge of `platform_events`** (30-9 AC9 disclaims it as an
> "Epic 17 follow-up" that no story picks up; 37-5 covers `audit_records` only) — a recurring job, and
> therefore a consumer of Epic 41's 41-30 seam; and **over-quota resource reclamation on plan
> downgrade** (34-4 flags-never-blocks and its `ITenantUsageReader` is null-wired in production, so the
> warning never fires; 35-6 owns quota enforcement but its text never mentions downgrade) — which
> belongs in 35-6's scope, not here.
>
> *File-layout note:* 30-11 uses the `story-30-11/` + `implementation-plan.md` layout that epics 39–44
> standardised on, rather than this epic's older flat `30-N-*-impl-plan.md` pairs.

## Review findings this epic closes

- **Finding 1** (per-tenant routing, real wiring) — fully closed by
  Story 30-8. Story 19-6 does the RLS half; 30-8 does the connection-
  resolution-to-provider-endpoints half that Epic 28's Phase-B only
  sketched.
- **Generalisation over Cranl** — Cranl-only coupling eliminated by
  30-1 + 30-3.

## Non-goals

- Does not add more AI providers, Git platforms, or CI integrations
  (separate epics).
- Does not implement CDN caching / cold-start mitigation for the
  Cloudflare backend (handled by Cloudflare's platform).
- Does not ship billing / invoicing.

## Risks

| Risk | Mitigation |
|---|---|
| Four backends × many topologies → combinatorial surface | `ProvisioningTopology` has three values (`DatabaseOnly`, `DedicatedCompute`, `Managed`); each backend declares a capability matrix. Onboarding UI filters to valid combos. Max ~10 valid (backend, topology) pairs, not 4×3. |
| Provisioning half-failure leaves orphan cloud resources | Story 30-2 workflow uses the saga pattern (same shape as Epic 29's rotation): each step has a compensation; Story 30-9 deprovisioning is the outer compensation. |
| Per-tenant routing cache staleness | Story 30-8 uses an LRU with TTL + an event-driven invalidation (publishes `TENANT.ROUTING.CHANGED` when provider endpoints change). |
| Cloudflare / Hetzner API rate limits | Each backend has a per-API-key token bucket (same pattern as Story 29-8's Cranl rate limiting). |
| BYO tenant provides a broken DB | 30-6 validates on intake (probe connection, check migration table, refuse if version drift); failures bubble back to the onboarding UI with a clear error. |

## Sources

- User design intent: 2026-04-20 planning session
- Research notes: [`../research/secret-management-and-multi-backend-provisioning-2026.md`](../research/secret-management-and-multi-backend-provisioning-2026.md) §2
- Epic 28 (today's Cranl baseline): [`../epic-28/README.md`](../epic-28/README.md)
- Today's Cranl code: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/`
