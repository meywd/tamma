# Epic 30: Pluggable Tenant Infrastructure Provisioning

**Status**: planning (briefs only, 2026-04-20)
**Layer**: Layer 5 (validation + scale-out) — see
[`plans/epic-29-30-placement.md`](../plans/epic-29-30-placement.md)
**Depends on**: Epic 28 (tenant DbContext factory, tenant lifecycle
workflows), Epic 29 Story 29-6..29-8 (rotation workflow + handlers for
secret-push into provisioned infra)

## Why this epic exists

Today `ITenantProvisioner` has two implementations: `Null` (dev
fallback) and `Cranl`. Everything else about the tenant plane — the
connection string, the engine host, the DB topology — is Cranl-specific.
This couples the platform to one vendor and makes it impossible to
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
| **Total** | | **216** | | |

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
