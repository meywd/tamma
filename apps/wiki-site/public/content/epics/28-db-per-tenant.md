---
title: "Epic 28: Database-per-Tenant Isolation"
sidebar:
  order: 28
---

**Status:** Planned (12 stories scoped + 28-13 deferred blocker, ~265h)
**Stories:** 12 active (28-1..28-12) + 1 deferred (28-13)
**Layer:** Layer 4 (Foundation + Provisioning + Auth + Operations)
**Depends on:** Epic 17 (`tenants` table), Epic 1.5 secret-management track (KEK primitives)

> **Overview**: This epic has no root-level wiki page. Closely related to [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) (Epic 30), which generalises the provisioning plane after Epic 28's tenant model lands.

## Purpose

Move Tamma from a single shared Postgres database (with `TenantId` column and EF global query filter) to a database-per-tenant topology with:

- A separate **control plane** DB (`tamma_control`) holding `users`, `tenants`, `memberships`, `platform_events`, `platform_queued_tasks`, `platform_email_outbox`.
- Per-tenant application DBs (`tamma_tenant_<id>`) holding `agent_configs`, `prompts`, `domain_events`, `queued_tasks`, `workflow_instances`.
- A **global Elsa DB** (`tamma_global_elsa`) for platform workflows (`CreateTenantWorkflow`, `DeleteTenantWorkflow`, `OrchestratorWorkflow` per tenant, `PlatformAnalyticsRollup`).
- A **per-tenant Elsa DB** (`tamma_tenant_<id>_elsa`) for each tenant's `LlmCall`, `Mentorship`, bookmarks, triggers.

The shared-DB model has reached the limit of what query filters can safely guarantee: a forgotten `HasQueryFilter` is a cross-tenant leak; GDPR "delete me" requires a multi-hour row-by-row purge; cryptographic tenant isolation is impossible. Database-per-tenant gives constant-time tenant deletion (`DROP DATABASE`), per-tenant encryption at rest, per-tenant scaling knobs, and eliminates an entire class of query-filter-bypass bugs. It unlocks SOC 2 / ISO 27001 tenant-isolation requirements and opens the door to BYO-database and on-prem tiers.

## Current state

- `TammaDb` + `TammaAppDb` Phase-3 dual-connection split committed
- Phase-2 RLS migration shipped (`20260419021119_Phase2RlsAndTriggers`); RLS scaffolded but **not live** until Story 19-6 wires `TammaAppDbContext` into endpoints
- `Cranl` is the only real `ITenantProvisioner`; everything else is Cranl-coupled (closed by Epic 30)
- KEK decision: ship env-var KEK (per Doc 01 §8.2); defer OpenBao to Story 28-13 until a trigger fires (paying tenants with breach clauses, compliance finding, threat-model change, OpenBao LF graduation)

## Stories

| # | Title | Effort | Category | Status |
|---|-------|--------|----------|--------|
| 28-1 | EF migration scripts (CP + tenant + global-Elsa + per-tenant Elsa) | L (30h) | Foundation | Planned |
| 28-2 | Split `TammaDbContext` into `ControlPlaneDbContext` | M (16h) | Foundation | Planned |
| 28-3 | `TenantDbContext` factory with runtime connection routing | M (14h) | Foundation | Planned |
| 28-4 | Tenant connection resolver + LRU pool cache | L (22h) | Foundation | Planned |
| 28-5 | `CreateTenantWorkflow` + `DeleteTenantWorkflow` on global Elsa | XL (45h) | Provisioning | Planned |
| 28-6 | `platform_events` + `platform_queued_tasks` + `platform_email_outbox` | M (18h) | Provisioning | Planned |
| 28-7 | API-key prefix routing (`tk_t_` / `tk_pl_` / `tk_u_`) | M (14h) | Auth | Planned |
| 28-8 | `TenantContextMiddleware` async-provisioning handling | M (12h) | Auth | Planned |
| 28-9 | JWT claims + `/auth/switch-org` + refresh tokens across tenants | L (24h) | Auth | Planned |
| 28-10 | `platform_analytics_hourly` rollup workflow | L (28h) | Operations | Planned |
| 28-11 | Admin UX for `tenants.Status` state machine | L (22h) | Operations | Planned |
| 28-12 | Roles (`admin`/`provisioner`/`app`) + KEK rotation | L (20h) | Operations | Planned |
| 28-13 | **DEFERRED** — OpenBao KMS backend for tenant KEK | L–XL (30–45h) | Operations / Security | Deferred (trigger-gated) |

**Total in scope**: 265h. Story 28-13 is **not counted** — it lands only if a trigger condition (paying tenants with breach clauses, compliance finding, threat-model change, OpenBao LF-graduation) is met. The `ISecretsService` seam stays intact so the switch is a driver swap when activated.

## Architecture / key decisions

