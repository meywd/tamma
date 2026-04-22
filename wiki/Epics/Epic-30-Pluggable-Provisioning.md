# Epic 30: Pluggable Tenant Infrastructure Provisioning

**Status:** Planning (briefs + impl plans authored 2026-04-20)
**Stories:** 10 (30-1 through 30-10), ~216h
**Layer:** Layer 5 (validation + scale-out)
**Depends on:** Epic 28 (tenant DbContext factory, tenant lifecycle workflows), Epic 29 Stories 29-6..29-8 (rotation primitive + handlers)

> **Overview**: [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) — root-level topic page with the v2 abstraction, topology enum, capability matrix, and per-backend semantics.

## Purpose

Today `ITenantProvisioner` has two implementations: `Null` (dev fallback) and `Cranl`. Everything else about the tenant plane — the connection string, the engine host, the DB topology — is Cranl-specific. This couples the platform to one vendor and makes it impossible to offer:

- **BYO** tenants (enterprise accounts on their own Postgres + their own Elsa runner; Tamma registers endpoints and routes traffic but doesn't provision infra)
- **Hetzner Cloud** tenants (dedicated VPS per tenant for data-residency / performance customers)
- **Cloudflare Workers for Platforms** tenants (edge-deployed engine + D1 DB; lowest-cost tier — matches the closest industry analogue)
- **Hybrid topologies** — a premium tenant on Hetzner for compute but connected to a customer-owned RDS instance for data

User design intent (2026-04-20):

> Cranl and maybe other replacements — either VPS-based DB servers, or Cloudflare or any DB provider — will allow tenant DBs to be created on the fly, physical or virtual servers, not just DBs per tenant.

This epic generalises the provisioning plane: one interface, multiple backends, multiple topologies, selectable per tenant at onboarding.

## Current state

- `Cranl` is the sole real backend; the `Null` provisioner is the dev fallback
- `CranlTenantProvisioner` lives in `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/`
- Tenants stay on the shared central Postgres via RLS until `Cranl:ApiKey` + `Cranl:OrganizationId` are set
- The provisioning plane is fully Cranl-coupled — interface, workflow, secret-handling all Cranl-shaped

## Stories

| # | Title | Effort | Depends on | Blocks | Status |
|---|-------|--------|------------|--------|--------|
| 30-1 | `ITenantInfrastructureProvider` v2 + `ProvisioningTopology` | 18h | 28-3 | 30-2..30-10 | Planned |
| 30-2 | Provisioning workflow in Elsa — resumable, per-backend dispatch | 22h | 30-1, 28-5 | 30-3..30-10 | Planned |
| 30-3 | Cranl provider refactor to v2 interface | 14h | 30-1, 30-2 | — | Planned |
| 30-4 | Hetzner Cloud provider (Cloud API + cloud-init) | 32h | 30-1, 30-2, 29-7 | 30-8, 30-9 | Planned |
| 30-5 | Cloudflare provider (D1 + Workers + KV) | 30h | 30-1, 30-2 | 30-8, 30-9 | Planned |
| 30-6 | BYO provider (validate external DB + engine-registry hook) | 18h | 30-1, 30-2 | 30-8, 30-9 | Planned |
| 30-7 | Admin UI — onboarding backend + topology picker | 24h | 30-1, 30-3, 30-4, 30-5, 30-6 | — | Planned |
| 30-8 | Per-tenant routing — resolve `tenantId` → provider+endpoints | 20h | 30-1, 30-3, 30-4, 30-5, 30-6, 19-6 | — | Planned |
| 30-9 | Deprovisioning saga — reverse each backend | 16h | 30-1, 30-2, 30-3, 30-4, 30-5, 30-6 | 30-10 | Planned |
| 30-10 | Cost + quota dashboard per tenant | 22h | 30-8 | — | Planned |

**Total**: 216h.

## Architecture / key decisions

1. **`ProvisioningTopology` enum**: `DatabaseOnly`, `DedicatedCompute`, `Managed`. Each backend declares a capability matrix; the onboarding UI filters to valid combos. Max ~10 valid (backend, topology) pairs, not 4×3.
2. **Per-backend dispatch via Elsa workflow**: 30-2 reshapes `CreateTenantWorkflow` to dispatch to a per-backend handler activity. Each backend has its own resumable workflow chain with saga compensation (failed step rolls back prior steps; outer compensation = 30-9 deprovisioning).
3. **BYO validation on intake**: 30-6 probes the customer's DB connection, checks the migration table, refuses if version drift. Failures bubble back to the onboarding UI with a clear error.
4. **Per-tenant routing cache**: 30-8 uses an LRU with TTL + event-driven invalidation (publishes `TENANT.ROUTING.CHANGED` when provider endpoints change).
5. **Each backend gets a per-API-key token bucket** (same pattern as 29-8's Cranl rate limiting) to respect Cloudflare/Hetzner rate limits.
6. **The `IRotationHandler` from Epic 29 is the seam**: each backend registers its own rotation handler with the cabinet (Hetzner = SSH + restart systemd unit; Cloudflare = wrangler API; BYO = customer-supplied webhook).

## Dependencies

**Upstream**:
- [Epic 28](Epic-28-DB-Per-Tenant.md) — tenant DbContext factory (28-3), tenant lifecycle workflows (28-5)
- [Epic 29](Epic-29-Secret-Management.md) Stories 29-6..29-8 — rotation primitive + handlers for secret-push into provisioned infra
- [Epic 19](Epic-19-Agent-Dispatch.md) Story 19-6 — for the per-tenant routing wiring half

**Downstream**:
- Future billing epic — consumes 30-10 cost/quota data per tenant

## Review findings closed

- **Finding 1** (per-tenant routing, real wiring) — fully closed by Story 30-8. Story 19-6 does the RLS half; 30-8 does the connection-resolution-to-provider-endpoints half that Epic 28's Phase-B only sketched.
- **Generalisation over Cranl** — Cranl-only coupling eliminated by 30-1 + 30-3.

## Non-goals

- Does not add more AI providers, Git platforms, or CI integrations (separate epics)
- Does not implement CDN caching / cold-start mitigation for the Cloudflare backend (handled by Cloudflare's platform)
- Does not ship billing / invoicing
- Does not introduce region-failover or DR (future epic)

## Risks

| Risk | Mitigation |
|------|------------|
| Four backends × many topologies → combinatorial surface | `ProvisioningTopology` has three values; each backend declares a capability matrix. Onboarding UI filters to valid combos. Max ~10 valid pairs, not 4×3. |
| Provisioning half-failure leaves orphan cloud resources | Story 30-2 workflow uses the saga pattern (same shape as Epic 29's rotation): each step has a compensation; Story 30-9 deprovisioning is the outer compensation. |
| Per-tenant routing cache staleness | Story 30-8 uses an LRU with TTL + event-driven invalidation. |
| Cloudflare / Hetzner API rate limits | Each backend has a per-API-key token bucket. |
| BYO tenant provides a broken DB | 30-6 validates on intake (probe connection, check migration table, refuse if version drift). |

## Open questions

1. **Cloudflare D1 limits at scale**: D1 has per-database storage and concurrency limits. At 1000+ tenants on Cloudflare topology, do we need to shard? Defer to first real Cloudflare-tenant deployment.
2. **Hetzner private networking**: tenants on Hetzner topology need a private network for the engine ↔ DB hop. Story 30-4 default is per-tenant private network (one Hetzner Cloud Network resource); revisit if cost or limits become an issue.
3. **BYO trust model**: how much of the tenant's DB do we manage vs leave to the customer's DBA? V1 = we own the schema (run migrations); customer owns ops (backup, patching). Documented in Story 30-6.

## Sources

- User design intent: 2026-04-20 planning session
- Research notes: `docs/stories/research/secret-management-and-multi-backend-provisioning-2026.md` §2
- Epic 28 (today's Cranl baseline): `docs/stories/epic-28/README.md`
- Today's Cranl code: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/`

## Story files

[Epic 30 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-30)

---

_Last updated: 2026-04-21_
