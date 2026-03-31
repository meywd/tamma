---
title: "Epic 17: Multi-Tenancy Foundation"
sidebar:
  order: 17
---

**Status:** Drafted
**Stories:** 5 (17-1 through 17-5)
**Estimated Effort:** 62 hours

## Overview

Epic 17 introduces first-class multi-tenancy to every data layer in Tamma so that a single PostgreSQL 17 cluster can serve many organizations with hard isolation guarantees, while the standalone CLI mode continues to work without any cloud dependency.

A **tenant** maps to a **GitHub App installation** (organization or user account). This is the natural billing and isolation boundary.

## Goals

1. Define tenant model and create database schema with `tenants` table
2. Implement PostgreSQL Row-Level Security (RLS) for defense-in-depth isolation
3. Scope event store to tenants (per-tenant event streams)
4. Scope ELSA workflow instances to tenants
5. Build API middleware for tenant context propagation

## Value Delivered

- Hard data isolation between tenants via PostgreSQL RLS
- Tenant-scoped event sourcing (DCB pattern) with per-tenant streams
- Tenant-scoped ELSA workflow instances preventing cross-tenant data leakage
- Zero-trust tenant context propagation from auth layer through all stores
- Backward-compatible: CLI/self-hosted mode uses implicit "default" tenant

## Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 17-1 | Tenant Model & Database Schema | P0 (Critical) | Planned |
| 17-2 | Row-Level Security & Tenant Isolation | P0 (Critical) | Planned |
| 17-3 | Tenant-Scoped Event Store | P1 (High) | Planned |
| 17-4 | Tenant-Scoped Workflow Instances | P1 (High) | Planned |
| 17-5 | API Tenant Context Middleware | P0 (Critical) | Planned |

## Key Technical Details

### Tenant Model

- One GitHub organization installs the Tamma GitHub App => one tenant
- Personal GitHub user installs the app => one tenant
- Self-hosted / CLI mode => implicit "default" tenant (all-zero UUID)
- `github_installations.installation_id` is the external identifier
- `tenants.tenant_id` (UUID PK) is the internal foreign key on all tenant-scoped tables

### Row-Level Security

- **Connection-level tenant setting**: `SET app.current_tenant_id = '<uuid>'` called at start of every request
- RLS policies read this session variable to filter rows
- Application-level WHERE clauses are primary filter; RLS is defense-in-depth safety net
- Shared-schema with RLS (no schema-per-tenant) -- simpler operationally up to thousands of tenants

### Design Constraints

1. CLI mode preserved: standalone works without configuring tenants
2. No schema-per-tenant: shared-schema with RLS
3. Agents run on user infrastructure -- tenant isolation in data layer is sufficient
4. Online ALTER TABLE with DEFAULT values to avoid table locks

### Dependency Graph

```
Story 17.1 (tenant model + schema)
  |
  +---> Story 17.2 (RLS policies)
  |       |
  |       +---> Story 17.5 (API tenant context middleware)
  |
  +---> Story 17.3 (tenant-scoped event store)
  |
  +---> Story 17.4 (tenant-scoped workflow instances)
```

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| GitHub App Auth | Epic 1.5 | Installation ID maps to tenant |
| Event Store | Epic 10 | Event store needs tenant scoping |
| ELSA Workflows | Epic 7 | Workflow instances need tenant scoping |
| API Framework | Epic 1.5 | Middleware injects tenant context |

## Story Files

[Story documents on GitHub](/stories/epic-17/)