1. **Provisioning trigger = email verification, not registration** (Doc 01 wins over Doc 03). Registration writes only to CP (`users`, `tenants` with `Status='pending_verification'`, `tenant_memberships`, `platform_events:TENANT.REGISTERED`). The verify-email endpoint flips `Status='provisioning'` and emits `TENANT.PROVISIONING_REQUESTED`, on which `CreateTenantWorkflow` correlates. Avoids paying for DBs for bot registrations and typo addresses.
2. **Welcome email outbox = control plane** (Doc 03 wins). Welcome email enqueues to `platform_email_outbox` in CP — reuses `OutboxSmtpSender` unchanged, decouples delivery from tenant-DB availability, no tenant-DB PII in welcome content.
3. **Per-tenant `OrchestratorWorkflow` instances on global Elsa**, with a benchmark task in 28-10 to verify scale (1k / 5k / 10k idle instances). If p95 bookmark scan > 500ms or instance RAM > 2 GB at 5k idle, split to a tenant-fanout singleton before production hits that scale.
4. **`platform_events` in control plane** (Doc 01 + Doc 03 agree). Same schema as `domain_events`; tenant-scoped events stay in each tenant DB; cross-tenant lifecycle events (`TENANT.*`, `USER.REGISTERED`, `ORCHESTRATOR.TICK.*`) write to CP.
5. **Connection pool = LRU cache, ~1024 cached pools × 10 max-conns each**. Steady-state `pg_stat_activity` count stays under 4096 on a cluster with `max_connections=8192`. Cold tenants evicted from cache after idle period.
6. **Tenant-delete is O(1)**: `DROP DATABASE` is the cost. A tenant with 10 events and a tenant with 10M events both finish deletion in < 30s.

## Dependencies

**Upstream**:
- [Epic 17](Epic-17-Multi-Tenancy.md) — `tenants` table from Story 17-1
- [Epic 1.5](Epic-1.5-Infrastructure.md) — secret-management track for KEK primitives (1.5-16 onwards)

**Downstream**:
- [Epic 19](Epic-19-Agent-Dispatch.md) — Story 19-6 wires `TammaAppDbContext` into endpoints (closes Phase-3 RLS scaffolding gap)
- [Epic 27](Epic-27-Prompt-Store.md) — depends on 28-3 DbContext factory
- [Epic 29](Epic-29-Secret-Management.md) — depends on 28-3, 28-9 for tenant-secret cabinet wiring
- [Epic 30](Epic-30-Pluggable-Provisioning.md) — depends on 28-3, 28-5 for the per-backend dispatch reshape
- [Epic 31](Epic-31-Multi-Git-Platform.md) — depends on 28-9 for per-tenant platform routing
- [Epic 33](Epic-33-Per-Tenant-IdP.md) — depends on 28 for tenant lifecycle hooks

## Success metrics

1. **Four migration sets run clean on a fresh Postgres 17 instance**, twice in a row (idempotent), zero warnings.
2. **Tenant-create latency**: verify-email click → `tenants.Status='active'` p95 < 60s with 1 concurrent provisioning workflow, p95 < 120s with 10 concurrent.
3. **Tenant-delete is O(1)**: independent of tenant data volume; 10 events vs 10M events both finish in < 30s.
4. **Cross-tenant leak integration tests**: 12 targeted scenarios all pass with zero leaked tenant data (query without tenant context, stale JWT with old `tid`, API key routed to wrong tenant, workflow-variable tenant spoof, admin impersonation exit, pool-cache eviction mid-request, concurrent switch-org, connection-string decrypt with wrong KEK, forgotten `TenantId` in raw SQL, webhook routing with unresolved installation, rootless JWT hitting tenant-scoped route, orphaned `user_id` from a deleted user).
5. **Steady-state connection count bounded**: 1024 pools × 10 max-conns = 10k ceiling; observed `pg_stat_activity` under 4096 at steady state.

## Open questions

1. **OpenBao adoption trigger** (Story 28-13): paying enterprise customer demands KMS-backed KEK; security audit flags env-var KEK; OpenBao reaches LF graduation. Until then, env-var KEK is the design (Doc 01 §8.2).
2. **Orchestrator-per-tenant scale ceiling**: revisit at 500 production tenants per Doc 02 §12 open-decision #2. Benchmark in Story 28-10 sets the threshold.
3. **Per-tenant Elsa DB cost vs benefit at small scale**: trial tenants (free tier with low usage) get a tenant DB but barely use the per-tenant Elsa. A future optimisation could share Elsa across small tenants — out of scope for v1.

## Design documents

- `docs/stories/plans/db-per-tenant/01-control-plane-split.md`
- `docs/stories/plans/db-per-tenant/02-elsa-two-tier.md`
- `docs/stories/plans/db-per-tenant/03-async-tenant-provisioning.md`
- `docs/stories/plans/db-per-tenant/04-connection-pool-and-delete.md`
- `docs/stories/plans/db-per-tenant/00-sequencing.md` (3-phase execution plan)

## Story files

[Epic 28 stories on GitHub](/stories/epic-28/)

---

_Last updated: 2026-04-21_
