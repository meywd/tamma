# Multi-Tenant Provisioning (Epic 30)

**Status**: planning (briefs + impl plans authored 2026-04-20). 10 stories, 216h, Layer 5.
**Depends on**: Epic 28 (tenant DbContext factory, tenant lifecycle workflows), Epic 29 Stories 29-6..29-8 (rotation primitive + handlers).
**Source**: `docs/stories/epic-30/` (10 briefs + 10 impl plans + README).

## Current state — unified tenancy Phases 2–4 (shipped 2026-06-10)

Before any Epic 30 backend lands, tenant creation already runs a **unified schema-per-tenant pipeline** (`TenantProvisioningService`, shared by the SaaS `CreateTenantWorkflow`, inline org creation (`OrgEndpoints.CreateOrg`), and the single-user `EnsurePersonalTenantMiddleware`):

1. **Placement** — `ITenantPlacementService` assigns the tenant to a `tenant_databases` pool row by plan tier (`plans.PlacementPolicy`: free/team → shared, enterprise → dedicated) and stamps `tenants.DatabaseId` + `SchemaName`.
2. **Role + schema** — `CREATE ROLE tamma_tenant_<hex>` on the pool row's cluster; `CREATE SCHEMA t_<hex> AUTHORIZATION` that role with schema-scoped grants only (no access to `public` or sibling schemas) + `GRANT CONNECT` + per-DB default `search_path`.
3. **Mint** — a `...;Search Path=t_<hex>` connection string is built against the pool row's database, AES-GCM-encrypted under the current KEK, and persisted on the tenant row.
4. **Migrate** — the `InitialTenant` baseline applies into the schema (in-schema `__TenantMigrationsHistory`).

The central DB auto-bootstraps as pool member #1 (`Label='central'`, shared, all tiers) so dev/self-host and SaaS share one placement path. Since Phase 3, provisioning is **mandatory and synchronous on both entry paths**: org creation (`POST /api/v1/orgs`) calls `ITenantProvisioningService.ProvisionAsync` inline before the org's first tenant-store write (failure propagates — no half-usable org), and personal tenants provision **synchronously at first login** in single-user mode with failures failing the request (the transitional soft-fail onto a shared path was removed with the stub resolver). Tenant data access goes through one path only — `LruPooledTenantConnectionResolver` resolving the tenant's stored encrypted `...;Search Path=t_<hex>` connection string; `StubTenantConnectionResolver` is deleted. Delete drops the schema (`DROP SCHEMA ... CASCADE`) and role (`DROP OWNED BY` + `DROP ROLE`) and releases the pool slot; backups are schema-scoped (`pg_dump -n t_<hex>`).

**Superseded**: the Epic-28 `CreateTenantDatabaseActivity`/`DropTenantDatabaseActivity` (one Postgres *database* per tenant) were deleted in Phase 2 — placement now hands out schemas inside pooled databases. Epic 30's role narrows accordingly: its backends provision **pool rows into `tenant_databases`** (and dedicated compute), not per-tenant DBs directly — see `ProviderKey` Decision 3 in `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md`.

### Admin DB-pool CRUD + tenant→database view (Phase 4)

Platform owners (the `PlatformOwnerAccess` policy) manage the pool directly — `AdminTenantDatabasesEndpoints`:

| Endpoint | Behavior |
|---|---|
| `GET /api/admin/tenant-databases` | list pool rows (Id, Label, Host, Port, PlacementClass, TierEligibility, TenantCapacity, TenantCount, Status, KekVersion, timestamps) — the admin connection string is **never** serialized into any response |
| `GET /api/admin/tenant-databases/{id}` | row + the tenants placed on it (Id, Slug, SchemaName, Status) — the pool side of the tenant→DB view |
| `POST /api/admin/tenant-databases` | register a row: plaintext `adminConnectionString` inbound only — probed live (`SELECT 1`, 5 s timeout; unreachable → 422 + Npgsql error), Host/Port parsed **from** the string (no separate body fields, no mismatch possible), AES-GCM-encrypted at rest. 409 on duplicate label and on a (Host, Port, Database) tuple aliasing an existing row's physical database |
| `PATCH /api/admin/tenant-databases/{id}` | mutable: label, tierEligibility, tenantCapacity, status (`active`\|`draining`\|`full`\|`retired`), adminConnectionString (re-probe + re-encrypt + pool decrypt-cache evict) |
| `DELETE /api/admin/tenant-databases/{id}` | hard delete; 409 unless TenantCount == 0 **and** no `tenants.DatabaseId` references the row (defensive count — bookkeeping could drift) |

