---
title: "Epic 17: Multi-Tenancy Foundation"
sidebar:
  order: 17
---

**Status:** Phase-3 Scaffolded (RLS migration shipped; runtime wiring blocked on Story 19-6)
**Stories:** 5 (17-1 through 17-5)
**Estimated Effort:** 62 hours

## Overview

Epic 17 introduces first-class multi-tenancy to every data layer in Tamma so that a single PostgreSQL 17 cluster can serve many organizations with hard isolation guarantees, while the standalone CLI mode continues to work without any cloud dependency.

A **tenant** maps to a **GitHub App installation** (organization or user account). This is the natural billing and isolation boundary.

## Current state

- **Phase-2 RLS migration shipped** (`20260419021119_Phase2RlsAndTriggers`) — RLS policies and triggers in place on all tenant-scoped tables
- **Phase-3 dual-connection scaffolding committed** — `TammaDb` (admin connection, no RLS) + `TammaAppDb` (app-role connection, RLS active) split landed during the auth-foundation sprint
- **RLS scaffold is not live**: no endpoints actually inject `TammaAppDbContext` yet. The runtime is still on the permissive admin connection (review finding 1, 2026-04-20)
- **Story 19-6 (Wire `TammaAppDbContext`)** closes the runtime wiring gap — it was added Wave-2 as the explicit follow-up
- **Epic 28 supersedes the shared-DB approach** for tenant isolation in the long term: db-per-tenant with `DROP DATABASE` deletion

## Goals

1. Define tenant model and create database schema with `tenants` table
2. Implement PostgreSQL Row-Level Security (RLS) for defense-in-depth isolation
3. Scope event store to tenants (per-tenant event streams)
4. Scope Elsa workflow instances to tenants
5. Build API middleware for tenant context propagation

## Value delivered

- Hard data isolation between tenants via PostgreSQL RLS (when wired)
- Tenant-scoped event sourcing (DCB pattern) with per-tenant streams
- Tenant-scoped Elsa workflow instances preventing cross-tenant data leakage
- Zero-trust tenant context propagation from auth layer through all stores
- Backward-compatible: CLI/self-hosted mode uses implicit "default" tenant

## Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 17-1 | Tenant Model & Database Schema | P0 | **Done** (`tenants`, `tenant_memberships` tables landed) |
| 17-2 | Row-Level Security & Tenant Isolation | P0 | **Phase-2 Done** (policies + triggers); Phase-3 scaffolded; runtime not live |
| 17-3 | Tenant-Scoped Event Store | P1 | Planned |
| 17-4 | Tenant-Scoped Workflow Instances | P1 | Planned |
| 17-5 | API Tenant Context Middleware | P0 | **Partially Done** — `TenantContextMiddleware` exists; not yet hitting `TammaAppDbContext` |

## Key technical details

### Tenant model

- One GitHub organization installs the Tamma GitHub App ⇒ one tenant
- Personal GitHub user installs the app ⇒ one tenant
- Self-hosted / CLI mode ⇒ implicit "default" tenant (all-zero UUID)
- `github_installations.installation_id` is the external identifier
- `tenants.tenant_id` (UUID PK) is the internal foreign key on all tenant-scoped tables
- M:N user-to-tenant via `tenant_memberships` (per Epic 18 extension)

### Row-Level Security (Phase-2 scaffold + Phase-3 dual-connection)

- **Phase 2** (shipped): RLS policies on tenant-scoped tables; `app.current_tenant_id` session variable; RLS exemption list documented (prompt store tables, system defaults)
- **Phase 3** (scaffolded, not live): two `DbContext` classes:
  - `TammaDb` — admin connection (bypasses RLS for migrations, system queries)
  - `TammaAppDb` — app-role connection (subject to RLS, used by tenant endpoints)
- **Runtime wiring** (gap): no endpoint injects `TammaAppDbContext` yet. Closed by Story 19-6 (app-role context wiring).
- **Per-tenant endpoint routing** (gap): closed by Epic 30 Story 30-8.

### Connection-level tenant setting

`SET app.current_tenant_id = '<uuid>'` called at start of every request. RLS policies read this session variable to filter rows.

### Design constraints

1. CLI mode preserved: standalone works without configuring tenants
2. No schema-per-tenant: shared-schema with RLS (superseded by Epic 28's db-per-tenant for SaaS)
3. Agents run on user infrastructure — tenant isolation in data layer is sufficient
4. Online ALTER TABLE with DEFAULT values to avoid table locks

### Dependency graph

```
Story 17-1 (tenant model + schema)
  ├─ Story 17-2 (RLS policies)
  │    └─ Story 17-5 (API tenant context middleware)
  │         └─ Story 19-6 (wire TammaAppDbContext into endpoints) ← closes runtime gap
  │              └─ Story 30-8 (per-tenant endpoint routing) ← closes routing gap
  ├─ Story 17-3 (tenant-scoped event store)
  └─ Story 17-4 (tenant-scoped workflow instances)
```

## Relationship to Epic 28

Epic 17 ships **shared-schema RLS** as the v1 isolation model. Epic 28 supersedes this with **database-per-tenant** for SaaS deployments. The two coexist:

- CLI / self-hosted mode: shared schema with default-tenant sentinel; RLS optional
- SaaS single-tenant (current): shared schema with RLS active (Epic 17 model)
- SaaS multi-tenant (Epic 28): db-per-tenant with `DROP DATABASE` deletion

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| GitHub App Auth | Epic 1.5 | Installation ID maps to tenant |
| Event Store | Epic 10 | Event store needs tenant scoping |
| Elsa Workflows | Epic 7 | Workflow instances need tenant scoping |
| API Framework | Epic 1.5 | Middleware injects tenant context |
| App-role wiring | Epic 19 (19-6) | Closes Phase-3 RLS scaffolding gap |
| Per-tenant routing | Epic 30 (30-8) | Closes per-tenant endpoint routing gap |
| DB-per-tenant | Epic 28 | Supersedes shared-schema for SaaS |

## Story files

[Epic 17 stories on GitHub](/stories/epic-17/)

---

_Last updated: 2026-04-21_
