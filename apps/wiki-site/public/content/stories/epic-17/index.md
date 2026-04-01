---
title: "Epic 17: Multi-Tenancy Foundation for Tamma SaaS"
---

## Overview

**Goal**: Introduce first-class multi-tenancy to every data layer in Tamma so that a single PostgreSQL 17 cluster can serve many organizations with hard isolation guarantees, while the standalone CLI mode continues to work without any cloud dependency.

**Value Delivered**:
- Hard data isolation between tenants via PostgreSQL Row-Level Security (RLS)
- Tenant-scoped event sourcing (DCB pattern) with per-tenant event streams
- Tenant-scoped ELSA workflow instances preventing cross-tenant data leakage
- Zero-trust tenant context propagation from authentication layer through all stores
- Backward-compatible: CLI/self-hosted mode operates as the implicit "default" tenant with no behavioral change

## Current State (Problems)

| Layer | Current Isolation | Issue |
|-------|-------------------|-------|
| PostgreSQL tables | None | `github_installations`, `users`, `user_api_keys`, `user_invites` have no `tenant_id` column |
| Event store (`IEventStore`) | None | `EngineEvent` has no tenant tag; all events share one stream |
| Workflow instances (`IWorkflowStore`) | None | `WorkflowInstance` has no tenant association |
| ELSA workflows (C# side) | None | `StartWorkflowAsync` takes no tenant context; variables are not scoped |
| API middleware | Partial | `InstallationContext` from API key auth carries `installationId`, but it is not propagated as a tenant boundary to stores |
| Task queue | Partial | `ITask.installationId` exists but is optional and not enforced |

**Key risk**: Without RLS, a bug in application code (missing WHERE clause, wrong join) can leak data across tenants. RLS provides defense-in-depth at the database level.

## Tenant Model Decision

A **tenant** in Tamma maps to a **GitHub App installation** (organization or user account). This is the natural billing and isolation boundary:

- One GitHub organization installs the Tamma GitHub App => one tenant
- A personal GitHub user installs the app => one tenant
- Self-hosted / CLI mode => implicit "default" tenant (UUID zero or a sentinel)

The `github_installations.installation_id` column is the external identifier. We introduce a new `tenants` table with a UUID primary key (`tenant_id`) that becomes the foreign key on all tenant-scoped tables. The `github_installations` table gains a `tenant_id` FK pointing to `tenants`.

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 17.1 | Tenant Model + Database Schema | P0 (Critical) | None | Planned |
| 17.2 | Row-Level Security for Tenant Isolation | P0 (Critical) | Story 17.1 | Planned |
| 17.3 | Tenant-Scoped Event Store | P1 (High) | Story 17.1 | Planned |
| 17.4 | Tenant-Scoped Workflow Instances | P1 (High) | Story 17.1 | Planned |
| 17.5 | API Tenant Context Middleware | P0 (Critical) | Story 17.1, 17.2 | Planned |

## Dependency Graph

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

Story 17.5 depends on both 17.1 and 17.2 because the middleware must set the PostgreSQL session variable that RLS policies read.

## Design Constraints

1. **CLI mode preserved**: Standalone/self-hosted mode must work without configuring tenants. A sentinel `DEFAULT_TENANT_ID` (all-zero UUID `00000000-0000-0000-0000-000000000000`) is used automatically.
2. **GitHub installations as tenant boundary**: `installation_id` is the external identifier; `tenant_id` (UUID) is the internal PK.
3. **PostgreSQL RLS as defense-in-depth**: Application-level WHERE clauses are the primary filter; RLS is the safety net.
4. **Connection-level tenant setting**: `SET app.current_tenant_id = '<uuid>'` is called at the start of every request/transaction. RLS policies read this variable.
5. **No schema-per-tenant**: Shared-schema with RLS is simpler operationally and works well up to thousands of tenants.
6. **Agents run on user infrastructure**: Tamma servers never execute code; agents run on GitHub-hosted or self-hosted runners. Tenant isolation in the data layer is sufficient.

## Estimated Total Effort

| Story | Estimate |
|-------|----------|
| 17.1 Tenant Model + Database Schema | 16 hours |
| 17.2 Row-Level Security for Tenant Isolation | 12 hours |
| 17.3 Tenant-Scoped Event Store | 10 hours |
| 17.4 Tenant-Scoped Workflow Instances | 10 hours |
| 17.5 API Tenant Context Middleware | 14 hours |
| **Total** | **62 hours** |

## Host Constraints

- **Database**: PostgreSQL 17 (existing single instance on Hetzner VPS)
- **No additional infrastructure**: RLS and session variables are built into PostgreSQL
- **Migration strategy**: Online ALTER TABLE with DEFAULT values to avoid table locks on small tables

---

**Last Updated**: 2026-03-28
**Epic Owner**: Platform Engineering