The tenant side of the view: `GET /api/admin/tenants` list/detail projections expose each tenant's `DatabaseId` + `SchemaName`. Pool capacity stays advisory (Phase 2 note) — CRUD validates shape, not global invariants.

### Move tenant (Phase 4)

`POST /api/admin/tenants/{tenantId}/move` body `{ "targetDatabaseId": "..." }` (owner-only) validates cheaply, returns **202 Accepted**, and enqueues a `tenant.move` platform-queue task (the same 202+queue pattern Cranl provisioning uses — there is **no** Elsa workflow for moves). `GET /api/admin/tenants/{tenantId}/move` polls: tenant `Status` (`draining` while the move runs, back to `active` on completion), the last move error (`FailureReason` shadow column, cleared on a later success), and current placement (`DatabaseId` flips to the target once the re-point commits).

`TenantMoveService` step order (each step idempotent or safely re-runnable; logs `tenant.move.<step>`):

1. **validate** — tenant active (or `draining` when resuming an interrupted move), has placement; target row active, differs from source, passes the same tier-eligibility/capacity predicate placement uses. A per-tenant Postgres **advisory lock** rejects concurrent moves; an **alias guard** rejects a "move" between two pool rows that point at the same physical database (it would drop the live schema).
2. **drain** — Status → `draining` + pool evict. This is the brief read-only window: `TenantContextMiddleware` 503s (+`Retry-After`) mutating verbs while GET/HEAD/OPTIONS keep flowing (the LRU resolver still yields connections for a draining tenant). A configurable grace delay (`TenantMove:DrainGraceSeconds`, default 2) lets in-flight writes land before the dump.
3. **dump** — `pg_dump -F c -n t_<hex>` from the source row (PGPASSWORD env only, never argv; 0700 temp dir).
4. **role on target** — same-cluster (source Host:Port == target) skips (role exists cluster-wide, password kept); cross-cluster creates the role on the target cluster with a fresh password.
5. **schema on target** — `CREATE SCHEMA ... AUTHORIZATION` + grants, same as provisioning.
6. **restore + verify** — `pg_restore --no-owner --role <tenant role>` into the target DB, with an ignored-error budget plus `__TenantMigrationsHistory` **and per-table row-count** verification against the source (catches silent partial restores). Mismatch aborts with the source intact and the tenant still `draining`.
7. **re-point** — same-cluster: decrypt the envelope and swap only `Database`; cross-cluster: mint a fresh `...;Search Path=t_<hex>` string with the new credentials. Encrypt + persist, flip `tenants.DatabaseId`, shift TenantCount source−/target+ in one SaveChanges.
8. **evict + verify** — pool evict, then a real `TenantDbContext` round-trip against the new placement.
9. **drop source** — `DROP SCHEMA ... CASCADE` on the source row; cross-cluster also `DROP OWNED BY` + `DROP ROLE` on the source cluster.
10. **activate** — Status → `active`; temp dump deleted in a finally.

Failure windows: steps 2–6 leave the tenant `draining` with the source intact (re-run the move — steps resume idempotently — or PATCH status back to `active`); failures after the step-7 commit leave the tenant pointing at the **target** (a re-run completes drop/activate).

> **Deployment note**: `PlatformTaskWorker:RunOnStartup` defaults to **false** — queued moves execute only on deployments that enable the worker. Binaries: `pg_dump`/`pg_restore` must be present in the API image (`TenantMove:PgDumpPath`/`PgRestorePath`, PATH-resolved by default) — see `docs/deployment/configuration-reference.md`.

## Why this epic exists

Today `ITenantProvisioner` has two implementations: `Null` (dev fallback) and `Cranl`. Everything else about the tenant plane — the connection string, the engine host, the DB topology — is Cranl-specific. This couples the platform to one vendor and makes it impossible to offer:

- **BYO** tenants (enterprise accounts on their own Postgres + their own Elsa runner; Tamma registers endpoints and routes traffic but doesn't provision infra).
- **Hetzner Cloud** tenants (dedicated VPS per tenant for data-residency / performance customers).
- **Cloudflare Workers for Platforms** tenants (edge-deployed engine + D1 DB; lowest-cost tier).
- **Hybrid topologies** — a premium tenant on Hetzner for compute but connected to a customer-owned RDS instance for data.

## Design intent

> Cranl and maybe other replacements — either VPS-based DB servers, or Cloudflare or any DB provider — will allow tenant DBs to be created on the fly, physical or virtual servers, not just DBs per tenant.

— User design intent, 2026-04-20 planning session.

## The v2 abstraction

```csharp
public interface ITenantInfrastructureProvider
{
    string ProviderKey { get; }   // "cranl" | "hetzner" | "cloudflare" | "byo"

    ProvisioningTopologyCapabilities Capabilities { get; }

    Task<ProvisionResult> ProvisionAsync(
        ProvisionRequest req,
        CancellationToken ct);

    Task<HealthStatus> ProbeAsync(Guid tenantId, CancellationToken ct);

    Task DeprovisionAsync(Guid tenantId, CancellationToken ct);
}

public enum ProvisioningTopology
{
    DatabaseOnly,        // shared compute; per-tenant DB
    DedicatedCompute,    // per-tenant VPS / Worker + per-tenant DB
    Managed              // fully-hosted (Cranl-style) — DB + engine app as one bundle
}
```

`ProvisioningTopologyCapabilities` declares which topologies each backend supports. The onboarding UI (Story 30-7) filters to **valid `(backend, topology)` combos** — ~10 valid pairs, not 4×3.

## Backend matrix

| Backend | Story | `DatabaseOnly` | `DedicatedCompute` | `Managed` | Scale cap | Rate limit |
|---------|-------|:---:|:---:|:---:|---|---|
| Cranl (refactor) | 30-3 | — | — | yes | unlimited (API-paginated) | Cranl-account-wide |
| Hetzner Cloud | 30-4 | yes (managed PG) | yes (VPS + Postgres) | — | ~200 tenants / account (soft) | **3600 req/h per account** (`SemaphoreSlim(8)` concurrency cap) |
| Cloudflare | 30-5 | yes (D1) | yes (Worker + D1) | — | **50,000 D1 DBs** / account; **10 GB / DB** | Workers API |
| BYO | 30-6 | yes | — | — | unlimited (tenant-owned) | n/a |

### Hetzner Cloud (Story 30-4)

- Provisions a dedicated Hetzner Cloud VPS per tenant (cloud-init bootstrap: Postgres 17 + Elsa runner + Tamma engine container).
- Rate-limit research (2026-04-20): Hetzner enforces **3600 req/hour per account**, shared across all API tokens. Drove a `SemaphoreSlim(8)` concurrency cap on the provisioner; all probe/status calls share the same bucket.

### Cloudflare (Story 30-5)

- Provisions a **Workers for Platforms** namespace + per-tenant Worker script + D1 database + KV namespace.
- API limits (2026 docs): **50,000 D1 databases per account** on the Workers Paid plan; **10 GB hard cap per D1 database** — drives 30-10 alert thresholds at 8 GB.
- Upload verb is **PUT** (not POST) on `/accounts/{aid}/workers/dispatch/namespaces/{ns}/scripts/{name}`. First-time uploads are synchronous (200 OK = ready) — no wait-for-ready poll needed.

### BYO (Story 30-6)

- Tenant-admin enters their own Postgres connection string + their Elsa runner URL.
- Provisioner validates on intake: probe connection, check migration version, refuse if version drifts from platform expectations.
- On success, registers endpoints in the platform routing table (Story 30-8). No cloud resources created.

## Provisioning workflow (Story 30-2)

Saga-pattern Elsa workflow; each step has a compensation:

```
1. Pick backend (onboarding UI → tenant.backend_key)
2. Validate request against backend capabilities
3. Reserve tenant ID + create placeholder row (CP index)
4. Dispatch to backend.ProvisionAsync(...)
5. Backend returns connection endpoints (DB URL, engine URL)
6. Push seed secrets into tenant secret cabinet (Epic 29)
   — e.g. the DB password, the tenant HMAC, the engine API key
7. Run initial schema migration on tenant DB
8. Mark tenant active; publish TENANT.PROVISIONED.SUCCESS
```

Compensations (reverse each step): drop schema → delete secrets → delete backend resources → mark tenant failed → publish TENANT.PROVISION.FAILED. The outer compensation is Story 30-9's deprovisioning saga.

## Per-tenant routing (Story 30-8)

```
Request → resolve apiKey / JWT → tenantId
      → resolve tenantId → provider_key + endpoints (cached LRU + TTL)
      → inject TammaAppDbContext for the right DB
      → inject engine client for the right engine URL
```

- Cache invalidation: event-driven via `TENANT.ROUTING.CHANGED` (publishes when provider endpoints change). 28-11 events-timeline and 30-8 routing listener share this channel.
- Closes **half of review finding 1** (per-tenant wiring). Story 19-6 closes the other half (app-role `TammaAppDbContext` injection at request scope).

## Cost & quota dashboard (Story 30-10)

- Per-tenant cost aggregation: compute × time + storage × months + egress.
- Quota alerts at 80% / 100% of configured limits (e.g. D1 8 GB warning, Hetzner VPS CPU).
- Platform admin can set per-backend default quotas; tenant admin can view and request increases.

## Story map

| # | Title | Est. hours | Depends on |
|---|---|---|---|
| 30-1 | `ITenantInfrastructureProvider` v2 + `ProvisioningTopology` | 18 | 28-3 |
| 30-2 | Provisioning workflow in Elsa — resumable, per-backend dispatch | 22 | 30-1, 28-5 |
| 30-3 | Cranl provider refactor to v2 interface | 14 | 30-1, 30-2 |
| 30-4 | Hetzner Cloud provider (Cloud API + cloud-init) | 32 | 30-1, 30-2, 29-7 |
| 30-5 | Cloudflare provider (D1 + Workers + KV) | 30 | 30-1, 30-2 |
| 30-6 | BYO provider (validate external DB + engine-registry hook) | 18 | 30-1, 30-2 |
| 30-7 | Admin UI — onboarding backend + topology picker | 24 | 30-1..30-6 |
| 30-8 | Per-tenant routing — resolve tenantId → provider+endpoints | 20 | 30-1..30-6, 19-6 |
| 30-9 | Deprovisioning saga — reverse each backend | 16 | 30-1..30-6 |
| 30-10 | Cost + quota dashboard per tenant | 22 | 30-8 |
| **Total** |  | **216h** | |

## Review findings closed

| Finding | Severity | Closes via |
|---------|----------|------------|
| #1 per-tenant wiring (full close) | P0 | 30-8 (per-tenant routing half) + 19-6 (app-role half) |
| #14 8-hex Cranl resource names → ~65k birthday collision | P2 | 30-3 (Cranl refactor; expand to 16 hex / full UUID) |
| Cranl-only coupling (design-intent, not numbered finding) | — | 30-1 (interface) + 30-3..30-6 (additional backends) |

## Risks

| Risk | Mitigation |
|------|------------|
| Four backends × many topologies → combinatorial surface | `ProvisioningTopology` has three values; each backend declares a capability matrix; onboarding UI filters — ~10 valid pairs, not 12 |
| Provisioning half-failure leaves orphan cloud resources | Saga pattern with per-step compensation; 30-9 is the outer compensation |
| Per-tenant routing cache staleness | LRU with TTL + event-driven invalidation (`TENANT.ROUTING.CHANGED`) |
| Cloudflare / Hetzner API rate limits | Per-API-key token bucket per backend; Hetzner uses `SemaphoreSlim(8)` |
| BYO tenant provides a broken DB | 30-6 validates on intake; errors bubble to the onboarding UI with a clear message |

## Non-goals

- Does not add AI providers, Git platforms, or CI integrations (separate epics).
- Does not implement CDN caching / cold-start mitigation for the Cloudflare backend.
- Does not ship billing / invoicing.

## Related

- See also: [Epic 30 detail](Epics/Epic-30-Pluggable-Provisioning.md)
- [Architecture → Deployment Modes](Architecture#deployment-modes-three-mode-architecture)
- [Secret Management](Secret-Management) — Epic 29 provides rotation primitives each backend registers against
- [Port Audit](Port-Audit) — review finding 14 closed by 30-3
- Source: [`docs/stories/epic-30/README.md`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-30)
- Layer placement: [`docs/stories/plans/epic-29-30-placement.md`](https://github.com/meywd/tamma/blob/main/docs/stories/plans/epic-29-30-placement.md)
- Research: [`docs/stories/research/secret-management-and-multi-backend-provisioning-2026.md`](https://github.com/meywd/tamma/blob/main/docs/stories/research/secret-management-and-multi-backend-provisioning-2026.md) §2
